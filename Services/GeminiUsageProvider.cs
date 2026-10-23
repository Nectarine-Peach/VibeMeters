using System;
using System.Threading;
using System.Threading.Tasks;
using VibeMeters.Models;

namespace VibeMeters.Services;

/// <summary>
/// Gemini's supported quota view is the official CLI /stats command. Its OAuth
/// backend is not a public usage API for third-party tools, so no percentage is invented.
/// </summary>
public sealed class GeminiUsageProvider : IUsageProvider
{
    public UsageProviderKind Kind => UsageProviderKind.Gemini;

    public Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new UsageSnapshot(Kind, UsageStatus.Unavailable, [], DateTimeOffset.UtcNow,
            Message: "공식 Gemini CLI의 /stats에서 사용량을 확인하세요."));
    }
}
