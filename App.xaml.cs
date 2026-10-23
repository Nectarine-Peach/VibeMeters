using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using VibeMeters.Models;
using VibeMeters.Services;
using Forms = System.Windows.Forms;

namespace VibeMeters;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activationWait;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private AppSettings _settings = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _credentialDebounce = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly List<FileSystemWatcher> _credentialWatchers = new();
    private IReadOnlyList<IUsageProvider> _providers = Array.Empty<IUsageProvider>();
    private readonly Dictionary<UsageProviderKind, UsageSnapshot> _latest = new();
    private bool _refreshing, _exiting, _suspended, _demo, _locked, _sleeping, _credentialsChanged;
    private DateTimeOffset _lastRefresh;
    private readonly Dictionary<string, int> _alertLevels = new();
    private HashSet<UsageProviderKind> _activeProviders = new();
    private int _exitCode;
    private bool _fitDesignOnce;
    private bool _smokeTest;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // A static, tiny 2D widget does not benefit from a dedicated Direct3D renderer.
        // Software rendering avoids a persistent GPU device and compositor threads.
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        _smokeTest = e.Args.Contains("--smoke-test");
        _demo = e.Args.Contains("--demo") || _smokeTest;
        _mutex = new Mutex(true, _smokeTest ? "Local\\VibeMeters.SmokeTest" : _demo ? "Local\\VibeMeters.Demo" : "Local\\VibeMeters.App", out bool first);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _smokeTest ? "Local\\VibeMeters.SmokeTest.Activate" : _demo ? "Local\\VibeMeters.Demo.Activate" : "Local\\VibeMeters.Activate");
        if (!first) { _activateEvent.Set(); Shutdown(); return; }
        _settings = _demo ? new AppSettings() : SettingsStore.Load();
        if (_smokeTest) _settings.RefreshIntervalSeconds = 0;
        if (_settings.CompactMode) { _settings.WindowWidth = 240; _settings.WindowHeight = 148; _settings.CompactMode = false; }
        _fitDesignOnce = !_demo && _settings.DesignLayoutVersion < 2;
        if (_fitDesignOnce) { _settings.WindowWidth = 360; _settings.WindowHeight = 400; _settings.DesignLayoutVersion = 2; }
        ThemeService.Apply(_settings.Theme);
        ThemeService.ThemeChanged += OnThemeChanged;
        _window = new MainWindow(_settings);
        if (_fitDesignOnce) _window.FitDesignSize();
        MainWindow = _window;
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) => Dispatcher.InvokeAsync(ShowWindow), null, Timeout.Infinite, false);
        _window.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/VibeMeters.ico"));
        _window.RefreshRequested += async () => await RefreshAsync();
        _window.LoginRequested += StartLogin;
        _window.SettingsChanged += ApplySettings;
        _window.HideRequested += () => _window.Hide();
        _window.Closing += (_, args) => { if (!_exiting) { args.Cancel = true; _window.Hide(); } };
        _window.StateChanged += (_, _) => { if (_window.WindowState == WindowState.Minimized && !_settings.ShowInTaskbar) _window.Hide(); };
        _window.SourceInitialized += (_, _) => RestorePosition();
        CreateTray();
        OnThemeChanged();
        _providers = _smokeTest ? Array.Empty<IUsageProvider>() : UsageProviderFactory.CreateDefault();
        if (!_demo) WatchCredentials();
        _timer.Tick += async (_, _) => await RefreshAsync();
        ApplySettings();
        SystemEvents.PowerModeChanged += OnPowerMode;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        if (!e.Args.Contains("--hidden")) _window.Show();
        _ = RefreshAsync();
        if (e.Args.Contains("--smoke-test")) _ = SmokeTestAsync();
    }

    private void CreateTray()
    {
        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/VibeMeters.ico")).Stream;
        _tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(iconStream), Text = "VibeMeters · 사용량 확인", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("사용량 창 열기", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("지금 새로고침", null, (_, _) => Dispatcher.InvokeAsync(async () => await RefreshAsync()));
        menu.Items.Add("항상 위에 표시", null, (_, _) => Dispatcher.Invoke(() => { _settings.AlwaysOnTop = !_settings.AlwaysOnTop; _window!.Topmost = _settings.AlwaysOnTop; ApplySettings(); }));
        menu.Items.Add("Claude 로그인", null, (_, _) => Dispatcher.Invoke(() => StartLogin(UsageProviderKind.Claude)));
        menu.Items.Add("Codex 로그인", null, (_, _) => Dispatcher.Invoke(() => StartLogin(UsageProviderKind.Codex)));
        menu.Items.Add("Gemini 로그인", null, (_, _) => Dispatcher.Invoke(() => StartLogin(UsageProviderKind.Gemini)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ShowWindow); };
    }

    private void StartLogin(UsageProviderKind provider)
    {
        if (_demo) { _window!.UpdateStatus("데모에서는 로그인을 실행하지 않습니다."); return; }
        var result = LoginService.StartLogin(provider);
        _window!.SetLoginStatus(provider, false, result.Message);
    }

    private void WatchCredentials()
    {
        _credentialDebounce.Tick += async (_, _) =>
        {
            _credentialDebounce.Stop();
            if (_refreshing) { _credentialsChanged = true; return; }
            await RefreshAsync();
        };
        foreach (var kind in Enum.GetValues<UsageProviderKind>())
        {
            var path = new CredentialReader().GetCredentialPath(kind);
            var folder = Path.GetDirectoryName(path)!;
            // Watch only an existing auth folder; a new folder is still detected on scheduled refresh.
            if (!Directory.Exists(folder)) continue;
            var watcher = new FileSystemWatcher(folder, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            void Changed(object sender, FileSystemEventArgs args) => Dispatcher.InvokeAsync(() => { if (!_exiting) { _credentialDebounce.Stop(); _credentialDebounce.Start(); } });
            watcher.Changed += Changed; watcher.Created += Changed; watcher.Renamed += (sender, args) => Changed(sender, args); watcher.Deleted += Changed;
            watcher.EnableRaisingEvents = true;
            _credentialWatchers.Add(watcher);
        }
    }

    private void ShowWindow()
    {
        _window!.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        if (DateTimeOffset.UtcNow - _lastRefresh > TimeSpan.FromSeconds(30)) _ = RefreshAsync();
    }

    private void ApplySettings()
    {
        ThemeService.Apply(_settings.Theme);
        var visible = Enum.GetValues<UsageProviderKind>().Where(_settings.IsProviderVisible).ToHashSet();
        var enabledNewProvider = visible.Except(_activeProviders).Any();
        _activeProviders = visible;
        _window!.Topmost = _settings.AlwaysOnTop;
        _window.ShowInTaskbar = _settings.ShowInTaskbar;
        foreach (var snapshot in _latest.Values) _window.RenderSnapshot(snapshot);
        UpdateTray();
        if (enabledNewProvider && !_smokeTest && _lastRefresh != default)
        {
            if (_refreshing) _credentialsChanged = true;
            else Dispatcher.InvokeAsync(async () => await RefreshAsync());
        }
        _timer.Stop();
        if (_settings.RefreshIntervalSeconds > 0 && !_suspended)
        {
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(30, _settings.RefreshIntervalSeconds));
            _timer.Start();
        }
        if (_demo) return;
        try { SettingsStore.Save(_settings); SettingsStore.SetStartup(_settings.StartWithWindows); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { _window.UpdateStatus("설정을 저장하지 못했습니다. 폴더 권한을 확인해 주세요."); }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _exiting || _suspended) return;
        _refreshing = true;
        _lastRefresh = DateTimeOffset.UtcNow;
        _window!.SetRefreshing(true);
        try
        {
            if (_demo)
            {
                foreach (var kind in Enum.GetValues<UsageProviderKind>().Where(_settings.IsProviderVisible))
                {
                    if (kind == UsageProviderKind.Gemini)
                    {
                        Render(new UsageSnapshot(kind, UsageStatus.Ready, new[] { new UsageWindow("Gemini Pro", 35, DateTimeOffset.Now.AddHours(5)), new UsageWindow("Gemini Flash", 12, DateTimeOffset.Now.AddHours(5)) }, DateTimeOffset.Now, "데모", "데모 데이터"));
                        continue;
                    }
                    Render(new UsageSnapshot(kind, UsageStatus.Ready, new[] { new UsageWindow("5시간", kind == UsageProviderKind.Codex ? 27 : 46, DateTimeOffset.Now.AddHours(2.4)), new UsageWindow("주간", kind == UsageProviderKind.Codex ? 39 : 18, DateTimeOffset.Now.AddDays(3)) }, DateTimeOffset.Now, kind == UsageProviderKind.Codex ? "Pro" : "Max", "데모 데이터"));
                }
                _window.UpdateStatus("데모 모드 · 실제 계정 데이터가 아닙니다");
                return;
            }
            await Task.WhenAll(_providers.Where(p => _settings.IsProviderVisible(p.Kind)).Select(async provider =>
            {
                UsageSnapshot snapshot;
                try { snapshot = await provider.FetchAsync(_lifetime.Token); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch { snapshot = new UsageSnapshot(provider.Kind, UsageStatus.Unavailable, Array.Empty<UsageWindow>(), DateTimeOffset.UtcNow, Message: "조회 중 문제가 발생했습니다. 잠시 후 다시 시도해 주세요."); }
                if (!_exiting) Render(snapshot);
            }));
            if (!_exiting)
            {
                if (_fitDesignOnce) { _fitDesignOnce = false; _window.FitDesignSize(); ApplySettings(); }
                _window.UpdateStatus(_settings.RefreshIntervalSeconds == 0 ? "수동 새로고침 · F5" : $"자동 새로고침 · {_settings.RefreshIntervalSeconds}초 간격");
            }
        }
        finally
        {
            _refreshing = false;
            if (!_exiting)
            {
                _window.SetRefreshing(false);
                if (_credentialsChanged) { _credentialsChanged = false; _credentialDebounce.Start(); }
            }
        }
    }

    private void Render(UsageSnapshot snapshot)
    {
        _latest[snapshot.Provider] = snapshot;
        _window!.RenderSnapshot(snapshot);
        UpdateTray();
        if (_tray != null) NotifyQuota(snapshot);
    }

    private void UpdateTray()
    {
        var selected = Enum.GetValues<UsageProviderKind>().Where(_settings.IsProviderVisible).ToDictionary(kind => kind,
            kind => _latest.TryGetValue(kind, out var snapshot) ? snapshot : new UsageSnapshot(kind, UsageStatus.NotConnected, Array.Empty<UsageWindow>(), DateTimeOffset.Now));
        var labels = selected.Values.OrderBy(s => s.Provider).Select(s => $"{s.Provider}: " + (s.Status == UsageStatus.Ready && s.Windows.Count > 0
            ? _settings.GetDisplayMode(s.Provider) == UsageDisplayMode.Used ? $"{s.Windows.Max(w => w.UsedPercent):0}% 사용" : $"{s.Windows.Min(w => w.RemainingPercent):0}% 남음"
            : "확인 필요"));
        if (_tray != null)
        {
            var tooltip = string.Join(" / ", labels);
            _tray.Text = string.IsNullOrWhiteSpace(tooltip) ? "VibeMeters · 사용량 확인" : tooltip[..Math.Min(127, tooltip.Length)];
            var oldIcon = _tray.Icon;
            _tray.Icon = TrayIconRenderer.Render(selected, _settings);
            oldIcon?.Dispose();
        }
    }

    private void NotifyQuota(UsageSnapshot snapshot)
    {
        if (_demo || !_settings.IsProviderVisible(snapshot.Provider) || snapshot.Status != UsageStatus.Ready) return;
        foreach (var quota in snapshot.Windows)
        {
            var key = $"{snapshot.Provider}:{quota.Name}";
            int level = quota.RemainingPercent <= 5 ? 2 : quota.RemainingPercent <= 20 ? 1 : 0;
            bool known = _alertLevels.TryGetValue(key, out int previous);
            _alertLevels[key] = level;
            if (_settings.NotifyLowQuota && known && level > previous)
                _tray!.ShowBalloonTip(6000, $"{snapshot.Provider} {quota.Name}", $"남은 사용량 {quota.RemainingPercent:0.#}% · 사용량 창에서 초기화 시간을 확인하세요.", Forms.ToolTipIcon.Warning);
        }
    }

    private void RestorePosition()
    {
        if (_settings.Left is not double left || _settings.Top is not double top || !double.IsFinite(left) || !double.IsFinite(top)) return;
        var bounds = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (bounds.Contains(new Rect(left, top, _window!.Width, 100))) { _window.Left = left; _window.Top = top; }
    }
    private void OnPowerMode(object sender, PowerModeChangedEventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        if (e.Mode == PowerModes.Suspend) _sleeping = true;
        else if (e.Mode == PowerModes.Resume) _sleeping = false;
        _suspended = _locked || _sleeping;
        ApplySettings();
        if (e.Mode == PowerModes.Resume && !_suspended) _ = RefreshAsync();
    });
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        if (e.Reason == SessionSwitchReason.SessionLock) _locked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock) _locked = false;
        _suspended = _locked || _sleeping;
        ApplySettings();
        if (e.Reason == SessionSwitchReason.SessionUnlock && !_suspended) _ = RefreshAsync();
    });

    private async Task SmokeTestAsync()
    {
        var output=Path.Combine(AppContext.BaseDirectory,"artifacts");
        Directory.CreateDirectory(output);
        _timer.Stop();
        try
        {
            var failures=await VisualVerification.RunAsync(_window!,_settings,ApplySettings,ShowWindow,Render,output);
            _exitCode=failures==0?0:1;
        }
        catch(Exception ex)
        {
            _exitCode=1;
            File.AppendAllText(Path.Combine(output,"smoke-test.txt"),$"FAIL: unhandled diagnostic exception · {ex}\n");
        }
        finally { ExitApplication(); }
    }
    private void ExitApplication()
    {
        _exiting = true; _timer.Stop(); _lifetime.Cancel();
        _credentialDebounce.Stop();
        foreach (var watcher in _credentialWatchers) watcher.Dispose();
        if (!_demo && _window != null) { _settings.Left = _window.Left; _settings.Top = _window.Top; try { SettingsStore.Save(_settings); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        Shutdown(_exitCode);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true; _timer.Stop(); _lifetime.Cancel();
        _credentialDebounce.Stop();
        foreach (var watcher in _credentialWatchers) watcher.Dispose();
        SystemEvents.PowerModeChanged -= OnPowerMode; SystemEvents.SessionSwitch -= OnSessionSwitch;
        ThemeService.ThemeChanged -= OnThemeChanged;
        ThemeService.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Icon?.Dispose(); _tray.Dispose(); }
        foreach (var provider in _providers.OfType<IDisposable>()) provider.Dispose();
        _activationWait?.Unregister(null);
        _activateEvent?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void OnThemeChanged()
    {
        _window?.RefreshTheme();
        if (_tray?.ContextMenuStrip is { } menu)
        {
            menu.BackColor = MenuColor("SurfaceBackground");
            menu.ForeColor = MenuColor("PrimaryText");
            menu.Renderer = new Forms.ToolStripProfessionalRenderer(new TrayMenuPalette());
        }
    }

    private static System.Drawing.Color MenuColor(string key)
    {
        var color = ((SolidColorBrush)Current.Resources[key]).Color;
        return System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private sealed class TrayMenuPalette : Forms.ProfessionalColorTable
    {
        public TrayMenuPalette() { UseSystemColors = false; }
        public override System.Drawing.Color ToolStripDropDownBackground => MenuColor("SurfaceBackground");
        public override System.Drawing.Color MenuItemSelected => MenuColor("SurfaceHover");
        public override System.Drawing.Color MenuItemBorder => MenuColor("PopupBorder");
        public override System.Drawing.Color MenuBorder => MenuColor("WindowBorder");
        public override System.Drawing.Color ImageMarginGradientBegin => MenuColor("SurfaceBackground");
        public override System.Drawing.Color ImageMarginGradientMiddle => MenuColor("SurfaceBackground");
        public override System.Drawing.Color ImageMarginGradientEnd => MenuColor("SurfaceBackground");
        public override System.Drawing.Color SeparatorDark => MenuColor("WindowBorder");
        public override System.Drawing.Color SeparatorLight => MenuColor("SurfaceBackground");
    }
}
