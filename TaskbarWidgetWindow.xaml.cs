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
        var visible = windows.Take(2).ToArray();
        bool stacked = _vertical && _maxWidth < 95;
        DockPanel.SetDock(Brand, stacked ? Dock.Top : Dock.Left);
        Brand.Orientation = stacked ? Orientation.Horizontal : Orientation.Vertical;
        Brand.Margin = stacked ? new Thickness(0,0,0,3) : new Thickness(0,0,6,0);
        Logo.Width = Logo.Height = stacked ? 16 : 19;
        int extra = windows.Length - visible.Length;
        Overflow.Text = extra > 0 ? $"+{extra}" : "";
        Overflow.Visibility = extra > 0 ? Visibility.Visible : Visibility.Collapsed;
        Overflow.Margin = stacked ? new Thickness(2,0,0,0) : new Thickness(0);

        var names = visible.Select(w => TaskbarProviderPresentation.WindowName(w.Name)).ToArray();
        double labelWidth = Math.Clamp(names.Select(n => n.Length * 6d + 3).DefaultIfEmpty(16).Max(), 16, 65);
        bool barBelow = stacked || _maxWidth < 97;
        double rowWidth = barBelow ? Math.Clamp(_maxWidth - (stacked ? 6 : 35), 46, 96) : labelWidth + 70;
        for (int i = 0; i < visible.Length; i++)
        {
            var row = QuotaRow(snapshot.Provider, visible[i], names[i], rowWidth, labelWidth, barBelow);
            AutomationProperties.SetAutomationId(row, $"WidgetQuota{i}");
            QuotaRows.Children.Add(row);
        }
        if (visible.Length == 0)
        {
            var unknown = new TextBlock { Text = "—", FontSize = 14, MinWidth = 24, TextAlignment = TextAlignment.Center };
            unknown.SetResourceReference(TextBlock.ForegroundProperty, "MutedText");
            QuotaRows.Children.Add(unknown);
        }
        Card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var padding = stacked ? 6 : 12;
        Width = Math.Min(_maxWidth, Math.Ceiling(Card.DesiredSize.Width) + padding);
        Height = Math.Min(_maxHeight, Math.Max(stacked ? 24 : 32, Math.Ceiling(Card.DesiredSize.Height) + 6));
        var details = Details(snapshot, basis);
        AutomationProperties.SetName(ProviderButton, $"{snapshot.Provider} · {basis}");
        AutomationProperties.SetHelpText(ProviderButton, details);
        ProviderButton.ToolTip = new ToolTip { Content = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap } };
    }

    private Grid QuotaRow(UsageProviderKind kind, UsageWindow window, string label, double width, double labelWidth, bool barBelow)
    {
        var row = new Grid { Width = width, Height = barBelow ? 18 : 15 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = barBelow ? new GridLength(1, GridUnitType.Star) : new GridLength(labelWidth) });
        if (!barBelow) row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        if (barBelow)
        {
            row.RowDefinitions.Add(new RowDefinition());
            row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
        }

        var name = new TextBlock { Text = label, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        row.Children.Add(name);
        double amount = _mode == UsageDisplayMode.Used ? Math.Clamp(window.UsedPercent, 0, 100) : window.RemainingPercent;
        var meter = new ProgressBar { Minimum = 0, Maximum = 100, Value = amount, VerticalAlignment = VerticalAlignment.Center,
            Margin = barBelow ? new Thickness(0,0,0,1) : new Thickness(3,0,5,0) };
        meter.SetResourceReference(ProgressBar.ForegroundProperty, window.RemainingPercent <= 10 ? "CriticalText" : kind + "Accent");
        if (barBelow) { Grid.SetRow(meter,1); Grid.SetColumnSpan(meter,2); }
        else Grid.SetColumn(meter,1);
        row.Children.Add(meter);
        var value = new TextBlock { Text = $"{amount.ToString("0", CultureInfo.InvariantCulture)}%", FontSize = 11,
            FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        value.SetResourceReference(TextBlock.ForegroundProperty, window.RemainingPercent <= 10 ? "CriticalText" : "PrimaryText");
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        Grid.SetColumn(value,row.ColumnDefinitions.Count - 1);
        row.Children.Add(value);
        return row;
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
