using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using VibeMeters.Models;
using VibeMeters.Services;

namespace VibeMeters;

public partial class TaskbarWidgetWindow : Window
{
    private readonly Action _openMain;
    private readonly List<(TextBlock Label, DateTimeOffset? Deadline)> _countdowns = [];
    private UsageSnapshot? _snapshot;
    private UsageDisplayMode _mode;
    private bool _vertical;
    private double _maxWidth = 220, _maxHeight = 46;

    public TaskbarWidgetWindow(Action openMain)
    {
        _openMain = openMain;
        InitializeComponent();
        Width = 1;
        Height = 1;
    }

    public void Update(IEnumerable<UsageSnapshot> selected, AppSettings settings)
    {
        _snapshot = selected.SingleOrDefault();
        if (_snapshot != null) _mode = settings.GetDisplayMode(_snapshot.Provider);
        Render();
    }

    public void SetLayout(bool vertical, double maxWidth, double maxHeight)
    {
        maxWidth = Math.Max(1, maxWidth);
        maxHeight = Math.Max(1, maxHeight);
        if (_vertical == vertical && _maxWidth == maxWidth && _maxHeight == maxHeight) return;
        _vertical = vertical;
        _maxWidth = maxWidth;
        _maxHeight = maxHeight;
        Render();
    }

