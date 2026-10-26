using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using VibeMeters.Models;
using VibeMeters.Services;

namespace VibeMeters;

/// <summary>Explicit consent and visible progress for an optional, user-scoped CLI installation.</summary>
internal sealed class CliInstallWindow : Window
{
    private readonly UsageProviderKind _provider;
    private readonly CancellationTokenSource _cancellation;
    private readonly TextBlock _status;
    private readonly Button _install;
    private readonly Button _cancel;
    private readonly ProgressBar _progress;
    private bool _busy, _closeWhenStopped;
    internal bool Installed { get; private set; }

    internal CliInstallWindow(UsageProviderKind provider, CancellationToken lifetime)
    {
        _provider = provider;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Title = OfficialCliInstaller.Name(provider) + " 설치";
        Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "PrimaryText");
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "로그인하려면 " + OfficialCliInstaller.Name(provider) + "가 필요합니다", FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "로그인을 진행하려면 공식 CLI가 필요합니다. 기존 인증 파일로 사용량을 조회할 때는 설치가 필요하지 않습니다.\n\n선택하면 공식 설치 프로그램을 내려받아 현재 사용자 계정에 설치한 뒤 브라우저 로그인을 시작합니다. 인터넷 연결이 필요하며 관리자 권한을 요청하지 않습니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "설치 출처: " + OfficialCliInstaller.Source(provider), TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        _progress = new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Collapsed };
        panel.Children.Add(_progress);
        _status = new TextBlock { Text = "설치하지 않고 취소할 수 있습니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16), MinHeight = 32 };
        panel.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _install = new Button { Content = "설치 후 로그인", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        _cancel = new Button { Content = "취소", Padding = new Thickness(14, 7, 14, 7) };
        _install.Click += Install_Click;
        _cancel.Click += (_, _) => Close();
        actions.Children.Add(_install); actions.Children.Add(_cancel); panel.Children.Add(actions);
        Content = panel;
        Localizer.Track(this);
        Closing += OnClosing;
        PreviewKeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Escape) { args.Handled = true; Close(); } };
        Closed += (_, _) => _cancellation.Dispose();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true; _install.IsEnabled = false; _progress.Visibility = Visibility.Visible;
        _status.Text = "설치를 준비합니다…";
        Localizer.Apply(this);
        var progress = new Progress<string>(message => { _status.Text = message; Localizer.Apply(this); });
        var result = await OfficialCliInstaller.InstallAsync(_provider, _cancellation.Token, progress);
        _busy = false;
        if (_closeWhenStopped || _cancellation.IsCancellationRequested) { DialogResult = false; return; }
        if (result.Success) { Installed = true; DialogResult = true; return; }
        _progress.Visibility = Visibility.Collapsed;
        _status.Text = result.Message;
        _install.Content = "다시 설치"; _install.IsEnabled = true;
        Localizer.Apply(this);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true; _closeWhenStopped = true;
        _cancellation.Cancel(); _cancel.IsEnabled = false;
        _status.Text = "설치를 중지하고 있습니다…";
        Localizer.Apply(this);
    }
}
