using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VibeMeters.Models;

namespace VibeMeters.Services;

public sealed record LoginResult(bool Success, string Message);

/// <summary>Runs official browser login; only sanitized diagnostics survive the process.</summary>
public static class LoginService
{
    private static readonly ConcurrentDictionary<UsageProviderKind, SemaphoreSlim> LoginGates = new();

    // Compatibility entry point. Use LoginAsync to observe completion and support cancellation.
    public static LoginResult StartLogin(UsageProviderKind provider)
    {
        _ = LoginAsync(provider);
        return new(true, "브라우저 로그인을 백그라운드에서 시작합니다.");
    }

    public static async Task<LoginResult> LoginAsync(UsageProviderKind provider,
        CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        var gate = LoginGates.GetOrAdd(provider, _ => new SemaphoreSlim(1, 1));
        if (cancellationToken.IsCancellationRequested) return new(false, "로그인을 취소했습니다.");
        if (!gate.Wait(0))
            return new(false, "이미 로그인 중입니다. 브라우저에서 로그인을 완료해 주세요.");
        try
        {
            if (provider == UsageProviderKind.Gemini) return GeminiCliBridge.StartLogin();
            var name = provider == UsageProviderKind.Codex ? "codex" : "claude";
            var arguments = provider == UsageProviderKind.Codex
                ? new[] { "login" } : new[] { "auth", "login", "--claudeai" };
            var start = OfficialCliProcess.CreateStartInfo(name, arguments);
            if (start is null)
                return new(false, provider == UsageProviderKind.Codex
                    ? "Codex CLI를 설치한 뒤 다시 로그인해 주세요."
                    : "Claude Code를 설치한 뒤 다시 로그인해 주세요.");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            using var process = new Process { StartInfo = start };
            var diagnostics = new LoginDiagnostics(provider);
            try
            {
                if (!process.Start()) return new(false, "로그인을 시작할 수 없습니다.");
                process.StandardInput.Close();
                progress?.Report("브라우저에서 로그인을 완료해 주세요. 터미널 창은 열리지 않습니다.");
                var output = DrainAsync(process.StandardOutput, diagnostics, deadline.Token);
                var error = DrainAsync(process.StandardError, diagnostics, deadline.Token);
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
                return process.ExitCode == 0
                    ? new(true, "로그인했습니다. 사용량을 다시 확인합니다.")
                    : new(false, diagnostics.FailureMessage);
            }
            catch (OperationCanceledException)
            {
                return new(false, cancellationToken.IsCancellationRequested
                    ? "로그인을 취소했습니다." : diagnostics.FailureHint ?? "로그인 대기 시간이 지났습니다. 다시 로그인해 주세요.");
            }
            finally { OfficialCliProcess.Stop(process); }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        {
            return new(false, "로그인을 시작할 수 없습니다. CLI 설치 상태를 확인해 주세요.");
        }
        finally { gate.Release(); }
    }

    private static async Task DrainAsync(StreamReader stream, LoginDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        // Bounded transient lines only. OAuth URLs are neither logged nor persisted.
        var line = new StringBuilder();
        var buffer = new char[1024];
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                for (var i = 0; i < count; i++)
                {
                    var c = buffer[i];
                    if (c is '\n' or '\r')
                    {
                        diagnostics.Observe(line.ToString());
                        line.Clear();
                    }
                    else if (line.Length < 8192) line.Append(c);
                }
            if (line.Length > 0) diagnostics.Observe(line.ToString());
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    // Whitelist before passing an OAuth link to the operating system. The CLI retains
    // responsibility for OAuth state, SSO, callback validation and credential storage.
    internal static Uri? BrowserLoginUri(UsageProviderKind provider, string text)
    {
        foreach (Match match in Regex.Matches(text, "https://[^\\s<>\\\"']+", RegexOptions.CultureInvariant))
        {
            if (!Uri.TryCreate(match.Value.TrimEnd('.', ',', ')', '\u001b'), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0) continue;
            var allowed = provider == UsageProviderKind.Codex
                ? uri.Host.Equals("auth.openai.com", StringComparison.OrdinalIgnoreCase)
                : provider == UsageProviderKind.Claude && (uri.Host.Equals("claude.ai", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals("console.anthropic.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals("platform.claude.com", StringComparison.OrdinalIgnoreCase));
            if (allowed) return uri;
        }
        return null;
    }

    private sealed class LoginDiagnostics(UsageProviderKind provider)
    {
        private readonly object _sync = new();
        private Uri? _browserUri;
        private bool _browserFailed;
        private bool _browserAttempted;
        private string? _failure;
        internal string? FailureHint => _failure;
        internal string FailureMessage => _failure ?? "로그인이 완료되지 않았습니다. 브라우저에서 개인 또는 회사 계정·워크스페이스를 선택하고 다시 시도해 주세요.";

        internal void Observe(string line)
        {
            lock (_sync)
            {
                var text = line.ToLowerInvariant();
                if (text.Contains("certificate") || text.Contains("self signed") || text.Contains("unable_to_verify"))
                    _failure = "회사 네트워크의 인증서 신뢰 설정을 확인해 주세요. TLS 검증을 해제하지 말고 회사 CA 설정을 사용하세요.";
                else if (text.Contains("proxy") || text.Contains("econn") || text.Contains("enotfound") || text.Contains("fetch failed"))
                    _failure = "로그인 서버에 연결하지 못했습니다. 회사 프록시·방화벽 및 네트워크 설정을 확인해 주세요.";
                else if (text.Contains("eaddrinuse") || text.Contains("1455") && text.Contains("bind"))
                    _failure = "로그인 콜백 포트를 사용할 수 없습니다. 진행 중인 다른 CLI 로그인을 마친 뒤 다시 시도해 주세요. Codex는 CLI의 codex login --device-auth도 사용할 수 있습니다.";
                else if (text.Contains("sso") || text.Contains("organization") && text.Contains("denied"))
                    _failure = "회사 SSO 또는 조직의 CLI 사용 권한을 확인해 주세요. Claude 회사 계정은 CLI에서 claude auth login --sso를 사용할 수 있습니다.";
                if (text.Contains("failed to open") || text.Contains("could not open") || text.Contains("unable to open") || text.Contains("cannot open browser"))
                    _browserFailed = true;
                _browserUri ??= BrowserLoginUri(provider, line);
                // Ordinary fallback instructions do not mean auto-open failed. Avoid
                // opening a duplicate browser unless the CLI explicitly reports failure.
                if (!_browserFailed || _browserUri is null || _browserAttempted) return;
                _browserAttempted = true;
                try
                {
                    using var browser = Process.Start(new ProcessStartInfo(_browserUri.AbsoluteUri) { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    _failure = provider == UsageProviderKind.Codex
                        ? "브라우저를 열 수 없습니다. CLI에서 codex login 또는 codex login --device-auth로 로그인해 주세요."
                        : "브라우저를 열 수 없습니다. CLI에서 claude auth login으로 로그인해 주세요. 회사 SSO는 --sso 옵션을 사용할 수 있습니다.";
                }
                finally { _browserUri = null; }
            }
        }
    }
}
