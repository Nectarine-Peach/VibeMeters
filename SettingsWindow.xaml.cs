using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VibeMeters.Models;
using VibeMeters.Services;

namespace VibeMeters;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private bool _initialized;
    public event Action? SettingsChanged;
    public event Action<UsageProviderKind>? LoginRequested;
    public SettingsWindow(AppSettings settings)
    {
        _settings=settings;
        InitializeComponent();
        Localizer.Track(this);
        foreach(ComboBoxItem item in LanguageCombo.Items)
            if(item.Tag?.ToString()==settings.Language.ToString()) { LanguageCombo.SelectedItem=item; break; }
        CodexCheck.IsChecked=settings.ShowCodex;
        ClaudeCheck.IsChecked=settings.ShowClaude;
        GeminiCheck.IsChecked=settings.ShowGemini;
        GrokCheck.IsChecked=settings.ShowGrok;
        TopCheck.IsChecked=settings.AlwaysOnTop;
        TaskbarCheck.IsChecked=settings.ShowInTaskbar;
        StartupCheck.IsChecked=settings.StartWithWindows;
        NotifyCheck.IsChecked=settings.NotifyLowQuota;
        UpdateServiceToggles();
        foreach(ComboBoxItem item in IntervalCombo.Items)
            if(int.TryParse(item.Tag?.ToString(),out var seconds)&&seconds==settings.RefreshIntervalSeconds) { IntervalCombo.SelectedItem=item; break; }
        foreach(ComboBoxItem item in ThemeCombo.Items)
            if(item.Tag?.ToString()==settings.Theme.ToString()) { ThemeCombo.SelectedItem=item; break; }
        SetDisplayMode(CodexModeCombo,settings.GetDisplayMode(UsageProviderKind.Codex));
        SetDisplayMode(ClaudeModeCombo,settings.GetDisplayMode(UsageProviderKind.Claude));
        SetDisplayMode(GeminiModeCombo,settings.GetDisplayMode(UsageProviderKind.Gemini));
        SetDisplayMode(GrokModeCombo,settings.GetDisplayMode(UsageProviderKind.Grok));
        _initialized=true;
        PreviewKeyDown+=(_,e)=> { if(e.Key==Key.Escape) { Close(); e.Handled=true; } };
    }
    private void Preference_Click(object sender,RoutedEventArgs e)
    {
        if(!_initialized) return;
        _settings.ShowCodex=CodexCheck.IsChecked==true;
        _settings.ShowClaude=ClaudeCheck.IsChecked==true;
        _settings.ShowGemini=GeminiCheck.IsChecked==true;
        _settings.ShowGrok=GrokCheck.IsChecked==true;
        _settings.AlwaysOnTop=TopCheck.IsChecked==true;
        _settings.ShowInTaskbar=TaskbarCheck.IsChecked==true;
        _settings.StartWithWindows=StartupCheck.IsChecked==true;
        _settings.NotifyLowQuota=NotifyCheck.IsChecked==true;
        UpdateServiceToggles();
        SettingsChanged?.Invoke();
    }
    private void UpdateServiceToggles()
    {
        var count=(CodexCheck.IsChecked==true?1:0)+(ClaudeCheck.IsChecked==true?1:0)+(GeminiCheck.IsChecked==true?1:0)+(GrokCheck.IsChecked==true?1:0);
        foreach(var check in new[]{CodexCheck,ClaudeCheck,GeminiCheck,GrokCheck})
        {
            check.IsEnabled=count>1||check.IsChecked!=true;
            check.ToolTip=check.IsEnabled?"메인 창에 이 서비스를 표시합니다.":"최소 1개의 서비스를 표시해야 합니다.";
        }
        Localizer.Apply(this);
    }
    private void Interval_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!_initialized||IntervalCombo.SelectedItem is not ComboBoxItem item) return;
        _settings.RefreshIntervalSeconds=int.Parse(item.Tag.ToString()!,CultureInfo.InvariantCulture);
        SettingsChanged?.Invoke();
    }
    private void Theme_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!_initialized||ThemeCombo.SelectedItem is not ComboBoxItem item) return;
        if(Enum.TryParse<AppTheme>(item.Tag?.ToString(),out var theme))
        {
            _settings.Theme=theme;
            SettingsChanged?.Invoke();
        }
    }
    private void Language_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!_initialized||LanguageCombo.SelectedItem is not ComboBoxItem item) return;
        if(Enum.TryParse<AppLanguage>(item.Tag?.ToString(),out var language))
        {
            _settings.Language=language;
            Localizer.SetLanguage(language);
            SettingsChanged?.Invoke();
        }
    }
    private static void SetDisplayMode(ComboBox combo,UsageDisplayMode mode)
    {
        foreach(ComboBoxItem item in combo.Items)
            if(item.Tag?.ToString()==mode.ToString()) { combo.SelectedItem=item; break; }
    }
    private void DisplayMode_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!_initialized||sender is not ComboBox combo||combo.SelectedItem is not ComboBoxItem item) return;
        if(!Enum.TryParse<UsageDisplayMode>(item.Tag?.ToString(),out var mode)) return;
        if(combo==CodexModeCombo) _settings.CodexDisplayMode=mode;
        else if(combo==ClaudeModeCombo) _settings.ClaudeDisplayMode=mode;
        else if(combo==GeminiModeCombo) _settings.GeminiDisplayMode=mode;
        else if(combo==GrokModeCombo) _settings.GrokDisplayMode=mode;
        SettingsChanged?.Invoke();
    }
    public void SetLoginStatus(UsageProviderKind kind,bool busy,string message)
    {
        var button=kind switch { UsageProviderKind.Codex=>CodexLoginButton,UsageProviderKind.Claude=>ClaudeLoginButton,UsageProviderKind.Grok=>GrokLoginButton,_=>GeminiLoginButton };
        button.IsEnabled=!busy;
        LoginStatusText.Text=message; LoginStatusText.ToolTip=message;
        Localizer.Apply(this);
    }
    private void CodexLogin_Click(object sender,RoutedEventArgs e)=>LoginRequested?.Invoke(UsageProviderKind.Codex);
    private void ClaudeLogin_Click(object sender,RoutedEventArgs e)=>LoginRequested?.Invoke(UsageProviderKind.Claude);
    private void GeminiLogin_Click(object sender,RoutedEventArgs e)=>LoginRequested?.Invoke(UsageProviderKind.Gemini);
    private void GrokLogin_Click(object sender,RoutedEventArgs e)=>LoginRequested?.Invoke(UsageProviderKind.Grok);
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
    private void Header_MouseLeftButtonDown(object sender,MouseButtonEventArgs e)
    {
        if(e.OriginalSource is DependencyObject source)
            for(var current=source;current!=null;current=VisualTreeHelper.GetParent(current)) if(current is Button) return;
        if(e.LeftButton==MouseButtonState.Pressed) DragMove();
    }
}
