using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly ICliMaintenance _cli;
    private readonly CancellationTokenSource _cliLifetime = new();
    private readonly Dictionary<UsageProviderKind, CliInstallation> _cliInstallations = new();
    private readonly Dictionary<UsageProviderKind, CancellationTokenSource> _cliWork = new();
    private bool _cliLoaded, _closed;
    public event Action? SettingsChanged;
    public event Action<UsageProviderKind>? LoginRequested;
    public SettingsWindow(AppSettings settings) : this(settings, new CliMaintenance()) { }
    internal SettingsWindow(AppSettings settings, ICliMaintenance cli)
    {
        _cli = cli;
        _settings=settings;
        InitializeComponent();
        Localizer.Track(this);
        foreach(ComboBoxItem item in LanguageCombo.Items)
            if(item.Tag?.ToString()==settings.Language.ToString()) { LanguageCombo.SelectedItem=item; break; }
        CodexCheck.IsChecked=settings.ShowCodex;
        ClaudeCheck.IsChecked=settings.ShowClaude;
        GeminiCheck.IsChecked=settings.ShowGemini;
        foreach (ComboBoxItem item in GeminiProfileCombo.Items)
            if (item.Tag?.ToString() == settings.GeminiProfile.ToString()) GeminiProfileCombo.SelectedItem = item;
        GrokCheck.IsChecked=settings.ShowGrok;
        TopCheck.IsChecked=settings.AlwaysOnTop;
        foreach (var check in new[] { CodexWidget, ClaudeWidget, GeminiWidget, GrokWidget })
            check.IsChecked = settings.IsWidgetSelected(Enum.Parse<UsageProviderKind>((string)check.Tag));
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
        Loaded += async (_, _) =>
        {
            if (_cliLoaded) return;
            _cliLoaded = true;
            await Task.WhenAll(new[] { UsageProviderKind.Codex, UsageProviderKind.Claude, UsageProviderKind.Gemini }
                .Select(kind => RefreshCliAsync(kind)));
        };
        Closed += (_, _) => { _closed = true; _cliLifetime.Cancel(); _cliLifetime.Dispose(); };
    }

    private (Button Button, TextBlock Status) CliControls(UsageProviderKind kind) => kind switch
    {
        UsageProviderKind.Codex => (CodexCliButton, CodexCliStatus),
        UsageProviderKind.Claude => (ClaudeCliButton, ClaudeCliStatus),
        _ => (AntigravityCliButton, AntigravityCliStatus)
    };

    private async Task RefreshCliAsync(UsageProviderKind kind, string? result = null)
    {
        try
        {
            var installation = await _cli.InspectAsync(kind, _cliLifetime.Token);
            if (_closed) return;
            _cliInstallations[kind] = installation;
            var (button, status) = CliControls(kind);
            button.Content = installation.Executable is null ? "설치" : "업데이트";
            button.IsEnabled = true;
            status.Text = result is null ? installation.Description : result + "\n" + installation.Description;
            status.ToolTip = installation.Executable ?? OfficialCliInstaller.Source(kind);
            Localizer.Apply(this);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            if (_closed) return;
            var (button, status) = CliControls(kind);
            button.Content = "다시 확인"; button.IsEnabled = true;
            status.Text = "설치 상태를 확인하지 못했습니다. 다시 확인해 주세요.";
            Localizer.Apply(this);
        }
    }

    private async void CliAction_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || sender is not Button button || !button.IsEnabled || !Enum.TryParse<UsageProviderKind>(button.Tag?.ToString(), out var kind)) return;
        if (_cliWork.TryGetValue(kind, out var running))
        {
            running.Cancel(); button.IsEnabled = false;
            CliControls(kind).Status.Text = "작업을 중지하고 있습니다…";
            Localizer.Apply(this); return;
        }
        if (!_cliInstallations.TryGetValue(kind, out var installation)) { await RefreshCliAsync(kind); return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cliLifetime.Token);
        _cliWork.Add(kind, cancellation);
        button.Content = "취소";
        var status = CliControls(kind).Status;
        status.Text = "CLI 작업을 준비합니다…";
        Localizer.Apply(this);
        string message;
        try
        {
            var progress = new Progress<string>(text =>
            {
                if (_closed || cancellation.IsCancellationRequested ||
                    !_cliWork.TryGetValue(kind, out var current) || !ReferenceEquals(current, cancellation)) return;
                status.Text = text; Localizer.Apply(this);
            });
            var result = await _cli.InstallAsync(kind, installation.Executable is not null, cancellation.Token, progress);
            message = result.Message;
        }
        catch (OperationCanceledException) { message = "작업을 취소했습니다. 일부 설치 파일이 남을 수 있습니다."; }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        { message = "CLI 작업을 실행하지 못했습니다. 설치 권한과 Windows 또는 회사 정책을 확인해 주세요."; }
        finally { _cliWork.Remove(kind); }
        if (!_closed) { button.IsEnabled = false; await RefreshCliAsync(kind, message); }
    }
    private void Preference_Click(object sender,RoutedEventArgs e)
    {
        if(!_initialized) return;
        _settings.ShowCodex=CodexCheck.IsChecked==true;
        _settings.ShowClaude=ClaudeCheck.IsChecked==true;
        _settings.ShowGemini=GeminiCheck.IsChecked==true;
        _settings.ShowGrok=GrokCheck.IsChecked==true;
        _settings.AlwaysOnTop=TopCheck.IsChecked==true;
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
    private void GeminiProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || GeminiProfileCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<GeminiProfile>(item.Tag?.ToString(), out var profile)) return;
        _settings.GeminiProfile = profile;
        SettingsChanged?.Invoke();
    }
    private void Widget_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not CheckBox check) return;
        var flag = AppSettings.WidgetFlag(Enum.Parse<UsageProviderKind>((string)check.Tag));
        if (check.IsChecked == true) _settings.WidgetProviders |= flag;
        else _settings.WidgetProviders &= ~flag;
        SettingsChanged?.Invoke();
    }
    private void GrokLogin_Click(object sender,RoutedEventArgs e)=>LoginRequested?.Invoke(UsageProviderKind.Grok);
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
    private void Header_MouseLeftButtonDown(object sender,MouseButtonEventArgs e)
    {
        if(e.OriginalSource is DependencyObject source)
            for(var current=source;current!=null;current=VisualTreeHelper.GetParent(current)) if(current is Button) return;
        if(e.LeftButton==MouseButtonState.Pressed) DragMove();
    }
}
