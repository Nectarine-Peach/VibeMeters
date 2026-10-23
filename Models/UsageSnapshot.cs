using System;
using System.Collections.Generic;

namespace VibeMeters.Models;

public enum UsageProviderKind { Codex, Claude, Gemini }
public enum UsageStatus { Ready, NotConnected, AuthenticationRequired, RateLimited, Unavailable }

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
    DateTimeOffset? RetryAfter = null);
