using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using VibeMeters.Models;

namespace VibeMeters.Services;

public static class SettingsStore
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeMeters");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    public static AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
            return Normalize(settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public static AppSettings Normalize(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.Theme)) settings.Theme = AppTheme.System;
        if (!Enum.IsDefined(settings.CodexDisplayMode)) settings.CodexDisplayMode = UsageDisplayMode.Remaining;
        if (!Enum.IsDefined(settings.ClaudeDisplayMode)) settings.ClaudeDisplayMode = UsageDisplayMode.Remaining;
        if (!Enum.IsDefined(settings.GeminiDisplayMode)) settings.GeminiDisplayMode = UsageDisplayMode.Remaining;
        if (!Enum.IsDefined(settings.GrokDisplayMode)) settings.GrokDisplayMode = UsageDisplayMode.Remaining;
        if (settings.RefreshIntervalSeconds != 0 && settings.RefreshIntervalSeconds is < 30 or > 86400) settings.RefreshIntervalSeconds = 120;
        if (settings.Left.HasValue && !double.IsFinite(settings.Left.Value)) settings.Left = null;
        if (settings.Top.HasValue && !double.IsFinite(settings.Top.Value)) settings.Top = null;
        settings.WindowWidth = double.IsFinite(settings.WindowWidth) ? Math.Clamp(settings.WindowWidth, 220, 16384) : 360;
        settings.WindowHeight = double.IsFinite(settings.WindowHeight) ? Math.Clamp(settings.WindowHeight, 140, 16384) : 360;
        if (!settings.ShowCodex && !settings.ShowClaude && !settings.ShowGemini && !settings.ShowGrok) settings.ShowCodex = true;
        return settings;
    }
    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(settings));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("VibeMeters", "\"" + Environment.ProcessPath + "\" --startup");
        else key.DeleteValue("VibeMeters", false);
    }
}
