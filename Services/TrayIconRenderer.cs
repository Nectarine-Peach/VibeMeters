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

    public static Icon Render(IReadOnlyDictionary<UsageProviderKind, UsageSnapshot> snapshots)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        int index = 0;
        foreach (var provider in Enum.GetValues<UsageProviderKind>())
        {
            snapshots.TryGetValue(provider, out var snapshot);
            var quota = snapshot?.Windows.Count > 0 ? snapshot.Windows.Min(w => w.RemainingPercent) : (double?)null;
            var ready = snapshot?.Status == UsageStatus.Ready;
            var color = !ready ? Color.FromArgb(138, 148, 166) : quota <= 5 ? Color.FromArgb(248, 113, 113) : quota <= 20 ? Color.FromArgb(251, 191, 80) : provider == UsageProviderKind.Codex ? Color.FromArgb(139, 226, 195) : Color.FromArgb(236, 169, 140);
            using var track = new SolidBrush(Color.FromArgb(155, 65, 74, 90));
            using var fill = new SolidBrush(color);
            var x = index++ * 15 + 3;
            graphics.FillRectangle(track, x, 3, 10, 26);
            int height = ready && quota.HasValue ? Math.Max(2, (int)Math.Round(26 * quota.Value / 100)) : 5;
            graphics.FillRectangle(fill, x, 29 - height, 10, height);
        }
        var native = bitmap.GetHicon();
        try { using var temporary = Icon.FromHandle(native); return (Icon)temporary.Clone(); }
        finally { DestroyIcon(native); }
    }
}
