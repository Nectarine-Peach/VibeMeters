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
            palette["BadgeText"] = SystemColors.HighlightTextColor;
            foreach(var key in new[] {"CodexBadge","ClaudeBadge","GeminiBadge"}) palette[key]=SystemColors.HighlightColor;
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
        "#1B2024", "#3A4248", "#F1F4F5", "#C2CACF", "#939FA7", "#23292D", "#30383E", "#3B464D",
        "#23292D", "#23292D", "#23292D", "#87DFC1", "#F4B68F", "#AAA7FF", "#41494F", "#F1B54E", "#F49A98",
        "#47535C", "#5EB49B", "#F1F5F6", "#74878F", "#10241E", "#87DFC1", "#F4B68F", "#AAA7FF"
    });

    private static Dictionary<string, Color> LightPalette() => Palette(new[]
    {
        "#F5F6F7", "#D9DFE3", "#192126", "#4E5962", "#64727C", "#FDFDFE", "#EAEFF2", "#DDE6E9",
        "#FDFDFE", "#FDFDFE", "#FDFDFE", "#56B99A", "#DB9870", "#8D8BE6", "#E1E6E9", "#A66B0B", "#B9413F",
        "#B4BEC5", "#4EAA8F", "#FFFFFF", "#94A6AE", "#10241E", "#83DCBB", "#F3B38D", "#AAA7FF"
    });

    private static Dictionary<string, Color> Palette(string[] values)
    {
        var keys = new[] { "WindowBackground", "WindowBorder", "PrimaryText", "SecondaryText", "MutedText", "SurfaceBackground", "SurfaceHover", "SurfacePressed", "CodexSurface", "ClaudeSurface", "GeminiSurface", "CodexAccent", "ClaudeAccent", "GeminiAccent", "TrackBrush", "WarningText", "CriticalText", "ToggleOff", "ToggleOn", "ToggleThumb", "PopupBorder", "BadgeText", "CodexBadge", "ClaudeBadge", "GeminiBadge" };
        var result = new Dictionary<string, Color>();
        for (var index = 0; index < keys.Length; index++) result[keys[index]] = (Color)ColorConverter.ConvertFromString(values[index]);
        return result;
    }
}
