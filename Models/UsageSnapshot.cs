using System;
using System.Collections.Generic;

namespace VibeMeters.Models;

public enum UsageProviderKind { Codex, Claude, Gemini, Grok }
public enum UsageStatus { Ready, NotConnected, AuthenticationRequired, RateLimited, Unavailable, AuthenticationRecovering, AuthenticationRetry }
public enum UsageWaitReason { None, SuccessCache, RateLimited, AuthenticationBackoff, FailureBackoff, AuthenticationRecovery }
public readonly record struct UsageFetchOptions(bool BypassSuccessCache = false, DateTimeOffset? RequestedAt = null, bool RecoverAuthentication = false);

public sealed record UsageWindow(string Name, double UsedPercent, DateTimeOffset? ResetsAt, TimeSpan? Duration = null)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

public sealed record UsageSnapshot(
    UsageProviderKind Provider,
    UsageStatus Status,
    IReadOnlyList<UsageWindow> Windows,
    DateTimeOffset CheckedAt,
    string? Plan = null,
    string? Message = null,
    DateTimeOffset? RetryAfter = null,
    DateTimeOffset? ObservedAt = null,
    DateTimeOffset? NextQueryAt = null,
    UsageWaitReason WaitReason = UsageWaitReason.None,
    bool IsCached = false,
    DateTimeOffset? NextAutomaticAt = null,
    bool AuthenticationWorkPending = false);
