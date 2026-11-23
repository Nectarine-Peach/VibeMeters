using System;

namespace VibeMeters.Services;

public static class UsageResetText
{
    public static string Countdown(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        bool english = Localizer.IsEnglish;
        if (resetsAt is null) return english ? "Reset N/A" : "리셋 미제공";
        var remaining = resetsAt.Value - now;
        if (remaining <= TimeSpan.Zero) return english ? "Reset pending" : "리셋 대기";

        long minutes = (remaining.Ticks - 1) / TimeSpan.TicksPerMinute + 1;
        long hours = minutes / 60;
        long days = hours / 24;
        string duration = days > 0 ? (english ? $"{days}d {hours % 24}h" : $"{days}일 {hours % 24}시간")
            : hours > 0 ? (english ? $"{hours}h {minutes % 60}m" : $"{hours}시간 {minutes % 60}분")
            : (english ? $"{minutes}m" : $"{minutes}분");
        return (english ? "Reset " : "리셋 ") + duration;
    }
}
