using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using VibeMeters.Models;
using VibeMeters.Services;

namespace VibeMeters;

public partial class TaskbarWidgetWindow : Window
{
    internal const double MinimumReadableWidth = 132;
    /// <summary>Slim vertical taskbars (Windows 10 defaults to about 120 DIP) stack the logo above the quota rows.</summary>
    internal const double NarrowMinimumWidth = 96;
    internal const double StackedBrandHeight = 15;
    internal const double MinimumHeight = 21;
    internal const double DefaultHeight = 46;
    private readonly Action _openMain;
    private readonly ToolTip _quotaTip = new();
    private readonly List<(TextBlock Label, DateTimeOffset? Deadline)> _tooltipCountdowns = [];
    private readonly List<(TextBlock Label, DateTimeOffset? Deadline)> _countdowns = [];
    private UsageSnapshot? _snapshot;
    private UsageDisplayMode _mode;
    private double _maxWidth = 220, _maxHeight = DefaultHeight;

    public TaskbarWidgetWindow(Action openMain)
    {
        _openMain = openMain;
        InitializeComponent();
        _quotaTip.Style = (Style)FindResource(typeof(ToolTip));
        ProviderButton.ToolTip = _quotaTip;
        ThemeService.ThemeChanged += RefreshLogo;
        Closed += (_, _) => ThemeService.ThemeChanged -= RefreshLogo;
        Width = 1;
        Height = 1;
    }

    public void Update(IEnumerable<UsageSnapshot> selected, AppSettings settings)
    {
        _snapshot = selected.SingleOrDefault();
        if (_snapshot != null) _mode = settings.GetDisplayMode(_snapshot.Provider);
        Render();
    }

    public void SetLayout(double maxWidth, double maxHeight)
    {
        maxWidth = Math.Max(1, maxWidth);
        maxHeight = Math.Max(1, maxHeight);
        if (_maxWidth == maxWidth && _maxHeight == maxHeight) return;
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
        RefreshLogo();
        AutomationProperties.SetAutomationId(ProviderButton, $"Widget{snapshot.Provider}");
        string basis = Localizer.T(_mode == UsageDisplayMode.Used ? "사용" : "남음");
        Title = $"{snapshot.Provider} · {basis} · VibeMeters";

        var windows = snapshot.Status == UsageStatus.Ready
            ? snapshot.Windows.Where(w => double.IsFinite(w.UsedPercent)).ToArray()
            : [];
        var visible = windows;
        // Size the tile from the host's available space. Every provider shares the same
        // frame and columns, even when a lookup fails or the number of windows changes.
        // The host only offers less than the readable width on a slim vertical taskbar,
        // which has height to spare, so the logo moves above the rows instead of beside them.
        double tileWidth = Math.Clamp(_maxWidth, NarrowMinimumWidth, 176);
        bool stacked = tileWidth < MinimumReadableWidth;
        bool compact = tileWidth < 156;
        double contentHeight = Math.Max(MinimumHeight, 4 + visible.Length * 14) + (stacked ? StackedBrandHeight : 0);
        double inset = stacked ? 5 : compact ? 6 : 7;
        ContentFrame.Width = tileWidth - 2;
        ContentFrame.Height = contentHeight - 2;
        ContentFrame.Padding = new Thickness(inset, 1, inset, 1);
        BrandColumn.Width = new GridLength(stacked ? 0 : compact ? 15 : 18);
        BrandRow.Height = new GridLength(stacked ? StackedBrandHeight : 0);
        Grid.SetColumn(Brand, 0);
        Grid.SetRow(Brand, stacked ? 0 : 1);
        Grid.SetColumnSpan(Brand, stacked ? 2 : 1);
        Grid.SetColumn(QuotaRows, stacked ? 0 : 1);
        Grid.SetColumnSpan(QuotaRows, stacked ? 2 : 1);
        QuotaRows.Margin = new Thickness(stacked ? 0 : inset, 0, 0, 0);
        Logo.Width = Logo.Height = stacked ? 13 : compact ? 15 : 17;
        Logo.VerticalAlignment = VerticalAlignment.Center;
        Logo.Margin = new Thickness(0);
        for (int i = 0; i < visible.Length; i++)
        {
            var row = QuotaRow(snapshot.Provider, visible[i], compact);
            AutomationProperties.SetAutomationId(row, $"WidgetQuota{i}");
            QuotaRows.Children.Add(row);
        }
        if (visible.Length == 0)
        {
            var unknown = RowText("—", "MutedText", TextAlignment.Right);
            unknown.FontSize = 14;
            unknown.Margin = new Thickness(0, 0, ResetWidth(compact), 0);
            QuotaRows.Children.Add(unknown);
        }
        Width = Math.Min(_maxWidth, tileWidth);
        Height = Math.Min(_maxHeight, stacked ? Math.Max(DefaultHeight, contentHeight) : DefaultHeight);
        AutomationProperties.SetName(ProviderButton, $"{snapshot.Provider} · {basis}");
        UpdateToolTip(snapshot, basis);
    }

