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
    public AppSettings Settings { get; }
    public event Action? RefreshRequested;
    public event Action? SettingsChanged;
    public event Action? HideRequested;

    public MainWindow(AppSettings settings)
    {
        Settings = settings;
        InitializeComponent();
        Topmost = settings.AlwaysOnTop;
        ShowInTaskbar = settings.ShowInTaskbar;
        TaskbarCheck.IsChecked = settings.ShowInTaskbar;
        StartupCheck.IsChecked = settings.StartWithWindows;
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
            else if (args.Key == Key.Escape) { HideRequested?.Invoke(); args.Handled = true; }
        };
        LocationChanged += (_, _) =>
        {
            if (!_initialized || WindowState != WindowState.Normal) return;
            Settings.Left = Left;
            Settings.Top = Top;
        };
        _initialized = true;
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
            var section = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = window.Name, FontSize = 11, Foreground = BrushFrom("#B6BFCD"), VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock { Text = $"{remaining:0.#}%", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = accent };
            value.ToolTip = $"사용 {window.UsedPercent:0.#}% · 남음 {remaining:0.#}%";
            Grid.SetColumn(value, 1);
            heading.Children.Add(label);
            heading.Children.Add(value);
            section.Children.Add(heading);
            section.Children.Add(new ProgressBar { Value = remaining, Foreground = remaining <= 10 ? BrushFrom("#F68181") : accent, Margin = new Thickness(0, 3, 0, 4), ToolTip = $"남은 사용량 {remaining:0.#}%" });
            section.Children.Add(new TextBlock { Text = ResetLabel(window.ResetsAt), Foreground = BrushFrom("#8B95A6"), FontSize = 10 });
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
        (codex ? CodexConnectButton : ClaudeConnectButton).Visibility = snapshot.Status == UsageStatus.Ready ? Visibility.Collapsed : Visibility.Visible;
        LastUpdatedText.Text = $"최근 확인 {snapshot.CheckedAt.ToLocalTime():HH:mm:ss}";
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

    private void CodexConnect_Click(object sender, RoutedEventArgs e) => OpenDashboard("https://chatgpt.com/codex/settings/usage");
    private void ClaudeConnect_Click(object sender, RoutedEventArgs e) => OpenDashboard("https://claude.ai/settings/usage");
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
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Pin_Click(object sender, RoutedEventArgs e) { Topmost = !Topmost; Settings.AlwaysOnTop = Topmost; UpdatePin(); SettingsChanged?.Invoke(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) { if (ShowInTaskbar) WindowState = WindowState.Minimized; else HideRequested?.Invoke(); }
    private void Hide_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var open = SettingsPanel.Visibility != Visibility.Visible;
        SettingsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        Height = open ? 714 : 536;
        SettingsButton.Background = open ? BrushFrom("#28303E") : Brushes.Transparent;
    }
    private void Interval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || IntervalCombo.SelectedItem is not ComboBoxItem item) return;
        Settings.RefreshIntervalSeconds = int.Parse(item.Tag.ToString()!, CultureInfo.InvariantCulture);
        SettingsChanged?.Invoke();
    }
    private void Taskbar_Click(object sender, RoutedEventArgs e) { Settings.ShowInTaskbar = TaskbarCheck.IsChecked == true; ShowInTaskbar = Settings.ShowInTaskbar; SettingsChanged?.Invoke(); }
    private void Startup_Click(object sender, RoutedEventArgs e) { Settings.StartWithWindows = StartupCheck.IsChecked == true; SettingsChanged?.Invoke(); }
}


