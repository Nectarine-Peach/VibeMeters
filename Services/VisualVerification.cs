using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        _=>new[]{new UsageWindow("Pro",17,DemoTime.AddHours(5)),new UsageWindow("Flash",6,DemoTime.AddHours(5))}
    },DemoTime,"DEMO","검증용 데모 데이터 · 실제 계정 사용량이 아닙니다.");

    private async Task<int> RunAsync()
    {
        Directory.CreateDirectory(_output);
        _report.Add("VibeMeters shown-window WPF verification");
        _report.Add("ISOLATION: --smoke-test uses its own mutex, in-memory settings, no provider instances and no credential watchers.");
        _report.Add("DATA: Synthetic Codex remaining 61%; Claude remaining 100% / 55%; Gemini remaining 83% / 94%. These are not account measurements.");
        _report.Add("CAPTURE: RenderTargetBitmap of the shown window's client root at its current monitor DPI; excludes native desktop/compositor and tray pixels.");
        try
        {
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
            await CaptureScenario("wide dark",600,300,"window-wide-dark.png");

            await CheckWindowActionsAsync();
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
                Check("settings contains no scroll viewers",!Descendants<ScrollViewer>(settings).Any());
                Check("settings has all service selection and mode controls",new[]{"CodexCheck","ClaudeCheck","GeminiCheck","CodexModeCombo","ClaudeModeCombo","GeminiModeCombo","ThemeCombo","IntervalCombo"}.All(name=>settings.FindName(name)!=null));
                Capture(settings,"settings-dark.png");
                Check("settings labels fit the client",TextFits(Root(settings),Root(settings)));
                await CheckSettingsInteractionsAsync(settings);
                settings.Close();
                await StableAsync(_window);
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

    private async Task CheckWindowActionsAsync()
    {
        Click((Button)_window.FindName("PinButton"));
        await StableAsync(_window);
        Check("pin control enables always on top",_window.Topmost&&_settings.AlwaysOnTop);
        Click((Button)_window.FindName("PinButton"));
        Check("pin control disables always on top",!_window.Topmost&&!_settings.AlwaysOnTop);
        _settings.ShowInTaskbar=false;_applySettings();
        Check("taskbar preference disabled",!_window.ShowInTaskbar);
        _settings.ShowInTaskbar=true;_applySettings();
        Check("taskbar preference restored",_window.ShowInTaskbar);
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
        Check("all services natural design height",Close(_window.ActualWidth,360)&&Close(_window.ActualHeight,394));
        Capture(_window,"window-all-services-dark.png");
        _window.Width=220;_window.Height=140;await StableAsync(_window);
        AssertMainLayout("all services minimum readable size",new[]{("Codex",1),("Claude",2),("Gemini",2)});
        Check("all service rows retained with readable minimum height",_window.ActualHeight>=160);
        Capture(_window,"window-all-services-minimum-dark.png");
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
        _render(new UsageSnapshot(UsageProviderKind.Claude,UsageStatus.AuthenticationRequired,Array.Empty<UsageWindow>(),DemoTime,Message:"로그인이 필요합니다."));
        await StableAsync(_window);
        AssertMainLayout("honest missing-data placeholders",new[]{("Codex",1),("Claude",2)});
        Check("missing data has dashes and empty tracks",Rows(Cards()[1]).All(row=>ValueText(row)=="—"&&Close(Meter(row).Value,0)));
        var invoked=false;
        void Login(UsageProviderKind kind) { if(kind==UsageProviderKind.Claude) invoked=true; }
        _window.LoginRequested+=Login;
        try
        {
            var connect=Descendants<Button>(_window).SingleOrDefault(button=>AutomationProperties.GetName(button)=="Claude 로그인");
            Check("authentication recovery action exposed",connect!=null);
            if(connect!=null) Click(connect);
            Check("authentication recovery dispatches login event",invoked);
        }
        finally { _window.LoginRequested-=Login; }
        Capture(_window,"window-login-required-dark.png");
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