    internal void SetToolTipPlacement(PlacementMode side, double offset)
    {
        _quotaTip.PlacementTarget = ProviderButton;
        _quotaTip.Placement = side;
        _quotaTip.HorizontalOffset = side is PlacementMode.Left or PlacementMode.Right ? offset : 0;
        _quotaTip.VerticalOffset = side is PlacementMode.Top or PlacementMode.Bottom ? offset : 0;
    }

    private void RefreshLogo()
    {
        if (_snapshot is { } snapshot) Logo.Source = TaskbarProviderPresentation.Icon(snapshot.Provider);
    }

    private const double RowHeight = 14, RowFontSize = 10, ResetGap = 5;
    private static double ValueWidth(bool compact) => compact ? 26 : 28;
    private static double ResetWidth(bool compact) => compact ? 29 : 31;

    /// <summary>Every row text shares one size and line box, so the label, percentage and reset sit on a common baseline.</summary>
    private static TextBlock RowText(string text, string color, TextAlignment alignment)
    {
        var block = new TextBlock { Text = text, FontSize = RowFontSize, LineHeight = RowHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight, TextAlignment = alignment, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, color);
        return block;
    }

    /// <summary>Short graph title. Business individual limits and Enterprise extra usage are monthly budgets.</summary>
    internal static string GraphLabel(UsageProviderKind kind, string windowName)
    {
        if (windowName is "개인 한도" or "월 추가 사용") return "M";
        var label = TaskbarProviderPresentation.WindowName(windowName);
        if (label == "5H") label = "H";
        if (kind == UsageProviderKind.Gemini) label = TaskbarProviderPresentation.GeminiGraphLabel(windowName) ?? label;
        return label;
    }