    private void Render()
    {
        var snapshot = _snapshot;
        QuotaRows.Children.Clear();
        _countdowns.Clear();
        if (snapshot == null)
        {
            ProviderButton.Visibility = Visibility.Hidden;
            Width = Height = 1;
            return;
        }
        ProviderButton.Visibility = Visibility.Visible;
        Logo.Source = TaskbarProviderPresentation.Icon(snapshot.Provider);
        AutomationProperties.SetAutomationId(ProviderButton, $"Widget{snapshot.Provider}");
        string basis = Localizer.T(_mode == UsageDisplayMode.Used ? "사용" : "남음");
        Title = $"{snapshot.Provider} · {basis} · VibeMeters";

        var windows = snapshot.Status == UsageStatus.Ready
            ? snapshot.Windows.Where(w => double.IsFinite(w.UsedPercent)).ToArray()
            : [];
        var visible = windows;
        // Size the tile from the host's available space. Every provider shares the same
        // frame and columns, even when a lookup fails or the number of windows changes.
        bool stacked = _vertical && _maxWidth < 95;
        double tileWidth = stacked ? Math.Clamp(_maxWidth, 48, 80) : Math.Clamp(_maxWidth, 88, 176);
        bool compact = !stacked && tileWidth < 150;
        double rowHeight = stacked ? 45 : compact ? 36 : 15;
        double tileHeight = (stacked ? 26 : 6) + Math.Max(stacked ? 0 : 19, Math.Max(1, visible.Length) * rowHeight);
        ContentFrame.Width = tileWidth - 2;
        ContentFrame.Height = tileHeight - 2;
        ContentFrame.Padding = stacked ? new Thickness(3,2,3,2) : new Thickness(5,2,5,2);
        BrandColumn.Width = new GridLength(stacked ? 0 : 22);
        BrandRow.Height = new GridLength(stacked ? 20 : 0);
        Grid.SetColumn(Brand, stacked ? 1 : 0);
        Grid.SetRow(Brand, stacked ? 0 : 1);
        QuotaRows.Margin = new Thickness(stacked ? 0 : 5,0,0,0);
        Logo.Width = Logo.Height = stacked ? 16 : 19;
        Logo.VerticalAlignment = VerticalAlignment.Center;
        Logo.Margin = new Thickness(0);
        for (int i = 0; i < visible.Length; i++)
        {
            var row = QuotaRow(snapshot.Provider, visible[i], tileWidth, stacked, compact);
            AutomationProperties.SetAutomationId(row, $"WidgetQuota{i}");
            QuotaRows.Children.Add(row);
        }
        if (visible.Length == 0)
        {
            var unknown = new TextBlock { Text = "—", FontSize = 14, Margin = new Thickness(0,0,stacked || compact ? 0 : 29,0), TextAlignment = TextAlignment.Right };
            unknown.SetResourceReference(TextBlock.ForegroundProperty, "MutedText");
            QuotaRows.Children.Add(unknown);
        }
        Width = Math.Min(_maxWidth, tileWidth);
        Height = Math.Min(_maxHeight, tileHeight);
        var details = Details(snapshot, basis);
        AutomationProperties.SetName(ProviderButton, $"{snapshot.Provider} · {basis}");
        AutomationProperties.SetHelpText(ProviderButton, details);
        ProviderButton.ToolTip = new ToolTip { Content = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap } };
    }

    private Grid QuotaRow(UsageProviderKind kind, UsageWindow window, double tileWidth, bool stacked, bool compact)
    {
        var row = new Grid { Height = stacked ? 45 : compact ? 36 : 15 };
        if (!compact && !stacked)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Min(30, tileWidth - 92)) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(stacked ? 25 : 30) });
        if (stacked || compact)
        {
            row.RowDefinitions.Add(new RowDefinition());
            row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(stacked ? 16 : 5) });
        }

        if (stacked || compact)
            row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
        else
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(29) });

        var label = TaskbarProviderPresentation.WindowName(window.Name);
        if (label == "5H") label = "H";
        if (kind == UsageProviderKind.Gemini && label.Contains("Argon", StringComparison.OrdinalIgnoreCase)) label = "Argon";
        var name = new TextBlock { Text = label, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        if (stacked) Grid.SetColumnSpan(name, 2);
        row.Children.Add(name);
        double amount = _mode == UsageDisplayMode.Used ? Math.Clamp(window.UsedPercent, 0, 100) : window.RemainingPercent;
        var meter = new ProgressBar { Minimum = 0, Maximum = 100, Value = amount, VerticalAlignment = VerticalAlignment.Center,
            Margin = compact ? new Thickness(0,0,0,1) : new Thickness(stacked ? 0 : 2,0,3,0) };
        meter.SetResourceReference(ProgressBar.ForegroundProperty, window.RemainingPercent <= 10 ? "CriticalText" : kind + "Accent");
        if (stacked || compact) { Grid.SetRow(meter,1); if (compact) Grid.SetColumnSpan(meter,2); }
        else Grid.SetColumn(meter,1);
        row.Children.Add(meter);
        var value = new TextBlock { Text = $"{amount.ToString("0", CultureInfo.InvariantCulture)}%", FontSize = 11,
            FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        value.SetResourceReference(TextBlock.ForegroundProperty, window.RemainingPercent <= 10 ? "CriticalText" : "PrimaryText");
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        Grid.SetColumn(value, stacked || compact ? 1 : 2);
        if (stacked) Grid.SetRow(value, 1);
        row.Children.Add(value);
        var reset = new TextBlock { FontSize = 10, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Text = UsageResetText.Compact(window.ResetsAt, DateTimeOffset.Now) };
        reset.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        Typography.SetNumeralAlignment(reset, FontNumeralAlignment.Tabular);
        AutomationProperties.SetAutomationId(reset, "WidgetReset");
        AutomationProperties.SetName(reset, UsageResetText.Countdown(window.ResetsAt, DateTimeOffset.Now));
        if (stacked || compact)
        {
            Grid.SetRow(reset, 2);
            Grid.SetColumnSpan(reset, 2);
        }
        else Grid.SetColumn(reset, 3);
        row.Children.Add(reset);
        _countdowns.Add((reset, window.ResetsAt));
        return row;
    }

    internal void RefreshCountdowns(DateTimeOffset now)
    {
        foreach (var (label, deadline) in _countdowns)
        {
            var duration = UsageResetText.Compact(deadline, now);
            if (label.Text == duration) continue;
            label.Text = duration;
            AutomationProperties.SetName(label, UsageResetText.Countdown(deadline, now));
        }
    }

    private string Details(UsageSnapshot snapshot, string basis)
    {
        var lines = new List<string> { $"{snapshot.Provider} · {basis}" + (string.IsNullOrWhiteSpace(snapshot.Plan) ? "" : " · " + snapshot.Plan) };
        if (snapshot.Status == UsageStatus.Ready)
        {
            foreach (var w in snapshot.Windows.Where(w => double.IsFinite(w.UsedPercent)))
            {
                var value = _mode == UsageDisplayMode.Used ? Math.Clamp(w.UsedPercent, 0, 100) : w.RemainingPercent;
                var when = UsageResetText.Countdown(w.ResetsAt, DateTimeOffset.Now);
                if (w.ResetsAt is { } reset) when += $" ({reset.ToLocalTime().ToString("g", Localizer.Culture)})";
                lines.Add($"{Localizer.T(w.Name)} · {value.ToString("0.#", Localizer.Culture)}% · {when}");
            }
            if (lines.Count == 1) lines.Add(Localizer.T("연결됨 · 비율 미제공"));
        }
        else
        {
            lines.Add(Localizer.T(snapshot.Status switch {
                UsageStatus.AuthenticationRequired => "로그인 필요", UsageStatus.NotConnected => "연결 필요",
                UsageStatus.AuthenticationRetry => "인증 재시도 대기", UsageStatus.AuthenticationRecovering => "인증 갱신 확인 중",
                UsageStatus.RateLimited => "요청 제한 대기", _ => "확인 불가" }));
        }
        if (!string.IsNullOrWhiteSpace(snapshot.Message)) lines.Add(Localizer.T(snapshot.Message));
        lines.Add(UsageRefreshText.Summary(snapshot));
        return string.Join("\n", lines);
    }

    private void RefreshToolTip(object sender, ToolTipEventArgs args)
    {
        if (_snapshot is { } snapshot && ProviderButton.ToolTip is ToolTip { Content: TextBlock text })
            text.Text = Details(snapshot, Localizer.T(_mode == UsageDisplayMode.Used ? "사용" : "남음"));
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e) => _openMain();
}
