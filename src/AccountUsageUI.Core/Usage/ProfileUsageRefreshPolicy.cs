namespace AccountUsageUI.Core.Usage;

public static class ProfileUsageRefreshPolicy
{
    public static readonly TimeSpan ActiveRefreshInterval =
        TimeSpan.FromSeconds(10);
    public static readonly TimeSpan InactiveRefreshInterval =
        TimeSpan.FromMinutes(3);
    public static readonly TimeSpan ActiveFailureBackoff =
        TimeSpan.FromSeconds(30);
    public static readonly TimeSpan InactiveFailureCacheTtl =
        TimeSpan.FromMinutes(1);

    /// <summary>
    /// 주기 틱에서 한 번에 수행할 최대 probe 수.
    /// </summary>
    public const int ScheduledProbeBudget = 2;

    public static bool IsDue(
        DateTimeOffset now,
        DateTimeOffset? lastAttempt,
        bool isActive,
        int consecutiveFailures)
    {
        if (lastAttempt is null)
        {
            return true;
        }

        var interval = isActive
            ? consecutiveFailures >= 3
                ? ActiveFailureBackoff
                : ActiveRefreshInterval
            : InactiveRefreshInterval;
        return now - lastAttempt.Value >= interval;
    }

    public static TimeSpan GetCacheTtl(
        ProfileQuotaStatus status,
        bool isActive)
    {
        if (status == ProfileQuotaStatus.Available)
        {
            return isActive
                ? ActiveRefreshInterval
                : InactiveRefreshInterval;
        }

        if (status == ProfileQuotaStatus.Loading)
        {
            return TimeSpan.Zero;
        }

        return isActive
            ? ActiveFailureBackoff
            : InactiveFailureCacheTtl;
    }

    public static bool IsCacheFresh(
        DateTimeOffset now,
        DateTimeOffset capturedAt,
        ProfileQuotaStatus status,
        bool isActive)
    {
        var ttl = GetCacheTtl(status, isActive);
        if (ttl <= TimeSpan.Zero)
        {
            return false;
        }

        return now - capturedAt < ttl;
    }
}
