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
    private IReadOnlyList<IUsageProvider> _providers = Array.Empty<IUsageProvider>();
    private readonly Dictionary<UsageProviderKind, UsageSnapshot> _latest = new();
    private bool _refreshing, _exiting, _suspended, _demo, _locked, _sleeping;
    private DateTimeOffset _lastRefresh;

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
        _window.SettingsChanged += ApplySettings;
        _window.HideRequested += () => _window.Hide();
        _window.Closing += (_, args) => { if (!_exiting) { args.Cancel = true; _window.Hide(); } };
        _window.StateChanged += (_, _) => { if (_window.WindowState == WindowState.Minimized && !_settings.ShowInTaskbar) _window.Hide(); };
        _window.SourceInitialized += (_, _) => RestorePosition();
        CreateTray();
        _providers = UsageProviderFactory.CreateDefault();
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
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ShowWindow); };
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
                if (_latest.TryGetValue(provider.Kind, out var previous) && previous.RetryAfter > DateTimeOffset.UtcNow) return;
                UsageSnapshot snapshot;
                try { snapshot = await provider.FetchAsync(_lifetime.Token); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch { snapshot = new UsageSnapshot(provider.Kind, UsageStatus.Unavailable, Array.Empty<UsageWindow>(), DateTimeOffset.UtcNow, Message: "조회 중 문제가 발생했습니다. 잠시 후 다시 시도해 주세요."); }
                if (!_exiting) Render(snapshot);
            }));
            if (!_exiting) _window.UpdateStatus($"최근 확인 {DateTime.Now:HH:mm:ss} · " + (_settings.RefreshIntervalSeconds == 0 ? "수동 갱신" : $"{_settings.RefreshIntervalSeconds}초 간격"));
        }
        finally { _refreshing = false; if (!_exiting) _window.SetRefreshing(false); }
    }

    private void Render(UsageSnapshot snapshot)
    {
        _latest[snapshot.Provider] = snapshot;
        _window!.RenderSnapshot(snapshot);
        var labels = _latest.Values.OrderBy(s => s.Provider).Select(s => $"{s.Provider}: " + (s.Status == UsageStatus.Ready && s.Windows.Count > 0 ? $"{s.Windows[0].RemainingPercent:0}% 남음" : "확인 필요"));
        if (_tray != null) _tray.Text = string.Join(" / ", labels)[..Math.Min(127, string.Join(" / ", labels).Length)];
        var ready = _latest.Values.Where(s => s.Status == UsageStatus.Ready).SelectMany(s => s.Windows).ToList();
        _window.TaskbarItemInfo!.ProgressState = ready.Count == 0 ? TaskbarItemProgressState.None : TaskbarItemProgressState.Normal;
        if (ready.Count > 0) _window.TaskbarItemInfo.ProgressValue = ready.Max(w => w.UsedPercent) / 100;
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
        await Task.Delay(900);
        _window!.UpdateLayout();
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "artifacts"));
        var bitmap = new RenderTargetBitmap((int)_window.ActualWidth, (int)_window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(_window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "artifacts", "window.png"))) encoder.Save(file);
        bool checks = _window.IsVisible && _window.ShowInTaskbar && !_window.Topmost;
        _settings.AlwaysOnTop = true; ApplySettings(); checks &= _window.Topmost;
        _settings.ShowInTaskbar = false; ApplySettings(); checks &= !_window.ShowInTaskbar;
        _settings.ShowInTaskbar = true; ApplySettings();
        _window.WindowState = WindowState.Minimized;
        checks &= _window.IsVisible;
        _window.WindowState = WindowState.Normal;
        _window.Hide(); checks &= !_window.IsVisible; ShowWindow(); checks &= _window.IsVisible;
        var settingsButton = (System.Windows.Controls.Button)_window.FindName("SettingsButton");
        settingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        _window.UpdateLayout();
        var settingsBitmap = new RenderTargetBitmap((int)_window.ActualWidth, (int)_window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        settingsBitmap.Render(_window);
        var settingsEncoder = new PngBitmapEncoder(); settingsEncoder.Frames.Add(BitmapFrame.Create(settingsBitmap));
        using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "artifacts", "settings.png"))) settingsEncoder.Save(file);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "artifacts", "smoke-test.txt"), checks ? "PASS: render, taskbar, always-on-top, minimize, hide, restore, settings" : "FAIL");
        ExitApplication();
    }

    private void ExitApplication()
    {
        _exiting = true; _timer.Stop(); _lifetime.Cancel();
        if (!_demo && _window != null) { _settings.Left = _window.Left; _settings.Top = _window.Top; try { SettingsStore.Save(_settings); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true; _timer.Stop(); _lifetime.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerMode; SystemEvents.SessionSwitch -= OnSessionSwitch;
        if (_tray != null) { _tray.Visible = false; _tray.Icon?.Dispose(); _tray.Dispose(); }
        foreach (var provider in _providers.OfType<IDisposable>()) provider.Dispose();
        _activationWait?.Unregister(null);
        _activateEvent?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

