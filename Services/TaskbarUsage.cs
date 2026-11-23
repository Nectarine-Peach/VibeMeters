using System.Windows;
using System.Windows.Shell;
using VibeMeters.Models;

namespace VibeMeters.Services;

internal static class TaskbarUsage
{
    public static void Update(Window window, AppSettings settings, IEnumerable<UsageSnapshot> visible)
    {
        if (!settings.ShowMainTaskbarButton)
        {
            window.Title = "VibeMeters";
            window.TaskbarItemInfo = null;
            return;
        }

        var details = new List<string>();
        var labels = new List<string>();
        UsageWindow? limit = null;
        foreach (var snapshot in visible.OrderBy(s => s.Provider))
        {
            var quota = snapshot.Status == UsageStatus.Ready
                ? snapshot.Windows.Where(w => double.IsFinite(w.UsedPercent)).MaxBy(w => w.UsedPercent)
                : null;
            if (quota is null)
            {
                labels.Add($"{snapshot.Provider} —");
                details.Add($"{snapshot.Provider} · {Localizer.T("확인 필요")}");
                continue;
            }

            var used = settings.GetDisplayMode(snapshot.Provider) == UsageDisplayMode.Used;
            var percent = used ? 100 - quota.RemainingPercent : quota.RemainingPercent;
            var label = used ? Localizer.T("사용") : Localizer.T("남음");
            labels.Add($"{snapshot.Provider} {percent:0}% {label}");
            var summary = $"{snapshot.Provider} {Localizer.T(quota.Name)} · {percent:0}% {label}";
            if (limit is null || quota.RemainingPercent < limit.RemainingPercent)
                limit = quota;
            if (quota.ResetsAt is { } reset)
            {
                var resetLabel = Localizer.IsEnglish ? "Reset" : "초기화";
                summary += $" · {resetLabel} {reset.ToLocalTime().ToString("g", Localizer.Culture)}";
            }
            details.Add(summary);
        }

        window.Title = labels.Count == 0 ? "VibeMeters" : string.Join(" · ", labels) + " | VibeMeters";
        var item = window.TaskbarItemInfo ??= new TaskbarItemInfo();
        item.Description = details.Count == 0 ? window.Title : string.Join("\n", details);
        // The bar always fills as quota is consumed. Its warning thresholds match the tray.
        item.ProgressValue = limit is null ? 0 : (100 - limit.RemainingPercent) / 100;
        item.ProgressState = limit?.RemainingPercent switch
        {
            null => TaskbarItemProgressState.None,
            <= 5 => TaskbarItemProgressState.Error,
            <= 20 => TaskbarItemProgressState.Paused,
            _ => TaskbarItemProgressState.Normal
        };
    }
}
