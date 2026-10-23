using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
        var needed = providers.Sum(kind => 42 + Math.Max(1, Snapshot(kind).Windows.Count) * 29 + (Snapshot(kind).Status == UsageStatus.Ready ? 0 : 36));
        if (wide) needed = providers.Max(kind => 42 + Math.Max(1, Snapshot(kind).Windows.Count) * 29 + (Snapshot(kind).Status == UsageStatus.Ready ? 0 : 36));
        IsCompactLayout = width < 280 || height - 45 < needed;
        SubtitleText.Visibility = width >= 330 ? Visibility.Visible : Visibility.Collapsed;
        TitleText.FontSize = IsCompactLayout ? 11 : 13;
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
            else host.RowDefinitions.Add(new RowDefinition { Height = IsCompactLayout ? new GridLength(1,GridUnitType.Star) : new GridLength(42 + Math.Max(1,snapshot.Windows.Count)*29 + (snapshot.Status==UsageStatus.Ready?0:36),GridUnitType.Star) });
            var element = IsCompactLayout ? RenderMicro(snapshot) : RenderCard(snapshot);
            if (wide && !IsCompactLayout) Grid.SetColumn(element,i); else Grid.SetRow(element,i);
            host.Children.Add(element);
        }
    }
    private static void ClearGrid(Grid grid) { grid.Children.Clear(); grid.RowDefinitions.Clear(); grid.ColumnDefinitions.Clear(); }
    private UsageSnapshot Snapshot(UsageProviderKind kind) => _snapshots.TryGetValue(kind, out var snapshot) ? snapshot : new(kind, UsageStatus.NotConnected, Array.Empty<UsageWindow>(), DateTimeOffset.Now, Message:"계정을 연결하면 사용량이 표시됩니다.");
    private static Brush Accent(UsageProviderKind kind) => Palette(kind switch { UsageProviderKind.Codex => "CodexAccent", UsageProviderKind.Claude => "ClaudeAccent", _ => "GeminiAccent" });
    private string DisplayBasis(UsageProviderKind kind) => Settings.GetDisplayMode(kind)==UsageDisplayMode.Used ? "사용" : "남음";
    private double DisplayPercent(UsageProviderKind kind,UsageWindow quota) => Settings.GetDisplayMode(kind)==UsageDisplayMode.Used ? Math.Clamp(quota.UsedPercent,0,100) : quota.RemainingPercent;

    private FrameworkElement RenderCard(UsageSnapshot snapshot)
    {
        var accent = Accent(snapshot.Provider);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var heading = new DockPanel { Margin = new Thickness(0,0,0,7) };
        heading.Children.Add(new TextBlock { Text=$"{snapshot.Provider} · {DisplayBasis(snapshot.Provider)}", FontSize=12, Foreground=accent, FontWeight=FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text=snapshot.Plan ?? StatusLabel(snapshot.Status), Foreground=Palette("SecondaryText"), FontSize=10, HorizontalAlignment=HorizontalAlignment.Right, TextTrimming=TextTrimming.CharacterEllipsis });
        stack.Children.Add(heading);
        foreach (var quota in snapshot.Windows)
        {
            var section = new StackPanel { Margin = new Thickness(0,0,0,5), ToolTip=WindowTooltip(quota,snapshot), Opacity=snapshot.Status == UsageStatus.Ready ? 1 : 0.65 };
            var line = new Grid(); line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto }); line.ColumnDefinitions.Add(new ColumnDefinition());
            line.Children.Add(new TextBlock { Text=quota.Name, Foreground=Palette("SecondaryText"), FontSize=10, VerticalAlignment=VerticalAlignment.Center, TextTrimming=TextTrimming.CharacterEllipsis });
            var value = new TextBlock { Text=$"{DisplayPercent(snapshot.Provider,quota):0.#}%", Foreground=quota.RemainingPercent<=10 ? Palette("CriticalText") : accent, FontSize=16, FontWeight=FontWeights.SemiBold, Margin=new Thickness(7,0,7,0) };
            Grid.SetColumn(value,1); line.Children.Add(value);
            var reset = new TextBlock { Text=ResetShort(quota.ResetsAt), Foreground=Palette("SecondaryText"), FontSize=10, HorizontalAlignment=HorizontalAlignment.Right, VerticalAlignment=VerticalAlignment.Center, TextTrimming=TextTrimming.CharacterEllipsis };
            Grid.SetColumn(reset,2); line.Children.Add(reset);
            section.Children.Add(line); section.Children.Add(new ProgressBar { Value=DisplayPercent(snapshot.Provider,quota), Foreground=quota.RemainingPercent<=10 ? Palette("CriticalText") : accent, Margin=new Thickness(0,2,0,0) }); stack.Children.Add(section);
        }
        if (snapshot.Status != UsageStatus.Ready || snapshot.Windows.Count==0)
        {
            var message = snapshot.Message ?? StatusLabel(snapshot.Status);
            stack.Children.Add(new TextBlock { Text=message, ToolTip=message, FontSize=10, Foreground=Palette("WarningText"), TextTrimming=TextTrimming.CharacterEllipsis, Margin=new Thickness(0,3,0,0) });
            if (snapshot.Status is UsageStatus.NotConnected or UsageStatus.AuthenticationRequired)
            {
                var login = new Button { Content="계정 연결 ↗", FontSize=10, Padding=new Thickness(0,3,0,0), HorizontalAlignment=HorizontalAlignment.Left };
                login.Click += (_,_) => LoginRequested?.Invoke(snapshot.Provider); stack.Children.Add(login);
            }
        }
        return new Border { Background=Palette(snapshot.Provider switch { UsageProviderKind.Codex=>"CodexSurface", UsageProviderKind.Claude=>"ClaudeSurface", _=>"GeminiSurface" }), CornerRadius=new CornerRadius(7), Padding=new Thickness(10,7,10,7), Margin=new Thickness(0,0,5,5), Child=stack };
    }
    private FrameworkElement RenderMicro(UsageSnapshot snapshot)
    {
        var grid = new Grid { VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(1,0,1,0), ToolTip=$"{StatusLabel(snapshot.Status)} · {snapshot.Message}" };
        var accent=Accent(snapshot.Provider); var stale=snapshot.Status!=UsageStatus.Ready;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(51) });
        var provider=new Button { Content=(stale?"! ":"")+snapshot.Provider, Foreground=stale?Palette("WarningText"):accent, Padding=new Thickness(0), FontSize=10, HorizontalContentAlignment=HorizontalAlignment.Left, ToolTip=$"{snapshot.Provider} · {StatusLabel(snapshot.Status)}\n{snapshot.Message}" };
        var providerLabel=new StackPanel();
        providerLabel.Children.Add(new TextBlock { Text=(stale?"! ":"")+snapshot.Provider,FontSize=10 });
        providerLabel.Children.Add(new TextBlock { Text=DisplayBasis(snapshot.Provider),FontSize=8,Foreground=Palette("SecondaryText") });
        provider.Content=providerLabel;
        provider.Click += (_,_) => { if(snapshot.Status is UsageStatus.NotConnected or UsageStatus.AuthenticationRequired) LoginRequested?.Invoke(snapshot.Provider); else Settings_Click(this,new RoutedEventArgs()); };
        grid.Children.Add(provider);
        var quotas=snapshot.Windows.Take(2).ToArray();
        if (quotas.Length==0)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var message=new TextBlock { Text=StatusLabel(snapshot.Status), FontSize=10, Foreground=Palette("SecondaryText"), VerticalAlignment=VerticalAlignment.Center, TextTrimming=TextTrimming.CharacterEllipsis };
            Grid.SetColumn(message,1); grid.Children.Add(message);
        }
        for(var i=0;i<quotas.Length;i++)
        {
            var quota=quotas[i]; grid.ColumnDefinitions.Add(new ColumnDefinition());
            var section=new StackPanel { Margin=new Thickness(5,0,0,0), ToolTip=WindowTooltip(quota,snapshot), Opacity=stale?0.65:1 };
            var line=new DockPanel();
            line.Children.Add(new TextBlock { Text=$"{DisplayPercent(snapshot.Provider,quota):0.#}%", FontSize=12, FontWeight=FontWeights.SemiBold, Foreground=quota.RemainingPercent<=10?Palette("CriticalText"):accent });
            line.Children.Add(new TextBlock { Text=quota.Name.Contains("주")?"주간":quota.Name.Contains("세션")||quota.Name.Contains("시간")?"세션":quota.Name, FontSize=9, Foreground=Palette("SecondaryText"), HorizontalAlignment=HorizontalAlignment.Right, VerticalAlignment=VerticalAlignment.Center, TextTrimming=TextTrimming.CharacterEllipsis });
            section.Children.Add(line); section.Children.Add(new ProgressBar { Value=DisplayPercent(snapshot.Provider,quota), Foreground=quota.RemainingPercent<=10?Palette("CriticalText"):accent, Margin=new Thickness(0,1,0,0) });
            System.Windows.Automation.AutomationProperties.SetName(section,$"{snapshot.Provider} {quota.Name} {DisplayBasis(snapshot.Provider)} {DisplayPercent(snapshot.Provider,quota):0.#}%");
            Grid.SetColumn(section,i+1); grid.Children.Add(section);
        }
        grid.ToolTip=snapshot.Windows.Count==0 ? snapshot.Message : string.Join("\n",snapshot.Windows.Select(quota=>WindowTooltip(quota,snapshot)));
        return grid;
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
        TitleText.ToolTip=text;
    }
    private static Brush Palette(string key) => (Brush)Application.Current.FindResource(key);
    private static string StatusLabel(UsageStatus status)=>status switch { UsageStatus.Ready=>"연결됨",UsageStatus.NotConnected=>"연결 필요",UsageStatus.AuthenticationRequired=>"로그인 필요",UsageStatus.RateLimited=>"잠시 대기",_=>"확인 불가" };
    private static string ResetShort(DateTimeOffset? resets)
    {
        if(resets is null) return "—";
        var delta=resets.Value-DateTimeOffset.Now;
        return delta<=TimeSpan.Zero?"초기화 대기":delta.TotalDays>=1?$"{(int)delta.TotalDays}일 {delta.Hours}시간 ↻":delta.TotalHours>=1?$"{(int)delta.TotalHours}시간 {delta.Minutes}분 ↻":$"{Math.Max(1,(int)delta.TotalMinutes)}분 ↻";
    }
    private static string WindowTooltip(UsageWindow quota,UsageSnapshot snapshot)=>$"{quota.Name} · 남음 {quota.RemainingPercent:0.#}% / 사용 {quota.UsedPercent:0.#}%\n{ResetShort(quota.ResetsAt)} · 초기화 {quota.ResetsAt?.ToLocalTime().ToString("M/d HH:mm",CultureInfo.InvariantCulture)??"정보 없음"}\n{StatusLabel(snapshot.Status)}\n{snapshot.Message}";
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


