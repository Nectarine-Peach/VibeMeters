using System;
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
    private bool _initialized;
    private bool _changingMode;
    private double _normalWidth = 340, _normalHeight = 320;
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
        TaskbarCheck.IsChecked = settings.ShowInTaskbar;
        StartupCheck.IsChecked = settings.StartWithWindows;
        NotifyCheck.IsChecked = settings.NotifyLowQuota;
        foreach (ComboBoxItem item in IntervalCombo.Items)
        {
            if (int.TryParse(item.Tag?.ToString(), out var interval) && interval == settings.RefreshIntervalSeconds)
            {
                IntervalCombo.SelectedItem = item;
                break;
            }
        }
        if (settings.Left is double left && settings.Top is double top && IsVisiblePosition(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        UpdatePin();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.F5) { RefreshRequested?.Invoke(); args.Handled = true; }
            else if (args.Key == Key.M && Keyboard.Modifiers == ModifierKeys.Control) { SetCompactMode(!Settings.CompactMode); args.Handled = true; }
            else if (args.Key == Key.Escape) { if (SettingsPanel.Visibility == Visibility.Visible) ToggleSettings(false); else HideRequested?.Invoke(); args.Handled = true; }
        };
        LocationChanged += (_, _) =>
        {
            if (!_initialized || WindowState != WindowState.Normal) return;
            Settings.Left = Left;
            Settings.Top = Top;
        };
        _initialized = true;
        SizeChanged += (_, _) =>
        {
            if (!_initialized || WindowState != WindowState.Normal) return;
            if (!Settings.CompactMode && !_changingMode) { Settings.WindowWidth = _normalWidth = Width; Settings.WindowHeight = _normalHeight = Height; }
            UpdateLayoutMode();
        };
        if (settings.CompactMode) { Width = 240; Height = 148; }
        UpdateLayoutMode();
    }

    public void SetCompactMode(bool compact)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetCompactMode(compact)); return; }
        if (Settings.CompactMode == compact) return;
        if (compact) { _normalWidth = Width; _normalHeight = Height; }
        Settings.CompactMode = compact;
        _changingMode = true;
        Width = compact ? 240 : Math.Max(MinWidth, _normalWidth);
        Height = compact ? 148 : Math.Max(MinHeight, _normalHeight);
        _changingMode = false;
        UpdateLayoutMode();
        SettingsChanged?.Invoke();
    }

    private void UpdateLayoutMode()
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        IsCompactLayout = width < 280 || height < 220;
        var wide = width >= 520 && !IsCompactLayout;
        ProviderGrid.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(ClaudeCard, wide ? 1 : 0); Grid.SetRow(ClaudeCard, wide ? 0 : 1);
        CodexCard.Margin = wide ? new Thickness(0,0,6,0) : new Thickness(0,0,0,6);
        SubtitleText.Visibility = width >= 330 ? Visibility.Visible : Visibility.Collapsed;
        TitleText.FontSize = IsCompactLayout ? 11 : 13;
        MinimizeButton.Visibility = width >= 280 ? Visibility.Visible : Visibility.Collapsed;
        LastUpdatedText.Visibility = width >= 330 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.MaxWidth = Math.Max(55, width - (width >= 330 ? 145 : 90));
        MiniButton.Foreground = BrushFrom(Settings.CompactMode ? "#8BE2C3" : "#B8C0CF");
        var open = SettingsPanel.Visibility == Visibility.Visible;
        UsageScroll.Visibility = !open && !IsCompactLayout ? Visibility.Visible : Visibility.Collapsed;
        MicroPanel.Visibility = !open && IsCompactLayout ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsVisiblePosition(double left, double top) =>
        double.IsFinite(left) && double.IsFinite(top) &&
        left + 100 > SystemParameters.VirtualScreenLeft && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 &&
        top + 40 > SystemParameters.VirtualScreenTop && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;

    public void RenderSnapshot(UsageSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => RenderSnapshot(snapshot)); return; }
        var codex = snapshot.Provider == UsageProviderKind.Codex;
        var rows = codex ? CodexRows : ClaudeRows;
        var plan = codex ? CodexPlan : ClaudePlan;
        var message = codex ? CodexMessage : ClaudeMessage;
        var accent = (Brush)new BrushConverter().ConvertFromString(codex ? "#8BE2C3" : "#ECA98C")!;
        plan.Text = snapshot.Plan ?? StatusLabel(snapshot.Status);
        rows.Children.Clear();
        foreach (var window in snapshot.Windows)
        {
            var remaining = Math.Clamp(window.RemainingPercent, 0, 100);
            var section = new StackPanel { Margin = new Thickness(0, 0, 0, 5), ToolTip = WindowTooltip(window, snapshot) };
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            var label = new TextBlock { Text = window.Name, FontSize = 11, Foreground = BrushFrom("#B6BFCD"), VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock { Text = $"{remaining:0.#}%", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = accent, Margin = new Thickness(8,0,8,0) };
            value.ToolTip = $"사용 {window.UsedPercent:0.#}% · 남음 {remaining:0.#}%";
            Grid.SetColumn(value, 1);
            heading.Children.Add(label);
            heading.Children.Add(value);
            var reset = new TextBlock { Text = ResetShort(window.ResetsAt), FontSize = 10, Foreground = BrushFrom("#A0ABBC"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(reset,2); heading.Children.Add(reset);
            section.Children.Add(heading);
            section.Children.Add(new ProgressBar { Value = remaining, Foreground = remaining <= 10 ? BrushFrom("#F68181") : accent, Margin = new Thickness(0, 2, 0, 0), ToolTip = $"남은 사용량 {remaining:0.#}%" });
            rows.Children.Add(section);
        }
        var fallback = snapshot.Status switch
        {
            UsageStatus.NotConnected => codex ? "Codex에 로그인하면 자동으로 연결됩니다." : "Claude Code에 로그인하면 자동으로 연결됩니다.",
            UsageStatus.AuthenticationRequired => "로그인을 갱신한 뒤 새로고침해 주세요.",
            UsageStatus.RateLimited => "요청이 많아 잠시 후 다시 확인합니다.",
            UsageStatus.Unavailable => "사용량을 확인할 수 없습니다.",
            _ => snapshot.Windows.Count == 0 ? "표시할 사용량 정보가 없습니다." : ""
        };
        message.Text = snapshot.Message ?? fallback;
        message.Visibility = string.IsNullOrWhiteSpace(message.Text) ? Visibility.Collapsed : Visibility.Visible;
        message.ToolTip = message.Text;
        (codex ? CodexConnectButton : ClaudeConnectButton).Visibility = snapshot.Status is UsageStatus.NotConnected or UsageStatus.AuthenticationRequired ? Visibility.Visible : Visibility.Collapsed;
        LastUpdatedText.Text = snapshot.CheckedAt.ToLocalTime().ToString("HH:mm");
        LastUpdatedText.ToolTip = $"최근 확인 {snapshot.CheckedAt.ToLocalTime():M/d HH:mm:ss}";
        RenderMicro(snapshot, codex ? CodexMicro : ClaudeMicro, accent);
    }

    private void RenderMicro(UsageSnapshot snapshot, Grid grid, Brush accent)
    {
        grid.Children.Clear(); grid.ColumnDefinitions.Clear();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(49) });
        var windows = snapshot.Windows.Take(2).ToArray();
        var stale = snapshot.Status != UsageStatus.Ready;
        var providerLabel = new TextBlock { Text = stale ? $"! {snapshot.Provider}" : snapshot.Provider.ToString(), FontSize = stale ? 10 : 11, FontWeight = FontWeights.SemiBold, Foreground = stale ? BrushFrom("#E4BD79") : accent, VerticalAlignment = VerticalAlignment.Center, ToolTip = $"{snapshot.Plan} · {StatusLabel(snapshot.Status)}\n{snapshot.Message}" };
        if (snapshot.Status is UsageStatus.AuthenticationRequired or UsageStatus.NotConnected)
        {
            var accountButton = new Button { Content = providerLabel, Padding = new Thickness(0), ToolTip = $"{snapshot.Provider} 로그인 · {snapshot.Message}" };
            accountButton.Click += (_, _) => LoginRequested?.Invoke(snapshot.Provider);
            System.Windows.Automation.AutomationProperties.SetName(accountButton, $"{snapshot.Provider} 로그인");
            grid.Children.Add(accountButton);
        }
        else grid.Children.Add(providerLabel);
        if (windows.Length == 0)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var button = new Button { Content = StatusLabel(snapshot.Status), Padding = new Thickness(2), FontSize = 10, ToolTip = snapshot.Message ?? "로그인하여 연결" };
            button.Click += (_, _) => { if (snapshot.Status is UsageStatus.NotConnected or UsageStatus.AuthenticationRequired) LoginRequested?.Invoke(snapshot.Provider); else RefreshRequested?.Invoke(); };
            Grid.SetColumn(button,1); grid.Children.Add(button);
        }
        for (int i=0; i<windows.Length; i++)
        {
            var window = windows[i];
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var section = new StackPanel { Margin = new Thickness(5,0,0,0), ToolTip = WindowTooltip(window, snapshot), Opacity = stale ? 0.6 : 1 };
            var row = new DockPanel();
            row.Children.Add(new TextBlock { Text = $"{window.RemainingPercent:0.#}%", FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = window.RemainingPercent <= 10 ? BrushFrom("#F69898") : accent });
            row.Children.Add(new TextBlock { Text = window.Name.Contains("주") ? "주간" : window.Name.Contains("세션") || window.Name.Contains("시간") ? "세션" : window.Name, FontSize = 9, Foreground = BrushFrom("#A0ABBC"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis });
            section.Children.Add(row); section.Children.Add(new ProgressBar { Value = window.RemainingPercent, Foreground = accent, Margin = new Thickness(0,2,0,0) });
            System.Windows.Automation.AutomationProperties.SetName(section, $"{snapshot.Provider} {window.Name} 남음 {window.RemainingPercent:0.#}%");
            Grid.SetColumn(section,i+1); grid.Children.Add(section);
        }
        grid.ToolTip = $"{StatusLabel(snapshot.Status)} · 최근 확인 {snapshot.CheckedAt.ToLocalTime():HH:mm:ss}\n{snapshot.Message}";
    }
    private static string WindowTooltip(UsageWindow window, UsageSnapshot snapshot) => $"{window.Name} · 남음 {window.RemainingPercent:0.#}% / 사용 {window.UsedPercent:0.#}%\n{ResetLabel(window.ResetsAt)}\n{StatusLabel(snapshot.Status)} · 확인 {snapshot.CheckedAt.ToLocalTime():HH:mm:ss}\n{snapshot.Message}";
    public void SetLoginStatus(UsageProviderKind kind, bool busy, string? message = null)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetLoginStatus(kind,busy,message)); return; }
        (kind == UsageProviderKind.Codex ? CodexLoginButton : ClaudeLoginButton).IsEnabled = !busy;
        (kind == UsageProviderKind.Codex ? CodexConnectButton : ClaudeConnectButton).IsEnabled = !busy;
        LoginStatusText.Text = message ?? (busy ? $"{kind} 로그인 진행 중…" : "로그인 후 사용량을 자동으로 다시 확인합니다.");
        UpdateStatus(LoginStatusText.Text);
    }
    private static string ResetShort(DateTimeOffset? resets)
    {
        if (resets is null) return "—";
        var delta = resets.Value - DateTimeOffset.Now;
        return delta <= TimeSpan.Zero ? "초기화 대기" : delta.TotalDays >= 1 ? $"{(int)delta.TotalDays}일 {delta.Hours}시간 ↻" : delta.TotalHours >= 1 ? $"{(int)delta.TotalHours}시간 {delta.Minutes}분 ↻" : $"{Math.Max(1,(int)delta.TotalMinutes)}분 ↻";
    }

    public void SetRefreshing(bool refreshing)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetRefreshing(refreshing)); return; }
        RefreshButton.IsEnabled = !refreshing;
        RefreshButton.Opacity = refreshing ? 0.4 : 1;
        if (refreshing) StatusText.Text = "사용량 확인 중…";
    }

    public void UpdateStatus(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => UpdateStatus(text)); return; }
        StatusText.Text = text;
        StatusText.ToolTip = text;
    }

    private static Brush BrushFrom(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
    private static string StatusLabel(UsageStatus status) => status switch
    {
        UsageStatus.Ready => "연결됨", UsageStatus.NotConnected => "연결 필요",
        UsageStatus.AuthenticationRequired => "로그인 필요", UsageStatus.RateLimited => "잠시 대기", _ => "확인 불가"
    };
    private static string ResetLabel(DateTimeOffset? resets)
    {
        if (resets is null) return "초기화 시간 정보 없음";
        var local = resets.Value.ToLocalTime();
        var remaining = resets.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "한도 초기화 대기 중";
        var duration = remaining.TotalDays >= 1 ? $"{(int)remaining.TotalDays}일 {remaining.Hours}시간 후" : remaining.TotalHours >= 1 ? $"{(int)remaining.TotalHours}시간 {remaining.Minutes}분 후" : $"{Math.Max(1, (int)remaining.TotalMinutes)}분 후";
        return $"{duration} 초기화 · {local.ToString("M/d HH:mm", CultureInfo.InvariantCulture)}";
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_initialized && e.Property == TopmostProperty)
        {
            Settings.AlwaysOnTop = Topmost;
            UpdatePin();
        }
    }

    private void CodexConnect_Click(object sender, RoutedEventArgs e) => LoginRequested?.Invoke(UsageProviderKind.Codex);
    private void ClaudeConnect_Click(object sender, RoutedEventArgs e) => LoginRequested?.Invoke(UsageProviderKind.Claude);
    private void CodexDashboard_Click(object sender, RoutedEventArgs e) => OpenDashboard("https://chatgpt.com/codex/settings/usage");
    private void ClaudeDashboard_Click(object sender, RoutedEventArgs e) => OpenDashboard("https://claude.ai/settings/usage");
    private void OpenDashboard(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { UpdateStatus("브라우저를 열 수 없습니다."); }
    }

    private void UpdatePin()
    {
        PinButton.Foreground = BrushFrom(Topmost ? "#8BE2C3" : "#B8C0CF");
        PinButton.ToolTip = Topmost ? "항상 위에 표시: 켜짐" : "항상 위에 표시: 꺼짐";
        System.Windows.Automation.AutomationProperties.SetName(PinButton, PinButton.ToolTip.ToString());
    }
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            for (var current = source; current != null; current = VisualTreeHelper.GetParent(current))
                if (current is Button) return;
        }
        if (e.ClickCount == 2) { SetCompactMode(!Settings.CompactMode); return; }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Mini_Click(object sender, RoutedEventArgs e) => SetCompactMode(!Settings.CompactMode);
    private void Pin_Click(object sender, RoutedEventArgs e) { Topmost = !Topmost; Settings.AlwaysOnTop = Topmost; UpdatePin(); SettingsChanged?.Invoke(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) { if (ShowInTaskbar) WindowState = WindowState.Minimized; else HideRequested?.Invoke(); }
    private void Hide_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ToggleSettings(SettingsPanel.Visibility != Visibility.Visible);
    }
    private void ToggleSettings(bool open)
    {
        SettingsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.Background = open ? BrushFrom("#28303E") : Brushes.Transparent;
        UpdateLayoutMode();
    }
    private void Interval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || IntervalCombo.SelectedItem is not ComboBoxItem item) return;
        Settings.RefreshIntervalSeconds = int.Parse(item.Tag.ToString()!, CultureInfo.InvariantCulture);
        SettingsChanged?.Invoke();
    }
    private void Taskbar_Click(object sender, RoutedEventArgs e) { Settings.ShowInTaskbar = TaskbarCheck.IsChecked == true; ShowInTaskbar = Settings.ShowInTaskbar; SettingsChanged?.Invoke(); }
    private void Startup_Click(object sender, RoutedEventArgs e) { Settings.StartWithWindows = StartupCheck.IsChecked == true; SettingsChanged?.Invoke(); }
    private void Notify_Click(object sender, RoutedEventArgs e) { Settings.NotifyLowQuota = NotifyCheck.IsChecked == true; SettingsChanged?.Invoke(); }
}


