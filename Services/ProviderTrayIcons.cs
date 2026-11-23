using System.Globalization;
using VibeMeters.Models;
using Forms = System.Windows.Forms;

namespace VibeMeters.Services;

internal sealed class ProviderTrayIcons : IDisposable
{
    private readonly Dictionary<UsageProviderKind, Forms.NotifyIcon> _icons = new();

    public ProviderTrayIcons(Forms.ContextMenuStrip menu, Action openWindow)
    {
        // Allocate in provider order so toggling a service does not change its shell ID.
        foreach (var provider in Enum.GetValues<UsageProviderKind>())
        {
            var icon = new Forms.NotifyIcon { ContextMenuStrip = menu };
            icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) openWindow(); };
            _icons.Add(provider, icon);
        }
    }

    public Forms.NotifyIcon? GetVisible(UsageProviderKind kind)
        => _icons.TryGetValue(kind, out var icon) && icon.Visible ? icon : null;

    public void Update(IReadOnlyDictionary<UsageProviderKind, UsageSnapshot> visible, AppSettings settings)
    {
        foreach (var (provider, icon) in _icons)
        {
            if (!settings.ShowProviderTrayIcons || !visible.TryGetValue(provider, out var snapshot))
            {
                icon.Visible = false;
                if (icon.Icon is { } previous)
                {
                    icon.Icon = null;
                    icon.Tag = null;
                    previous.Dispose();
                }
                continue;
            }

            var quota = snapshot.Status == UsageStatus.Ready
                ? snapshot.Windows.Where(w => double.IsFinite(w.UsedPercent)).MaxBy(w => w.UsedPercent)
                : null;
            var number = "—";
            string tooltip;
            if (quota is not null)
            {
                var used = settings.GetDisplayMode(provider) == UsageDisplayMode.Used;
                var percent = used ? 100 - quota.RemainingPercent : quota.RemainingPercent;
                number = percent.ToString("0", CultureInfo.InvariantCulture);
                tooltip = $"{provider} · {number}% {Localizer.T(used ? "사용" : "남음")} · {Localizer.T(quota.Name)}";
                if (quota.ResetsAt is { } reset)
                    tooltip += $" · {(Localizer.IsEnglish ? "Reset" : "초기화")} {reset.ToLocalTime().ToString("g", Localizer.Culture)}";
            }
            else tooltip = $"{provider} · — · {Localizer.T(snapshot.Message ?? "확인 필요")}";

            if (!Equals(icon.Tag, number))
            {
                var previous = icon.Icon;
                icon.Icon = TrayIconRenderer.RenderNumber(provider, number);
                icon.Tag = number;
                previous?.Dispose();
            }
            icon.Text = tooltip[..Math.Min(tooltip.Length, 127)];
            icon.Visible = true;
        }
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            icon.Visible = false;
            icon.Icon?.Dispose();
            icon.Dispose();
        }
    }
}
