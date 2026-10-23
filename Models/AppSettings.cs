namespace VibeMeters.Models;

public sealed class AppSettings
{
    public int RefreshIntervalSeconds { get; set; } = 120;
    public bool AlwaysOnTop { get; set; }
    public bool ShowInTaskbar { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
}
