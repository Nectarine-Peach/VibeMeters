namespace VibeMeters.Models;

public enum AppTheme { System, Dark, Light }
public enum UsageDisplayMode { Remaining, Used }
public enum AppLanguage { System, Korean, English }
public enum GeminiProfile { Default, Antigravity, CodeAssist1, CodeAssist2 }
public enum TaskbarMode { Off, Combined, Separate, Widget }
[System.Flags]
public enum TaskbarWidgetProviders { None = 0, Codex = 1, Claude = 2, Gemini = 4, Grok = 8 }

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public AppLanguage Language { get; set; } = AppLanguage.System;
    public UsageDisplayMode CodexDisplayMode { get; set; }
    public UsageDisplayMode ClaudeDisplayMode { get; set; }
    public UsageDisplayMode GeminiDisplayMode { get; set; }
    public GeminiProfile GeminiProfile { get; set; }
    public UsageDisplayMode GrokDisplayMode { get; set; }
    public int RefreshIntervalSeconds { get; set; } = 120;
    public bool AlwaysOnTop { get; set; }
    public bool ShowInTaskbar { get; set; } = true;
    public bool SeparateTaskbarButtons { get; set; }
    public bool ShowTaskbarWidget { get; set; }
    public TaskbarWidgetProviders WidgetProviders { get; set; } = TaskbarWidgetProviders.Codex | TaskbarWidgetProviders.Claude;
    [System.Text.Json.Serialization.JsonIgnore]
    public TaskbarMode TaskbarDisplay
    {
        get => !ShowInTaskbar ? TaskbarMode.Off : ShowTaskbarWidget ? TaskbarMode.Widget :
            SeparateTaskbarButtons ? TaskbarMode.Separate : TaskbarMode.Combined;
        set
        {
            ShowInTaskbar = value != TaskbarMode.Off;
            SeparateTaskbarButtons = value == TaskbarMode.Separate;
            ShowTaskbarWidget = value == TaskbarMode.Widget;
        }
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShowMainTaskbarButton => TaskbarDisplay == TaskbarMode.Combined;
    public bool ShowProviderTrayIcons { get; set; }
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

    public static TaskbarWidgetProviders WidgetFlag(UsageProviderKind provider) => provider switch
    {
        UsageProviderKind.Codex => TaskbarWidgetProviders.Codex,
        UsageProviderKind.Claude => TaskbarWidgetProviders.Claude,
        UsageProviderKind.Gemini => TaskbarWidgetProviders.Gemini,
        UsageProviderKind.Grok => TaskbarWidgetProviders.Grok,
        _ => TaskbarWidgetProviders.None
    };

    public bool IsWidgetSelected(UsageProviderKind provider) => (WidgetProviders & WidgetFlag(provider)) != 0;
    public bool ShouldFetch(UsageProviderKind provider) => IsProviderVisible(provider) ||
        (TaskbarDisplay == TaskbarMode.Widget && IsWidgetSelected(provider));

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
