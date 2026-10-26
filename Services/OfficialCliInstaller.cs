using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VibeMeters.Models;

namespace VibeMeters.Services;

internal sealed record CliInstallResult(bool Success, string Message);

/// <summary>Only called after the user explicitly chooses installation in CliInstallWindow.</summary>
internal static class OfficialCliInstaller
{
    internal static string Name(UsageProviderKind kind) => kind == UsageProviderKind.Codex ? "Codex CLI" : "Claude Code";
    internal static string? Find(UsageProviderKind kind) => kind switch
    {
        UsageProviderKind.Codex => OfficialCliProcess.FindExecutable("codex"),
        UsageProviderKind.Claude => OfficialCliProcess.FindExecutable("claude"),
        _ => null
    };
    internal static string Source(UsageProviderKind kind) => kind switch
    {
        UsageProviderKind.Codex => "https://chatgpt.com/codex/install.ps1",
        UsageProviderKind.Claude => "https://claude.ai/install.ps1",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static ProcessStartInfo CreateStartInfo(UsageProviderKind kind)
    {
        // Fixed HTTPS sources, default certificate validation, no elevation or policy changes.
        // The official installers own downloads, integrity checks and per-user installation.
        var script = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " +
            "$global:LASTEXITCODE=0; try { " +
            "$payload=(Invoke-WebRequest -UseBasicParsing -Uri '" + Source(kind) + "').Content; " +
            "if ($payload -is [byte[]]) {$payload=[Text.Encoding]::UTF8.GetString($payload)}; " +
            "& ([scriptblock]::Create([string]$payload)); " +
            "if ($LASTEXITCODE -ne 0) {exit $LASTEXITCODE}; exit 0 " +
            "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        start.Environment.Remove("NODE_OPTIONS");
        start.Environment.Remove("NODE_PATH");
        start.Environment["NO_COLOR"] = "1";
        if (kind == UsageProviderKind.Codex)
        {
            // Defaults decline replacing/uninstalling another installation and launching
            // Codex after setup. Login is a separate action owned by the existing login flow.
            start.Environment["CODEX_NON_INTERACTIVE"] = "1";
            start.Environment["CODEX_INSTALL_DIR"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin");
        }
        return start;
    }

    internal static async Task<CliInstallResult> InstallAsync(UsageProviderKind kind, CancellationToken cancellationToken, IProgress<string> progress)
    {
        if (kind == UsageProviderKind.Gemini) return new(false, "Gemini는 동봉된 런타임을 사용합니다.");
        if (Find(kind) is not null) return new(true, "이미 설치된 CLI를 사용합니다.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var process = new Process();
        var diagnostics = new InstallDiagnostics(progress);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            process.StartInfo = CreateStartInfo(kind);
            progress.Report("공식 설치 파일을 내려받고 현재 사용자 계정에 설치합니다. 잠시 기다려 주세요.");
            if (!process.Start()) return new(false, "설치를 시작하지 못했습니다. 다시 시도해 주세요.");
            process.StandardInput.Close();
            var output = DrainAsync(process.StandardOutput, diagnostics, deadline.Token);
            var error = DrainAsync(process.StandardError, diagnostics, deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0) return new(false, diagnostics.Failure);
            // Exit code alone does not prove the executable is available to this app.
            return Find(kind) is not null
                ? new(true, "CLI 설치를 확인했습니다. 브라우저 로그인을 준비합니다.")
                : new(false, "설치가 종료됐지만 CLI를 찾지 못했습니다. 회사 설치 정책과 사용자 PATH를 확인한 뒤 다시 시도해 주세요.");
        }
        catch (OperationCanceledException)
        { return new(false, cancellationToken.IsCancellationRequested ? "설치를 취소했습니다. 일부 설치 파일이 남을 수 있습니다." : "설치 대기 시간(10분)이 지났습니다. 네트워크와 설치 정책을 확인한 뒤 다시 시도해 주세요."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        { return new(false, "설치를 시작하지 못했습니다. Windows PowerShell과 회사 설치 정책을 확인해 주세요."); }
        finally { OfficialCliProcess.Stop(process); }
    }

    private static async Task DrainAsync(StreamReader stream, InstallDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                diagnostics.Observe(new string(buffer, 0, count));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private sealed class InstallDiagnostics(IProgress<string> progress)
    {
        private readonly object _sync = new();
        private string _failure = "설치에 실패했습니다. 인터넷 연결·프록시·회사 설치 정책을 확인하고 다시 시도해 주세요. 필요하면 표시된 공식 설치 주소를 사용하세요.";
        private bool _reportedInstall;
        internal string Failure { get { lock (_sync) return _failure; } }
        internal void Observe(string fragment)
        {
            // Map bounded transient output to fixed messages; never expose or save raw output.
            lock (_sync)
            {
                var text = fragment.ToLowerInvariant();
                if (text.Contains("certificate") || text.Contains("trust relationship") || text.Contains("ssl/tls"))
                    _failure = "공식 설치 서버의 인증서를 확인하지 못했습니다. 회사 CA·프록시 설정을 확인하세요. TLS 검증은 해제하지 않습니다.";
                else if (text.Contains("access is denied") || text.Contains("unauthorized") || text.Contains("execution policy") || text.Contains("cannot be loaded"))
                    _failure = "현재 사용자 설치가 Windows 또는 회사 정책에 의해 차단됐습니다. 관리자에게 공식 CLI 설치를 요청하세요.";
                if (!_reportedInstall && (text.Contains("installing") || text.Contains("setting up")))
                { _reportedInstall = true; progress.Report("공식 CLI를 설치하고 있습니다. 완료 후 브라우저 로그인으로 이어집니다."); }
            }
        }
    }
}
