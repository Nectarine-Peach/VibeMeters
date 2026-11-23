using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeMeters.Models;

namespace VibeMeters.Services;

/// <summary>Opt-in checks of a shown WPF window. Never used by the normal application path.</summary>
internal sealed class VisualVerification
{
    private readonly MainWindow _window;
    private readonly AppSettings _settings;
    private readonly Action _applySettings;
    private readonly Action _restoreWindow;
    private readonly Action<UsageSnapshot> _render;
    private readonly string _output;
    private readonly List<string> _report=new();
    private int _failures;
    private static readonly DateTimeOffset DemoTime=new(2026,10,24,12,0,0,TimeSpan.FromHours(9));

    private VisualVerification(MainWindow window,AppSettings settings,Action applySettings,Action restoreWindow,Action<UsageSnapshot> render,string output)
    { _window=window;_settings=settings;_applySettings=applySettings;_restoreWindow=restoreWindow;_render=render;_output=output; }

    public static Task<int> RunAsync(MainWindow window,AppSettings settings,Action applySettings,Action restoreWindow,Action<UsageSnapshot> render,string output)=>
        new VisualVerification(window,settings,applySettings,restoreWindow,render,output).RunAsync();

    private static UsageSnapshot Demo(UsageProviderKind kind)=>new(kind,UsageStatus.Ready,kind switch
    {
        UsageProviderKind.Codex=>new[]{new UsageWindow("주간",39,DemoTime.AddDays(3))},
        UsageProviderKind.Claude=>new[]{new UsageWindow("5시간",0,DemoTime.AddHours(2)),new UsageWindow("주간",45,DemoTime.AddDays(3))},
        UsageProviderKind.Grok=>new[]{new UsageWindow("Build 주간",32,DemoTime.AddDays(4))},
        _=>new[]{new UsageWindow("Pro",17,DemoTime.AddHours(5)),new UsageWindow("Flash",6,DemoTime.AddHours(5))}
    },DemoTime,"DEMO","검증용 데모 데이터 · 실제 계정 사용량이 아닙니다.");

    private async Task<int> RunAsync()
    {
        Directory.CreateDirectory(_output);
        _report.Add("VibeMeters shown-window WPF verification");
        _report.Add("ISOLATION: --smoke-test uses its own mutex, in-memory settings, no provider instances and no credential watchers.");
        _report.Add("DATA: Synthetic Codex remaining 61%; Claude remaining 100% / 55%; Gemini remaining 83% / 94%; Grok Build remaining 68%. These are not account measurements.");
        _report.Add("CAPTURE: RenderTargetBitmap of the shown window's client root at its current monitor DPI; excludes native desktop/compositor and tray pixels.");
        try
        {
            _settings.Language=AppLanguage.Korean;
            Localizer.SetLanguage(AppLanguage.Korean);
            _settings.RefreshIntervalSeconds=0;
            _settings.ShowCodex=true;_settings.ShowClaude=true;_settings.ShowGemini=false;
            _settings.CodexDisplayMode=UsageDisplayMode.Remaining;
            _settings.ClaudeDisplayMode=UsageDisplayMode.Remaining;
            _settings.GeminiDisplayMode=UsageDisplayMode.Remaining;
            _settings.Theme=AppTheme.Dark;_settings.AlwaysOnTop=false;_settings.ShowInTaskbar=true;
            _applySettings();
            foreach(var kind in Enum.GetValues<UsageProviderKind>()) _render(Demo(kind));
            _window.Show();
            await StableAsync(_window);
            Check("shown HWND and presentation source",_window.IsVisible&&_window.IsLoaded&&new WindowInteropHelper(_window).Handle!=IntPtr.Zero&&PresentationSource.FromVisual(_window)!=null);
            Check("manual refresh diagnostics isolation",_settings.RefreshIntervalSeconds==0);

            await CaptureScenario("standard dark",360,267,"window-standard-dark.png");
            Check("controlled demonstration percentages",Rows(Cards()[0]).Select(ValueText).SequenceEqual(new[]{"61%"})&&Rows(Cards()[1]).Select(ValueText).SequenceEqual(new[]{"100%","55%"}));
            await CaptureScenario("mini dark",274,174,"window-mini-dark.png");
            await CaptureScenario("minimum dark",220,140,"window-minimum-dark.png");
            await CheckResetCountdownAsync();
            await CaptureScenario("wide dark",600,300,"window-wide-dark.png");

            await CheckWindowActionsAsync();
            await CheckRefreshStatusAsync();
            await ResizeAsync(360,267);
            var beforeSettingsSize=new Size(_window.ActualWidth,_window.ActualHeight);
            Click((Button)_window.FindName("SettingsButton"));
            await StableAsync(_window);
            var settings=Application.Current.Windows.OfType<SettingsWindow>().SingleOrDefault(w=>w.IsVisible);
            Check("settings opens a separate owned window",settings!=null&&settings.Owner==_window);
            Check("settings preserves main window size",Close(_window.ActualWidth,beforeSettingsSize.Width)&&Close(_window.ActualHeight,beforeSettingsSize.Height));
            if(settings!=null)
            {
                await StableAsync(settings);
                Check("settings contains no scroll viewers", !Descendants<ScrollViewer>(settings).Any());
                var previousHeight = settings.Height;
                settings.Height = settings.MinHeight;
                await StableAsync(settings);
                var footer = Descendants<TextBlock>(settings).Single(text => text.Text.StartsWith("자동 저장"));
                var status = (TextBlock)settings.FindName("LoginStatusText");
                var lastLogin = (Button)settings.FindName("GrokLoginButton");
                var frame = Root(settings);
                Check("last login button and status fit above the footer at minimum settings height",
                    lastLogin.ActualHeight > 0 && status.ActualHeight > 0 &&
                    Bounds(lastLogin, frame).Bottom <= Bounds(status, frame).Top + 1 &&
                    Bounds(status, frame).Bottom <= Bounds(footer, frame).Top + 1);
                settings.Height = previousHeight;
                await StableAsync(settings);
                Check("settings has all service selection and mode controls",new[]{"CodexCheck","ClaudeCheck","GeminiCheck","GrokCheck","CodexModeCombo","ClaudeModeCombo","GeminiModeCombo","GrokModeCombo","ThemeCombo","IntervalCombo"}.All(name=>settings.FindName(name)!=null));
                Capture(settings,"settings-dark.png");
                Check("settings labels fit the client",TextFits(Root(settings),Root(settings)));
                var language = (ComboBox)settings.FindName("LanguageCombo");
                SelectTag(language, "English");
                await StableAsync(settings);
                Capture(settings, "settings-en-dark.png");
                SelectTag(language, "Korean");
                await StableAsync(settings);
                await CheckSettingsInteractionsAsync(settings);
                settings.Close();
                await StableAsync(_window);
                await CheckProviderTaskbarButtonsAsync();
                await CheckTaskbarWidgetAsync();
            }

            SetTheme(AppTheme.Light);
            await CaptureScenario("standard light",360,267,"window-standard-light.png");
            Check("light theme active",!ThemeService.IsDark);
            await CaptureScenario("mini light",274,174,"window-mini-light.png");
            SetTheme(AppTheme.Dark);

            await CheckSelectionsAsync();
            await CheckDisplayModesAsync();
            await CheckUnavailableStatesAsync();
            await CheckBusinessStatesAsync();
            await CheckGeometryRoundTripAsync();
            Check("no footer or mini toggle controls",_window.FindName("LastUpdatedText")==null&&_window.FindName("StatusText")==null&&_window.FindName("MiniButton")==null);
            Check("same responsive composition at every size",_window.FindName("MicroPanel")==null);
        }
        catch(Exception ex)
        {
            _failures++;
            _report.Add($"FAIL: diagnostic exception · {ex}");
        }
        finally
        {
            foreach(var owned in Application.Current.Windows.OfType<SettingsWindow>().ToArray()) owned.Close();
            _report.Add($"RESULT: {_report.Count(line=>line.StartsWith("PASS:",StringComparison.Ordinal))} passed; {_failures} failed");
            File.WriteAllLines(Path.Combine(_output,"smoke-test.txt"),_report);
        }
        return _failures;
    }

