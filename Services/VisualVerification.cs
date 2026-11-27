using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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
        bool scrollOnly = Environment.GetCommandLineArgs().Contains("--settings-scroll-only");
        bool layoutOnly = Environment.GetCommandLineArgs().Contains("--settings-layout-only");
        bool settingsOnly = scrollOnly || layoutOnly || Environment.GetCommandLineArgs().Contains("--settings-only");
        bool focused = settingsOnly || Environment.GetCommandLineArgs().Contains("--taskbar-cli-only");
        if (focused) _report.Add("SCOPE: related settings/CLI controls and taskbar/native-placement verification only. The earlier full report preserves unrelated successful checks.");
        try
        {
            _settings.Language=AppLanguage.Korean;
            Localizer.SetLanguage(AppLanguage.Korean);
            _settings.RefreshIntervalSeconds=0;
            _settings.ShowCodex=true;_settings.ShowClaude=true;_settings.ShowGemini=false;
            _settings.CodexDisplayMode=UsageDisplayMode.Remaining;
            _settings.ClaudeDisplayMode=UsageDisplayMode.Remaining;
            _settings.GeminiDisplayMode=UsageDisplayMode.Remaining;
            _settings.Theme=AppTheme.Dark;_settings.AlwaysOnTop=false;_settings.WidgetProviders=TaskbarWidgetProviders.None;
            _applySettings();
            foreach(var kind in Enum.GetValues<UsageProviderKind>()) _render(Demo(kind));
            _window.Show();
            await StableAsync(_window);
            Check("shown HWND and presentation source",_window.IsVisible&&_window.IsLoaded&&new WindowInteropHelper(_window).Handle!=IntPtr.Zero&&PresentationSource.FromVisual(_window)!=null);
            Check("manual refresh diagnostics isolation",_settings.RefreshIntervalSeconds==0);

            if (!focused)
            {
            await CaptureScenario("standard dark",360,267,"window-standard-dark.png");
            Check("controlled demonstration percentages",Rows(Cards()[0]).Select(ValueText).SequenceEqual(new[]{"61%"})&&Rows(Cards()[1]).Select(ValueText).SequenceEqual(new[]{"100%","55%"}));
            await CaptureScenario("mini dark",274,174,"window-mini-dark.png");
            await CaptureScenario("minimum dark",220,140,"window-minimum-dark.png");
            await CheckResetCountdownAsync();
            await CaptureScenario("wide dark",600,300,"window-wide-dark.png");

            await CheckWindowActionsAsync();
            await CheckRefreshStatusAsync();
            }
            await ResizeAsync(360,267);
            var cli = new StubCliMaintenance();
            _window.CliMaintenance = cli;
            var beforeSettingsSize=new Size(_window.ActualWidth,_window.ActualHeight);
            Click((Button)_window.FindName("SettingsButton"));
            await StableAsync(_window);
            var settings=Application.Current.Windows.OfType<SettingsWindow>().SingleOrDefault(w=>w.IsVisible);
            Check("settings opens a separate owned window",settings!=null&&settings.Owner==_window);
            Check("settings preserves main window size",Close(_window.ActualWidth,beforeSettingsSize.Width)&&Close(_window.ActualHeight,beforeSettingsSize.Height));
            if(settings!=null)
            {
                await StableAsync(settings);
                if (layoutOnly)
                {
                    await PrimeCompletedCliStatesAsync(settings);
                    await CheckSettingsTabsLayoutAsync(settings);
                }
                else if (!scrollOnly)
                {
                await CheckSettingsTabsLayoutAsync(settings);
                Check("settings has all service selection and mode controls",new[]{"CodexCheck","ClaudeCheck","GeminiCheck","GrokCheck","CodexModeCombo","ClaudeModeCombo","GeminiModeCombo","GrokModeCombo","ThemeCombo","IntervalCombo"}.All(name=>settings.FindName(name)!=null));
                Capture(settings,"settings-dark.png");
                var language = (ComboBox)settings.FindName("LanguageCombo");
                SelectTag(language, "English");
                await StableAsync(settings);
                Capture(settings, "settings-en-dark.png");
                SelectTag(language, "Korean");
                await StableAsync(settings);
                await CheckSettingsInteractionsAsync(settings);
                await CheckCliMaintenanceAsync(settings, cli);
                }
                await CheckSettingsConstraintsAsync(settings, cli);
                settings.Close();
                await StableAsync(_window);
                if (!settingsOnly)
                {
                    await CheckTaskbarWidgetAsync();
                    await CheckNativeOrderRecoveryAsync();
                }
            }

            if (focused) return _failures;

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
            labels[0].Text == "리셋 0:01" && labels[1].Text == "리셋 미제공" &&
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
        Check("dashboard opens without an Explorer button", !_window.ShowInTaskbar && _window.TaskbarItemInfo == null);
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

    private async Task CheckSettingsTabsLayoutAsync(SettingsWindow settings)
    {
        var tabs = (TabControl)settings.FindName("SettingsTabs");
        var language = (ComboBox)settings.FindName("LanguageCombo");
        var theme = (ComboBox)settings.FindName("ThemeCombo");
        Check("settings uses two native tabs without removing named settings controls", tabs.Items.Count == 2 &&
            tabs.Items.OfType<TabItem>().All(tab => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(tab))));
        foreach (var palette in new[] { "Dark", "Light" })
        foreach (var locale in new[] { "Korean", "English" })
        {
            tabs.SelectedIndex = 0; await StableAsync(settings);
            SelectTag(language, locale); SelectTag(theme, palette); await StableAsync(settings);
            foreach (int index in new[] { 0, 1 })
            {
                tabs.SelectedIndex = index; await StableAsync(settings);
                var scroll = (ScrollViewer)settings.FindName(index == 0 ? "SettingsScroll" : "AccountSettingsScroll");
                Check($"{palette} {locale} settings tab {index} fits without scrolling at its default size",
                    scroll.ScrollableHeight <= 0.5 && scroll.ComputedVerticalScrollBarVisibility == Visibility.Collapsed &&
                    scroll.ScrollableWidth <= 0.5 && TextFits(Root(settings), Root(settings)));
                Capture(settings, $"settings-{(index == 0 ? "general" : "accounts")}-{palette.ToLowerInvariant()}-{(locale == "Korean" ? "ko" : "en")}.png");
            }
            var background = (Brush)Application.Current.Resources["WindowBackground"];
            Check($"{palette} settings tab labels and focus use contrast above their required limits",
                ContrastRatio((Brush)Application.Current.Resources["PrimaryText"], background) >= 4.5 &&
                ContrastRatio((Brush)Application.Current.Resources["SecondaryText"], background) >= 4.5 &&
                ContrastRatio((Brush)Application.Current.Resources["MutedText"], background) >= 3);
        }
        var nativeHeader = (TabItem)tabs.Items[0];
        var peer = new TabItemAutomationPeer(nativeHeader, new TabControlAutomationPeer(tabs));
        var selection = (ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem);
        selection.Select(); await StableAsync(settings);
        Check("native tab selection exposes the accessibility selection pattern", peer.GetAutomationControlType() == AutomationControlType.TabItem && tabs.SelectedIndex == 0);
        bool focused = nativeHeader.Focus();
        if (focused)
        {
            nativeHeader.UpdateLayout();
            var focusFrame = (Border)nativeHeader.Template.FindName("TabFrame", nativeHeader);
            Check("focused tab displays an explicit high contrast outline", nativeHeader.IsKeyboardFocused &&
                (focusFrame.BorderBrush as SolidColorBrush)?.Color == ((SolidColorBrush)Application.Current.Resources["PrimaryText"]).Color);
        }
        else _report.Add("KEYBOARD: native TabControl/TabItem navigation is retained; this desktop session did not grant actual header keyboard focus. Physical Ctrl+Tab and arrow input were not synthesized.");
        tabs.SelectedIndex = 0;
        SelectTag(language, "Korean"); SelectTag(theme, "Dark"); await StableAsync(settings);
    }

    private async Task CheckSettingsConstraintsAsync(SettingsWindow settings, StubCliMaintenance cli)
    {
        var tabs = (TabControl)settings.FindName("SettingsTabs");
        double normalHeight = settings.Height;
        settings.Height = settings.MinHeight;
        foreach (int index in new[] { 0, 1 })
        {
            tabs.SelectedIndex = index; await StableAsync(settings);
            var scroll = (ScrollViewer)settings.FindName(index == 0 ? "SettingsScroll" : "AccountSettingsScroll");
            Check($"minimum settings tab {index} has vertical fallback without horizontal scrolling",
                scroll.ScrollableHeight > 0 && scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible && scroll.ScrollableWidth <= 0.5);
            var bar = Descendants<ScrollBar>(scroll).Single(item => item.Orientation == Orientation.Vertical);
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            var grip = (Border)track.Thumb.Template.FindName("Grip", track.Thumb);
            _report.Add($"SCROLLBAR VALUES: tab={index}, localWidth={bar.ReadLocalValue(FrameworkElement.WidthProperty)}, width={bar.ActualWidth}, minWidth={bar.MinWidth}, maxWidth={bar.MaxWidth}, desired={bar.DesiredSize.Width}, thumb={track.Thumb.ActualWidth}, grip={grip.ActualWidth}, peer={new ScrollBarAutomationPeer(bar).GetAutomationControlType()}, pageUp={track.DecreaseRepeatButton.Command}, pageDown={track.IncreaseRepeatButton.Command}");
            Check($"minimum settings tab {index} keeps a native 14px scrollbar hit area around its 6px thumb",
                Close(bar.ActualWidth, 14) && Close(track.Thumb.ActualWidth, 14) && Close(grip.ActualWidth, 6) &&
                new ScrollBarAutomationPeer(bar).GetAutomationControlType() == AutomationControlType.ScrollBar &&
                track.DecreaseRepeatButton.Command == ScrollBar.PageUpCommand && track.IncreaseRepeatButton.Command == ScrollBar.PageDownCommand);
            scroll.ScrollToTop(); await StableAsync(settings);
            ScrollBar.PageDownCommand.Execute(null, bar); await StableAsync(settings);
            Check($"minimum settings tab {index} responds to native page-down scrolling", scroll.VerticalOffset > 0);
            scroll.ScrollToTop(); await StableAsync(settings);
            track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
            track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 20));
            track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 20, false));
            await StableAsync(settings);
            Check($"minimum settings tab {index} responds to native thumb drag events", scroll.VerticalOffset > 0);
            scroll.ScrollToBottom(); await StableAsync(settings);
            var last = (FrameworkElement)settings.FindName(index == 0 ? "NotifyCheck" : "AntigravityCliStatus");
            var footer = Descendants<TextBlock>(settings).Single(text => text.Text.StartsWith("자동 저장"));
            Check($"minimum settings tab {index} can reveal its last setting above the footer",
                Bounds(last, Root(settings)).Bottom <= Bounds(footer, Root(settings)).Top + 1 && last.ActualHeight > 0);
            Capture(settings, $"settings-minimum-{(index == 0 ? "general" : "accounts")}-dark-ko.png");
        }
        settings.Height = normalHeight; tabs.SelectedIndex = 1; await StableAsync(settings);
        cli.FailureMessage = string.Join(" ", Enumerable.Repeat("CLI 작업이 실패했습니다. 네트워크와 기존 CLI 설치 방식을 확인한 뒤 다시 시도해 주세요.", 8));
        cli.FailNext = true;
        Click((Button)settings.FindName("ClaudeCliButton")); await StableAsync(settings);
        var accountScroll = (ScrollViewer)settings.FindName("AccountSettingsScroll");
        Check("a long CLI error remains readable through overflow fallback without shrinking controls", accountScroll.ScrollableHeight > 0 &&
            ((Button)settings.FindName("ClaudeCliButton")).FontSize == 12 && accountScroll.ScrollableWidth <= 0.5);
        accountScroll.ScrollToBottom(); await StableAsync(settings);
        Capture(settings, "settings-long-error-dark-ko.png");
        cli.FailureMessage = null;
        Click((Button)settings.FindName("ClaudeCliButton")); await StableAsync(settings);
        Check("successful retry restores a scroll-free account page", accountScroll.ScrollableHeight <= 0.5);
        _report.Add("SETTINGS: default General/Accounts pages are checked in both locales and themes; only constrained height or long errors use the themed native fallback. Native TabControl retains built-in Ctrl+Tab/arrow behavior.");
    }

    private static double ContrastRatio(Brush foreground, Brush background)
    {
        static double Linear(byte value) { double s = value / 255d; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        static double Luminance(Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        double first = Luminance(((SolidColorBrush)foreground).Color), second = Luminance(((SolidColorBrush)background).Color);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private async Task PrimeCompletedCliStatesAsync(SettingsWindow settings)
    {
        ((TabControl)settings.FindName("SettingsTabs")).SelectedIndex = 1; await StableAsync(settings);
        foreach (var name in new[] { "CodexCliButton", "ClaudeCliButton", "AntigravityCliButton" })
        {
            Click((Button)settings.FindName(name)); await StableAsync(settings);
        }
        Check("all three normal CLI completion rows retain their result and installed version",
            new[] { "CodexCliStatus", "ClaudeCliStatus", "AntigravityCliStatus" }.All(name =>
                ((TextBlock)settings.FindName(name)).Text.Contains('\n') && ((TextBlock)settings.FindName(name)).Text.Contains("2.1.323")));
    }

    private async Task CheckSettingsInteractionsAsync(SettingsWindow settings)
    {
        var tabs = (TabControl)settings.FindName("SettingsTabs");
        tabs.SelectedIndex = 1; await StableAsync(settings);
        var account = (ComboBox)settings.FindName("GeminiProfileCombo");
        SelectTag(account, "CodeAssist2");
        Check("Gemini profile selection persists across settings windows", _settings.GeminiProfile == GeminiProfile.CodeAssist2 &&
            JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))?.GeminiProfile == GeminiProfile.CodeAssist2);
        SelectTag(account, "Default");
        tabs.SelectedIndex = 0; await StableAsync(settings);
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
        var interval=(ComboBox)settings.FindName("IntervalCombo");
        SelectTag(interval,"0");
        Check("settings manual interval retained",_settings.RefreshIntervalSeconds==0);
        foreach (var kind in new[]{UsageProviderKind.Codex, UsageProviderKind.Claude, UsageProviderKind.Grok})
        {
            var toggle = (CheckBox)settings.FindName(kind + "Widget");
            toggle.IsChecked = true;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        Check("service row widget toggle persists without enabling its dashboard card",
            !_settings.ShowGrok && !_window.ShowInTaskbar && JsonSerializer.Deserialize<AppSettings>(
                JsonSerializer.Serialize(_settings))!.IsWidgetSelected(UsageProviderKind.Grok));
    }

    private async Task CheckCliMaintenanceAsync(SettingsWindow settings, StubCliMaintenance cli)
    {
        var tabs = (TabControl)settings.FindName("SettingsTabs");
        tabs.SelectedIndex = 1; await StableAsync(settings);
        var scroll = (ScrollViewer)settings.FindName("AccountSettingsScroll");
        scroll.ScrollToBottom(); await StableAsync(settings);
        var codex = (Button)settings.FindName("CodexCliButton");
        var claude = (Button)settings.FindName("ClaudeCliButton");
        var agy = (Button)settings.FindName("AntigravityCliButton");
        var codexStatus = (TextBlock)settings.FindName("CodexCliStatus");
        var claudeStatus = (TextBlock)settings.FindName("ClaudeCliStatus");
        int logins = 0; settings.LoginRequested += _ => logins++;
        Check("CLI management distinguishes missing Codex from installed Claude and Antigravity",
            codex.Content as string == "설치" && claude.Content as string == "업데이트" && agy.Content as string == "업데이트");
        Check("CLI buttons retain native keyboard focus and accessible names", new[] { codex, claude, agy }.All(button =>
            button.Focusable && button.FocusVisualStyle != null && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))));
        Click(codex); await StableAsync(settings);
        Check("one Codex install click completes and changes its next action to update",
            cli.Calls.SequenceEqual([(UsageProviderKind.Codex, false)]) && codex.Content as string == "업데이트" && codexStatus.Text.Contains("설치 완료"));
        Click(agy); await StableAsync(settings);
        Check("one Antigravity update click uses the update operation", cli.Calls.Last() == (UsageProviderKind.Gemini, true));
        cli.FailNext = true;
        Click(claude); await StableAsync(settings);
        Check("failed CLI maintenance remains visible and can be retried", claude.IsEnabled && claudeStatus.Text.Contains("실패"));
        Click(claude); await StableAsync(settings);
        Check("retrying CLI maintenance updates the installed version", claudeStatus.Text.Contains("업데이트 명령 완료"));
        cli.Hold = true;
        int before = cli.Calls.Count;
        Click(claude); await StableAsync(settings);
        Check("running CLI maintenance exposes a cancel action and progress", claude.Content as string == "취소" && claudeStatus.Text.Contains("업데이트"));
        tabs.SelectedIndex = 0; await StableAsync(settings);
        tabs.SelectedIndex = 1; await StableAsync(settings);
        Check("switching settings tabs retains the same running CLI operation and cancel action",
            cli.Calls.Count == before + 1 && claude.Content as string == "취소" && claudeStatus.Text.Contains("업데이트"));
        Click(claude);
        await Task.Delay(100); await StableAsync(settings);
        Check("a second click cancels the existing operation instead of starting a duplicate",
            cli.Calls.Count == before + 1 && claude.IsEnabled && claude.Content as string == "업데이트" && claudeStatus.Text.Contains("취소"));
        cli.Hold = false;
        Check("install and update never trigger account sign-in", logins == 0);
        scroll.ScrollToBottom(); await StableAsync(settings);
        Capture(settings, "settings-cli-dark-ko.png");
        var language = (ComboBox)settings.FindName("LanguageCombo");
        tabs.SelectedIndex = 0; await StableAsync(settings);
        SelectTag(language, "English"); await StableAsync(settings);
        tabs.SelectedIndex = 1; await StableAsync(settings);
        scroll.ScrollToBottom(); await StableAsync(settings);
        Check("CLI controls and status localize to English", codex.Content as string == "Update" && codexStatus.Text.Contains("Installed"));
        Capture(settings, "settings-cli-dark-en.png");
        tabs.SelectedIndex = 0; await StableAsync(settings);
        SelectTag(language, "Korean");
        var previousTheme = _settings.Theme;
        SelectTag((ComboBox)settings.FindName("ThemeCombo"), "Light"); await StableAsync(settings);
        tabs.SelectedIndex = 1; await StableAsync(settings); scroll.ScrollToBottom();
        Capture(settings, "settings-cli-light-ko.png");
        var content = (FrameworkElement)scroll.Content;
        Check("CLI content fits its scroll container in light mode", TextFits(content, content) && scroll.ExtentWidth <= scroll.ViewportWidth + 1);
        tabs.SelectedIndex = 0; await StableAsync(settings);
        SelectTag((ComboBox)settings.FindName("ThemeCombo"), previousTheme.ToString()); await StableAsync(settings);
        tabs.SelectedIndex = 1; await StableAsync(settings);

        var closingCli = new StubCliMaintenance { Hold = true };
        var closing = new SettingsWindow(new AppSettings(), closingCli) { Owner = settings };
        closing.Show(); await StableAsync(closing);
        ((TabControl)closing.FindName("SettingsTabs")).SelectedIndex = 1; await StableAsync(closing);
        Click((Button)closing.FindName("AntigravityCliButton"));
        closing.Close(); await Task.Delay(100);
        Check("closing settings cancels its CLI work without opening sign-in", closingCli.Cancelled == 1);
        _report.Add("CLI: all install/update/cancel/failure clicks used an in-memory maintenance stub. No CLI installer, updater, login, account request or settings-file write was executed.");
    }

    private sealed class StubCliMaintenance : ICliMaintenance
    {
        private readonly Dictionary<UsageProviderKind, CliInstallation> _installed = new()
        {
            [UsageProviderKind.Codex] = new(null, null),
            [UsageProviderKind.Claude] = new(@"C:\synthetic\claude.exe", "2.1.323"),
            [UsageProviderKind.Gemini] = new(@"C:\synthetic\agy.exe", "1.4.7")
        };
        internal readonly List<(UsageProviderKind Kind, bool Update)> Calls = new();
        internal bool FailNext, Hold;
        internal string? FailureMessage;
        internal int Cancelled;
        public Task<CliInstallation> InspectAsync(UsageProviderKind kind, CancellationToken token) => Task.FromResult(_installed[kind]);
        public async Task<CliInstallResult> InstallAsync(UsageProviderKind kind, bool update, CancellationToken token, IProgress<string> progress)
        {
            Calls.Add((kind, update));
            progress.Report(update ? "기존 CLI를 업데이트하고 있습니다." : "공식 CLI를 설치하고 있습니다.");
            if (Hold)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { Cancelled++; throw; }
            }
            if (FailNext) { FailNext = false; return new(false, FailureMessage ?? "CLI 작업이 실패했습니다. 네트워크와 기존 CLI 설치 방식을 확인한 뒤 다시 시도해 주세요."); }
            _installed[kind] = new(@"C:\synthetic\" + OfficialCliInstaller.Command(kind) + ".exe", "2.1.323");
            return new(true, (update ? "업데이트 명령 완료 · " : "설치 완료 · ") + "2.1.323");
        }
    }

    private async Task CheckNativeOrderRecoveryAsync()
    {
        var owner = new Window { Width = 180, Height = 80, ShowInTaskbar = false, ShowActivated = false, Topmost = true };
        var widget = new TaskbarWidgetWindow(() => { }) { ShowInTaskbar = false, ShowActivated = false, Topmost = true };
        var ownerHandle = new WindowInteropHelper(owner).EnsureHandle();
        var handle = new WindowInteropHelper(widget).EnsureHandle();
        try
        {
            widget.Update([Demo(UsageProviderKind.Claude)], _settings);
            var target = new System.Drawing.Rectangle(20, 20, 160, 46);
            TaskbarWidgetHost.Move(widget, target, ownerHandle);
            await StableAsync(widget);
            Check("synthetic placement starts with the same initialized native handle as production", handle != IntPtr.Zero && IsWindow(handle));
            var foreground = GetForegroundWindow();
            SetWindowPos(handle, new IntPtr(-2) /* HWND_NOTOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            Check("synthetic native demotion preserves WPF Topmost while dropping the native band", widget.Topmost && (GetWindowLongW(handle, -20) & 8) == 0);
            TaskbarWidgetHost.Move(widget, target, ownerHandle);
            bool hasBounds = GetWindowRect(handle, out var rect);
            _report.Add($"Z-ORDER VALUES: hwnd={handle}, ex=0x{GetWindowLongW(handle, -20):x}, foreground={foreground}->{GetForegroundWindow()}, bounds={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}, target={target}, GetWindowRect={hasBounds}");
            Check("placement repairs native topmost without moving or activating an unchanged widget",
                (GetWindowLongW(handle, -20) & 8) != 0 && GetForegroundWindow() == foreground &&
                hasBounds && rect.Left == target.Left && rect.Top == target.Top &&
                rect.Right == target.Right && rect.Bottom == target.Bottom);
            _report.Add("Z-ORDER: demotion/recovery uses an independent synthetic owner; Explorer's native band is not changed. Placement uses SWP_NOACTIVATE. This is not a real foreground-switch reproduction.");
        }
        finally { widget.Close(); owner.Close(); }
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    private struct WidgetRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string className, string? windowName);
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
        var now = DateTimeOffset.Now;
        var codex = Demo(UsageProviderKind.Codex) with { Windows = [new("5시간", 27, now.AddMinutes(271)), new("주간", 39, now.AddDays(3).AddHours(1))] };
        var claude = Demo(UsageProviderKind.Claude) with { Windows = [new("5시간", 0, now.AddMinutes(92)), new("주간", 45, now.AddHours(10))] };
        try
        {
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
            if (GetWindowRect(FindWindowW("Shell_TrayWnd", null), out var shell))
            {
                var taskbar = new Rect(shell.Left, shell.Top, shell.Right - shell.Left, shell.Bottom - shell.Top);
                Check("every visible service widget is wholly inside the native taskbar", bounds.All(taskbar.Contains));
                _report.Add($"GEOMETRY: taskbar {taskbar}, visible widget rectangles {string.Join(" / ", bounds)}");
            }
            if (bounds.Count > 1)
            {
                Check("shown taskbar widgets occupy disjoint rectangles",
                    bounds.SelectMany((rect, i) => bounds.Skip(i + 1).Select(other => Rect.Intersect(rect, other))).All(r => r.IsEmpty || r.Width <= 1 || r.Height <= 1));
                Check("all visible widget HWNDs have equal pixel widths", bounds.Max(rect => rect.Width) - bounds.Min(rect => rect.Width) <= 1);
                Check("all visible widget HWNDs have equal pixel heights", bounds.Max(rect => rect.Height) - bounds.Min(rect => rect.Height) <= 1);
            }
            else _report.Add("INFO: taskbar offered room for fewer than two visible widgets; no overlap pair to measure.");
            if (shown.Length > 0)
            {
                var handle = new WindowInteropHelper(shown[0]).Handle;
                ShowWindow(handle, 0 /* SW_HIDE */);
                var timeout = Stopwatch.StartNew();
                while (!IsWindowVisible(handle) && timeout.Elapsed < TimeSpan.FromSeconds(4))
                    await Task.Delay(100);
                Check("a widget hidden by its native owner is restored by the placement timer",
                    IsWindowVisible(handle));
            }
            var deadline = DateTimeOffset.Now.AddSeconds(2);
            _render(codex with { Windows = [new("5시간", 27, deadline)] });
            static TextBlock WidgetReset(Window widget) => Descendants<TextBlock>(widget)
                .Single(t => AutomationProperties.GetAutomationId(t) == "WidgetReset");
            Check("widget shows the last minute before reset", WidgetReset(codexView).Text == "0:01");
            await Task.Delay(3200);
            Check("widget countdown expires without another quota lookup", WidgetReset(codexView).Text == "0:00");
            _render(codex);
            var claudeChip = Tile(view);
            var darkText = (QuotaRows(claudeChip).FirstOrDefault()?.Foreground as SolidColorBrush)?.Color;
            Check("widget accessibility describes remaining Claude limits in Korean",
                AutomationProperties.GetName(claudeChip).Contains("Claude") && AutomationProperties.GetName(claudeChip).Contains("남음"));
            var darkGrok = MeasureWidgetIcon(grok);
            var darkCodex = MeasureWidgetIcon(codexView);
            _settings.ClaudeDisplayMode = UsageDisplayMode.Used;
            _settings.Language = AppLanguage.English;
            _settings.Theme = AppTheme.Light;
            _applySettings();
            await StableAsync(view);
            var lightGrok = MeasureWidgetIcon(grok);
            var lightCodex = MeasureWidgetIcon(codexView);
            Check("live Codex widget swaps between contrasting marks when the theme changes", SystemParameters.HighContrast ||
                darkCodex.Brightness > lightCodex.Brightness + 80 && darkCodex.Extent == lightCodex.Extent);
            Check("live Grok widget swaps between contrasting marks when the theme changes", SystemParameters.HighContrast ||
                darkGrok.Brightness > lightGrok.Brightness + 80 && darkGrok.Extent == lightGrok.Extent);
            claudeChip = Tile(view);
            Check("widget theme and language update immediately with independent display modes",
                AutomationProperties.GetName(claudeChip).Contains("Used", StringComparison.OrdinalIgnoreCase) &&
                QuotaRows(claudeChip).Any(t => t.Text.Contains("45%")) &&
                QuotaRows(Tile(codexView)).Any(t => t.Text.Contains("73%")) &&
                (SystemParameters.HighContrast || (QuotaRows(claudeChip).First().Foreground as SolidColorBrush)?.Color != darkText));
            _render(claude with { Status = UsageStatus.RateLimited });
            claudeChip = Tile(view);
            Check("widget keeps the previous values the dashboard still shows during a wait and marks them",
                QuotaRows(claudeChip).Any(t => t.Text.Contains("45%")) &&
                AutomationProperties.GetHelpText(claudeChip).Contains("Previous values"));
            _render(claude with { Status = UsageStatus.RateLimited, Windows = [] });
            claudeChip = Tile(view);
            Check("failed widget lookup clears measurements the provider dropped", QuotaRows(claudeChip).Length == 0 &&
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
            var closed = Widgets().Single(w => Equals(w.Tag, UsageProviderKind.Codex));
            closed.Close();
            var restoreTimeout = Stopwatch.StartNew();
            while (Widgets().Length < 2 && restoreTimeout.Elapsed < TimeSpan.FromSeconds(4))
                await Task.Delay(100);
            Check("the placement timer recreates a closed selected widget without another usage update",
                Widgets().Length == 2 && Widgets().Any(w => Equals(w.Tag, closed.Tag) && w != closed &&
                    IsWindow(new WindowInteropHelper(w).Handle)));
            _settings.WidgetProviders = TaskbarWidgetProviders.None;
            _applySettings();
            Check("disabling widgets clears the taskbar",
                !Widgets().Any(w => w.IsVisible) && !_window.ShowInTaskbar);

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
                        preview.SetLayout(200, 48);
                        if (!preview.IsVisible) preview.Show();
                        await StableAsync(preview);
                    }
                    Check(filename + " shows both quota periods on separate lines without clipping",
                        IsTwoLine(Tile(previews[0]), "73%", "61%") && IsTwoLine(Tile(previews[1]), "100%", "55%") &&
                        previews.All(w => TextFits(Root(w), Root(w))));
                    CaptureWidgetPreview(previews, filename);
                    await CheckWidgetTooltipAsync(previews[1], claude, theme);
                }
                var narrow = previews[1];
                narrow.Update([claude with { Windows = [.. claude.Windows, new("Opus 주간", 60, null)] }], _settings);
                narrow.SetLayout(132, 160);
                await StableAsync(narrow);
                Check("narrow vertical widget shows all three values with its unknown reset",
                    Math.Abs(narrow.ActualWidth - 132) <= 1 && QuotaRows(Tile(narrow)).Length == 3 && TextFits(Root(narrow), Root(narrow)) &&
                    Descendants<TextBlock>(narrow).Any(t => t.Text == "40%" && t.IsVisible) &&
                    AutomationProperties.GetHelpText(Tile(narrow)).Contains("Opus") &&
                    AutomationProperties.GetHelpText(Tile(narrow)).Contains("40% · Reset N/A"));
                narrow.SetLayout(118, 400);
                await StableAsync(narrow);
                var slimRows = QuotaRows(Tile(narrow));
                var slimLogo = Bounds((FrameworkElement)narrow.FindName("Logo"), Root(narrow));
                var slimMeters = Descendants<ProgressBar>(narrow).Select(m => Bounds(m, Root(narrow))).ToArray();
                Check("a slim vertical taskbar stacks the logo above every readable quota row",
                    Math.Abs(narrow.ActualWidth - 118) <= 1 && slimRows.Length == 3 && TextFits(Root(narrow), Root(narrow)) &&
                    slimRows.All(value => Bounds(value, Root(narrow)).Top >= slimLogo.Bottom) &&
                    slimMeters.Length == 3 && slimMeters.All(r => r.Width >= 15) &&
                    narrow.ActualHeight >= TaskbarWidgetWindow.DefaultHeight + TaskbarWidgetWindow.StackedBrandHeight - 1);
                narrow.SetLayout(148, 32);
                await StableAsync(narrow);
                var tile = Tile(narrow);
                var values = QuotaRows(tile);
                Check("a short horizontal taskbar caps the widget height and contains all quota rows",
                    Math.Abs(narrow.ActualHeight - 32) <= 1 && values.Length == 3 &&
                    values.All(value => new Rect(tile.RenderSize).Contains(Bounds(value, tile))));
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
            _settings.WidgetProviders = TaskbarWidgetProviders.None;
            _applySettings();
            _render(Demo(UsageProviderKind.Codex));
            _render(claude);
            if (!_window.IsVisible) _restoreWindow();
        }
    }

    private async Task CheckWidgetTooltipAsync(TaskbarWidgetWindow widget, UsageSnapshot sample, AppTheme theme)
    {
        var now = DateTimeOffset.Now;
        const string model = "Claude Sonnet weekly quota";
        var ready = sample with { Plan = "Enterprise", Message = null, CheckedAt = now, ObservedAt = now,
            Windows = [new(model, 41, now.AddMinutes(271)), new("Opus", 72, null)] };
        var button = (Button)widget.FindName("ProviderButton");
        foreach (var snapshot in new[] { ready, ready with { Status = UsageStatus.RateLimited,
            WaitReason = UsageWaitReason.RateLimited, NextQueryAt = now.AddMinutes(10), Message = "Retry after 10 minutes" } })
        {
            widget.Update([snapshot], _settings);
            var tip = (ToolTip)button.ToolTip;
            tip.PlacementTarget = button;
            tip.Placement = PlacementMode.Top;
            tip.IsOpen = true;
            try
            {
                await StableAsync(widget);
                tip.UpdateLayout();
                var texts = Descendants<TextBlock>(tip).Where(t => t.IsVisible).ToArray();
                var details = AutomationProperties.GetHelpText(button);
                bool Contains(string text) => texts.Any(t => t.Text.Contains(text, StringComparison.Ordinal));
                Check(theme + " opens a rendered widget tooltip inside its popup",
                    PresentationSource.FromVisual(tip) != null && tip.ActualWidth > 0 && tip.ActualHeight > 0 && TextFits(tip, tip));
                var background = ((SolidColorBrush)tip.Background).Color;
                Check(theme + " tooltip uses the active surface and legible foreground colors",
                    background == ((SolidColorBrush)Application.Current.FindResource("SurfaceBackground")).Color &&
                    texts.All(t => t.Foreground is SolidColorBrush brush && ContrastRatio(background, brush.Color) >= 4.5));
                if (snapshot.Status == UsageStatus.Ready)
                {
                    var deadline = ready.Windows[0].ResetsAt!.Value.ToLocalTime().ToString("g", Localizer.Culture);
                    Check(theme + " tooltip shows full names, percentages, reset countdown and local date",
                        Contains(model) && Contains("Opus") && Contains("59%") && Contains("28%") && Contains("4:31") &&
                        Contains(deadline) && details.Contains(model) && details.Contains("59%") && details.Contains(deadline));
                    Check(theme + " tooltip lays out quota names on separate readable rows",
                        texts.Any(t => t.Text == model && t.FontSize >= 11) && texts.Any(t => t.Text == "Opus" && t.FontSize >= 11) &&
                        Bounds(texts.First(t => t.Text == model), tip).Bottom <= Bounds(texts.First(t => t.Text == "Opus"), tip).Top);
                    CaptureTooltip(tip, theme == AppTheme.Dark ? "widget-tooltip-dark-ko.png" : "widget-tooltip-light-en.png");
                }
                else
                {
                    var status = Localizer.T("요청 제한 대기");
                    var previous = Localizer.T(" · 이전 확인값 표시").Trim(' ', '·');
                    Check(theme + " rate-limited tooltip explains the status and marks the retained values as previous",
                        Contains(status) && Contains(previous) && Contains(snapshot.Message!) && details.Contains(status) &&
                        details.Contains(previous) && Contains("59%") && Contains(model));
                }
            }
            finally { tip.IsOpen = false; }
        }
        widget.Update([sample], _settings);
    }

    private static double ContrastRatio(Color a, Color b)
    {
        static double Luminance(Color color) => 0.2126 * color.ScR + 0.7152 * color.ScG + 0.0722 * color.ScB;
        var first = Luminance(a);
        var second = Luminance(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private void CaptureTooltip(ToolTip tip, string filename)
    {
        var dpi = VisualTreeHelper.GetDpi(tip);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(tip.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(tip.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(tip);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(_output, filename));
        encoder.Save(file);
        _report.Add($"CAPTURE: {filename} · opened widget tooltip with synthetic quota data · {bitmap.PixelWidth}×{bitmap.PixelHeight} px");
    }

    private static (int Extent, bool ClearCorners, double Brightness) MeasureWidgetIcon(Window widget)
    {
        const int side = 96;
        var logo = (System.Windows.Controls.Image)widget.FindName("Logo");
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawImage(logo.Source, new Rect(0, 0, side, side));
        var bitmap = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[side * side * 4];
        bitmap.CopyPixels(pixels, side * 4, 0);
        var left = side; var top = side; var right = -1; var bottom = -1;
        var count = 0; var brightness = 0d;
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            var i = 4 * (y * side + x);
            var alpha = pixels[i + 3];
            if (alpha < 32) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            brightness += (pixels[i] + pixels[i + 1] + pixels[i + 2]) * 255d / (3 * alpha);
            count++;
        }
        var corners = new[] { 0, side - 1, side * (side - 1), side * side - 1 };
        return (Math.Max(right - left, bottom - top) + 1, corners.All(i => pixels[4 * i + 3] == 0),
            count > 0 ? brightness / count : 0);
    }

    private async Task CheckWidgetAlignmentAsync()
    {
        const string longName = "Gemini 3.1 Pro Preview extended quota";
        var now = DateTimeOffset.Now;
        var samples = new[]
        {
            Demo(UsageProviderKind.Codex) with { Windows = [new("5시간", 27, now.AddMinutes(271)), new("주간", 39, now.AddDays(3.5))] },
            Demo(UsageProviderKind.Claude) with { Windows = [new("월 추가 사용", 41, null)] },
            Demo(UsageProviderKind.Gemini) with { Windows = [new(longName, 17, now.AddHours(10)), new("Flash", 6, now.AddMinutes(30)), new("4 Argon 주간", 20, now.AddDays(2.5))] },
            Demo(UsageProviderKind.Grok) with { Status = UsageStatus.Unavailable, Windows = [] }
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
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                _settings.Theme = theme;
                _applySettings();
                for (var i = 0; i < views.Length; i++)
                {
                    views[i].Update([samples[i]], _settings);
                    views[i].SetLayout(220, 180);
                }
                await StableAsync(views[0]);
                var marks = views.Select(MeasureWidgetIcon).ToArray();
                Check(theme + " widget logos have clear corners and comparable visible extents",
                    marks.All(mark => mark.ClearCorners && Math.Abs(mark.Extent - 90) <= 2) &&
                    marks.Max(mark => mark.Extent) - marks.Min(mark => mark.Extent) <= 3);
                _report.Add($"GEOMETRY: {theme} widget icon extents on 96px canvas: {string.Join(", ", marks.Select(mark => mark.Extent))}");
                if (theme == AppTheme.Light) CaptureWidgetPreview(views, "taskbar-widget-four-services-light.png");
            }
            foreach (var (layout, width, height) in new[] {
                ("horizontal", 220d, 48d), ("narrow horizontal", 132d, 48d),
                ("vertical", 156d, 180d), ("narrow vertical", 134.4d, 180d) })
            {
                foreach (var view in views) view.SetLayout(width, height);
                await StableAsync(views[0]);
                foreach (var view in views) view.UpdateLayout();
                var roots = views.Select(Root).ToArray();
                Check(layout + " widget widths match across two-row, single-row, long-label and unavailable services",
                    EqualCoordinates(views.Select(w => w.ActualWidth)) && views.All(w => w.ActualWidth > 0 && w.ActualWidth <= width + 1));
                var inset = views[0].ActualWidth < 156 ? 6d : 7d;
                Check(layout + " uses matching horizontal padding around the widget content",
                    views.Select(w => ((Border)w.FindName("ContentFrame")).Padding).All(p => p.Left == inset && p.Right == inset));
                var logos = views.Select((w, i) => Bounds((FrameworkElement)w.FindName("Logo"), roots[i])).ToArray();
                Check(layout + " aligns provider logos even with different row counts",
                    EqualCoordinates(logos.Select(r => r.Left)) && EqualCoordinates(logos.Select(r => r.Width)));
                var labels = new List<Rect>();
                var meters = new List<Rect>();
                var percentages = new List<Rect>();
                var resets = new List<Rect>();
                var resetLabels = new List<TextBlock>();
                var rightAligned = true;
                var logoPrecedesRows = true;
                var readable = true;
                var singleLetterStarts = new List<double>();
                var balancedSpacing = true;
                for (var i = 0; i < views.Length - 1; i++)
                {
                    var rows = Descendants<Grid>(roots[i]).Where(row => AutomationProperties.GetAutomationId(row).StartsWith("WidgetQuota", StringComparison.Ordinal));
                    foreach (var row in rows)
                    {
                        var value = row.Children.OfType<TextBlock>().Single(t => t.Text.EndsWith('%'));
                        rightAligned &= value.TextAlignment == TextAlignment.Right;
                        var reset = Descendants<TextBlock>(row).Single(t => AutomationProperties.GetAutomationId(t) == "WidgetReset");
                        resetLabels.Add(reset);
                        resets.Add(Bounds(reset, roots[i]));
                        var name = row.Children.OfType<TextBlock>().Single(t => t != value && t != reset);
                        foreach (var text in new[] { name, value, reset })
                        {
                            var bounds = Bounds(text, roots[i]);
                            readable &= text.ActualHeight > 0 && text.FontSize * bounds.Height / text.ActualHeight >= 9;
                        }
                        if (name.Text.Length == 1)
                        {
                            var transform = name.TransformToAncestor(roots[i]);
                            var start = transform.TransformBounds(name.ContentStart.GetCharacterRect(LogicalDirection.Forward)).Left;
                            var end = transform.TransformBounds(name.ContentEnd.GetCharacterRect(LogicalDirection.Backward)).Right;
                            var graph = Bounds(row.Children.OfType<ProgressBar>().Single(), roots[i]);
                            singleLetterStarts.Add(start);
                            var leftGap = start - logos[i].Right;
                            var rightGap = graph.Left - end;
                            balancedSpacing &= end > start && Math.Abs(leftGap - inset) <= 1.6 && rightGap >= 2 && rightGap <= 10;
                        }
                        var labelBounds = Bounds(name, roots[i]);
                        if (VisualTreeHelper.GetClip(name) is Geometry clip)
                            labelBounds.Intersect(name.TransformToAncestor(roots[i]).TransformBounds(clip.Bounds));
                        labels.Add(labelBounds);
                        var gutter = labels[^1].Left - logos[i].Right;
                        logoPrecedesRows &= gutter >= 5 && gutter <= 8;
                        meters.Add(Bounds(row.Children.OfType<ProgressBar>().Single(), roots[i]));
                        percentages.Add(Bounds(value, roots[i]));
                    }
                }
                Check(layout + " leaves a small gap between the left logo and every quota row", logoPrecedesRows);
                Check(layout + " aligns the actual one-letter titles and leaves space before each graph",
                    singleLetterStarts.Count == 6 && EqualCoordinates(singleLetterStarts) && balancedSpacing);
                Check(layout + " labels monthly extra usage M and starts every countdown at one column",
                    Descendants<TextBlock>(roots[1]).Any(t => t.Text == "M" && t.IsVisible) &&
                    EqualCoordinates(resets.Select(r => r.Left)) &&
                    EqualCoordinates(Enumerable.Range(0, labels.Count).Select(i => resets[i].Left - percentages[i].Right)));
                Check(layout + " aligns label origins, meter tracks and percentage right edges across providers",
                    labels.Count == 6 && rightAligned && EqualCoordinates(labels.Select(r => r.Left)) &&
                    EqualCoordinates(meters.Select(r => r.Left)) && EqualCoordinates(meters.Select(r => r.Right)) &&
                    EqualCoordinates(percentages.Select(r => r.Right)) && meters.All(r => r.Width > 0));
                static bool Separate(Rect first, Rect second)
                {
                    var overlap = Rect.Intersect(first, second);
                    return overlap.IsEmpty || overlap.Width <= 1 || overlap.Height <= 1;
                }
                Check(layout + " shows each reset countdown directly on its quota row",
                    resetLabels.All(t => t.IsVisible && t.ActualWidth > 0) && resetLabels[2].Text == "—" &&
                    new[] { "4:31", "3d", "10:00", "0:30", "2d" }.All(time => resetLabels.Any(t => t.Text == time)));
                Check(layout + " keeps label, graph, percentage and reset on one readable line", readable && meters.All(r => r.Width >= 15) &&
                    Enumerable.Range(0, labels.Count).All(i => EqualCoordinates(new[] { CenterY(labels[i]), CenterY(meters[i]),
                        CenterY(percentages[i]), CenterY(resets[i]) })));
                var overlapRows = Enumerable.Range(0, labels.Count).Where(i => !(Separate(labels[i], meters[i])
                        && Separate(meters[i], percentages[i]) && Separate(labels[i], percentages[i])
                        && Separate(resets[i], labels[i]) && Separate(resets[i], meters[i]) && Separate(resets[i], percentages[i]))).ToArray();
                Check(layout + " keeps names, meter tracks and numbers in distinct spaces", overlapRows.Length == 0);
                foreach (var i in overlapRows)
                    _report.Add($"GEOMETRY: {layout} row {i} · label {labels[i]} · meter {meters[i]} · value {percentages[i]}");
                var gemini = (Button)views[2].FindName("ProviderButton");
                var grok = (Button)views[3].FindName("ProviderButton");
                var unavailableValue = Descendants<TextBlock>(grok).Single(t => t.Text == "—");
                Check(layout + " aligns the unavailable marker with measured values",
                    unavailableValue.TextAlignment == TextAlignment.Right &&
                    EqualCoordinates(percentages.Select(r => r.Right).Append(Bounds(unavailableValue, roots[3]).Right)));
                Check(layout + " keeps the default height across quota counts and unavailable states",
                    views.All(w => Math.Abs(w.ActualHeight - TaskbarWidgetWindow.DefaultHeight) <= 1));
                Check(layout + " retains full quota names in tooltips and omits unmeasured percentages",
                    AutomationProperties.GetHelpText(gemini).Contains(longName) && new[] { "P", "F", "A" }.All(label => Descendants<TextBlock>(gemini).Any(t => t.Text == label && t.IsVisible)) &&
                    !Descendants<TextBlock>(grok).Any(t => t.Text.Contains('%')) && roots.All(root => TextFits(root, root)));
                if (layout == "horizontal") CaptureWidgetPreview(views, "taskbar-widget-four-services-ko.png");
            }
        }
        finally { foreach (var view in views) view.Close(); }
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
        _render(Demo(UsageProviderKind.Gemini) with { Windows = [new("Pro", 17, null), new("Flash", 6, null), new("Flash-Lite", 8, null), new("4 Argon 주간", 20, null)] });
        await StableAsync(_window);
        Check("dashboard shows the five-hour window by name", Descendants<TextBlock>(Cards()[1]).Any(t => t.Text == "5시간" && t.IsVisible));
        Check("dashboard preserves readable Gemini model names", new[] { "Pro", "Flash", "Flash-Lite", "Argon" }.All(label =>
            Descendants<TextBlock>(Cards()[2]).Any(t => t.Text == label && t.IsVisible)));
        _render(Demo(UsageProviderKind.Gemini));
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
        ThemeService.Apply(AppTheme.Light);
        await StableAsync(_window);
        Capture(_window, "window-four-services-grok-en-light.png");
        Localizer.SetLanguage(AppLanguage.Korean);
        await StableAsync(_window);
        Capture(_window, "window-four-services-grok-light.png");
        ThemeService.Apply(AppTheme.Dark);
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
