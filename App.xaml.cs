using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
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
    private int _exitCode;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _demo = e.Args.Contains("--demo") || e.Args.Contains("--smoke-test");
        _mutex = new Mutex(true, _demo ? "Local\\VibeMeters.Demo" : "Local\\VibeMeters.App", out bool first);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _demo ? "Local\\VibeMeters.Demo.Activate" : "Local\\VibeMeters.Activate");
        if (!first) { _activateEvent.Set(); Shutdown(); return; }
        _settings = _demo ? new AppSettings() : SettingsStore.Load();
        _window = new MainWindow(_settings);
        MainWindow = _window;
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) => Dispatcher.InvokeAsync(ShowWindow), null, Timeout.Infinite, false);
        _window.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/VibeMeters.ico"));
        _window.TaskbarItemInfo = new TaskbarItemInfo();
        _window.RefreshRequested += async () => await RefreshAsync();
        _window.LoginRequested += StartLogin;
        _window.SettingsChanged += ApplySettings;
        _window.HideRequested += () => _window.Hide();
        _window.Closing += (_, args) => { if (!_exiting) { args.Cancel = true; _window.Hide(); } };
        _window.StateChanged += (_, _) => { if (_window.WindowState == WindowState.Minimized && !_settings.ShowInTaskbar) _window.Hide(); };
        _window.SourceInitialized += (_, _) => RestorePosition();
        CreateTray();
        _providers = UsageProviderFactory.CreateDefault();
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
        menu.Items.Add("미니 모드", null, (_, _) => Dispatcher.Invoke(() => _window!.SetCompactMode(!_settings.CompactMode)));
        menu.Items.Add("Claude 로그인", null, (_, _) => Dispatcher.Invoke(() => StartLogin(UsageProviderKind.Claude)));
        menu.Items.Add("Codex 로그인", null, (_, _) => Dispatcher.Invoke(() => StartLogin(UsageProviderKind.Codex)));
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
        _window!.Topmost = _settings.AlwaysOnTop;
        _window.ShowInTaskbar = _settings.ShowInTaskbar;
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
                foreach (var kind in Enum.GetValues<UsageProviderKind>())
                    Render(new UsageSnapshot(kind, UsageStatus.Ready, new[] { new UsageWindow("5시간", kind == UsageProviderKind.Codex ? 27 : 46, DateTimeOffset.Now.AddHours(2.4)), new UsageWindow("주간", kind == UsageProviderKind.Codex ? 39 : 18, DateTimeOffset.Now.AddDays(3)) }, DateTimeOffset.Now, kind == UsageProviderKind.Codex ? "Pro" : "Max", "데모 데이터"));
                _window.UpdateStatus("데모 모드 · 실제 계정 데이터가 아닙니다");
                return;
            }
            await Task.WhenAll(_providers.Select(async provider =>
            {
                UsageSnapshot snapshot;
                try { snapshot = await provider.FetchAsync(_lifetime.Token); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch { snapshot = new UsageSnapshot(provider.Kind, UsageStatus.Unavailable, Array.Empty<UsageWindow>(), DateTimeOffset.UtcNow, Message: "조회 중 문제가 발생했습니다. 잠시 후 다시 시도해 주세요."); }
                if (!_exiting) Render(snapshot);
            }));
            if (!_exiting) _window.UpdateStatus($"최근 확인 {DateTime.Now:HH:mm:ss} · " + (_settings.RefreshIntervalSeconds == 0 ? "수동 갱신" : $"{_settings.RefreshIntervalSeconds}초 간격"));
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
        var labels = _latest.Values.OrderBy(s => s.Provider).Select(s => $"{s.Provider}: " + (s.Status == UsageStatus.Ready && s.Windows.Count > 0 ? $"{s.Windows[0].RemainingPercent:0}% 남음" : "확인 필요"));
        if (_tray != null)
        {
            var tooltip = string.Join(" / ", labels);
            _tray.Text = tooltip[..Math.Min(127, tooltip.Length)];
            var oldIcon = _tray.Icon;
            _tray.Icon = TrayIconRenderer.Render(_latest);
            oldIcon?.Dispose();
            NotifyQuota(snapshot);
        }
        var ready = _latest.Values.Where(s => s.Status == UsageStatus.Ready).SelectMany(s => s.Windows).ToList();
        _window.TaskbarItemInfo!.ProgressState = ready.Count == 0 ? TaskbarItemProgressState.None : TaskbarItemProgressState.Normal;
        if (ready.Count > 0) _window.TaskbarItemInfo.ProgressValue = ready.Max(w => w.UsedPercent) / 100;
    }

    private void NotifyQuota(UsageSnapshot snapshot)
    {
        if (_demo || snapshot.Status != UsageStatus.Ready) return;
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
        var output = Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(output);
        var results = new List<string>();
        void Check(string name, bool passed) { results.Add($"{(passed ? "PASS" : "FAIL")}: {name}"); if (!passed) _exitCode = 1; }
        try
        {
            await Task.Delay(600);
            Check("initial window", _window!.IsVisible && _window.ShowInTaskbar && !_window.Topmost);
            _settings.AlwaysOnTop = true; ApplySettings(); Check("always on top", _window.Topmost);
            _settings.ShowInTaskbar = false; ApplySettings(); Check("taskbar disabled", !_window.ShowInTaskbar);
            _settings.ShowInTaskbar = true; ApplySettings();
            _window.WindowState = WindowState.Minimized; Check("minimize keeps taskbar window", _window.IsVisible);
            _window.WindowState = WindowState.Normal;
            _window.Hide(); Check("tray hide", !_window.IsVisible); ShowWindow(); Check("restore", _window.IsVisible);
            foreach (var size in new[] { (340, 320), (220, 140), (260, 180), (600, 300) })
            {
                _window.Width = size.Item1; _window.Height = size.Item2;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                CaptureWindow(Path.Combine(output, $"window-{size.Item1}x{size.Item2}.png"));
                var micro = (FrameworkElement)_window.FindName("MicroPanel");
                Check($"responsive {size.Item1}x{size.Item2}", micro.Visibility == (size.Item1 < 280 || size.Item2 < 220 ? Visibility.Visible : Visibility.Collapsed));
            }
            _window.SetCompactMode(true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check("mini mode", _settings.CompactMode);
            _window.SetCompactMode(false);
            _window.Width = 340; _window.Height = 320;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var settingsButton = (System.Windows.Controls.Button)_window.FindName("SettingsButton");
            settingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            CaptureWindow(Path.Combine(output, "settings.png"));
            Check("settings overlay", ((FrameworkElement)_window.FindName("SettingsPanel")).Visibility == Visibility.Visible);
            settingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            _window.RenderSnapshot(new UsageSnapshot(UsageProviderKind.Claude, UsageStatus.AuthenticationRequired, Array.Empty<UsageWindow>(), DateTimeOffset.Now, Message: "로그인을 완료한 뒤 자동으로 다시 확인합니다."));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            CaptureWindow(Path.Combine(output, "login-required.png"));
            Check("login recovery action", ((FrameworkElement)_window.FindName("ClaudeConnectButton")).Visibility == Visibility.Visible);
        }
        catch (Exception ex) { _exitCode = 1; results.Add("FAIL: " + ex.GetType().Name + " · " + ex.Message); }
        finally { File.WriteAllLines(Path.Combine(output, "smoke-test.txt"), results); ExitApplication(); }
    }

    private void CaptureWindow(string path)
    {
        _window!.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)_window.ActualWidth, (int)_window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(_window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
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
        if (_tray != null) { _tray.Visible = false; _tray.Icon?.Dispose(); _tray.Dispose(); }
        foreach (var provider in _providers.OfType<IDisposable>()) provider.Dispose();
        _activationWait?.Unregister(null);
        _activateEvent?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