    private async Task CaptureScenario(string name,double width,double height,string filename)
    {
        await ResizeAsync(width,height);
        Check(name+" requested logical size",Close(_window.ActualWidth,width)&&Close(_window.ActualHeight,height));
        AssertMainLayout(name,new[]{("Codex",1),("Claude",2)});
        Capture(_window,filename);
    }

    private async Task CheckResetCountdownAsync()
    {
        var deadline = DateTimeOffset.Now.AddSeconds(2);
        _render(Demo(UsageProviderKind.Claude) with
        {
            Windows = new[] { new UsageWindow("5시간", 21, deadline), new UsageWindow("주간", 44, null) }
        });
        var rows = Rows(Cards()[1]);
        var labels = rows.Select(row => row.Children.OfType<TextBlock>().Single(text => Grid.GetColumn(text) == 3)).ToArray();
        _window.UpdateLayout();
        Check("reset time and unknown deadline visible at minimum width",
            labels[0].Text == "리셋 1분" && labels[1].Text == "리셋 미제공" &&
            labels.All(label => label.IsVisible && label.ActualWidth >= label.DesiredSize.Width - label.Margin.Left - 1) &&
            rows.All(row => Meter(row).ActualWidth > 0));
        await Task.Delay(TimeSpan.FromMilliseconds(2200));
        await StableAsync(_window);
        Check("reset expires on screen with automatic fetching disabled", labels[0].Text == "리셋 대기" && ValueText(rows[0]) == "79%");
        _render(Demo(UsageProviderKind.Claude));
    }

    private async Task CheckWindowActionsAsync()
    {
        Click((Button)_window.FindName("PinButton"));
        await StableAsync(_window);
        Check("pin control enables always on top",_window.Topmost&&_settings.AlwaysOnTop);
        Click((Button)_window.FindName("PinButton"));
        Check("pin control disables always on top",!_window.Topmost&&!_settings.AlwaysOnTop);
        Check("taskbar title shows each selected provider and progress tracks the tightest limit",
            _window.Title.Contains("Codex 61% 남음") && _window.Title.Contains("Claude 55% 남음") &&
            !_window.Title.Contains("Gemini") && !_window.Title.Contains("Grok") &&
            _window.TaskbarItemInfo is { ProgressState: System.Windows.Shell.TaskbarItemProgressState.Normal } quota &&
            Math.Abs(quota.ProgressValue - .45) < .001 && quota.Description.Contains("Codex") &&
            quota.Description.Contains("초기화") && !quota.Description.Contains("Gemini"));
        var claude = Demo(UsageProviderKind.Claude);
        _render(claude with { Windows = new[] { new UsageWindow("주간", 80, null) } });
        Check("taskbar warns at 20% remaining",_window.TaskbarItemInfo.ProgressState == System.Windows.Shell.TaskbarItemProgressState.Paused);
        _render(claude with { Windows = new[] { new UsageWindow("주간", 95, null) } });
        Check("taskbar warns at 5% remaining",_window.TaskbarItemInfo.ProgressState == System.Windows.Shell.TaskbarItemProgressState.Error);
        _render(claude with { Status = UsageStatus.RateLimited });
        Check("taskbar excludes retained quotas from failed lookups",
            _window.Title.Contains("Claude —") && !_window.Title.Contains("55%") &&
            _window.Title.Contains("Codex 61% 남음") && Math.Abs(_window.TaskbarItemInfo.ProgressValue - .39) < .001 &&
            _window.TaskbarItemInfo.Description.Contains("Claude · 확인 필요"));
        _render(claude);
        _settings.ClaudeDisplayMode=UsageDisplayMode.Used;
        _settings.ShowGemini=true;_settings.ShowGrok=true;
        _settings.Language=AppLanguage.English;
        _applySettings();
        Check("taskbar title shows four independent percentages in the selected language and display modes",
            new[]{"Codex 61% Left","Claude 45% Used","Gemini 83% Left","Grok 68% Left"}.All(_window.Title.Contains));
        _settings.ShowClaude=false;_settings.ShowGrok=false;
        _applySettings();
        Check("hiding providers removes their text and recalculates the tightest displayed quota",
            !_window.Title.Contains("Claude") && !_window.Title.Contains("Grok") &&
            !_window.TaskbarItemInfo.Description.Contains("Claude") && !_window.TaskbarItemInfo.Description.Contains("Grok") &&
            _window.Title.Contains("Codex 61% Left") && _window.Title.Contains("Gemini 83% Left") &&
            Math.Abs(_window.TaskbarItemInfo.ProgressValue - .39) < .001);
        _settings.ShowCodex=false;_settings.ShowGemini=false;
        _applySettings();
        Check("hiding all providers clears taskbar percentages and progress",_window.Title == "VibeMeters" &&
            _window.TaskbarItemInfo is { ProgressState: System.Windows.Shell.TaskbarItemProgressState.None, ProgressValue: 0 });
        _settings.ShowCodex=true;_settings.ShowClaude=true;
        _settings.ClaudeDisplayMode=UsageDisplayMode.Remaining;_settings.Language=AppLanguage.Korean;
        _settings.ShowInTaskbar=false;_applySettings();
        Check("taskbar preference disabled",!_window.ShowInTaskbar && _window.TaskbarItemInfo == null && _window.Title == "VibeMeters");
        _settings.ShowInTaskbar=true;_applySettings();
        Check("taskbar preference restores both selected percentages",_window.ShowInTaskbar &&
            _window.Title.Contains("Codex 61% 남음") && _window.Title.Contains("Claude 55% 남음"));
        _window.WindowState=WindowState.Minimized;
        await StableAsync(_window);
        Check("taskbar minimize retains shown window",_window.WindowState==WindowState.Minimized&&_window.IsVisible);
        _window.WindowState=WindowState.Normal;
        Click((Button)_window.FindName("HideButton"));
        Check("header close hides to tray",!_window.IsVisible);
        Check("tray hide records hidden startup preference",!_settings.WindowVisible);
        _restoreWindow();
        await StableAsync(_window);
        Check("tray restore action restores main window",_window.IsVisible&&_window.WindowState==WindowState.Normal);
        Check("tray restore records visible startup preference",_settings.WindowVisible);
    }

