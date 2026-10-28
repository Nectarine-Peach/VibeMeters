using System;
using VibeMeters.Models;

namespace VibeMeters.Services;

public enum RefreshTrigger { Automatic, Manual, Credentials, Login, Resume, Activation }

/// <summary>UI-thread request gate. Preserve one manual request while a bulk lookup is running.</summary>
public sealed class RefreshRequestGate
{
    public bool IsRunning { get; private set; }
    private DateTimeOffset? _pendingManual;

    public bool TryBegin(RefreshTrigger trigger, DateTimeOffset requestedAt)
    {
        if (IsRunning)
        {
            if (trigger == RefreshTrigger.Manual) _pendingManual ??= requestedAt;
            return false;
        }
        IsRunning = true;
        return true;
    }

    public DateTimeOffset? Complete()
    {
        IsRunning = false;
        var pending = _pendingManual;
        _pendingManual = null;
        return pending;
    }
}

public static class RefreshPolicy
{
    public static bool BypassSuccessCache(RefreshTrigger trigger) => trigger is RefreshTrigger.Manual or RefreshTrigger.Automatic;

    public static bool CanReuse(UsageSnapshot? last, DateTimeOffset next, DateTimeOffset now,
        UsageFetchOptions options, DateTimeOffset requestedAt) => last is not null && now < next
        && (!options.BypassSuccessCache || last.Status != UsageStatus.Ready || last.CheckedAt >= requestedAt);

    public static UsageSnapshot Reuse(UsageSnapshot snapshot, DateTimeOffset next) => snapshot with
    {
        IsCached = true,
        NextQueryAt = next == DateTimeOffset.MaxValue ? null : next,
        WaitReason = snapshot.Status == UsageStatus.Ready ? UsageWaitReason.SuccessCache
            : snapshot.WaitReason != UsageWaitReason.None ? snapshot.WaitReason : Reason(snapshot.Status)
    };

    public static UsageWaitReason Reason(UsageStatus status) => status switch
    {
        UsageStatus.Ready => UsageWaitReason.SuccessCache,
        UsageStatus.RateLimited => UsageWaitReason.RateLimited,
        UsageStatus.AuthenticationRequired or UsageStatus.NotConnected => UsageWaitReason.AuthenticationBackoff,
        UsageStatus.AuthenticationRetry or UsageStatus.AuthenticationRecovering => UsageWaitReason.AuthenticationRecovery,
        _ => UsageWaitReason.FailureBackoff
    };

    public static DateTimeOffset? NextAutomatic(DateTimeOffset now, int intervalSeconds, bool suspended)
        => !suspended && intervalSeconds > 0 ? now.AddSeconds(Math.Max(30, intervalSeconds)) : null;

    public static DateTimeOffset? NextAutomaticQuery(UsageSnapshot snapshot, DateTimeOffset? nextTick, int intervalSeconds)
    {
        if (nextTick is null || intervalSeconds <= 0) return null;
        if (snapshot.Status == UsageStatus.AuthenticationRecovering) return null;
        if (snapshot.Status is UsageStatus.AuthenticationRequired or UsageStatus.NotConnected && snapshot.NextQueryAt is null) return null;
        if (snapshot.Status == UsageStatus.Ready || snapshot.NextQueryAt is not { } earliest || earliest <= nextTick) return nextTick;
        var intervalTicks = TimeSpan.FromSeconds(Math.Max(30, intervalSeconds)).Ticks;
        var ticks = earliest.UtcTicks - nextTick.Value.UtcTicks;
        return nextTick.Value.AddTicks(((ticks + intervalTicks - 1) / intervalTicks) * intervalTicks);
    }
}
