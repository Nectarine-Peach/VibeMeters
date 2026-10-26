using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VibeMeters.Models;

namespace VibeMeters.Services;

/// <summary>
/// Reads quota through the official Gemini CLI core in a short-lived hidden process.
/// </summary>
public sealed class GeminiUsageProvider : IUsageProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UsageSnapshot? _last;
    private DateTimeOffset _nextAttempt;
    private long _credentialStamp;
    private readonly Func<CancellationToken, Task<UsageSnapshot>> _read;
    private readonly Func<long> _stamp;
    private int _loginCompleted;
    public UsageProviderKind Kind => UsageProviderKind.Gemini;

    public GeminiUsageProvider() : this(GeminiCliBridge.ReadAsync, CredentialStamp) { }
    public GeminiUsageProvider(Func<CancellationToken, Task<UsageSnapshot>> read, Func<long> credentialStamp)
    { _read = read; _stamp = credentialStamp; }
    public void NotifyLoginCompleted() => Interlocked.Exchange(ref _loginCompleted, 1);

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stamp = _stamp();
            var rateLimited = _last?.Status == UsageStatus.RateLimited && DateTimeOffset.UtcNow < _nextAttempt;
            if (Interlocked.Exchange(ref _loginCompleted, 0) != 0 || stamp != _credentialStamp)
            {
                if (stamp != _credentialStamp) GeminiCliBridge.ResetRoute();
                _credentialStamp = stamp;
                _last = rateLimited ? _last! with { Windows = [], Message = "계정이 변경되었습니다. 기존 요청 제한 대기 후 사용량을 확인합니다." } : null;
                if (!rateLimited) _nextAttempt = default;
            }
            if (_last is not null && DateTimeOffset.UtcNow < _nextAttempt) return _last;
            var snapshot = await _read(cancellationToken).ConfigureAwait(false);
            _credentialStamp = _stamp(); // Official client may renew its own credentials.
            // Keyring identity is owned by agy and not exposed in /usage. Requery every
            // refresh and never carry numbers through a failure or account switch.
            var antigravity = snapshot.Plan == AntigravityUsageBridge.Source;
            _nextAttempt = antigravity && snapshot.Status != UsageStatus.RateLimited ? default
                : DateTimeOffset.UtcNow.AddMinutes(snapshot.Status == UsageStatus.RateLimited ? 10 : 5);
            if (snapshot.Status == UsageStatus.RateLimited) snapshot = snapshot with { RetryAfter = _nextAttempt };
            if (!antigravity && snapshot.Status != UsageStatus.Ready && _last?.Windows.Count > 0
                && _last.Plan != AntigravityUsageBridge.Source)
                snapshot = snapshot with { Windows = _last.Windows, Plan = _last.Plan,
                    Message = "이전 조회값 표시 중 · " + snapshot.Message };
            _last = snapshot;
            return snapshot;
        }
        finally { _gate.Release(); }
    }

    private static long CredentialStamp()
    {
        try
        {
            var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
            return Math.Max(File.GetLastWriteTimeUtc(Path.Combine(home, "oauth_creds.json")).Ticks,
                File.GetLastWriteTimeUtc(Path.Combine(home, "token-storage.json")).Ticks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
