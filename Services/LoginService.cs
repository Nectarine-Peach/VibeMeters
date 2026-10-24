using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VibeMeters.Models;

namespace VibeMeters.Services;

public sealed record LoginResult(bool Success, string Message);

/// <summary>Runs official browser login without opening a console or retaining CLI output.</summary>
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
            try
            {
                if (!process.Start()) return new(false, "로그인을 시작할 수 없습니다.");
                process.StandardInput.Close();
                progress?.Report("브라우저에서 로그인을 완료해 주세요. 터미널 창은 열리지 않습니다.");
                // Never retain CLI output: it can contain OAuth URLs or other sensitive data.
                var output = DrainAsync(process.StandardOutput.BaseStream, deadline.Token);
                var error = DrainAsync(process.StandardError.BaseStream, deadline.Token);
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
                return process.ExitCode == 0
                    ? new(true, "로그인했습니다. 사용량을 다시 확인합니다.")
                    : new(false, "로그인이 완료되지 않았습니다. 브라우저를 확인한 뒤 다시 시도해 주세요.");
            }
            catch (OperationCanceledException)
            {
                return new(false, cancellationToken.IsCancellationRequested
                    ? "로그인을 취소했습니다." : "로그인 대기 시간이 지났습니다. 다시 로그인해 주세요.");
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

    private static async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        try { await stream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}
