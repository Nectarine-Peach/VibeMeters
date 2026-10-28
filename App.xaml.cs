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
    private readonly DispatcherTimer _windowSaveDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly HashSet<UsageProviderKind> _loginsInProgress = new();
    private readonly List<FileSystemWatcher> _credentialWatchers = new();
    private IReadOnlyList<IUsageProvider> _providers = Array.Empty<IUsageProvider>();
    private readonly Dictionary<UsageProviderKind, UsageSnapshot> _latest = new();
    private readonly RefreshRequestGate _refreshRequests = new();
    private bool _refreshing => _refreshRequests.IsRunning;
    private bool _exiting, _suspended, _demo, _locked, _sleeping, _credentialsChanged;
    private DateTimeOffset? _nextAutomaticAt;
    private DateTimeOffset _lastRefresh;
    private readonly Dictionary<string, int> _alertLevels = new();
    private HashSet<UsageProviderKind> _activeProviders = new();
    private int _exitCode;
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
        Localizer.SetLanguage(_settings.Language);
        if (_smokeTest) _settings.RefreshIntervalSeconds = 0;
        if (_settings.CompactMode) { _settings.WindowWidth = 240; _settings.WindowHeight = 148; _settings.CompactMode = false; }
        _settings.DesignLayoutVersion = Math.Max(2, _settings.DesignLayoutVersion);
        ThemeService.Apply(_settings.Theme);
        ThemeService.ThemeChanged += OnThemeChanged;
        _window = new MainWindow(_settings);
        MainWindow = _window;
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) => Dispatcher.InvokeAsync(ShowWindow), null, Timeout.Infinite, false);
        _window.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/VibeMeters.ico"));
        _window.RefreshRequested += async () => await RefreshAsync(RefreshTrigger.Manual);
        _window.LoginRequested += StartLogin;
        _window.SettingsChanged += ApplySettings;
        _window.GeometryChanged += ScheduleWindowStateSave;
        _windowSaveDebounce.Tick += (_, _) => PersistWindowState();
        _window.HideRequested += HideWindow;
        _window.Closing += (_, args) => { if (!_exiting) { args.Cancel = true; HideWindow(); } };
        _window.StateChanged += (_, _) => { if (_window.WindowState == WindowState.Minimized && !_settings.ShowInTaskbar) HideWindow(); };
        CreateTray();
        OnThemeChanged();
        _providers = _smokeTest ? Array.Empty<IUsageProvider>() : UsageProviderFactory.CreateDefault();
        foreach (var provider in _providers.OfType<UsageProvider>())
            provider.AuthenticationProgress += snapshot =>
            {
                if (!_exiting) _ = Dispatcher.InvokeAsync(() => { if (!_exiting) Render(snapshot); });
            };
        if (!_demo) WatchCredentials();
        _timer.Tick += async (_, _) =>
        {
            _nextAutomaticAt = RefreshPolicy.NextAutomatic(DateTimeOffset.UtcNow, _settings.RefreshIntervalSeconds, _suspended);
            foreach (var snapshot in _latest.Values) _window!.RenderSnapshot(WithSchedule(snapshot));
            await RefreshAsync(RefreshTrigger.Automatic);
        };
        ApplySettings();
        SystemEvents.PowerModeChanged += OnPowerMode;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        var startupLaunch = e.Args.Contains("--startup") || e.Args.Contains("--hidden");
        if (!startupLaunch || _settings.WindowVisible) ShowWindow();
        _ = RefreshAsync(RefreshTrigger.Automatic);
        if (e.Args.Contains("--smoke-test")) _ = SmokeTestAsync();
    }

    private void CreateTray()
    {
        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/VibeMeters.ico")).Stream;
        _tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(iconStream), Text = "VibeMeters · 사용량 확인", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("사용량 창 열기", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("지금 새로고침", null, (_, _) => Dispatcher.InvokeAsync(async () => await RefreshAsync(RefreshTrigger.Manual)));
        menu.Items.Add("항상 위에 표시", null, (_, _) => Dispatcher.Invoke(() => { _settings.AlwaysOnTop = !_settings.AlwaysOnTop; _window!.Topmost = _settings.AlwaysOnTop; ApplySettings(); }));
        menu.Items.Add("Claude 로그인", null, (_, _) => Dispatcher.Invoke(() => StartLogin(UsageProviderKind.Claude)));
        menu.Items.Add("Codex 로그인", null, (_, _) => Dispatcher.Invoke(() => StartLogin(UsageProviderKind.Codex)));
        menu.Items.Add("Gemini 로그인", null, (_, _) => Dispatcher.Invoke(() => StartLogin(UsageProviderKind.Gemini)));
        menu.Items.Add("Grok 로그인", null, (_, _) => Dispatcher.Invoke(() => StartLogin(UsageProviderKind.Grok)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ShowWindow); };
        foreach (Forms.ToolStripItem item in menu.Items) { item.Tag = item.Text; item.Text = Localizer.T(item.Text); }
        _tray.Text = Localizer.T("VibeMeters · 사용량 확인");
    }

    private async void StartLogin(UsageProviderKind provider)
    {
        if (_demo) { _window!.UpdateStatus("데모에서는 로그인을 실행하지 않습니다."); return; }
        if (!_loginsInProgress.Add(provider)) return;
        _window!.SetLoginStatus(provider, true, "백그라운드에서 로그인을 준비합니다. 브라우저에서 인증을 완료해 주세요.");
        try
        {
            var progress = new Progress<string>(message =>
            {
                if (!_exiting) _window.SetLoginStatus(provider, true, message);
            });
            LoginResult result;
            if (provider == UsageProviderKind.Claude && _providers.OfType<UsageProvider>().FirstOrDefault(p => p.Kind == provider) is { } claude)
            {
                _window.SetLoginStatus(provider, true, "브라우저를 열기 전에 기존 인증의 자동 복구를 확인합니다.");
                var flow = await ClaudeLoginFlow.RunAsync(
                    async token =>
                    {
                        var snapshot = await claude.FetchAsync(token, new UsageFetchOptions(true, DateTimeOffset.UtcNow, RecoverAuthentication: true));
                        if (!_exiting) Render(snapshot);
                        return snapshot;
                    },
                    token => PrepareAndLoginAsync(provider, token, progress), _lifetime.Token, new ClaudeAuthDiagnostics());
                if (_exiting) return;
                result = flow.Result;
                if (!flow.LoginStarted)
                {
                    _window.SetLoginStatus(provider, false, result.Message);
                    return;
                }
            }
            else result = await PrepareAndLoginAsync(provider, _lifetime.Token, progress);
            if (_exiting) return;
            _window.SetLoginStatus(provider, false, result.Message);
            if (result.Success)
            {
                foreach (var usage in _providers.OfType<UsageProvider>().Where(usage => usage.Kind == provider)) usage.NotifyLoginCompleted();
                if (provider == UsageProviderKind.Gemini)
                    foreach (var usage in _providers.OfType<GeminiUsageProvider>()) usage.NotifyLoginCompleted();
                if (provider == UsageProviderKind.Grok)
                    foreach (var usage in _providers.OfType<GrokUsageProvider>()) usage.NotifyLoginCompleted();
                if (_refreshing) _credentialsChanged = true;
                else await RefreshAsync(RefreshTrigger.Login);
            }
        }
        catch (OperationCanceledException) { if (!_exiting) _window.SetLoginStatus(provider, false, "로그인이 취소되었습니다."); }
        finally { _loginsInProgress.Remove(provider); }
    }

    private async Task<LoginResult> PrepareAndLoginAsync(UsageProviderKind provider, CancellationToken token, IProgress<string> progress)
    {
        if ((provider is UsageProviderKind.Codex or UsageProviderKind.Claude) && OfficialCliInstaller.Find(provider) is null)
        {
            ShowWindow();
            var installer = new CliInstallWindow(provider, token) { Owner = _window, Topmost = _window!.Topmost };
            installer.ShowDialog();
            token.ThrowIfCancellationRequested();
            if (!installer.Installed) return new(false, "CLI 설치를 완료하지 않았습니다. 로그인 버튼에서 다시 설치할 수 있습니다.");
        }
        return await LoginService.LoginAsync(provider, token, progress);
    }

    private void WatchCredentials()
    {
        _credentialDebounce.Tick += async (_, _) =>
        {
            _credentialDebounce.Stop();
            if (_refreshing) { _credentialsChanged = true; return; }
            await RefreshAsync(RefreshTrigger.Credentials);
        };
        foreach (var kind in Enum.GetValues<UsageProviderKind>())
        {
            string path;
            try { path = new CredentialReader().GetCredentialPath(kind); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { continue; }
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
        _settings.WindowVisible = true;
        _window!.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        ScheduleWindowStateSave();
        if (DateTimeOffset.UtcNow - _lastRefresh > TimeSpan.FromSeconds(30)) _ = RefreshAsync(RefreshTrigger.Activation);
    }

    private void HideWindow()
    {
        _window!.CaptureWindowGeometry();
        _settings.WindowVisible = false;
        _window.Hide();
        PersistWindowState();
    }

    private void ScheduleWindowStateSave()
    {
        if (_demo || _exiting) return;
        _windowSaveDebounce.Stop();
        _windowSaveDebounce.Start();
    }

    private void PersistWindowState()
    {
        _windowSaveDebounce.Stop();
        if (_demo || _window is null) return;
        _window.CaptureWindowGeometry();
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { if (!_exiting) _window.UpdateStatus("창 상태를 저장하지 못했습니다. 폴더 권한을 확인해 주세요."); }
    }

    private void ApplySettings()
    {
        Localizer.SetLanguage(_settings.Language);
        ThemeService.Apply(_settings.Theme);
        if (_tray?.ContextMenuStrip is { } localizedMenu)
            foreach (Forms.ToolStripItem item in localizedMenu.Items)
                if (item.Tag is string source) item.Text = Localizer.T(source);
        var visible = Enum.GetValues<UsageProviderKind>().Where(_settings.IsProviderVisible).ToHashSet();
        var enabledNewProvider = visible.Except(_activeProviders).Any();
        _activeProviders = visible;
        _window!.Topmost = _settings.AlwaysOnTop;
        _window.ShowInTaskbar = _settings.ShowInTaskbar;
        _nextAutomaticAt = RefreshPolicy.NextAutomatic(DateTimeOffset.UtcNow, _settings.RefreshIntervalSeconds, _suspended);
        foreach (var snapshot in _latest.Values) _window.RenderSnapshot(WithSchedule(snapshot));
        UpdateTray();
        if (enabledNewProvider && !_smokeTest && _lastRefresh != default)
        {
            if (_refreshing) _credentialsChanged = true;
            else Dispatcher.InvokeAsync(async () => await RefreshAsync(RefreshTrigger.Automatic));
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

    private async Task RefreshAsync(RefreshTrigger trigger, DateTimeOffset? queuedAt = null)
    {
        if (_exiting || _suspended) return;
        var requestedAt = queuedAt ?? DateTimeOffset.UtcNow;
        if (!_refreshRequests.TryBegin(trigger, requestedAt))
        {
            if (trigger == RefreshTrigger.Manual) _window!.UpdateStatus("조회 중 · 수동 새로고침을 예약했습니다.");
            return;
        }
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
                    if (kind == UsageProviderKind.Grok)
                    {
                        Render(new UsageSnapshot(kind, UsageStatus.Ready, new[] { new UsageWindow("Build 주간", 32, DateTimeOffset.Now.AddDays(4)) }, DateTimeOffset.Now, "데모", "데모 데이터"));
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
                try { snapshot = await provider.FetchAsync(_lifetime.Token,
                    new UsageFetchOptions(RefreshPolicy.BypassSuccessCache(trigger), requestedAt)); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch { snapshot = new UsageSnapshot(provider.Kind, UsageStatus.Unavailable, Array.Empty<UsageWindow>(), DateTimeOffset.UtcNow, Message: "조회 중 문제가 발생했습니다. 잠시 후 다시 시도해 주세요."); }
                if (!_exiting) Render(snapshot);
            }));
            if (!_exiting)
            {
                _window.UpdateStatus(_settings.RefreshIntervalSeconds == 0 ? "수동 새로고침 · F5" : $"자동 새로고침 · {_settings.RefreshIntervalSeconds}초 간격");
            }
        }
        finally
        {
            var pendingManual = _refreshRequests.Complete();
            if (!_exiting)
            {
                _window.SetRefreshing(false);
                if (pendingManual is { } pending)
                {
                    _credentialsChanged = false;
                    _ = Dispatcher.InvokeAsync(async () => await RefreshAsync(RefreshTrigger.Manual, pending));
                }
                else if (_credentialsChanged) { _credentialsChanged = false; _credentialDebounce.Start(); }
            }
        }
    }

    private void Render(UsageSnapshot snapshot)
    {
        _latest[snapshot.Provider] = snapshot;
        _window!.RenderSnapshot(WithSchedule(snapshot));
        UpdateTray();
        if (_tray != null) NotifyQuota(snapshot);
    }

    private UsageSnapshot WithSchedule(UsageSnapshot snapshot) => snapshot with
    {
        NextAutomaticAt = _suspended ? null : RefreshPolicy.NextAutomaticQuery(snapshot, _nextAutomaticAt, _settings.RefreshIntervalSeconds)
    };

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
            tooltip = Localizer.T(string.IsNullOrWhiteSpace(tooltip) ? "VibeMeters · 사용량 확인" : tooltip);
            _tray.Text = tooltip[..Math.Min(127, tooltip.Length)];
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
                _tray!.ShowBalloonTip(6000, Localizer.T($"{snapshot.Provider} {quota.Name}"), Localizer.T($"남은 사용량 {quota.RemainingPercent:0.#}% · 사용량 창에서 초기화 시간을 확인하세요."), Forms.ToolTipIcon.Warning);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (e.Cancel) return;
        PersistWindowState();
        _exiting = true;
    }
    private void OnPowerMode(object sender, PowerModeChangedEventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        if (e.Mode == PowerModes.Suspend) { PersistWindowState(); _sleeping = true; }
        else if (e.Mode == PowerModes.Resume) _sleeping = false;
        _suspended = _locked || _sleeping;
        ApplySettings();
        if (e.Mode == PowerModes.Resume && !_suspended) _ = RefreshAsync(RefreshTrigger.Resume);
    });
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        if (e.Reason == SessionSwitchReason.SessionLock) { PersistWindowState(); _locked = true; }
        else if (e.Reason == SessionSwitchReason.SessionUnlock) _locked = false;
        _suspended = _locked || _sleeping;
        ApplySettings();
        if (e.Reason == SessionSwitchReason.SessionUnlock && !_suspended) _ = RefreshAsync(RefreshTrigger.Resume);
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
        PersistWindowState();
        _exiting = true; _timer.Stop(); _lifetime.Cancel();
        _credentialDebounce.Stop();
        foreach (var watcher in _credentialWatchers) watcher.Dispose();
        Shutdown(_exitCode);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        PersistWindowState();
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
