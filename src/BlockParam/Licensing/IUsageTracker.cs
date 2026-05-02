namespace BlockParam.Licensing;

/// <summary>
/// Tracks daily usage of value changes (Freemium model).
/// Free tier: 200 individual value changes per calendar day, charged on
/// successful Apply. Bulk-staged and inline-edited changes count the same
/// — every committed value-write is one unit. Abstracted as interface for
/// future swap to online licensing.
/// </summary>
public interface IUsageTracker
{
    /// <summary>Returns the current usage status (count vs daily limit).</summary>
    UsageStatus GetStatus();

    /// <summary>
    /// Records <paramref name="count"/> value-changes against today's quota.
    /// Atomic: returns true and increments only if the full <paramref name="count"/>
    /// fits under <see cref="DailyLimit"/>; otherwise returns false and leaves
    /// the counter untouched (callers must block the entire Apply, not write a
    /// partial batch).
    /// </summary>
    bool RecordUsage(int count);

    /// <summary>Maximum value-changes per day for the free tier.</summary>
    int DailyLimit { get; }
}

public class UsageStatus
{
    public UsageStatus(int usedToday, int dailyLimit)
    {
        UsedToday = usedToday;
        DailyLimit = dailyLimit;
    }

    public int UsedToday { get; }
    public int DailyLimit { get; }
    public int RemainingToday => Math.Max(0, DailyLimit - UsedToday);
    public bool IsLimitReached => UsedToday >= DailyLimit;
}
