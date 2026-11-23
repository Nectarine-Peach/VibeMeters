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
    private readonly GeminiCliBridge? _bridge;
    private readonly Action? _resetRoute;
    internal GeminiProfile Profile { get; }
    private int _loginCompleted;
    public UsageProviderKind Kind => UsageProviderKind.Gemini;

    public GeminiUsageProvider() : this(GeminiProfile.Default) { }
    internal GeminiUsageProvider(GeminiProfile profile)
    {
        Profile = profile;
        _bridge = new GeminiCliBridge(profile);
        _read = _bridge.ReadAsync;
        _resetRoute = _bridge.ResetRoute;
        _stamp = () => CredentialStamp(_bridge.HomeDirectory);
    }
    internal Task<LoginResult> LoginAsync(CancellationToken token) => _bridge!.LoginAsync(token);
    public GeminiUsageProvider(Func<CancellationToken, Task<UsageSnapshot>> read, Func<long> credentialStamp)
    { _read = read; _stamp = credentialStamp; }
    public void NotifyLoginCompleted() => Interlocked.Exchange(ref _loginCompleted, 1);

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default, UsageFetchOptions options = default)
    {
        var requestedAt = options.RequestedAt ?? DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stamp = _stamp();
            var rateLimited = _last?.Status == UsageStatus.RateLimited && DateTimeOffset.UtcNow < _nextAttempt;
            if (Interlocked.Exchange(ref _loginCompleted, 0) != 0 || stamp != _credentialStamp)
            {
                _resetRoute?.Invoke();
                _credentialStamp = stamp;
                _last = rateLimited ? _last! with { Windows = [], Plan = null, ObservedAt = null, Message = "계정이 변경되었습니다. 기존 요청 제한 대기 후 사용량을 확인합니다." } : null;
                if (!rateLimited) _nextAttempt = default;
            }
            var now = DateTimeOffset.UtcNow;
            if (RefreshPolicy.CanReuse(_last, _nextAttempt, now, options, requestedAt))
            {
                if (_last!.Status == UsageStatus.RateLimited) _last = WithoutExpiredWindows(_last, now);
                return RefreshPolicy.Reuse(_last, _nextAttempt);
            }
            var snapshot = await _read(cancellationToken).ConfigureAwait(false);
            var currentStamp = _stamp();
            var changed = Interlocked.Exchange(ref _loginCompleted, 0) != 0 || stamp != currentStamp;
            if (changed)
            {
                _last = null;
                _resetRoute?.Invoke();
                if (snapshot.Status != UsageStatus.RateLimited)
                {
                    // A refresh-token write or a real account switch can both move the
                    // stamp. Only trust an answer observed against a stable account.
                    cancellationToken.ThrowIfCancellationRequested();
                    stamp = currentStamp;
                    snapshot = await _read(cancellationToken).ConfigureAwait(false);
                    currentStamp = _stamp();
                    changed = Interlocked.Exchange(ref _loginCompleted, 0) != 0 || stamp != currentStamp;
                    if (changed) _resetRoute?.Invoke();
                }
                if (changed)
                {
                    snapshot = snapshot.Status == UsageStatus.RateLimited
                        ? snapshot with { Windows = [], Plan = null, ObservedAt = null, Message = "계정이 변경되었습니다. 기존 요청 제한 대기 후 사용량을 확인합니다." }
                        : new(Kind, UsageStatus.Unavailable, [], DateTimeOffset.UtcNow, Message: "조회 중 로그인 정보가 변경되었습니다. 새로고침해 주세요.");
                }
            }
            _credentialStamp = currentStamp;
            // Keyring identity is owned by agy and not exposed in /usage. Requery every
            // refresh and never carry numbers through a failure or account switch.
            var antigravity = snapshot.Plan == AntigravityUsageBridge.Source;
            var checkedAt = DateTimeOffset.UtcNow;
            if (!antigravity && snapshot.Status == UsageStatus.Ready && snapshot.Windows.Count > 0)
            {
                snapshot = WithoutExpiredWindows(snapshot, checkedAt);
                if (snapshot.Windows.Count == 0)
                    snapshot = snapshot with { Status = UsageStatus.Unavailable, Message = "초기화 대기" };
            }
            if (snapshot.Status == UsageStatus.RateLimited)
            {
                _nextAttempt = checkedAt.AddMinutes(10);
                if (snapshot.RetryAfter is { } serverDeadline && serverDeadline > _nextAttempt)
                    _nextAttempt = serverDeadline;
                snapshot = snapshot with { RetryAfter = _nextAttempt };
            }
            else if (antigravity) _nextAttempt = default;
            else if (snapshot.Status == UsageStatus.Ready)
            {
                _nextAttempt = checkedAt.AddMinutes(5);
                foreach (var window in snapshot.Windows)
                    if (window.ResetsAt is { } reset && reset > checkedAt && reset < _nextAttempt) _nextAttempt = reset;
            }
            else _nextAttempt = snapshot.Status == UsageStatus.AuthenticationRequired
                ? checkedAt.AddMinutes(5) : checkedAt.AddSeconds(30);
            if (!antigravity && snapshot.Status == UsageStatus.RateLimited && _last is { Windows.Count: > 0 } previous
                && previous.Plan != AntigravityUsageBridge.Source)
            {
                previous = WithoutExpiredWindows(previous, checkedAt);
                if (previous.Windows.Count > 0)
                    snapshot = snapshot with { Windows = previous.Windows, Plan = previous.Plan,
                        Message = "이전 조회값 표시 중 · " + snapshot.Message };
            }
            _last = snapshot with { CheckedAt = checkedAt,
                ObservedAt = snapshot.Status == UsageStatus.Ready ? checkedAt : snapshot.Windows.Count > 0 ? _last?.ObservedAt : null,
                NextQueryAt = _nextAttempt > checkedAt ? _nextAttempt : null,
                WaitReason = snapshot.Status == UsageStatus.Ready ? UsageWaitReason.None : RefreshPolicy.Reason(snapshot.Status) };
            return _last;
        }
        finally { _gate.Release(); }
    }

    private static UsageSnapshot WithoutExpiredWindows(UsageSnapshot snapshot, DateTimeOffset now)
    {
        if (!snapshot.Windows.Any(w => w.ResetsAt <= now)) return snapshot;
        var active = snapshot.Windows.Where(w => w.ResetsAt is null || w.ResetsAt > now).ToArray();
        const string previous = "이전 조회값 표시 중 · ";
        var message = snapshot.Message;
        if (active.Length == 0 && message?.StartsWith(previous, StringComparison.Ordinal) == true)
            message = message[previous.Length..];
        return snapshot with { Windows = active, ObservedAt = active.Length > 0 ? snapshot.ObservedAt : null, Message = message };
    }

    private static long CredentialStamp(string profileHome)
    {
        try
        {
            var home = Path.Combine(profileHome, ".gemini");
            return HashCode.Combine(File.GetLastWriteTimeUtc(Path.Combine(home, "oauth_creds.json")).Ticks,
                File.GetLastWriteTimeUtc(Path.Combine(home, "token-storage.json")).Ticks,
                File.GetLastWriteTimeUtc(Path.Combine(home, "gemini-credentials.json")).Ticks,
                File.GetLastWriteTimeUtc(Path.Combine(home, "google_accounts.json")).Ticks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
