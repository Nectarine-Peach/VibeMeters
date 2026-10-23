using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using VibeMeters.Models;

namespace VibeMeters;

public partial class MainWindow : Window
{
    private bool _initialized, _changingMode;
    private bool _userShortenedWindow;
    private double _normalWidth = 360, _normalHeight = 360;
    private readonly Dictionary<UsageProviderKind, UsageSnapshot> _snapshots = new();
    private SettingsWindow? _settingsWindow;
    public bool IsCompactLayout { get; private set; }
    public string CurrentLayout => IsCompactLayout ? "micro" : ActualWidth >= 520 ? "wide" : "normal";
    public AppSettings Settings { get; }
    public event Action? RefreshRequested;
    public event Action? SettingsChanged;
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
        if (settings.Left is double left && settings.Top is double top && IsVisiblePosition(left, top))
        { WindowStartupLocation = WindowStartupLocation.Manual; Left = left; Top = top; }
        UpdatePin();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.F5) { RefreshRequested?.Invoke(); args.Handled = true; }
            else if (args.Key == Key.Escape) { HideRequested?.Invoke(); args.Handled = true; }
        };
        LocationChanged += (_, _) => { if (_initialized && WindowState == WindowState.Normal) { Settings.Left = Left; Settings.Top = Top; } };
        SizeChanged += (_, _) =>
        {
            if (!_initialized || WindowState != WindowState.Normal) return;
            if (!_changingMode)
            {
                Settings.WindowWidth = _normalWidth = Width; Settings.WindowHeight = _normalHeight = Height;
                if(IsLoaded) _userShortenedWindow=Height<NaturalDesignHeight(Width)-1;
            }
            RenderLayout();
        };
        Closed += (_, _) => _settingsWindow?.Close();
        _initialized = true;
        RenderLayout();
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
        _userShortenedWindow=false;
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

    private void RenderLayout()
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var providers = Enum.GetValues<UsageProviderKind>().Where(Settings.IsProviderVisible).ToArray();
        var wide = providers.Length > 1 && width >= Math.Max(520, providers.Length * 240);
        var narrow=width<300;
        IsCompactLayout = narrow || _userShortenedWindow;
        WindowFrame.Padding=narrow?new Thickness(9,4,9,7):new Thickness(10,0,10,10);
        HeaderRow.Height=new GridLength(narrow?32:42);
        SubtitleText.Visibility = Visibility.Collapsed;
        TitleText.FontSize = narrow?11.5:15;
        MinimizeButton.Visibility = width >= 520 ? Visibility.Visible : Visibility.Collapsed;
        ProviderGrid.Visibility = IsCompactLayout ? Visibility.Collapsed : Visibility.Visible;
        MicroPanel.Visibility = IsCompactLayout ? Visibility.Visible : Visibility.Collapsed;
        ClearGrid(ProviderGrid); ClearGrid(MicroPanel);
        if (providers.Length == 0)
        {
            var button = new Button { Content = "표시할 서비스를 선택해 주세요  ⚙", FontSize = 11 };
            button.Click += Settings_Click;
            (IsCompactLayout ? MicroPanel : ProviderGrid).Children.Add(button);
            return;
        }
        for (var i=0; i<providers.Length; i++)
        {
            var snapshot = Snapshot(providers[i]);
            var host = IsCompactLayout ? MicroPanel : ProviderGrid;
            if (wide && !IsCompactLayout) host.ColumnDefinitions.Add(new ColumnDefinition());
            else host.RowDefinitions.Add(new RowDefinition { Height = IsCompactLayout ? new GridLength(1,GridUnitType.Star) : GridLength.Auto });
            var element = IsCompactLayout ? RenderMicro(snapshot) : RenderCard(snapshot);
            element.Margin=wide&&!IsCompactLayout?new Thickness(0,0,i==providers.Length-1?0:11,0):new Thickness(0,0,0,i==providers.Length-1?0:IsCompactLayout?4:11);
            if (wide && !IsCompactLayout) Grid.SetColumn(element,i); else Grid.SetRow(element,i);
            host.Children.Add(element);
        }
    }
    private static double RequiredCardHeight(UsageSnapshot snapshot)=>56+30*(snapshot.Windows.Count==0?PlaceholderNames(snapshot.Provider).Length:snapshot.Windows.Count);
    private static void ClearGrid(Grid grid) { grid.Children.Clear(); grid.RowDefinitions.Clear(); grid.ColumnDefinitions.Clear(); }
    private UsageSnapshot Snapshot(UsageProviderKind kind) => _snapshots.TryGetValue(kind, out var snapshot) ? snapshot : new(kind, UsageStatus.NotConnected, Array.Empty<UsageWindow>(), DateTimeOffset.Now, Message:"계정을 연결하면 사용량이 표시됩니다.");
    private static Brush Accent(UsageProviderKind kind) => Palette(kind switch { UsageProviderKind.Codex => "CodexAccent", UsageProviderKind.Claude => "ClaudeAccent", _ => "GeminiAccent" });
    private string DisplayBasis(UsageProviderKind kind) => Settings.GetDisplayMode(kind)==UsageDisplayMode.Used ? "사용" : "남음";
    private double DisplayPercent(UsageProviderKind kind,UsageWindow quota) => Settings.GetDisplayMode(kind)==UsageDisplayMode.Used ? Math.Clamp(quota.UsedPercent,0,100) : quota.RemainingPercent;

    private FrameworkElement ProviderHeading(UsageSnapshot snapshot,bool compact,bool dense=false)
    {
        var heading=new StackPanel { Height=compact?14:26,Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,ToolTip=WrappedTip(ProviderTooltip(snapshot)) };
        heading.Children.Add(new TextBlock { Text=snapshot.Provider.ToString(),Foreground=Palette("PrimaryText"),FontWeight=FontWeights.SemiBold,FontSize=compact?(dense?9:10):16,VerticalAlignment=VerticalAlignment.Center });
        heading.Children.Add(new TextBlock { Text=DisplayBasis(snapshot.Provider),Foreground=Palette("SecondaryText"),FontSize=compact?8:11,Margin=new Thickness(compact?5:11,0,0,0),VerticalAlignment=VerticalAlignment.Center });
        if(snapshot.Status!=UsageStatus.Ready)
        {
            var dot=new System.Windows.Shapes.Ellipse { Width=compact?4:6,Height=compact?4:6,Fill=Palette("WarningText"),VerticalAlignment=VerticalAlignment.Center };
            if(snapshot.Status is UsageStatus.NotConnected or UsageStatus.AuthenticationRequired)
            {
                var connect=new Button { Content=dot,Style=(Style)FindResource("StatusButton"),Width=compact?10:14,Height=compact?14:18,Margin=new Thickness(4,0,0,0),ToolTip=WrappedTip($"{snapshot.Provider} 로그인\n{ProviderStatus(snapshot)}") };
                connect.Click+=(_,_)=>LoginRequested?.Invoke(snapshot.Provider);System.Windows.Automation.AutomationProperties.SetName(connect,$"{snapshot.Provider} 로그인");heading.Children.Add(connect);
            }
            else heading.Children.Add(new Border { Child=dot,Margin=new Thickness(compact?5:9,0,0,0),VerticalAlignment=VerticalAlignment.Center });
        }
        return heading;
    }
    private static string[] PlaceholderNames(UsageProviderKind provider)=>provider switch
    {
        UsageProviderKind.Codex=>new[]{"주간"},
        UsageProviderKind.Claude=>new[]{"5시간","주간"},
        _=>new[]{"Pro","Flash"}
    };
    private FrameworkElement RenderCard(UsageSnapshot snapshot)
    {
        var stack=new StackPanel();
        stack.Children.Add(ProviderHeading(snapshot,false));
        stack.Children.Add(new Border { Background=Palette("WindowBorder"),Height=1,Margin=new Thickness(0,8,0,1),Opacity=0.7 });
        var entries=snapshot.Windows.Count==0
            ?PlaceholderNames(snapshot.Provider).Select(name=>(Name:name,Quota:(UsageWindow?)null)).ToArray()
            :snapshot.Windows.Select(quota=>(Name:quota.Name,Quota:(UsageWindow?)quota)).ToArray();
        for(var i=0;i<entries.Length;i++)
        {
            var entry=entries[i];var quota=entry.Quota;
            var row=new Grid { Height=30,ToolTip=WrappedTip(quota==null?ProviderTooltip(snapshot)+"\n사용량 정보 없음 · 수치를 확인하면 표시합니다.":WindowTooltip(quota,snapshot)) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(42) });row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(52) });
            row.Children.Add(new TextBlock { Text=ShortQuotaName(entry.Name),Foreground=Palette("SecondaryText"),FontSize=12,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis });
            var meter=new ProgressBar { Value=quota==null?0:DisplayPercent(snapshot.Provider,quota),Foreground=quota!=null&&quota.RemainingPercent<=10?Palette("CriticalText"):Accent(snapshot.Provider),Height=6,Margin=new Thickness(0,0,9,0),VerticalAlignment=VerticalAlignment.Center };
            Grid.SetColumn(meter,1);row.Children.Add(meter);
            var value=quota==null?new TextBlock { Text="—",FontSize=18,Foreground=Palette("MutedText"),TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center }:QuotaValue(snapshot.Provider,quota,18);
            Grid.SetColumn(value,2);row.Children.Add(value);
            if(i<entries.Length-1)
            {
                var separator=new Border { Height=1,Background=Palette("WindowBorder"),Opacity=0.45,VerticalAlignment=VerticalAlignment.Bottom };
                Grid.SetColumnSpan(separator,3);row.Children.Add(separator);
            }
            stack.Children.Add(row);
        }
        return new Border { Height=RequiredCardHeight(snapshot),Background=Palette("SurfaceBackground"),BorderBrush=Palette("WindowBorder"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(9),Padding=new Thickness(12,10,12,8),Child=stack };
    }
    private FrameworkElement RenderMicro(UsageSnapshot snapshot)
    {
        var providers=Enum.GetValues<UsageProviderKind>().Count(Settings.IsProviderVisible);
        var dense=(ActualHeight>0?ActualHeight:Height)<185&&providers>=3;
        var content=new Grid { VerticalAlignment=VerticalAlignment.Center,ToolTip=WrappedTip(ProviderTooltip(snapshot)) };
        var heading=ProviderHeading(snapshot,true,dense);
        var meters=new Grid();
        if(dense)
        {
            content.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(snapshot.Status==UsageStatus.Ready?62:72) });content.ColumnDefinitions.Add(new ColumnDefinition());
            content.Children.Add(heading);Grid.SetColumn(meters,1);content.Children.Add(meters);
        }
        else
        {
            content.RowDefinitions.Add(new RowDefinition { Height=new GridLength(14) });content.RowDefinitions.Add(new RowDefinition());
            content.Children.Add(heading);Grid.SetRow(meters,1);meters.Margin=new Thickness(0,2,0,0);content.Children.Add(meters);
        }
        var quotas=snapshot.Windows.Take(2).ToArray();
        for(var i=0;i<quotas.Length;i++)
        {
            var quota=quotas[i];meters.ColumnDefinitions.Add(new ColumnDefinition());
            var section=new Grid { ToolTip=WrappedTip(WindowTooltip(quota,snapshot)),Margin=new Thickness(i==0?0:9,0,0,0) };
            section.RowDefinitions.Add(new RowDefinition());section.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            var line=new Grid();line.ColumnDefinitions.Add(new ColumnDefinition());line.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            line.Children.Add(new TextBlock { Text=ShortQuotaName(quota.Name),FontSize=8,Foreground=Palette("MutedText"),VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,0,3,0) });
            var value=QuotaValue(snapshot.Provider,quota,dense?12:13);Grid.SetColumn(value,1);line.Children.Add(value);section.Children.Add(line);
            var meter=new ProgressBar { Value=DisplayPercent(snapshot.Provider,quota),Foreground=quota.RemainingPercent<=10?Palette("CriticalText"):Accent(snapshot.Provider),Height=2,Margin=new Thickness(0,dense?1:2,0,0) };
            Grid.SetRow(meter,1);section.Children.Add(meter);
            System.Windows.Automation.AutomationProperties.SetName(section,$"{snapshot.Provider} {quota.Name} {DisplayBasis(snapshot.Provider)} {DisplayPercent(snapshot.Provider,quota):0.#}%");
            Grid.SetColumn(section,i);meters.Children.Add(section);
        }
        if(quotas.Length==0) meters.Children.Add(new TextBlock { Text=snapshot.Status is UsageStatus.NotConnected or UsageStatus.AuthenticationRequired?"로그인 필요":StatusLabel(snapshot.Status),Foreground=Palette("SecondaryText"),FontSize=10,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right,TextTrimming=TextTrimming.CharacterEllipsis });
        return new Border { Background=Palette("SurfaceBackground"),BorderBrush=Palette("WindowBorder"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(7),Padding=new Thickness(6,dense?3:2,6,dense?3:2),Child=content };
    }
    private TextBlock QuotaValue(UsageProviderKind provider,UsageWindow quota,double size)
    {
        var value=new TextBlock { Text=$"{DisplayPercent(provider,quota):0.#}%",FontSize=size,FontWeight=FontWeights.SemiBold,Foreground=quota.RemainingPercent<=10?Palette("CriticalText"):Palette("PrimaryText"),TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center };
        Typography.SetNumeralAlignment(value,FontNumeralAlignment.Tabular);
        return value;
    }
    private static string ShortQuotaName(string name)=>name.Contains("주")?"주간":name.Contains("5시간")?"5시간":name.Contains("세션")||name.Contains("시간")?"세션":name.Replace("모델","").Trim();
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
    private static bool IsVisiblePosition(double left,double top)=>double.IsFinite(left)&&double.IsFinite(top)&&left+100>SystemParameters.VirtualScreenLeft&&left<SystemParameters.VirtualScreenLeft+SystemParameters.VirtualScreenWidth-40&&top+40>SystemParameters.VirtualScreenTop&&top<SystemParameters.VirtualScreenTop+SystemParameters.VirtualScreenHeight-40;
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






