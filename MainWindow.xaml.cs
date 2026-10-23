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
    private double _normalWidth = 340, _normalHeight = 320;
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
        Width = _normalWidth = Math.Clamp(double.IsFinite(settings.WindowWidth) ? settings.WindowWidth : 340, MinWidth, SystemParameters.VirtualScreenWidth);
        Height = _normalHeight = Math.Clamp(double.IsFinite(settings.WindowHeight) ? settings.WindowHeight : 320, MinHeight, SystemParameters.VirtualScreenHeight);
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
            if (!_changingMode) { Settings.WindowWidth = _normalWidth = Width; Settings.WindowHeight = _normalHeight = Height; }
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
        var needed = providers.Sum(kind => RequiredCardHeight(Snapshot(kind)));
        if (wide) needed = providers.Max(kind => RequiredCardHeight(Snapshot(kind)));
        IsCompactLayout = width < 280 || height - 50 < needed;
        SubtitleText.Visibility = width >= 380 ? Visibility.Visible : Visibility.Collapsed;
        TitleText.FontSize = 12;
        MinimizeButton.Visibility = width >= 280 ? Visibility.Visible : Visibility.Collapsed;
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
            else host.RowDefinitions.Add(new RowDefinition { Height = IsCompactLayout ? new GridLength(1,GridUnitType.Star) : new GridLength(RequiredCardHeight(snapshot),GridUnitType.Star) });
            var element = IsCompactLayout ? RenderMicro(snapshot) : RenderCard(snapshot);
            element.Margin=wide&&!IsCompactLayout?new Thickness(0,0,i==providers.Length-1?0:7,0):new Thickness(0,0,0,i==providers.Length-1?0:4);
            if (wide && !IsCompactLayout) Grid.SetColumn(element,i); else Grid.SetRow(element,i);
            host.Children.Add(element);
        }
    }
    private static double RequiredCardHeight(UsageSnapshot snapshot)=>44+snapshot.Windows.Count*52+(snapshot.Windows.Count==0?30:0)+4;
    private static void ClearGrid(Grid grid) { grid.Children.Clear(); grid.RowDefinitions.Clear(); grid.ColumnDefinitions.Clear(); }
    private UsageSnapshot Snapshot(UsageProviderKind kind) => _snapshots.TryGetValue(kind, out var snapshot) ? snapshot : new(kind, UsageStatus.NotConnected, Array.Empty<UsageWindow>(), DateTimeOffset.Now, Message:"계정을 연결하면 사용량이 표시됩니다.");
    private static Brush Accent(UsageProviderKind kind) => Palette(kind switch { UsageProviderKind.Codex => "CodexAccent", UsageProviderKind.Claude => "ClaudeAccent", _ => "GeminiAccent" });
    private string DisplayBasis(UsageProviderKind kind) => Settings.GetDisplayMode(kind)==UsageDisplayMode.Used ? "사용" : "남음";
    private double DisplayPercent(UsageProviderKind kind,UsageWindow quota) => Settings.GetDisplayMode(kind)==UsageDisplayMode.Used ? Math.Clamp(quota.UsedPercent,0,100) : quota.RemainingPercent;

    private FrameworkElement ProviderHeading(UsageSnapshot snapshot,bool compact)
    {
        var heading=new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        var identity=new StackPanel { Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center };
        var dot=new System.Windows.Shapes.Ellipse { Width=5,Height=5,Fill=snapshot.Status==UsageStatus.Ready?Accent(snapshot.Provider):Palette("WarningText"),VerticalAlignment=VerticalAlignment.Center };
        if(snapshot.Status is UsageStatus.NotConnected or UsageStatus.AuthenticationRequired)
        {
            var connect=new Button { Content=dot,Style=(Style)FindResource("StatusButton"),ToolTip=WrappedTip($"{snapshot.Provider} 로그인\n{ProviderStatus(snapshot)}"),Margin=new Thickness(-3,0,0,0) };
            connect.Click+=(_,_)=>LoginRequested?.Invoke(snapshot.Provider);
            System.Windows.Automation.AutomationProperties.SetName(connect,$"{snapshot.Provider} 로그인");
            identity.Children.Add(connect);
        }
        else identity.Children.Add(new Border { Child=dot,Width=10,VerticalAlignment=VerticalAlignment.Center });
        identity.Children.Add(new TextBlock { Text=snapshot.Provider.ToString(),Foreground=Palette("PrimaryText"),FontWeight=FontWeights.SemiBold,FontSize=compact?10:12,VerticalAlignment=VerticalAlignment.Center });
        identity.Children.Add(new TextBlock { Text=DisplayBasis(snapshot.Provider),Foreground=Palette("MutedText"),FontSize=compact?8:10,Margin=new Thickness(compact?4:7,0,0,0),VerticalAlignment=VerticalAlignment.Center });
        heading.Children.Add(identity);
        if(!compact)
        {
            var plan=new TextBlock { Text=snapshot.Plan,Foreground=Palette("MutedText"),FontSize=10,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=120 };
            Grid.SetColumn(plan,1);heading.Children.Add(plan);
        }
        heading.ToolTip=WrappedTip(ProviderTooltip(snapshot));
        return heading;
    }
    private FrameworkElement RenderCard(UsageSnapshot snapshot)
    {
        var accent=Accent(snapshot.Provider);
        var stack=new StackPanel { VerticalAlignment=VerticalAlignment.Center };
        var heading=ProviderHeading(snapshot,false);heading.Margin=new Thickness(0,0,0,10);stack.Children.Add(heading);
        foreach(var quota in snapshot.Windows)
        {
            var section=new StackPanel { Margin=new Thickness(0,0,0,9),ToolTip=WrappedTip(WindowTooltip(quota,snapshot)) };
            var line=new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition());line.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var label=new TextBlock { Text=quota.Name,Foreground=Palette("SecondaryText"),FontSize=11,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis };
            line.Children.Add(label);
            var value=QuotaValue(snapshot.Provider,quota,18);Grid.SetColumn(value,1);line.Children.Add(value);
            section.Children.Add(line);
            section.Children.Add(new ProgressBar { Value=DisplayPercent(snapshot.Provider,quota),Foreground=quota.RemainingPercent<=10?Palette("CriticalText"):accent,Height=3,Margin=new Thickness(0,4,0,0) });
            var reset=new TextBlock { Text=ResetShort(quota.ResetsAt),FontSize=9,Foreground=Palette("MutedText"),HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,4,0,0) };
            section.Children.Add(reset);stack.Children.Add(section);
        }
        if(snapshot.Windows.Count==0)
        {
            var needsLogin=snapshot.Status is UsageStatus.NotConnected or UsageStatus.AuthenticationRequired;
            if(needsLogin)
            {
                var login=new Button { Content="로그인하여 연결 ↗",HorizontalAlignment=HorizontalAlignment.Left,FontSize=11,Padding=new Thickness(0,4,0,4),ToolTip=WrappedTip(ProviderTooltip(snapshot)) };
                login.Click+=(_,_)=>LoginRequested?.Invoke(snapshot.Provider);stack.Children.Add(login);
            }
            else stack.Children.Add(new TextBlock { Text=ProviderStatus(snapshot),FontSize=10,Foreground=Palette("SecondaryText"),TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=WrappedTip(ProviderTooltip(snapshot)),Margin=new Thickness(0,5,0,5) });
        }
        var content=new Border { Padding=new Thickness(12,9,12,7),Child=stack };
        var frame=new Grid();
        frame.Children.Add(content);
        frame.Children.Add(new Border { Background=accent,Width=2,HorizontalAlignment=HorizontalAlignment.Left,CornerRadius=new CornerRadius(1),Margin=new Thickness(0,12,0,12),Opacity=0.65 });
        return new Border { Background=Palette("SurfaceBackground"),CornerRadius=new CornerRadius(8),Child=frame };
    }
    private FrameworkElement RenderMicro(UsageSnapshot snapshot)
    {
        var providers=Enum.GetValues<UsageProviderKind>().Count(Settings.IsProviderVisible);
        var dense=(ActualHeight>0?ActualHeight:Height)<180&&providers>=3;
        var content=new Grid { VerticalAlignment=VerticalAlignment.Center,ToolTip=WrappedTip(ProviderTooltip(snapshot)) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(64) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.Children.Add(ProviderHeading(snapshot,true));
        var quotas=snapshot.Windows.Take(2).ToArray();
        var meters=new Grid();Grid.SetColumn(meters,1);content.Children.Add(meters);
        for(var i=0;i<quotas.Length;i++)
        {
            var quota=quotas[i];meters.ColumnDefinitions.Add(new ColumnDefinition());
            var section=new StackPanel { Margin=new Thickness(i==0?0:4,0,0,0),ToolTip=WrappedTip(WindowTooltip(quota,snapshot)) };
            var line=new Grid();line.ColumnDefinitions.Add(new ColumnDefinition());line.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            line.Children.Add(new TextBlock { Text=ShortQuotaName(quota.Name),FontSize=8,Foreground=Palette("MutedText"),VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,0,2,0) });
            var value=QuotaValue(snapshot.Provider,quota,dense?12:14);Grid.SetColumn(value,1);line.Children.Add(value);
            section.Children.Add(line);
            section.Children.Add(new ProgressBar { Value=DisplayPercent(snapshot.Provider,quota),Foreground=quota.RemainingPercent<=10?Palette("CriticalText"):Accent(snapshot.Provider),Height=2,Margin=new Thickness(0,dense?2:4,0,0) });
            System.Windows.Automation.AutomationProperties.SetName(section,$"{snapshot.Provider} {quota.Name} {DisplayBasis(snapshot.Provider)} {DisplayPercent(snapshot.Provider,quota):0.#}%");
            Grid.SetColumn(section,i);meters.Children.Add(section);
        }
        if(quotas.Length==0)
        {
            meters.Children.Add(new TextBlock { Text=snapshot.Status is UsageStatus.NotConnected or UsageStatus.AuthenticationRequired?"로그인 필요":StatusLabel(snapshot.Status),Foreground=Palette("SecondaryText"),FontSize=10,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right,TextTrimming=TextTrimming.CharacterEllipsis });
        }
        return new Border { Background=Palette("SurfaceBackground"),CornerRadius=new CornerRadius(6),Padding=new Thickness(7,dense?3:5,7,dense?3:5),Child=content };
    }
    private TextBlock QuotaValue(UsageProviderKind provider,UsageWindow quota,double size)
    {
        var value=new TextBlock { Text=$"{DisplayPercent(provider,quota):0.#}%",FontSize=size,FontWeight=FontWeights.SemiBold,Foreground=quota.RemainingPercent<=10?Palette("CriticalText"):Palette("PrimaryText"),TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center };
        Typography.SetNumeralAlignment(value,FontNumeralAlignment.Tabular);
        return value;
    }
    private static string ShortQuotaName(string name)=>name.Contains("주")?"주간":name.Contains("세션")||name.Contains("시간")?"세션":name.Replace("모델","").Trim();
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



