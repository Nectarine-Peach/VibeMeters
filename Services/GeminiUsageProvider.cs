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
    public UsageProviderKind Kind => UsageProviderKind.Gemini;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stamp = CredentialStamp();
            if (stamp != _credentialStamp)
            {
                _credentialStamp = stamp;
                _last = null;
                _nextAttempt = default;
            }
            if (_last is not null && DateTimeOffset.UtcNow < _nextAttempt) return _last;
            var snapshot = await GeminiCliBridge.ReadAsync(cancellationToken).ConfigureAwait(false);
            _credentialStamp = CredentialStamp(); // Official client may renew its own credentials.
            _nextAttempt = DateTimeOffset.UtcNow.AddMinutes(snapshot.Status == UsageStatus.RateLimited ? 10 : 5);
            if (snapshot.Status != UsageStatus.Ready && _last?.Windows.Count > 0)
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
