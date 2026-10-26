namespace VibeMeters.Models;

public enum AppTheme { System, Dark, Light }
public enum UsageDisplayMode { Remaining, Used }

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public UsageDisplayMode CodexDisplayMode { get; set; }
    public UsageDisplayMode ClaudeDisplayMode { get; set; }
    public UsageDisplayMode GeminiDisplayMode { get; set; }
    public UsageDisplayMode GrokDisplayMode { get; set; }
    public int RefreshIntervalSeconds { get; set; } = 120;
    public bool AlwaysOnTop { get; set; }
    public bool ShowInTaskbar { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double WindowWidth { get; set; } = 360;
    public double WindowHeight { get; set; } = 360;
    public bool WindowVisible { get; set; } = true;
    public bool CompactMode { get; set; }
    public int DesignLayoutVersion { get; set; }
    public bool NotifyLowQuota { get; set; }
    public bool ShowCodex { get; set; } = true;
    public bool ShowClaude { get; set; } = true;
    public bool ShowGemini { get; set; }
    public bool ShowGrok { get; set; }

    public bool IsProviderVisible(UsageProviderKind provider) => provider switch
    {
        UsageProviderKind.Codex => ShowCodex,
        UsageProviderKind.Claude => ShowClaude,
        UsageProviderKind.Gemini => ShowGemini,
        UsageProviderKind.Grok => ShowGrok,
        _ => false
    };

    public UsageDisplayMode GetDisplayMode(UsageProviderKind provider) => provider switch
    {
        UsageProviderKind.Codex => CodexDisplayMode,
        UsageProviderKind.Claude => ClaudeDisplayMode,
        UsageProviderKind.Gemini => GeminiDisplayMode,
        UsageProviderKind.Grok => GrokDisplayMode,
        _ => UsageDisplayMode.Remaining
    };
}
