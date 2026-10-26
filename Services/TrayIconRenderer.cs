using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using VibeMeters.Models;

namespace VibeMeters.Services;

internal static class TrayIconRenderer
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    public static Icon Render(IReadOnlyDictionary<UsageProviderKind, UsageSnapshot> snapshots, AppSettings settings)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        int index = 0;
        var providers = snapshots.Keys.OrderBy(provider => provider).ToArray();
        int gap = providers.Length >= 4 ? 2 : 4;
        int width = providers.Length <= 1 ? 18 : providers.Length == 2 ? 10 : providers.Length == 3 ? 6 : 5;
        int totalWidth = providers.Length * width + Math.Max(0, providers.Length - 1) * gap;
        foreach (var provider in providers)
        {
            snapshots.TryGetValue(provider, out var snapshot);
            var quota = snapshot?.Windows.Count > 0 ? snapshot.Windows.Min(w => w.RemainingPercent) : (double?)null;
            var ready = snapshot?.Status == UsageStatus.Ready;
            var color = !ready ? Color.FromArgb(138, 148, 166) : quota <= 5 ? Color.FromArgb(248, 113, 113) : quota <= 20 ? Color.FromArgb(251, 191, 80) : provider == UsageProviderKind.Codex ? Color.FromArgb(139, 226, 195) : provider == UsageProviderKind.Claude ? Color.FromArgb(236, 169, 140) : provider == UsageProviderKind.Grok ? Color.FromArgb(216, 224, 234) : Color.FromArgb(145, 169, 249);
            using var track = new SolidBrush(Color.FromArgb(155, 65, 74, 90));
            using var fill = new SolidBrush(color);
            var x = (32 - totalWidth) / 2 + index++ * (width + gap);
            graphics.FillRectangle(track, x, 3, width, 26);
            if (ready && quota.HasValue)
            {
                var shownPercent = settings.GetDisplayMode(provider) == UsageDisplayMode.Used ? 100 - quota.Value : quota.Value;
                int height = Math.Max(2, (int)Math.Round(26 * shownPercent / 100));
                graphics.FillRectangle(fill, x, 29 - height, width, height);
            }
            else graphics.FillRectangle(fill, x, 14, width, 3);
        }
        var native = bitmap.GetHicon();
        try { using var temporary = Icon.FromHandle(native); return (Icon)temporary.Clone(); }
        finally { DestroyIcon(native); }
    }
}
