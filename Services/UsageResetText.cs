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

        return (english ? "Reset " : "리셋 ") + Compact(resetsAt, now);
    }

    public static string Compact(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is null) return "—";
        var remaining = resetsAt.Value - now;
        if (remaining <= TimeSpan.Zero) return "0:00";
        long minutes = (remaining.Ticks - 1) / TimeSpan.TicksPerMinute + 1;
        return remaining.TotalDays > 1 ? $"{remaining.Days}d"
            : remaining.TotalHours > 5 ? $"{(int)remaining.TotalHours}h"
            : $"{minutes / 60}:{minutes % 60:00}";
    }
}
