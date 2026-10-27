using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VibeMeters.Models;

namespace VibeMeters.Services;

public sealed class GrokUsageProvider : IUsageProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<CancellationToken, Task<UsageSnapshot>> _read;
    private readonly Func<long> _stamp;
    private UsageSnapshot? _last;
    private DateTimeOffset _nextAttempt;
    private long _credentialStamp;
    private int _loginGeneration, _observedGeneration;
    public UsageProviderKind Kind => UsageProviderKind.Grok;
    public GrokUsageProvider() : this(GrokCliBridge.ReadAsync, CredentialStamp) { }
    public GrokUsageProvider(Func<CancellationToken, Task<UsageSnapshot>> read, Func<long> credentialStamp)
    { _read = read; _stamp = credentialStamp; }
    public void NotifyLoginCompleted() => Interlocked.Increment(ref _loginGeneration);

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default, UsageFetchOptions options = default)
    {
        var requestedAt = options.RequestedAt ?? DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stamp = _stamp(); var generation = Volatile.Read(ref _loginGeneration);
            var limited = _last?.Status == UsageStatus.RateLimited && DateTimeOffset.UtcNow < _nextAttempt;
            if (stamp != _credentialStamp || generation != _observedGeneration)
            {
                _credentialStamp = stamp; _observedGeneration = generation;
                _last = limited ? _last! with { Windows = [], ObservedAt = null, Message = "로그인이 변경되었습니다. 기존 요청 제한 대기 후 다시 확인합니다." } : null;
                if (!limited) _nextAttempt = default;
            }
            if (RefreshPolicy.CanReuse(_last, _nextAttempt, DateTimeOffset.UtcNow, options, requestedAt)) return RefreshPolicy.Reuse(_last!, _nextAttempt);
            var snapshot = await _read(cancellationToken).ConfigureAwait(false);
            var currentStamp = _stamp(); var currentGeneration = Volatile.Read(ref _loginGeneration);
            if (stamp != currentStamp || generation != currentGeneration)
            {
                if (snapshot.Status == UsageStatus.RateLimited)
                {
                    _credentialStamp = currentStamp; _observedGeneration = currentGeneration;
                    _nextAttempt = DateTimeOffset.UtcNow.AddMinutes(10);
                    return _last = snapshot with { Windows = [], ObservedAt = null, RetryAfter = _nextAttempt,
                        NextQueryAt = _nextAttempt, WaitReason = UsageWaitReason.RateLimited,
                        Message = "로그인 정보가 변경되었습니다. 기존 요청 제한 대기 후 다시 확인합니다." };
                }
                _last = null; _nextAttempt = default;
                return GrokCliBridge.Failure(UsageStatus.Unavailable, "조회 중 로그인 정보가 변경되었습니다. 새로고침해 주세요.");
            }
            _nextAttempt = snapshot.Status switch
            {
                UsageStatus.Ready => DateTimeOffset.UtcNow.AddMinutes(2),
                UsageStatus.RateLimited => DateTimeOffset.UtcNow.AddMinutes(10),
                UsageStatus.AuthenticationRequired => DateTimeOffset.MaxValue,
                UsageStatus.NotConnected => DateTimeOffset.UtcNow.AddSeconds(30),
                _ => DateTimeOffset.UtcNow.AddSeconds(30)
            };
            if (snapshot.Status == UsageStatus.RateLimited) snapshot = snapshot with { RetryAfter = _nextAttempt };
            // Errors always have empty windows, including after account changes. Never borrow old values.
            var checkedAt = DateTimeOffset.UtcNow;
            _last = snapshot with { CheckedAt = checkedAt, Windows = snapshot.Status == UsageStatus.Ready ? snapshot.Windows : [],
                ObservedAt = snapshot.Status == UsageStatus.Ready ? checkedAt : null,
                NextQueryAt = _nextAttempt == DateTimeOffset.MaxValue ? null : _nextAttempt,
                WaitReason = snapshot.Status == UsageStatus.Ready ? UsageWaitReason.None : RefreshPolicy.Reason(snapshot.Status) };
            return _last;
        }
        finally { _gate.Release(); }
    }

    private static long CredentialStamp()
    {
        try
        {
            var path = GrokAuthPath.Resolve();
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return 0; }
    }
}

/// <summary>Matches the official CLI's environment/working-directory path contract; never reads credentials.</summary>
internal static class GrokAuthPath
{
    internal static string Resolve(string? userProfile = null)
    {
        var profile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var explicitPath = Environment.GetEnvironmentVariable("GROK_AUTH_PATH");
        if (!string.IsNullOrEmpty(explicitPath)) return Path.GetFullPath(explicitPath, profile);
        var configuredHome = Environment.GetEnvironmentVariable("GROK_HOME");
        var home = configuredHome is null ? Path.Combine(profile, ".grok")
            : Path.GetFullPath(configuredHome.Length == 0 ? "." : configuredHome, profile);
        return Path.Combine(home, "auth.json");
    }
}