    private Grid QuotaRow(UsageProviderKind kind, UsageWindow window, bool compact)
    {
        const double labelWidth = 12;
        var row = new Grid { Height = RowHeight };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ValueWidth(compact)) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ResetWidth(compact)) });
        var name = RowText(GraphLabel(kind, window.Name), "SecondaryText", TextAlignment.Left);
        name.MaxWidth = labelWidth;
        name.ClipToBounds = true;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(name);
        double amount = _mode == UsageDisplayMode.Used ? Math.Clamp(window.UsedPercent, 0, 100) : window.RemainingPercent;
        var meter = new ProgressBar { Minimum = 0, Maximum = 100, Value = amount, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1,0,4,0) };
        meter.SetResourceReference(ProgressBar.ForegroundProperty, window.RemainingPercent <= 10 ? "CriticalText" : kind + "Accent");
        Grid.SetColumn(meter,1);
        row.Children.Add(meter);
        var value = RowText($"{amount.ToString("0", CultureInfo.InvariantCulture)}%",
            window.RemainingPercent <= 10 ? "CriticalText" : "PrimaryText", TextAlignment.Right);
        value.FontWeight = FontWeights.SemiBold;
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        // A fixed gap after the right-aligned percentage keeps every countdown starting at the same x.
        var reset = RowText(UsageResetText.Compact(window.ResetsAt, DateTimeOffset.Now), "SecondaryText", TextAlignment.Left);
        reset.Margin = new Thickness(ResetGap, 0, 0, 0);
        Typography.SetNumeralAlignment(reset, FontNumeralAlignment.Tabular);
        AutomationProperties.SetAutomationId(reset, "WidgetReset");
        AutomationProperties.SetName(reset, UsageResetText.Countdown(window.ResetsAt, DateTimeOffset.Now));
        Grid.SetColumn(reset, 3);
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
        if (_quotaTip.IsOpen)
            foreach (var (label, deadline) in _tooltipCountdowns)
            {
                var duration = UsageResetText.Compact(deadline, now);
                if (label.Text != duration) label.Text = duration;
            }
    }

    private void UpdateToolTip(UsageSnapshot snapshot, string basis)
    {
        _tooltipCountdowns.Clear();
        var now = DateTimeOffset.Now;
        var panel = new StackPanel { MaxWidth = 378 };
        string heading = snapshot.Provider.ToString();
        if (!string.IsNullOrWhiteSpace(snapshot.Plan)) heading += " · " + snapshot.Plan;
        var title = TipText(heading, "PrimaryText", 14);
        title.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(title);
        var lines = new List<string> { $"{heading} · {basis}" };
        var windows = snapshot.Status == UsageStatus.Ready
            ? snapshot.Windows.Where(w => double.IsFinite(w.UsedPercent)).ToArray() : [];
        if (windows.Length > 0)
        {
            var table = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(144) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(59) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(146) });
            panel.Children.Add(table);
            void AddCell(FrameworkElement cell, int row, int column)
            {
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                table.Children.Add(cell);
            }
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(TipText(Localizer.IsEnglish ? "Quota" : "한도", "SecondaryText", 11), 0, 0);
            AddCell(TipText(basis, "SecondaryText", 11), 0, 1);
            AddCell(TipText(Localizer.IsEnglish ? "Until reset" : "리셋까지", "SecondaryText", 11), 0, 2);
            foreach (var w in windows)
            {
                int row = table.RowDefinitions.Count;
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                double value = _mode == UsageDisplayMode.Used ? Math.Clamp(w.UsedPercent, 0, 100) : w.RemainingPercent;
                var amount = value.ToString("0.#", Localizer.Culture) + "%";
                var name = Localizer.T(w.Name);
                var nameText = TipText(name, "PrimaryText", 12);
                nameText.Margin = new Thickness(0, 5, 8, 0);
                AddCell(nameText, row, 0);
                var valueText = TipText(amount, "PrimaryText", 12);
                valueText.Margin = new Thickness(0, 5, 5, 0);
                valueText.FontWeight = FontWeights.SemiBold;
                AddCell(valueText, row, 1);
                var resetCell = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
                var time = TipText(UsageResetText.Compact(w.ResetsAt, now), "PrimaryText", 12);
                resetCell.Children.Add(time);
                _tooltipCountdowns.Add((time, w.ResetsAt));
                var when = UsageResetText.Countdown(w.ResetsAt, now);
                if (w.ResetsAt is { } reset)
                {
                    var date = reset.ToLocalTime().ToString("g", Localizer.Culture);
                    resetCell.Children.Add(TipText(date, "SecondaryText", 11));
                    when += $" ({date})";
                }
                AddCell(resetCell, row, 2);
                lines.Add($"{name} · {amount} · {when}");
            }
        }
        void AddNote(string text, string color)
        {
            var note = TipText(text, color, 11);
            note.Margin = new Thickness(0, 7, 0, 0);
            panel.Children.Add(note);
            lines.Add(text);
        }
        if (windows.Length == 0)
            AddNote(Localizer.T(snapshot.Status switch {
                UsageStatus.Ready => "연결됨 · 비율 미제공", UsageStatus.AuthenticationRequired => "로그인 필요",
                UsageStatus.NotConnected => "연결 필요", UsageStatus.AuthenticationRetry => "인증 재시도 대기",
                UsageStatus.AuthenticationRecovering => "인증 갱신 확인 중", UsageStatus.RateLimited => "요청 제한 대기",
                _ => "확인 불가" }), "PrimaryText");
        if (!string.IsNullOrWhiteSpace(snapshot.Message)) AddNote(Localizer.T(snapshot.Message), "SecondaryText");
        AddNote(UsageRefreshText.Summary(snapshot), "SecondaryText");
        _quotaTip.Content = panel;
        AutomationProperties.SetHelpText(ProviderButton, string.Join("\n", lines));
    }

    private static TextBlock TipText(string text, string color, double size)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, color);
        return label;
    }

    private void RefreshToolTip(object sender, ToolTipEventArgs args)
    {
        if (_snapshot is { } snapshot)
            UpdateToolTip(snapshot, Localizer.T(_mode == UsageDisplayMode.Used ? "사용" : "남음"));
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e) => _openMain();
}
