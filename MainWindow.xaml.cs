using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using VibeMeters.Models;

namespace VibeMeters;

public partial class MainWindow : Window
{
    private bool _initialized, _changingMode;
    private double _normalWidth = 360, _normalHeight = 360;
    private readonly Dictionary<UsageProviderKind, UsageSnapshot> _snapshots = new();
    private SettingsWindow? _settingsWindow;
    public bool IsCompactLayout { get; private set; }
    public string CurrentLayout => IsCompactLayout ? "micro" : ActualWidth >= 520 ? "wide" : "normal";
    public AppSettings Settings { get; }
    public event Action? RefreshRequested;
    public event Action? SettingsChanged;
    public event Action? GeometryChanged;
    public event Action? HideRequested;
    public event Action<UsageProviderKind>? LoginRequested;

    public MainWindow(AppSettings settings)
    {
        Settings = settings;
        InitializeComponent();
        Width = _normalWidth = Math.Clamp(double.IsFinite(settings.WindowWidth) ? settings.WindowWidth : 360, MinWidth, SystemParameters.VirtualScreenWidth);
        Height = _normalHeight = Math.Clamp(double.IsFinite(settings.WindowHeight) ? settings.WindowHeight : 360, MinHeight, SystemParameters.VirtualScreenHeight);
        Topmost = settings.AlwaysOnTop;
        ShowInTaskbar = settings.ShowInTaskbar;
        if (settings.Left is double left && settings.Top is double top && double.IsFinite(left) && double.IsFinite(top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual; Left = left; Top = top;
            SourceInitialized += (_, _) => EnsureVisibleBounds();
        }
        UpdatePin();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.F5) { RefreshRequested?.Invoke(); args.Handled = true; }
            else if (args.Key == Key.Escape) { HideRequested?.Invoke(); args.Handled = true; }
        };
        LocationChanged += (_, _) =>
        {
            if (!_initialized || WindowState != WindowState.Normal) return;
            CaptureWindowGeometry(); GeometryChanged?.Invoke();
        };
        SizeChanged += (_, _) =>
        {
            if (!_initialized || WindowState != WindowState.Normal) return;
            if (!_changingMode)
            {
                Settings.WindowWidth = _normalWidth = Width; Settings.WindowHeight = _normalHeight = Height;
            }
            RenderLayout();
            CaptureWindowGeometry(); GeometryChanged?.Invoke();
        };
        StateChanged += (_, _) => { if (_initialized) { CaptureWindowGeometry(); GeometryChanged?.Invoke(); } };
        Closed += (_, _) => _settingsWindow?.Close();
        _initialized = true;
        RenderLayout();
    }

    public void CaptureWindowGeometry()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(CaptureWindowGeometry); return; }
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
            bounds.Width < MinWidth || bounds.Height < MinHeight || bounds.Left <= -30000 || bounds.Top <= -30000) return;
        Settings.Left = bounds.Left; Settings.Top = bounds.Top;
        Settings.WindowWidth = _normalWidth = bounds.Width;
        Settings.WindowHeight = _normalHeight = bounds.Height;
    }

    private void EnsureVisibleBounds()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not { } target) return;
        var transform = new MatrixTransform(target.TransformFromDevice);
        var screens = System.Windows.Forms.Screen.AllScreens;
        var areas = screens.Select(screen =>
        {
            var area = screen.WorkingArea;
            return transform.TransformBounds(new Rect(area.X, area.Y, area.Width, area.Height));
        }).ToArray();
        var current = new Rect(double.IsFinite(Left) ? Left : double.MinValue / 2,
            double.IsFinite(Top) ? Top : double.MinValue / 2, Width, Height);
        // A small title-bar portion is enough to retain intentional placement across monitors.
        var titleBounds = new Rect(current.Left, current.Top, current.Width, Math.Min(32, current.Height));
        if (areas.Any(area =>
        {
            var visible = Rect.Intersect(area, titleBounds);
            return !visible.IsEmpty && visible.Width >= 40 && visible.Height >= 20;
        })) return;
        var monitor = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var work = transform.TransformBounds(new Rect(monitor.X, monitor.Y, monitor.Width, monitor.Height));
        Width = Math.Max(MinWidth, Math.Min(Width, work.Width));
        Height = Math.Max(MinHeight, Math.Min(Height, work.Height));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Math.Clamp(double.IsFinite(Left) ? Left : work.Left, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(double.IsFinite(Top) ? Top : work.Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
        CaptureWindowGeometry();
    }

    public void SetCompactMode(bool compact)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetCompactMode(compact)); return; }
        if (compact) { _normalWidth = Width; _normalHeight = Height; }
        _changingMode = true;
        Width = compact ? 240 : Math.Max(MinWidth, _normalWidth);
        Height = compact ? 148 : Math.Max(MinHeight, _normalHeight);
        _changingMode = false;
        Settings.WindowWidth=Width; Settings.WindowHeight=Height;
        RenderLayout(); SettingsChanged?.Invoke();
    }

    public void ApplyProviderVisibility() => RenderLayout();
    public void FitDesignSize()
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.Invoke(FitDesignSize); return; }
        _changingMode=true;
        Width=360;
        Height=Math.Max(MinHeight,NaturalDesignHeight(360));
        Settings.WindowWidth=_normalWidth=Width;
        Settings.WindowHeight=_normalHeight=Height;
        _changingMode=false;
        RenderLayout();
    }
    private double NaturalDesignHeight(double width)
    {
        var providers=Enum.GetValues<UsageProviderKind>().Where(Settings.IsProviderVisible).ToArray();
        if(providers.Length==0) return 140;
        var wide=providers.Length>1&&width>=Math.Max(520,providers.Length*240);
        return 54+(wide?providers.Max(kind=>RequiredCardHeight(Snapshot(kind))):providers.Sum(kind=>RequiredCardHeight(Snapshot(kind)))+11*(providers.Length-1));
    }
    public void RefreshTheme()
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RefreshTheme); return; }
        RenderLayout(); UpdatePin();
    }

    private readonly record struct CardMetrics(double Scale)
    {
        public double Size(double minimum,double normal)=>minimum+(normal-minimum)*Scale;
        public double HeaderHeight=>Size(24,42);
        public double FramePadding=>Size(6,10);
        public double BottomPadding=>Size(4,10);
        public double HeadingHeight=>Size(12,26);
        public double HeadingFont=>Size(9,16);
        public double BasisFont=>Size(8,11);
        public double LabelFont=>Size(8,12);
        public double ValueFont=>Size(10.5,18);
        public double RowHeight=>Size(14,30);
        public double PadHorizontal=>Size(6,12);
        public double PadTop=>Size(1,10);
        public double PadBottom=>Size(1,8);
        public double DividerTop=>Size(1,8);
        public double DividerBottom=>Size(0,1);
        public double Gap=>Size(3,11);
        public double CardRadius=>Size(5,9);
        public double LabelWidth=>Size(26,42);
        public double ValueWidth=>Size(32,52);
        public double MeterHeight=>Size(3,6);
        public double MeterGap=>Size(4,9);
    }
    private static int QuotaRowCount(UsageSnapshot snapshot)=>snapshot.Windows.Count==0?(snapshot.Status==UsageStatus.Ready?1:PlaceholderNames(snapshot.Provider).Length):snapshot.Windows.Count;
    private static double CardHeight(UsageSnapshot snapshot,CardMetrics metrics)=>2+metrics.PadTop+metrics.PadBottom+metrics.HeadingHeight+metrics.DividerTop+1+metrics.DividerBottom+QuotaRowCount(snapshot)*metrics.RowHeight;
    private double LayoutHeight(UsageProviderKind[] providers,bool wide,CardMetrics metrics)
    {
        var cards=providers.Length==0?70:wide?providers.Max(kind=>CardHeight(Snapshot(kind),metrics)):providers.Sum(kind=>CardHeight(Snapshot(kind),metrics))+Math.Max(0,providers.Length-1)*metrics.Gap;
        return 2+metrics.HeaderHeight+metrics.BottomPadding+cards;
    }
    private void RenderLayout()
    {
        var width=ActualWidth>0?ActualWidth:Width;
        var height=ActualHeight>0?ActualHeight:Height;
        var providers=Enum.GetValues<UsageProviderKind>().Where(Settings.IsProviderVisible).ToArray();
        var wide=providers.Length>1&&width>=Math.Max(520,providers.Length*240);
        var minimumHeight=LayoutHeight(providers,wide,new CardMetrics(0));
        var normalHeight=LayoutHeight(providers,wide,new CardMetrics(1));
        var effectiveMinimum=Math.Max(140,Math.Ceiling(minimumHeight));
        if(Math.Abs(MinHeight-effectiveMinimum)>0.1) MinHeight=effectiveMinimum;
        height=Math.Max(height,effectiveMinimum);
        var widthScale=Math.Clamp((width-160)/200,0,1);
        var heightScale=Math.Clamp((height-minimumHeight)/Math.Max(1,normalHeight-minimumHeight),0,1);
        var metrics=new CardMetrics(Math.Min(widthScale,heightScale));
        IsCompactLayout=metrics.Scale<0.999;
        WindowFrame.Padding=new Thickness(metrics.FramePadding,0,metrics.FramePadding,metrics.BottomPadding);
        WindowFrame.CornerRadius=new CornerRadius(metrics.Size(8,12));
        HeaderRow.Height=new GridLength(metrics.HeaderHeight);
        TitleText.FontSize=metrics.Size(10.5,15);
        BrandLogo.LayoutTransform=new ScaleTransform(metrics.Size(0.78,1),metrics.Size(0.78,1));
        SubtitleText.Visibility=Visibility.Collapsed;
        MinimizeButton.Visibility=width>=520?Visibility.Visible:Visibility.Collapsed;
        foreach(var button in new[]{RefreshButton,SettingsButton,PinButton,MinimizeButton,HideButton})
        {
            button.Width=button.Height=metrics.Size(18,22);
            button.MinHeight=18;
            button.FontSize=metrics.Size(9,10);
            button.Padding=new Thickness(2);
        }
        ClearGrid(ProviderGrid);
        if(providers.Length==0)
        {
            var button=new Button { Content="표시할 서비스를 선택해 주세요  ⚙",FontSize=11 };
            button.Click+=Settings_Click;ProviderGrid.Children.Add(button);return;
        }
        for(var i=0;i<providers.Length;i++)
        {
            var snapshot=Snapshot(providers[i]);
            if(wide) ProviderGrid.ColumnDefinitions.Add(new ColumnDefinition());
            else ProviderGrid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            var element=RenderCard(snapshot,metrics);
            element.Margin=wide?new Thickness(0,0,i==providers.Length-1?0:metrics.Gap,0):new Thickness(0,0,0,i==providers.Length-1?0:metrics.Gap);
            if(wide) Grid.SetColumn(element,i);else Grid.SetRow(element,i);
            ProviderGrid.Children.Add(element);
        }
    }
    private static double RequiredCardHeight(UsageSnapshot snapshot)=>CardHeight(snapshot,new CardMetrics(1));
    private static void ClearGrid(Grid grid) { grid.Children.Clear(); grid.RowDefinitions.Clear(); grid.ColumnDefinitions.Clear(); }
    private UsageSnapshot Snapshot(UsageProviderKind kind) => _snapshots.TryGetValue(kind, out var snapshot) ? snapshot : new(kind, UsageStatus.NotConnected, Array.Empty<UsageWindow>(), DateTimeOffset.Now, Message:"계정을 연결하면 사용량이 표시됩니다.");
    private static Brush Accent(UsageProviderKind kind) => Palette(kind switch { UsageProviderKind.Codex => "CodexAccent", UsageProviderKind.Claude => "ClaudeAccent", _ => "GeminiAccent" });
    private string DisplayBasis(UsageProviderKind kind) => Settings.GetDisplayMode(kind)==UsageDisplayMode.Used ? "사용" : "남음";
    private double DisplayPercent(UsageProviderKind kind,UsageWindow quota) => Settings.GetDisplayMode(kind)==UsageDisplayMode.Used ? Math.Clamp(quota.UsedPercent,0,100) : quota.RemainingPercent;

    private FrameworkElement ProviderHeading(UsageSnapshot snapshot,CardMetrics metrics)
    {
        var heading=new StackPanel { Height=metrics.HeadingHeight,Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,ToolTip=WrappedTip(ProviderTooltip(snapshot)) };
        var providerName=snapshot.Provider==UsageProviderKind.Gemini&&snapshot.Plan=="Antigravity"?"Gemini · Antigravity":snapshot.Provider.ToString();
        heading.Children.Add(new TextBlock { Text=providerName,Foreground=Palette("PrimaryText"),FontWeight=FontWeights.SemiBold,FontSize=metrics.HeadingFont,VerticalAlignment=VerticalAlignment.Center });
        heading.Children.Add(new TextBlock { Text=DisplayBasis(snapshot.Provider),Foreground=Palette("SecondaryText"),FontSize=metrics.BasisFont,Margin=new Thickness(metrics.Size(5,11),0,0,0),VerticalAlignment=VerticalAlignment.Center });
        if(snapshot.Status!=UsageStatus.Ready)
        {
            var dot=new System.Windows.Shapes.Ellipse { Width=metrics.Size(4,6),Height=metrics.Size(4,6),Fill=Palette("WarningText"),VerticalAlignment=VerticalAlignment.Center };
            if(snapshot.Status is UsageStatus.NotConnected or UsageStatus.AuthenticationRequired)
            {
                var connect=new Button { Content=dot,Style=(Style)FindResource("StatusButton"),Width=metrics.Size(10,14),Height=Math.Min(metrics.HeadingHeight,18),Margin=new Thickness(4,0,0,0),ToolTip=WrappedTip($"{snapshot.Provider} 로그인\n{ProviderStatus(snapshot)}") };
                connect.Click+=(_,_)=>LoginRequested?.Invoke(snapshot.Provider);System.Windows.Automation.AutomationProperties.SetName(connect,$"{snapshot.Provider} 로그인");heading.Children.Add(connect);
            }
            else heading.Children.Add(new Border { Child=dot,Margin=new Thickness(metrics.Size(5,9),0,0,0),VerticalAlignment=VerticalAlignment.Center });
        }
        return heading;
    }
    private static string[] PlaceholderNames(UsageProviderKind provider)=>provider switch
    {
        UsageProviderKind.Codex=>new[]{"주간"},
        UsageProviderKind.Claude=>new[]{"5시간","주간"},
        _=>new[]{"Pro","Flash"}
    };
    private FrameworkElement RenderCard(UsageSnapshot snapshot,CardMetrics metrics)
    {
        var stack=new StackPanel();
        stack.Children.Add(ProviderHeading(snapshot,metrics));
        stack.Children.Add(new Border { Background=Palette("WindowBorder"),Height=1,Margin=new Thickness(0,metrics.DividerTop,0,metrics.DividerBottom),Opacity=0.7 });
        if(snapshot.Status==UsageStatus.Ready&&snapshot.Windows.Count==0)
        {
            stack.Children.Add(new TextBlock { Text="연결됨 · 비율 미제공",Foreground=Palette("SecondaryText"),FontSize=metrics.LabelFont,Height=metrics.RowHeight,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=WrappedTip(ProviderTooltip(snapshot)) });
            return new Border { Height=CardHeight(snapshot,metrics),Background=Palette("SurfaceBackground"),BorderBrush=Palette("WindowBorder"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(metrics.CardRadius),Padding=new Thickness(metrics.PadHorizontal,metrics.PadTop,metrics.PadHorizontal,metrics.PadBottom),Child=stack };
        }
        var entries=snapshot.Windows.Count==0
            ?PlaceholderNames(snapshot.Provider).Select(name=>(Name:name,Quota:(UsageWindow?)null)).ToArray()
            :snapshot.Windows.Select(quota=>(Name:quota.Name,Quota:(UsageWindow?)quota)).ToArray();
        for(var i=0;i<entries.Length;i++)
        {
            var entry=entries[i];var quota=entry.Quota;
            var row=new Grid { Height=metrics.RowHeight,ToolTip=WrappedTip(quota==null?ProviderTooltip(snapshot)+"\n사용량 정보 없음 · 수치를 확인하면 표시합니다.":WindowTooltip(quota,snapshot)+(quota.Name=="월 추가 사용"?"\n"+snapshot.Message:"")) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(metrics.LabelWidth) });row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(metrics.ValueWidth) });
            row.Children.Add(new TextBlock { Text=ShortQuotaName(entry.Name),Foreground=Palette("SecondaryText"),FontSize=metrics.LabelFont,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis });
            var meter=new ProgressBar { Value=quota==null?0:DisplayPercent(snapshot.Provider,quota),Foreground=quota!=null&&quota.RemainingPercent<=10?Palette("CriticalText"):Accent(snapshot.Provider),Height=metrics.MeterHeight,Margin=new Thickness(0,0,metrics.MeterGap,0),VerticalAlignment=VerticalAlignment.Center };
            Grid.SetColumn(meter,1);row.Children.Add(meter);
            var value=quota==null?new TextBlock { Text="—",FontSize=metrics.ValueFont,Foreground=Palette("MutedText"),TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center }:QuotaValue(snapshot.Provider,quota,metrics.ValueFont);
            Grid.SetColumn(value,2);row.Children.Add(value);
            if(i<entries.Length-1)
            {
                var separator=new Border { Height=1,Background=Palette("WindowBorder"),Opacity=0.45,VerticalAlignment=VerticalAlignment.Bottom };
                Grid.SetColumnSpan(separator,3);row.Children.Add(separator);
            }
            stack.Children.Add(row);
        }
        return new Border { Height=CardHeight(snapshot,metrics),Background=Palette("SurfaceBackground"),BorderBrush=Palette("WindowBorder"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(metrics.CardRadius),Padding=new Thickness(metrics.PadHorizontal,metrics.PadTop,metrics.PadHorizontal,metrics.PadBottom),Child=stack };
    }
    private TextBlock QuotaValue(UsageProviderKind provider,UsageWindow quota,double size)
    {
        var value=new TextBlock { Text=$"{DisplayPercent(provider,quota):0.#}%",FontSize=size,FontWeight=FontWeights.SemiBold,Foreground=quota.RemainingPercent<=10?Palette("CriticalText"):Palette("PrimaryText"),TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center };
        Typography.SetNumeralAlignment(value,FontNumeralAlignment.Tabular);
        return value;
    }
    private static string ShortQuotaName(string name)=>name.Contains("Argon",StringComparison.OrdinalIgnoreCase)?"Argon":name=="개인 한도"?"개인":name=="월 추가 사용"?"월추가":name.Contains("주")?"주간":name.Contains("5시간")?"5시간":name.Contains("세션")||name.Contains("시간")?"세션":name.Replace("모델","").Trim();
    private static ToolTip WrappedTip(string text)=>new ToolTip { MaxWidth=280,Content=new TextBlock { Text=text,TextWrapping=TextWrapping.Wrap,MaxWidth=254,FontSize=11,LineHeight=17 } };
    private static string ProviderStatus(UsageSnapshot snapshot)
    {
        if(snapshot.Status==UsageStatus.Ready) return "연결됨";
        var cache=snapshot.Windows.Count>0?" · 이전 확인값 표시":"";
        if(snapshot.Status==UsageStatus.RateLimited)
        {
            var wait=snapshot.RetryAfter is DateTimeOffset retry?retry-DateTimeOffset.Now:TimeSpan.Zero;
            var retryText=wait.TotalSeconds>0?(wait.TotalMinutes>=1?$" · {Math.Max(1,(int)Math.Ceiling(wait.TotalMinutes))}분 뒤 재시도":$" · {Math.Max(1,(int)Math.Ceiling(wait.TotalSeconds))}초 뒤 재시도"):" · 자동 재시도 대기";
            return "요청 제한"+retryText+cache;
        }
        return StatusLabel(snapshot.Status)+cache;
    }
    private static string ProviderTooltip(UsageSnapshot snapshot)
    {
        var message=ShortMessage(snapshot.Message);
        var heading=$"{snapshot.Provider}{(string.IsNullOrWhiteSpace(snapshot.Plan)?"":" · "+snapshot.Plan)}\n{ProviderStatus(snapshot)}";
        return string.IsNullOrWhiteSpace(message)?heading:heading+"\n"+message;
    }
    private static string ShortMessage(string? message)
    {
        if(string.IsNullOrWhiteSpace(message)) return "";
        var text=string.Join(" ",message.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));
        return text.Length>140?text.Substring(0,140)+"…":text;
    }
    public void RenderSnapshot(UsageSnapshot snapshot)
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.Invoke(()=>RenderSnapshot(snapshot)); return; }
        _snapshots[snapshot.Provider]=snapshot;
        RenderLayout();
    }
    public void SetLoginStatus(UsageProviderKind kind,bool busy,string? message=null)
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.Invoke(()=>SetLoginStatus(kind,busy,message)); return; }
        var text=message??(busy?$"{kind} 로그인 진행 중…":"로그인 후 자동으로 다시 확인합니다.");
        _settingsWindow?.SetLoginStatus(kind,busy,text); UpdateStatus(text);
    }
    public void SetRefreshing(bool refreshing)
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.Invoke(()=>SetRefreshing(refreshing)); return; }
        RefreshButton.IsEnabled=!refreshing;
        RefreshButton.ToolTip=refreshing?"사용량 확인 중…":"지금 새로고침 (F5)";
    }
    public void UpdateStatus(string text)
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.Invoke(()=>UpdateStatus(text)); return; }
        TitleText.ToolTip=WrappedTip(ShortMessage(text));
    }
    private static Brush Palette(string key) => (Brush)Application.Current.FindResource(key);
    private static string StatusLabel(UsageStatus status)=>status switch { UsageStatus.Ready=>"연결됨",UsageStatus.NotConnected=>"연결 필요",UsageStatus.AuthenticationRequired=>"로그인 필요",UsageStatus.RateLimited=>"잠시 대기",_=>"확인 불가" };
    private static string ResetShort(DateTimeOffset? resets)
    {
        if(resets is null) return "—";
        var delta=resets.Value-DateTimeOffset.Now;
        return delta<=TimeSpan.Zero?"초기화 대기":delta.TotalDays>=1?$"{(int)delta.TotalDays}일 {delta.Hours}시간 ↻":delta.TotalHours>=1?$"{(int)delta.TotalHours}시간 {delta.Minutes}분 ↻":$"{Math.Max(1,(int)delta.TotalMinutes)}분 ↻";
    }
    private static string WindowTooltip(UsageWindow quota,UsageSnapshot snapshot)=>$"{snapshot.Provider} · {quota.Name}\n남음 {quota.RemainingPercent:0.#}% · 사용 {quota.UsedPercent:0.#}%\n{ResetShort(quota.ResetsAt)} · {quota.ResetsAt?.ToLocalTime().ToString("M/d HH:mm",CultureInfo.InvariantCulture)??"초기화 정보 없음"}\n{ProviderStatus(snapshot)}";
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    { base.OnPropertyChanged(e); if(_initialized&&e.Property==TopmostProperty) { Settings.AlwaysOnTop=Topmost; UpdatePin(); } }
    private void UpdatePin() { PinButton.Foreground=Palette(Topmost?"CodexAccent":"SecondaryText"); PinButton.ToolTip=Topmost?"항상 위: 켜짐":"항상 위: 꺼짐"; }
    private void Header_MouseLeftButtonDown(object sender,MouseButtonEventArgs e)
    {
        if(e.OriginalSource is DependencyObject source)
            for(var current=source;current!=null;current=VisualTreeHelper.GetParent(current)) if(current is Button) return;
        if(e.LeftButton==MouseButtonState.Pressed) DragMove();
    }
    private void Pin_Click(object sender,RoutedEventArgs e) { Topmost=!Topmost; SettingsChanged?.Invoke(); }
    private void Minimize_Click(object sender,RoutedEventArgs e) { if(ShowInTaskbar) WindowState=WindowState.Minimized; else HideRequested?.Invoke(); }
    private void Hide_Click(object sender,RoutedEventArgs e)=>HideRequested?.Invoke();
    private void Refresh_Click(object sender,RoutedEventArgs e)=>RefreshRequested?.Invoke();
    private void Settings_Click(object sender,RoutedEventArgs e)
    {
        if(_settingsWindow!=null) { _settingsWindow.Activate(); return; }
        _settingsWindow=new SettingsWindow(Settings) { Owner=this };
        _settingsWindow.SettingsChanged+=()=> { Topmost=Settings.AlwaysOnTop; ShowInTaskbar=Settings.ShowInTaskbar; RenderLayout(); SettingsChanged?.Invoke(); };
        _settingsWindow.LoginRequested+=kind=>LoginRequested?.Invoke(kind);
        _settingsWindow.Closed+=(_,_)=>_settingsWindow=null;
        _settingsWindow.Show();
    }
}







