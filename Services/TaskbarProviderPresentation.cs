using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeMeters.Models;

namespace VibeMeters.Services;

internal static class TaskbarProviderPresentation
{
    public static string Title(UsageSnapshot snapshot, UsageDisplayMode mode)
    {
        if (snapshot.Status != UsageStatus.Ready) return "—";
        var windows = snapshot.Windows.Where(window => double.IsFinite(window.UsedPercent)).ToArray();
        if (windows.Length == 0) return "—";

        var labels = windows.Take(2).Select(window =>
        {
            var percent = mode == UsageDisplayMode.Used ? Math.Clamp(window.UsedPercent, 0, 100) : window.RemainingPercent;
            var name = WindowName(window.Name);
            var gap = windows.Length > 1 && name is "5H" or "W" or "D" or "M" ? "" : " ";
            return $"{name}{gap}{percent.ToString("0", CultureInfo.InvariantCulture)}%";
        });
        string title = string.Join("/", labels);
        return windows.Length <= 2 ? title : $"{title} +{windows.Length - 2}";
    }

    internal static string? GeminiGraphLabel(string name)
    {
        if (name.StartsWith("Build ", StringComparison.Ordinal) || name.StartsWith("공유 ", StringComparison.Ordinal)) return null;
        var tokens = name.Split([' ', '-', '_', '/', '.', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        bool Has(string model) => tokens.Contains(model, StringComparer.OrdinalIgnoreCase);
        if (Has("Argon")) return "A";
        if (Has("Flash")) return Has("Lite") ? "FL" : "F";
        if (Has("Pro")) return "P";
        return null;
    }

    internal static string WindowName(string name)
    {
        if (name == "개인 한도") return Localizer.IsEnglish ? "Individual" : "개인";
        if (name == "월 추가 사용") return Localizer.IsEnglish ? "Extra M" : "추가 M";
        // Keep named model and billing scopes. For example, "sonnet 주간" and "주간" are independent limits.
        return string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(token => token switch
        {
            "주간" => "W",
            "월간" => "M",
            "일간" => "D",
            "공유" => Localizer.IsEnglish ? "Shared" : "공유",
            "한도" => Localizer.IsEnglish ? "Limit" : "한도",
            _ when token.EndsWith("시간", StringComparison.Ordinal) && IsNumber(token[..^2]) => token[..^2] + "H",
            _ when token.EndsWith("분", StringComparison.Ordinal) && IsNumber(token[..^1]) => token[..^1] + "min",
            _ => token
        }));
    }

    private static bool IsNumber(string text) => double.TryParse(text, NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out var number) && double.IsFinite(number);

    public static ImageSource Icon(UsageProviderKind provider)
    {
        if (provider is UsageProviderKind.Codex or UsageProviderKind.Grok)
        {
            bool white = SystemParameters.HighContrast ? SystemColors.WindowColor.ScR < 0.5f : ThemeService.IsDark;
            if (white) return Icons.WhiteMarks[provider];
        }
        return Icons.ByProvider[provider];
    }

    private static class Icons
    {
        internal static readonly Dictionary<UsageProviderKind, ImageSource> ByProvider =
            Enum.GetValues<UsageProviderKind>().ToDictionary(provider => provider, provider => Load(provider.ToString()));
        internal static readonly Dictionary<UsageProviderKind, ImageSource> WhiteMarks =
            new[] { UsageProviderKind.Codex, UsageProviderKind.Grok }.ToDictionary(provider => provider, provider => Load(provider + "-white"));

        private static ImageSource Load(string asset)
        {
            using var stream = typeof(TaskbarProviderPresentation).Assembly.GetManifestResourceStream($"VibeMeters.Taskbar.{asset}.png")
                ?? throw new InvalidOperationException($"Missing {asset} taskbar icon.");
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 96;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
    }
}
