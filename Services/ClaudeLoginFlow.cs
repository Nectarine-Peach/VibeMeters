using System;
using System.Threading;
using System.Threading.Tasks;
using VibeMeters.Models;

namespace VibeMeters.Services;

public sealed record ClaudeLoginFlowResult(bool LoginStarted, LoginResult Result, UsageSnapshot Snapshot);

public static class ClaudeLoginFlow
{
    public static async Task<ClaudeLoginFlowResult> RunAsync(
        Func<CancellationToken, Task<UsageSnapshot>> recover,
        Func<CancellationToken, Task<LoginResult>> login,
        CancellationToken cancellationToken = default, IClaudeAuthDiagnostics? diagnostics = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await recover(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Status == UsageStatus.Ready)
        {
            diagnostics?.Write(ClaudeAuthEvent.PreflightReady);
            return new(false, new(true, "기존 인증으로 사용량을 확인했습니다. 브라우저 로그인은 열지 않았습니다."), snapshot);
        }
        if (snapshot.Status == UsageStatus.RateLimited)
        {
            diagnostics?.Write(ClaudeAuthEvent.PreflightLimited);
            return new(false, new(false, "사용량 조회 요청이 제한되어 있습니다. 보호 대기 후 다시 확인하며 로그인 창은 열지 않습니다."), snapshot);
        }
        if (snapshot.AuthenticationWorkPending || snapshot.Status == UsageStatus.AuthenticationRecovering
            || snapshot.Status == UsageStatus.Unavailable && snapshot.WaitReason != UsageWaitReason.AuthenticationBackoff)
            return new(false, new(false, "아직 조회 또는 인증 확인이 진행 중입니다. 잠시 후 다시 확인해 주세요."), snapshot);
        // An explicit user login request may proceed after inconclusive bounded recovery.
        diagnostics?.Write(ClaudeAuthEvent.BrowserLoginRequested);
        cancellationToken.ThrowIfCancellationRequested();
        return new(true, await login(cancellationToken), snapshot);
    }
}
