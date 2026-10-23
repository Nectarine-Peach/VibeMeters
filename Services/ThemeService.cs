using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using VibeMeters.Models;

namespace VibeMeters.Services;

public static class ThemeService
{
    private static bool _initialized;
    private static bool _highContrast;
    private static AppTheme _preference;
    public static bool IsDark { get; private set; }
    public static event Action? ThemeChanged;

    public static void Apply(AppTheme preference)
    {
        _preference = preference;
        var dark = preference == AppTheme.Dark || preference == AppTheme.System && ReadSystemDark();
        var highContrast = SystemParameters.HighContrast;
        if (_initialized && dark == IsDark && highContrast == _highContrast) return;
        if (!_initialized) SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        _initialized = true; IsDark = dark; _highContrast = highContrast;
        var palette = dark ? DarkPalette() : LightPalette();
        if (highContrast)
        {
            foreach (var key in new[] { "WindowBackground", "SurfaceBackground", "CodexSurface", "ClaudeSurface", "GeminiSurface" }) palette[key] = SystemColors.WindowColor;
            foreach (var key in new[] { "PrimaryText", "SecondaryText", "MutedText", "CodexAccent", "ClaudeAccent", "GeminiAccent", "WarningText", "CriticalText" }) palette[key] = SystemColors.WindowTextColor;
            foreach (var key in new[] { "WindowBorder", "PopupBorder", "TrackBrush", "ToggleOff" }) palette[key] = SystemColors.GrayTextColor;
            foreach (var key in new[] { "SurfaceHover", "SurfacePressed", "ToggleOn" }) palette[key] = SystemColors.HighlightColor;
            palette["ToggleThumb"] = SystemColors.HighlightTextColor;
        }
        foreach (var item in palette)
        {
            var brush = new SolidColorBrush(item.Value); brush.Freeze();
            Application.Current.Resources[item.Key] = brush;
        }
        ThemeChanged?.Invoke();
    }

    private static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        var application = Application.Current;
        if (application == null || application.Dispatcher.HasShutdownStarted) return;
        application.Dispatcher.InvokeAsync(() => Apply(_preference));
    }

    public static void Dispose()
    {
        if (_initialized) SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        _initialized = false;
    }

    private static Dictionary<string, Color> DarkPalette() => Palette(new[]
    {
        "#141922", "#3A4659", "#E9EDF5", "#B9C6D8", "#98A9BF", "#1D2633", "#30405A", "#3A4F6D",
        "#1C282D", "#2B2527", "#21283A", "#8BE2C3", "#ECA98C", "#A5B9FF", "#39465A", "#E7BE7C", "#F69898",
        "#46536A", "#477C71", "#F2F6FC", "#536780"
    });

    private static Dictionary<string, Color> LightPalette() => Palette(new[]
    {
        "#F5F7FB", "#CAD3E0", "#17243A", "#41536C", "#586A82", "#FFFFFF", "#E7EDF7", "#D8E4F5",
        "#E6F4ED", "#FFF0E8", "#EDF0FF", "#146B50", "#9B4725", "#4058AE", "#CBD5E1", "#85550C", "#AE3434",
        "#A4B1C4", "#237B61", "#FFFFFF", "#A9B8CC"
    });

    private static Dictionary<string, Color> Palette(string[] values)
    {
        var keys = new[] { "WindowBackground", "WindowBorder", "PrimaryText", "SecondaryText", "MutedText", "SurfaceBackground", "SurfaceHover", "SurfacePressed", "CodexSurface", "ClaudeSurface", "GeminiSurface", "CodexAccent", "ClaudeAccent", "GeminiAccent", "TrackBrush", "WarningText", "CriticalText", "ToggleOff", "ToggleOn", "ToggleThumb", "PopupBorder" };
        var result = new Dictionary<string, Color>();
        for (var index = 0; index < keys.Length; index++) result[keys[index]] = (Color)ColorConverter.ConvertFromString(values[index]);
        return result;
    }
}