    private async Task CheckGeometryRoundTripAsync()
    {
        // Exercise the same serialized preferences used at startup without accessing the user's file.
        var oldLeft=_window.Left;var oldTop=_window.Top;
        var oldModes=(_settings.CodexDisplayMode,_settings.ClaudeDisplayMode,_settings.GeminiDisplayMode);
        MainWindow? restored=null;
        try
        {
            await ResizeAsync(274,174);
            var area=System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(_window).Handle).WorkingArea;
            var deviceToDip=PresentationSource.FromVisual(_window)?.CompositionTarget?.TransformFromDevice??Matrix.Identity;
            var work=new MatrixTransform(deviceToDip).TransformBounds(new Rect(area.X,area.Y,area.Width,area.Height));
            var left=work.Left+Math.Min(32,Math.Max(0,work.Width-274));
            var top=work.Top+Math.Min(32,Math.Max(0,work.Height-174));
            _window.Left=left;_window.Top=top;
            _settings.CodexDisplayMode=UsageDisplayMode.Used;
            _settings.ClaudeDisplayMode=UsageDisplayMode.Remaining;
            _settings.GeminiDisplayMode=UsageDisplayMode.Used;
            _window.ApplyProviderVisibility();
            await StableAsync(_window);
            _window.CaptureWindowGeometry();
            var saved=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
            Check("geometry JSON stores last size and position",Close(saved.WindowWidth,274)&&Close(saved.WindowHeight,174)&&saved.Left is double savedLeft&&Close(savedLeft,left)&&saved.Top is double savedTop&&Close(savedTop,top));
            Check("geometry JSON preserves theme, modes and visibility",saved.Theme==_settings.Theme&&saved.CodexDisplayMode==UsageDisplayMode.Used&&saved.ClaudeDisplayMode==UsageDisplayMode.Remaining&&saved.GeminiDisplayMode==UsageDisplayMode.Used&&saved.WindowVisible==_settings.WindowVisible);
            restored=new MainWindow(saved);
            foreach(var kind in Enum.GetValues<UsageProviderKind>()) restored.RenderSnapshot(Demo(kind));
            restored.Show();
            await StableAsync(restored);
            Check("new shown window restores serialized size",Close(restored.ActualWidth,274)&&Close(restored.ActualHeight,174));
            Check("new shown window restores serialized monitor position",Close(restored.Left,left)&&Close(restored.Top,top));
            Check("restored preferences use an independent in-memory copy",!ReferenceEquals(restored.Settings,_settings)&&restored.Settings.Theme==_settings.Theme&&restored.Settings.GetDisplayMode(UsageProviderKind.Codex)==UsageDisplayMode.Used);
            restored.CaptureWindowGeometry();
            var expected=new Rect(saved.Left!.Value,saved.Top!.Value,saved.WindowWidth,saved.WindowHeight);
            restored.WindowState=WindowState.Minimized;
            await StableAsync(restored);
            restored.CaptureWindowGeometry();
            var minimized=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved))!;
            Check("minimized capture retains normal RestoreBounds",minimized.Left is double minLeft&&minimized.Top is double minTop&&Close(minLeft,expected.Left)&&Close(minTop,expected.Top)&&Close(minimized.WindowWidth,expected.Width)&&Close(minimized.WindowHeight,expected.Height)&&minLeft>-30000&&minTop>-30000);
            restored.WindowState=WindowState.Normal;
            await StableAsync(restored);
            Check("minimized round trip restores usable window geometry",Close(restored.ActualWidth,expected.Width)&&Close(restored.ActualHeight,expected.Height)&&Close(restored.Left,expected.Left)&&Close(restored.Top,expected.Top));
            saved.WindowVisible=false;
            var hidden=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved))!;
            Check("hidden startup preference survives JSON round trip",!hidden.WindowVisible&&Close(hidden.WindowWidth,274)&&Close(hidden.WindowHeight,174));
            _report.Add("GEOMETRY: JSON round trip and shown-window reconstruction only; no OS shutdown/reboot was simulated and no user settings file was written.");
        }
        finally
        {
            restored?.Close();
            _settings.CodexDisplayMode=oldModes.Item1;_settings.ClaudeDisplayMode=oldModes.Item2;_settings.GeminiDisplayMode=oldModes.Item3;
            _window.Left=oldLeft;_window.Top=oldTop;
            _window.ApplyProviderVisibility();
        }
    }

    private async Task CheckSettingsInteractionsAsync(SettingsWindow settings)
    {
        var account = (ComboBox)settings.FindName("GeminiProfileCombo");
        SelectTag(account, "CodeAssist2");
        Check("Gemini profile selection persists across settings windows", _settings.GeminiProfile == GeminiProfile.CodeAssist2 &&
            JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))?.GeminiProfile == GeminiProfile.CodeAssist2);
        SelectTag(account, "Default");
        var theme=(ComboBox)settings.FindName("ThemeCombo");
        SelectTag(theme,"Light");await StableAsync(_window);
        Check("settings light selection updates application",_settings.Theme==AppTheme.Light&&!ThemeService.IsDark);
        SelectTag(theme,"System");await StableAsync(_window);
        Check("settings system selection persisted in memory",_settings.Theme==AppTheme.System);
        SelectTag(theme,"Dark");await StableAsync(_window);
        Check("settings dark selection updates application",_settings.Theme==AppTheme.Dark&&ThemeService.IsDark);
        var mode=(ComboBox)settings.FindName("ClaudeModeCombo");
        SelectTag(mode,"Used");await StableAsync(_window);
        Check("settings Claude mode selection updates preference",_settings.ClaudeDisplayMode==UsageDisplayMode.Used);
        SelectTag(mode,"Remaining");await StableAsync(_window);
        CheckProviderTrayIcons(settings, mode);
        var interval=(ComboBox)settings.FindName("IntervalCombo");
        SelectTag(interval,"0");
        Check("settings manual interval retained",_settings.RefreshIntervalSeconds==0);
        var taskbarMode = (ComboBox)settings.FindName("TaskbarModeCombo");
        SelectTag(taskbarMode, "Off");
        Check("taskbar off removes the main window button", !_settings.ShowInTaskbar && !_window.ShowInTaskbar);
        SelectTag(taskbarMode, "Widget");
        var widgetSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
        Check("taskbar widget selection persists and suppresses the dashboard button",
            widgetSettings.ShowInTaskbar && widgetSettings.ShowTaskbarWidget && !widgetSettings.SeparateTaskbarButtons && !_window.ShowInTaskbar);
        var chooser = (Button)settings.FindName("WidgetServicesButton");
        Click(chooser);
        var menu = chooser.ContextMenu!;
        var items = menu.Items.OfType<MenuItem>().ToDictionary(item => item.Tag!.ToString()!);
        Check("widget chooser distinguishes enabled services from dashboard selection",
            items.Count == 4 && items["Codex"].IsChecked && items["Claude"].IsChecked && !items["Grok"].IsChecked);
        items["Grok"].IsChecked = true;
        items["Grok"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check("widget chooser adds a service without enabling its dashboard card",
            _settings.IsWidgetSelected(UsageProviderKind.Grok) && !_settings.ShowGrok &&
            JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!.IsWidgetSelected(UsageProviderKind.Grok));
        menu.IsOpen = false;
        SelectTag(taskbarMode, "Separate");
        var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
        Check("separate taskbar mode persists and removes the combined button",
            saved.ShowInTaskbar && saved.SeparateTaskbarButtons && !_window.ShowInTaskbar);
    }

    private async Task CheckProviderTaskbarButtonsAsync()
    {
        static Window[] Buttons() => Application.Current.Windows.OfType<Window>()
            .Where(w => w.Tag is UsageProviderKind).ToArray();
        static IntPtr Handle(Window window) => new WindowInteropHelper(window).Handle;
        static string NativeTitle(Window window)
        {
            var title = new StringBuilder(512);
            GetWindowTextW(Handle(window), title, title.Capacity);
            return title.ToString();
        }
        static bool ShownMinimized(Window window) => window.ShowInTaskbar && window.IsVisible && window.Owner == null
            && IsWindowVisible(Handle(window)) && IsIconic(Handle(window));
        try
        {
            var initial = Buttons();
            Check("separate taskbar mode exposes just the two enabled providers as minimized native windows",
                !_window.ShowInTaskbar && initial.Length == 2 && initial.All(ShownMinimized) &&
                initial.Select(w => w.Tag).Distinct().Count() == 2);
            var claude = initial.FirstOrDefault(w => Equals(w.Tag, UsageProviderKind.Claude));
            var codex = initial.FirstOrDefault(w => Equals(w.Tag, UsageProviderKind.Codex));
            if (codex == null || claude == null) return;
            Check("native taskbar titles and window icons expose each selected service's quota",
                NativeTitle(codex) == "W 61%" && NativeTitle(claude) == "5H100%/W55%" &&
                codex.TaskbarItemInfo.Description.Contains("Codex") && claude.TaskbarItemInfo.Description.Contains("Claude") &&
                !ReferenceEquals(codex.Icon, claude.Icon) &&
                initial.All(w => w.Icon != null && SendMessageW(Handle(w), 0x007f /* WM_GETICON */, 1 /* ICON_BIG */, 0) != 0));
            _settings.ClaudeDisplayMode = UsageDisplayMode.Used;
            _applySettings();
            _render(Demo(UsageProviderKind.Claude));
            Check("independent native titles change in place without duplicate buttons",
                Buttons().Length == 2 && NativeTitle(claude).Contains("45%") && NativeTitle(codex).Contains("61%"));
            _render(Demo(UsageProviderKind.Claude) with { Status = UsageStatus.RateLimited });
            Check("unavailable native taskbar button drops its previous percentage",
                NativeTitle(claude).Contains("—") && !NativeTitle(claude).Contains('%'));
            _render(Demo(UsageProviderKind.Claude));
            _settings.ShowGemini = _settings.ShowGrok = true;
            _applySettings();
            var all = Buttons();
            Check("four enabled providers have four distinct native taskbar handles",
                all.Length == 4 && all.All(ShownMinimized) && all.Select(Handle).Distinct().Count() == 4);
            Click((Button)_window.FindName("HideButton"));
            Check("provider buttons remain accessible with dashboard hidden", !_window.IsVisible && all.All(ShownMinimized));
            SendMessageW(Handle(claude), 0x0112 /* WM_SYSCOMMAND */, 0xf120 /* SC_RESTORE */, 0);
            await StableAsync(_window);
            Check("native taskbar restore brings back dashboard and leaves provider buttons minimized",
                _window.IsVisible && _window.WindowState == WindowState.Normal && !_window.ShowInTaskbar &&
                Buttons().Length == 4 && all.All(ShownMinimized));
            var removed = all.Where(w => w != codex && w != claude).Select(Handle).ToArray();
            _settings.ShowGemini = _settings.ShowGrok = false;
            _applySettings();
            Check("disabling providers destroys their native handles only",
                Buttons().Length == 2 && removed.All(hwnd => !IsWindow(hwnd)) && initial.All(ShownMinimized));
            var remaining = initial.Select(Handle).ToArray();
            _settings.SeparateTaskbarButtons = false;
            _applySettings();
            Check("switching back to combined restores one button and destroys all provider windows",
                _window.ShowInTaskbar && Buttons().Length == 0 && remaining.All(hwnd => !IsWindow(hwnd)));
        }
        finally
        {
            _settings.ShowGemini = _settings.ShowGrok = _settings.SeparateTaskbarButtons = false;
            _settings.ClaudeDisplayMode = UsageDisplayMode.Remaining;
            _applySettings();
            if (!_window.IsVisible) _restoreWindow();
        }
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    private struct WidgetRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out WidgetRect bounds);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr hwnd, int index);

    private async Task CheckTaskbarWidgetAsync()
    {
        static TaskbarWidgetWindow[] Widgets() => Application.Current.Windows.OfType<TaskbarWidgetWindow>().ToArray();
        static Button Tile(TaskbarWidgetWindow view) => (Button)view.FindName("ProviderButton");
        static TextBlock[] QuotaRows(Button chip) => Descendants<TextBlock>((DependencyObject)chip.Content).Where(t => t.Text.Contains('%')).ToArray();
        static bool IsTwoLine(Button chip, string topPercent, string bottomPercent)
        {
            var rows = QuotaRows(chip);
            var meters = Descendants<ProgressBar>(chip).ToArray();
            return rows.Length == 2 && rows[0].Text.Contains(topPercent) && rows[1].Text.Contains(bottomPercent)
                && meters.Length == 2 && $"{meters[0].Value:0}%" == topPercent && $"{meters[1].Value:0}%" == bottomPercent
                && Bounds(rows[0], chip).Bottom <= Bounds(rows[1], chip).Top + 1;
        }
        var initialTheme = _settings.Theme;
        var initialLanguage = _settings.Language;
        var codex = Demo(UsageProviderKind.Codex) with { Windows = [new("5시간", 27, DemoTime.AddHours(2)), new("주간", 39, DemoTime.AddDays(3))] };
        var claude = Demo(UsageProviderKind.Claude);
        try
        {
            _settings.ShowTaskbarWidget = true;
            _settings.Language = AppLanguage.Korean;
            _settings.Theme = AppTheme.Dark;
            _applySettings();
            _render(codex);
            _render(claude);
            await StableAsync(_window);
            var views = Widgets();
            Check("selected Codex, Claude and dashboard-hidden Grok each own one widget without taskbar buttons", views.Length == 3 &&
                views.Select(w => w.Tag).ToHashSet().SetEquals([UsageProviderKind.Codex, UsageProviderKind.Claude, UsageProviderKind.Grok]) &&
                !_window.ShowInTaskbar && !Application.Current.Windows.OfType<Window>().Any(w => w.Tag is UsageProviderKind && w.ShowInTaskbar));
            if (views.Length != 3) return;
            var codexView = views.Single(w => Equals(w.Tag, UsageProviderKind.Codex));
            var view = views.Single(w => Equals(w.Tag, UsageProviderKind.Claude));
            var grok = views.Single(w => Equals(w.Tag, UsageProviderKind.Grok));
            Check("widgets do not activate or register independent Explorer buttons", views.All(w => !w.ShowInTaskbar && !w.ShowActivated));
            var shown = views.Where(w => w.IsVisible).ToArray();
            var bounds = new List<Rect>();
            foreach (var widget in shown)
            {
                var handle = new WindowInteropHelper(widget).Handle;
                var style = GetWindowLongW(handle, -20 /* GWL_EXSTYLE */);
                Check($"{widget.Tag} widget has a visible non-activating native tool window without an app button",
                    IsWindowVisible(handle) && (style & 0x08000080) == 0x08000080 && (style & 0x00040000) == 0);
                if (GetWindowRect(handle, out var rect))
                    bounds.Add(new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
            }
            if (bounds.Count > 1)
            {
                Check("shown taskbar widgets occupy disjoint rectangles",
                    bounds.SelectMany((rect, i) => bounds.Skip(i + 1).Select(other => Rect.Intersect(rect, other))).All(r => r.IsEmpty || r.Width <= 1 || r.Height <= 1));
                Check("all visible widget HWNDs have equal pixel widths", bounds.Max(rect => rect.Width) - bounds.Min(rect => rect.Width) <= 1);
            }
            else _report.Add("INFO: taskbar offered room for fewer than two visible widgets; no overlap pair to measure.");
            var claudeChip = Tile(view);
            var darkText = (QuotaRows(claudeChip).FirstOrDefault()?.Foreground as SolidColorBrush)?.Color;
            Check("widget accessibility describes remaining Claude limits in Korean",
                AutomationProperties.GetName(claudeChip).Contains("Claude") && AutomationProperties.GetName(claudeChip).Contains("남음"));
            _settings.ClaudeDisplayMode = UsageDisplayMode.Used;
            _settings.Language = AppLanguage.English;
            _settings.Theme = AppTheme.Light;
            _applySettings();
            await StableAsync(view);
            claudeChip = Tile(view);
            Check("widget theme and language update immediately with independent display modes",
                AutomationProperties.GetName(claudeChip).Contains("Used", StringComparison.OrdinalIgnoreCase) &&
                QuotaRows(claudeChip).Any(t => t.Text.Contains("45%")) &&
                QuotaRows(Tile(codexView)).Any(t => t.Text.Contains("73%")) &&
                (SystemParameters.HighContrast || (QuotaRows(claudeChip).First().Foreground as SolidColorBrush)?.Color != darkText));
            _render(claude with { Status = UsageStatus.RateLimited });
            claudeChip = Tile(view);
            Check("failed widget lookup clears stale measurements", QuotaRows(claudeChip).Length == 0 &&
                Descendants<TextBlock>((DependencyObject)claudeChip.Content).Any(t => t.Text.Contains('—')));
            Click((Button)_window.FindName("HideButton"));
            Click(claudeChip);
            await StableAsync(_window);
            Check("clicking a widget service restores the dashboard without adding a taskbar button",
                _window.IsVisible && _window.WindowState == WindowState.Normal && !_window.ShowInTaskbar && Widgets().Length == 3);
            var oldClaude = new WindowInteropHelper(view).Handle;
            _settings.WidgetProviders &= ~TaskbarWidgetProviders.Claude;
            _applySettings();
            Check("removing one widget releases its window while keeping the dashboard and other widgets",
                !Widgets().Contains(view) && !IsWindow(oldClaude) && _settings.ShowClaude && Widgets().Contains(codexView) && Widgets().Contains(grok));
            _settings.WidgetProviders = TaskbarWidgetProviders.None;
            _applySettings();
            await StableAsync(_window);
            Check("no selected widgets leaves no widget window", Widgets().Length == 0);
            _settings.WidgetProviders = TaskbarWidgetProviders.Codex | TaskbarWidgetProviders.Claude;
            _applySettings();
            Check("restoring selection recreates exactly two widgets", Widgets().Length == 2);
            _settings.ShowTaskbarWidget = false;
            _applySettings();
            Check("combined taskbar mode removes the widget and restores the dashboard button",
                !Widgets().Any(w => w.IsVisible) && _window.ShowInTaskbar);

            // Capture the widget content at a reproducible two-line size even when the user's
            // Explorer taskbar has no free space or is configured to auto-hide.
            var previews = new[] { new TaskbarWidgetWindow(() => { }) { Tag = UsageProviderKind.Codex }, new TaskbarWidgetWindow(() => { }) { Tag = UsageProviderKind.Claude } };
            try
            {
                _settings.ClaudeDisplayMode = UsageDisplayMode.Remaining;
                foreach (var (theme, language, filename) in new[] {
                    (AppTheme.Dark, AppLanguage.Korean, "taskbar-widget-dark-ko.png"),
                    (AppTheme.Light, AppLanguage.English, "taskbar-widget-light-en.png") })
                {
                    _settings.Theme = theme;
                    _settings.Language = language;
                    _applySettings();
                    foreach (var preview in previews)
                    {
                        preview.Update([Equals(preview.Tag, UsageProviderKind.Codex) ? codex : claude], _settings);
                        preview.SetLayout(false, 200, 48);
                        if (!preview.IsVisible) preview.Show();
                        await StableAsync(preview);
                    }
                    Check(filename + " shows both quota periods on separate lines without clipping",
                        IsTwoLine(Tile(previews[0]), "73%", "61%") && IsTwoLine(Tile(previews[1]), "100%", "55%") &&
                        previews.All(w => TextFits(Root(w), Root(w))));
                    CaptureWidgetPreview(previews, filename);
                }
                var narrow = previews[1];
                narrow.Update([claude with { Windows = [.. claude.Windows, new("Opus 주간", 60, null)] }], _settings);
                narrow.SetLayout(true, 48, 80);
                await StableAsync(narrow);
                Check("narrow vertical widget keeps two readable values and exposes the extra limit with its unknown reset",
                    narrow.ActualWidth <= 48 && IsTwoLine(Tile(narrow), "100%", "55%") && TextFits(Root(narrow), Root(narrow)) &&
                    Descendants<TextBlock>(narrow).Any(t => t.Text == "+1" && t.IsVisible) &&
                    AutomationProperties.GetHelpText(Tile(narrow)).Contains("Opus") &&
                    AutomationProperties.GetHelpText(Tile(narrow)).Contains("40% · Reset N/A"));
            }
            finally { foreach (var preview in previews) preview.Close(); }
            await CheckWidgetAlignmentAsync();
        }
        finally
        {
            _settings.Theme = initialTheme;
            _settings.Language = initialLanguage;
            _settings.ClaudeDisplayMode = UsageDisplayMode.Remaining;
            _settings.ShowCodex = _settings.ShowClaude = true;
            _settings.WidgetProviders = TaskbarWidgetProviders.Codex | TaskbarWidgetProviders.Claude;
            _settings.ShowTaskbarWidget = false;
            _applySettings();
            _render(Demo(UsageProviderKind.Codex));
            _render(claude);
            if (!_window.IsVisible) _restoreWindow();
        }
    }

    private async Task CheckWidgetAlignmentAsync()
    {
        const string longName = "Gemini 3.1 Pro Preview extended quota";
        var samples = new[]
        {
            Demo(UsageProviderKind.Codex) with { Windows = [new("5시간", 27, null), new("주간", 39, null)] },
            Demo(UsageProviderKind.Claude) with { Windows = [new("월 추가 사용", 41, null)] },
            Demo(UsageProviderKind.Gemini) with { Windows = [new(longName, 17, null), new("Flash", 6, null), new("Shared", 20, null)] },
            Demo(UsageProviderKind.Grok) with { Status = UsageStatus.Unavailable }
        };
        var views = samples.Select(s => new TaskbarWidgetWindow(() => { }) { Tag = s.Provider }).ToArray();
        static bool EqualCoordinates(IEnumerable<double> coordinates)
        {
            var values = coordinates.ToArray();
            return values.Max() - values.Min() <= 1.25;
        }
        try
        {
            _settings.Theme = AppTheme.Dark;
            _settings.Language = AppLanguage.Korean;
            _applySettings();
            for (var i = 0; i < views.Length; i++)
            {
                views[i].Update([samples[i]], _settings);
                views[i].Show();
            }
            foreach (var (layout, vertical, width, height) in new[] {
                ("horizontal", false, 220d, 48d), ("narrow horizontal", false, 92d, 48d),
                ("vertical", true, 128d, 180d), ("narrow vertical", true, 48d, 80d) })
            {
                foreach (var view in views) view.SetLayout(vertical, width, height);
                await StableAsync(views[0]);
                foreach (var view in views) view.UpdateLayout();
                var roots = views.Select(Root).ToArray();
                Check(layout + " widget widths match across two-row, single-row, long-label and unavailable services",
                    EqualCoordinates(views.Select(w => w.ActualWidth)) && views.All(w => w.ActualWidth > 0 && w.ActualWidth <= width + 1));
                var logos = views.Select((w, i) => Bounds((FrameworkElement)w.FindName("Logo"), roots[i])).ToArray();
                Check(layout + " aligns provider logos even with an overflow marker",
                    EqualCoordinates(logos.Select(r => r.Left)) && EqualCoordinates(logos.Select(r => r.Width)) && EqualCoordinates(logos.Select(CenterY)));
                var labels = new List<Rect>();
                var meters = new List<Rect>();
                var percentages = new List<Rect>();
                var rightAligned = true;
                for (var i = 0; i < views.Length - 1; i++)
                {
                    var rows = Descendants<Grid>(roots[i]).Where(row => AutomationProperties.GetAutomationId(row).StartsWith("WidgetQuota", StringComparison.Ordinal));
                    foreach (var row in rows)
                    {
                        var value = row.Children.OfType<TextBlock>().Single(t => t.Text.EndsWith('%'));
                        rightAligned &= value.TextAlignment == TextAlignment.Right;
                        var name = row.Children.OfType<TextBlock>().Single(t => t != value);
                        labels.Add(Bounds(name, roots[i]));
                        meters.Add(Bounds(row.Children.OfType<ProgressBar>().Single(), roots[i]));
                        percentages.Add(Bounds(value, roots[i]));
                    }
                }
                Check(layout + " aligns label origins, meter tracks and percentage right edges across providers",
                    labels.Count == 5 && rightAligned && EqualCoordinates(labels.Select(r => r.Left)) &&
                    EqualCoordinates(meters.Select(r => r.Left)) && EqualCoordinates(meters.Select(r => r.Right)) &&
                    EqualCoordinates(percentages.Select(r => r.Right)) && meters.All(r => r.Width > 0));
                static bool Separate(Rect first, Rect second)
                {
                    var overlap = Rect.Intersect(first, second);
                    return overlap.IsEmpty || overlap.Width <= 1 || overlap.Height <= 1;
                }
                var overlapRows = Enumerable.Range(0, labels.Count).Where(i => !(Separate(labels[i], meters[i])
                        && Separate(meters[i], percentages[i]) && Separate(labels[i], percentages[i]))).ToArray();
                Check(layout + " keeps names, meter tracks and numbers in distinct spaces", overlapRows.Length == 0);
                foreach (var i in overlapRows)
                    _report.Add($"GEOMETRY: {layout} row {i} · label {labels[i]} · meter {meters[i]} · value {percentages[i]}");
                var gemini = (Button)views[2].FindName("ProviderButton");
                var grok = (Button)views[3].FindName("ProviderButton");
                var unavailableValue = Descendants<TextBlock>(grok).Single(t => t.Text == "—");
                Check(layout + " aligns the unavailable marker with measured values",
                    unavailableValue.TextAlignment == TextAlignment.Right &&
                    EqualCoordinates(percentages.Select(r => r.Right).Append(Bounds(unavailableValue, roots[3]).Right)));
                Check(layout + " vertically centers the single measured quota in the same frame",
                    Close(CenterY(labels[2]), (CenterY(labels[0]) + CenterY(labels[1])) / 2));
                Check(layout + " retains full quota names in tooltips and omits unmeasured percentages",
                    AutomationProperties.GetHelpText(gemini).Contains(longName) && Descendants<TextBlock>(gemini).Any(t => t.Text == "+1" && t.IsVisible) &&
                    !Descendants<TextBlock>(grok).Any(t => t.Text.Contains('%')) && roots.All(root => TextFits(root, root)));
                if (layout == "horizontal") CaptureWidgetPreview(views, "taskbar-widget-four-services-ko.png");
            }
        }
        finally { foreach (var view in views) view.Close(); }
    }

    private void CheckProviderTrayIcons(SettingsWindow settings, ComboBox claudeMode)
    {
        var app = (App)Application.Current;
        var option = (CheckBox)settings.FindName("ProviderTrayCheck");
        var claudeCheck = (CheckBox)settings.FindName("ClaudeCheck");
        static void Toggle(CheckBox check, bool selected)
        {
            check.IsChecked = selected;
            check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        bool SavedPreference() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!.ShowProviderTrayIcons;
        var enabledKinds = Enum.GetValues<UsageProviderKind>();
        Check("provider tray icons default to disabled", option.IsChecked == false && enabledKinds.All(kind => app.GetVisibleProviderTrayIcon(kind) is null));
        Toggle(option, true);
        var codex = app.GetVisibleProviderTrayIcon(UsageProviderKind.Codex);
        var claude = app.GetVisibleProviderTrayIcon(UsageProviderKind.Claude);
        Check("enabling provider icons persists and creates selected services with their current quotas", SavedPreference() &&
            codex is { Visible: true, Icon: not null } && codex.Text.Contains("61%") &&
            claude is { Visible: true, Icon: not null } && claude.Text.Contains("55%") &&
            codex.ContextMenuStrip != null && ReferenceEquals(codex.ContextMenuStrip, claude.ContextMenuStrip) &&
            app.GetVisibleProviderTrayIcon(UsageProviderKind.Gemini) == null && app.GetVisibleProviderTrayIcon(UsageProviderKind.Grok) == null);
        SelectTag(claudeMode, "Used");
        Check("provider tray icon follows its own used display mode",
            app.GetVisibleProviderTrayIcon(UsageProviderKind.Claude)?.Text.Contains("45%") == true &&
            app.GetVisibleProviderTrayIcon(UsageProviderKind.Codex)?.Text.Contains("61%") == true);
        _render(Demo(UsageProviderKind.Claude) with { Status = UsageStatus.RateLimited });
        var unavailable = app.GetVisibleProviderTrayIcon(UsageProviderKind.Claude);
        Check("provider tray icon clears the previous percentage when the lookup is unavailable",
            unavailable is { Visible: true, Icon: not null } && unavailable.Text.Contains("—") && !unavailable.Text.Contains("45%"));
        _render(Demo(UsageProviderKind.Claude));
        SelectTag(claudeMode, "Remaining");
        Toggle(claudeCheck, false);
        Check("hiding one provider removes only its tray icon", claude?.Visible == false &&
            app.GetVisibleProviderTrayIcon(UsageProviderKind.Claude) == null && app.GetVisibleProviderTrayIcon(UsageProviderKind.Codex)?.Visible == true);
        Toggle(claudeCheck, true);
        Check("reenabling a provider preserves its tray icon identity and current percentage",
            claude is { Visible: true } && ReferenceEquals(claude, app.GetVisibleProviderTrayIcon(UsageProviderKind.Claude)) && claude.Text.Contains("55%"));
        Toggle(option, false);
        Check("disabling provider icons persists and removes active shell entries", !SavedPreference() &&
            claude?.Visible == false && codex?.Visible == false &&
            enabledKinds.All(kind => app.GetVisibleProviderTrayIcon(kind) == null));
    }

    private async Task CheckSelectionsAsync()
    {
        _settings.ShowCodex=true;_settings.ShowClaude=false;_settings.ShowGemini=false;
        _window.ApplyProviderVisibility();_window.FitDesignSize();await StableAsync(_window);
        AssertMainLayout("Codex only",new[]{("Codex",1)});
        Capture(_window,"window-codex-only-dark.png");
        _settings.ShowClaude=true;_settings.ShowGemini=true;
        _window.ApplyProviderVisibility();_window.FitDesignSize();await StableAsync(_window);
        AssertMainLayout("all services",new[]{("Codex",1),("Claude",2),("Gemini",2)});
        Check("all services natural design height with three refresh-status rows",Close(_window.ActualWidth,360)&&Close(_window.ActualHeight,442));
        Capture(_window,"window-all-services-dark.png");
        _window.Width=220;_window.Height=140;await StableAsync(_window);
        AssertMainLayout("all services minimum readable size",new[]{("Codex",1),("Claude",2),("Gemini",2)});
        Check("all service rows retained with readable minimum height",_window.ActualHeight>=160);
        Capture(_window,"window-all-services-minimum-dark.png");
        _settings.ShowGrok=true;_window.ApplyProviderVisibility();await ResizeAsync(360,510);
        AssertMainLayout("four services including Grok",new[]{("Codex",1),("Claude",2),("Gemini",2),("Grok",1)});
        Check("Grok Build scope stays explicit without fabricated Bot row",Rows(Cards()[3]).Select(ValueText).SequenceEqual(new[]{"68%"})&&Descendants<TextBlock>(Cards()[3]).Any(t=>t.Text=="Build 주간"));
        Capture(_window,"window-four-services-grok-dark.png");
        Localizer.SetLanguage(AppLanguage.English);
        await StableAsync(_window);
        Capture(_window, "window-four-services-grok-en-dark.png");
        Localizer.SetLanguage(AppLanguage.Korean);
        _window.Width=220;_window.Height=140;await StableAsync(_window);
        AssertMainLayout("four services minimum readable size",new[]{("Codex",1),("Claude",2),("Gemini",2),("Grok",1)});
        Capture(_window,"window-four-services-grok-minimum-dark.png");
        _settings.ShowGrok=false;
        _settings.ShowGemini=false;_window.ApplyProviderVisibility();await ResizeAsync(360,267);
    }

    private async Task CheckDisplayModesAsync()
    {
        _settings.ClaudeDisplayMode=UsageDisplayMode.Used;
        _window.ApplyProviderVisibility();await StableAsync(_window);
        var cards=Cards();
        Check("display basis independently changes Claude percentages",Rows(cards[0]).Select(ValueText).SequenceEqual(new[]{"61%"})&&Rows(cards[1]).Select(ValueText).SequenceEqual(new[]{"0%","45%"}));
        Check("used display drives matching meter values",Rows(cards[1]).Select(Meter).Select(m=>m.Value).SequenceEqual(new[]{0d,45d}));
        Check("per-service basis labels remain visible",Descendants<TextBlock>(cards[0]).Any(t=>t.Text=="남음")&&Descendants<TextBlock>(cards[1]).Any(t=>t.Text=="사용"));
        _settings.ClaudeDisplayMode=UsageDisplayMode.Remaining;_window.ApplyProviderVisibility();await StableAsync(_window);
    }

    private async Task CheckUnavailableStatesAsync()
    {
        var cached=Demo(UsageProviderKind.Claude) with { Status=UsageStatus.RateLimited,Message="요청 제한 · 다음 확인까지 기다립니다.",RetryAfter=DateTimeOffset.Now.AddMinutes(2) };
        _render(cached);await ResizeAsync(274,174);
        AssertMainLayout("cached quota rows",new[]{("Codex",1),("Claude",2)});
        Check("cached values stay at full opacity",Descendants<TextBlock>(Cards()[1]).Where(t=>t.Text.EndsWith("%",StringComparison.Ordinal)).All(t=>Close(t.Opacity,1)&&Ancestors(t).OfType<UIElement>().All(parent=>Close(parent.Opacity,1))));
        Capture(_window,"window-cached-dark.png");
        _render(new UsageSnapshot(UsageProviderKind.Claude,UsageStatus.AuthenticationRequired,Demo(UsageProviderKind.Claude).Windows,DemoTime,Message:"로그인이 필요합니다."));
        await StableAsync(_window);
        AssertMainLayout("explicit Claude reauthentication state",new[]{("Codex",1),("Claude",0)});
        Check("Claude authentication never shows old percentages or empty meters",!Descendants<ProgressBar>(Cards()[1]).Any()&&!Descendants<TextBlock>(Cards()[1]).Any(text=>text.Text.EndsWith("%",StringComparison.Ordinal)));
        var invoked=false;
        void Login(UsageProviderKind kind) { if(kind==UsageProviderKind.Claude) invoked=true; }
        _window.LoginRequested+=Login;
        try
        {
            var connect=Descendants<Button>(_window).SingleOrDefault(button=>AutomationProperties.GetName(button)=="Claude 다시 로그인");
            Check("authentication recovery action exposed",connect!=null);
            if(connect!=null) Click(connect);
            Check("authentication recovery dispatches login event",invoked);
        }
        finally { _window.LoginRequested-=Login; }
        await ResizeAsync(220,140);
        Check("Claude reauthentication text fits minimum width",Descendants<Button>(Cards()[1]).Where(button=>button.Content is string).All(button=>button.ActualWidth>=new FormattedText((string)button.Content,CultureInfo.GetCultureInfo("ko-KR"),FlowDirection.LeftToRight,new Typeface(button.FontFamily,button.FontStyle,button.FontWeight,button.FontStretch),button.FontSize,Brushes.White,1).WidthIncludingTrailingWhitespace));
        Localizer.SetLanguage(AppLanguage.English);await StableAsync(_window);
        Check("Claude reauthentication action localizes immediately",Descendants<Button>(Cards()[1]).Any(button=>button.Content as string=="Sign in again"));
        Check("English Claude reauthentication text fits minimum width",TextFits(Cards()[1],Cards()[1]));
        Capture(_window,"window-login-required-en.png");
        Localizer.SetLanguage(AppLanguage.Korean);await StableAsync(_window);
        Capture(_window,"window-login-required-dark.png");
        _window.RenderSnapshot(new(UsageProviderKind.Claude,UsageStatus.AuthenticationRecovering,Demo(UsageProviderKind.Claude).Windows,DemoTime,WaitReason:UsageWaitReason.AuthenticationRecovery));
        await StableAsync(_window);
        Check("Claude pending recovery is distinct from required login",Descendants<Button>(Cards()[1]).Any(button=>button.Content as string=="자동 복구 확인 중…"&&!button.IsEnabled)&&!Descendants<ProgressBar>(Cards()[1]).Any());
        var retry=new UsageSnapshot(UsageProviderKind.Claude,UsageStatus.AuthenticationRetry,[],DemoTime,NextQueryAt:DemoTime.AddSeconds(30),WaitReason:UsageWaitReason.AuthenticationRecovery,AuthenticationWorkPending:true);
        _window.RenderSnapshot(retry);await StableAsync(_window);
        Check("Claude retry accessibility describes recovery rather than required login",Descendants<Button>(Cards()[1]).Any(button=>AutomationProperties.GetName(button)=="Claude 인증 다시 확인")&&!Descendants<TextBlock>(Cards()[1]).Any(text=>text.Text.Contains("재로그인 필요",StringComparison.Ordinal)));
        Localizer.SetLanguage(AppLanguage.English);await StableAsync(_window);
        Check("Claude retry and pending state localize at minimum width",TextFits(Cards()[1],Cards()[1])&&Descendants<Button>(Cards()[1]).Any(button=>button.Content as string=="Renewal pending · Check again"));
        Capture(_window,"window-auth-retry-en.png");
        Localizer.SetLanguage(AppLanguage.Korean);
        _render(Demo(UsageProviderKind.Claude));await StableAsync(_window);
    }
    private async Task CheckBusinessStatesAsync()
    {
        using var codex=JsonDocument.Parse("""{"plan_type":"business","rate_limit":null,"spend_control":{"individual_limit":{"remaining_percent":48,"reset_at":1793491200}}}""");
        using var claude=JsonDocument.Parse("""{"extra_usage":{"is_enabled":true,"utilization":60}}""");
        _render(UsageParser.Parse(UsageProviderKind.Codex,codex.RootElement,DemoTime));
        _render(UsageParser.Parse(UsageProviderKind.Claude,claude.RootElement,DemoTime,"enterprise"));
        await ResizeAsync(274,174);
        AssertMainLayout("business personal and monthly quota rows",new[]{("Codex",1),("Claude",1)});
        var cards=Cards();
        Check("business personal label and supplied percentage",Descendants<TextBlock>(cards[0]).Any(t=>t.Text=="개인")&&Rows(cards[0]).Select(ValueText).SequenceEqual(new[]{"48%"}));
        Check("enterprise monthly label and supplied percentage",Descendants<TextBlock>(cards[1]).Any(t=>t.Text=="월추가")&&Rows(cards[1]).Select(ValueText).SequenceEqual(new[]{"40%"}));
        Check("business and enterprise meters match remaining display",Close(Meter(Rows(cards[0])[0]).Value,48)&&Close(Meter(Rows(cards[1])[0]).Value,40));
        Capture(_window,"window-business-dark.png");
        using var credits=JsonDocument.Parse("""{"plan_type":"business","credits":{"has_credits":true}}""");
        _render(UsageParser.Parse(UsageProviderKind.Codex,credits.RootElement,DemoTime));
        await StableAsync(_window);
        AssertMainLayout("connected without supplied ratio",new[]{("Codex",0),("Claude",1)});
        Check("connected without ratio never invents a meter",!Descendants<ProgressBar>(Cards()[0]).Any()&&Descendants<TextBlock>(Cards()[0]).Any(t=>t.Text=="연결됨 · 비율 미제공"));
        Capture(_window,"window-connected-no-ratio-dark.png");
        _render(Demo(UsageProviderKind.Codex));_render(Demo(UsageProviderKind.Claude));
        await ResizeAsync(360,267);
    }
    private async Task CheckRefreshStatusAsync()
    {
        var ready=Demo(UsageProviderKind.Claude) with { ObservedAt=DemoTime,NextAutomaticAt=DemoTime.AddMinutes(2) };
        _window.RenderSnapshot(ready);await ResizeAsync(220,140);
        Check("refresh status separates observation from next automatic query",Descendants<TextBlock>(Cards()[1]).Any(text=>text.Text.Contains("확인 ",StringComparison.Ordinal)&&text.Text.Contains("다음 자동",StringComparison.Ordinal)));
        Check("refresh status minimum width stays inside card",TextFits(Cards()[1],Cards()[1]));
        Capture(_window,"window-refresh-schedule-ko.png");
        var limited=ready with { Status=UsageStatus.RateLimited,CheckedAt=DemoTime.AddMinutes(1),NextQueryAt=DemoTime.AddMinutes(5),NextAutomaticAt=DemoTime.AddMinutes(6),WaitReason=UsageWaitReason.RateLimited,IsCached=true };
        _window.RenderSnapshot(limited);await StableAsync(_window);
        Check("previous values and the protection reason are visible on the compact card",Descendants<TextBlock>(Cards()[1]).Any(text=>text.Text.StartsWith("이전 값 · 요청 제한 대기",StringComparison.Ordinal)));
        var details=UsageRefreshText.Details(limited);
        Check("protection details preserve observation, attempt and eligibility separately",details.Contains("12:00:00",StringComparison.Ordinal)&&details.Contains("12:01:00",StringComparison.Ordinal)&&details.Contains("12:05:00",StringComparison.Ordinal)&&details.Contains("12:06:00",StringComparison.Ordinal));
        Localizer.SetLanguage(AppLanguage.English);await StableAsync(_window);
        Check("previous values and the protection reason translate immediately",Descendants<TextBlock>(Cards()[1]).Any(text=>text.Text.StartsWith("Previous · Rate-limit wait",StringComparison.Ordinal)));
        Check("English refresh wait text stays inside minimum card",TextFits(Cards()[1],Cards()[1]));
        Capture(_window,"window-refresh-wait-en.png");
        Localizer.SetLanguage(AppLanguage.Korean);
        var requested=0;void Requested()=>requested++;
        _window.RefreshRequested+=Requested;
        try
        {
            _window.SetRefreshing(true);var button=(Button)_window.FindName("RefreshButton");
            Check("refresh button accepts a request while lookup is running",button.IsEnabled);
            Click(button);Check("running refresh button dispatches request",requested==1);
        }
        finally { _window.RefreshRequested-=Requested;_window.SetRefreshing(false); }
        foreach(var kind in Enum.GetValues<UsageProviderKind>()) _render(Demo(kind));
        await StableAsync(_window);
    }

    private void AssertMainLayout(string scenario,(string Provider,int Rows)[] expected)
    {
        var cards=Cards();
        Check(scenario+" selected provider card count",cards.Length==expected.Length);
        Check(scenario+" no scroll viewers",!Descendants<ScrollViewer>(_window).Any());
        Check(scenario+" no C/A/G initial badges",!Descendants<TextBlock>(_window).Any(t=>t.Text is "C" or "A" or "G"));
        Check(scenario+" all card bounds inside client",cards.All(card=>Inside(Bounds(card,Root(_window)),new Rect(new Size(Root(_window).ActualWidth,Root(_window).ActualHeight)))));
        Check(scenario+" header text remains inside client",TextFits((FrameworkElement)_window.FindName("TitleText"),Root(_window)));
        for(var i=0;i<Math.Min(cards.Length,expected.Length);i++)
        {
            var card=cards[i];var rows=Rows(card);var name=expected[i].Provider;
            Check(scenario+" "+name+" heading and row count",Descendants<TextBlock>(card).Any(t=>t.Text==name)&&rows.Length==expected[i].Rows);
            Check(scenario+" "+name+" quota rows are vertical",rows.Zip(rows.Skip(1),(first,second)=>Bounds(first,card).Bottom<=Bounds(second,card).Top+1.25).All(passed=>passed));
            Check(scenario+" "+name+" content bounds inside card",TextFits(card,card)&&Descendants<ProgressBar>(card).All(meter=>Inside(Bounds(meter,card),new Rect(new Size(card.ActualWidth,card.ActualHeight)))));
            Check(scenario+" "+name+" label-meter-value inline",rows.All(row=>
            {
                var label=row.Children.OfType<TextBlock>().SingleOrDefault(text=>Grid.GetColumn(text)==0);
                var meter=row.Children.OfType<ProgressBar>().SingleOrDefault();
                var value=row.Children.OfType<TextBlock>().SingleOrDefault(text=>Grid.GetColumn(text)==2);
                if(label==null||meter==null||value==null) return false;
                var left=Bounds(label,card);var middle=Bounds(meter,card);var right=Bounds(value,card);
                return middle.Width>0&&middle.Height>0&&left.Right<=middle.Left+1.25&&middle.Right<=right.Left+1.25&&
                    Math.Abs(CenterY(left)-CenterY(middle))<=1.5&&Math.Abs(CenterY(right)-CenterY(middle))<=1.5;
            }));
        }
    }
    private Border[] Cards()=>((Grid)_window.FindName("ProviderGrid")).Children.OfType<Border>().ToArray();
    private static Grid[] Rows(Border card)=>card.Child is Panel panel?panel.Children.OfType<Grid>().ToArray():Array.Empty<Grid>();
    private static string ValueText(Grid row)=>row.Children.OfType<TextBlock>().Single(text=>Grid.GetColumn(text)==2).Text;
    private static ProgressBar Meter(Grid row)=>row.Children.OfType<ProgressBar>().Single();
    private static double CenterY(Rect rect)=>rect.Top+rect.Height/2;
    private static bool Close(double left,double right)=>Math.Abs(left-right)<=1.25;
    private static bool Inside(Rect inner,Rect outer)=>inner.Left>=outer.Left-1.25&&inner.Top>=outer.Top-1.25&&inner.Right<=outer.Right+1.25&&inner.Bottom<=outer.Bottom+1.25;
    private static Rect Bounds(FrameworkElement element,Visual root)=>element.TransformToAncestor(root).TransformBounds(new Rect(new Size(element.ActualWidth,element.ActualHeight)));
    private static bool TextFits(FrameworkElement subtree,FrameworkElement root)
    {
        var boundary=new Rect(new Size(root.ActualWidth,root.ActualHeight));
        var texts=Descendants<TextBlock>(subtree).Where(text=>text.IsVisible&&text.ActualWidth>0&&text.ActualHeight>0);
        return texts.All(text=>Inside(Bounds(text,root),boundary));
    }
    private static FrameworkElement Root(Window window)=>window.FindName("WindowFrame") as FrameworkElement??(FrameworkElement)window.Content;
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T match) yield return match;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in Descendants<T>(VisualTreeHelper.GetChild(root,i))) yield return child;
    }
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for(var parent=VisualTreeHelper.GetParent(element);parent!=null;parent=VisualTreeHelper.GetParent(parent)) yield return parent;
    }
    private void Check(string name,bool passed)
    {
        _report.Add($"{(passed?"PASS":"FAIL")}: {name}");
        if(!passed) _failures++;
    }
    private static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void SelectTag(ComboBox combo,string tag)=>combo.SelectedItem=combo.Items.OfType<ComboBoxItem>().Single(item=>item.Tag?.ToString()==tag);
    private void SetTheme(AppTheme theme) { _settings.Theme=theme;_applySettings(); }
    private async Task ResizeAsync(double width,double height)
    {
        _window.WindowState=WindowState.Normal;
        _window.Width=width;_window.Height=height;
        await StableAsync(_window);
    }
    private static async Task StableAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.Loaded);
        await Dispatcher.Yield(DispatcherPriority.Render);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(70);
        window.UpdateLayout();
    }
    private void CaptureWidgetPreview(IEnumerable<Window> widgets, string filename)
    {
        // Compose the real WPF client surfaces against a neutral taskbar-colored
        // background. No other application names or pixels enter the preview.
        var roots = widgets.Select(Root).ToArray();
        const double padding = 6, gap = 4, scale = 2;
        var width = (int)Math.Ceiling(roots.Sum(w => w.ActualWidth) + 2 * padding + gap * (roots.Length - 1));
        var height = (int)Math.Ceiling(roots.Max(w => w.ActualHeight) + 2 * padding);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle((Brush)Application.Current.Resources["WindowBackground"], null, new Rect(0, 0, width, height));
            var x = padding;
            foreach (var root in roots)
            {
                context.DrawRectangle(new VisualBrush(root), null, new Rect(x, (height - root.ActualHeight) / 2, root.ActualWidth, root.ActualHeight));
                x += root.ActualWidth + gap;
            }
        }
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(_output, filename));
        encoder.Save(file);
        _report.Add($"CAPTURE: {filename} · actual widget controls with synthetic quota data · {bitmap.PixelWidth}×{bitmap.PixelHeight} px");
    }

    private void Capture(Window window,string filename)
    {
        window.UpdateLayout();
        var root=Root(window);
        var dpi=VisualTreeHelper.GetDpi(root);
        var width=Math.Max(1,(int)Math.Ceiling(root.ActualWidth*dpi.DpiScaleX));
        var height=Math.Max(1,(int)Math.Ceiling(root.ActualHeight*dpi.DpiScaleY));
        var drawing=new DrawingVisual();
        using(var context=drawing.RenderOpen())
        {
            // Window.Background belongs to the native window's client surface, outside
            // the captured content root. Paint that surface before its visual tree.
            var clientBounds=new Rect(new Size(root.ActualWidth,root.ActualHeight));
            var radius=root is Border frame?frame.CornerRadius.TopLeft:0;
            context.DrawRoundedRectangle(window.Background,null,clientBounds,radius,radius);
            var brush=new VisualBrush(root) { Stretch=Stretch.Fill,AlignmentX=AlignmentX.Left,AlignmentY=AlignmentY.Top };
            context.DrawRectangle(brush,null,clientBounds);
        }
        var bitmap=new RenderTargetBitmap(width,height,dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var file=File.Create(Path.Combine(_output,filename))) encoder.Save(file);
        _report.Add(string.Format(CultureInfo.InvariantCulture,"CAPTURE: {0} · logical {1:0.##}×{2:0.##} · pixels {3}×{4} · DPI {5:0.##}×{6:0.##}",filename,root.ActualWidth,root.ActualHeight,width,height,dpi.PixelsPerInchX,dpi.PixelsPerInchY));
        Check(filename+" captures full client at actual DPI",bitmap.PixelWidth>=root.ActualWidth*dpi.DpiScaleX&&bitmap.PixelHeight>=root.ActualHeight*dpi.DpiScaleY);
    }
}
