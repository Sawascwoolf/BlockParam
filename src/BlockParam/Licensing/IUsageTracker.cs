namespace BlockParam.Licensing;

/// <summary>
/// Tracks daily usage of bulk and inline operations (Freemium model).
/// Abstracted as interface for future swap to online licensing.
/// </summary>
public interface IUsageTracker
{
    /// <summary>Returns the current bulk operation usage status.</summary>
    UsageStatus GetStatus();

    /// <summary>Records one bulk operation. Returns false if limit is reached.</summary>
    bool RecordUsage();

    /// <summary>Maximum bulk operations per day.</summary>
    int DailyLimit { get; }

    /// <summary>Returns the current inline edit usage status.</summary>
    UsageStatus GetInlineStatus();

    /// <summary>Records one inline edit. Returns false if limit is reached.</summary>
    bool RecordInlineEdit();

    /// <summary>Maximum inline edits per day.</summary>
    int InlineEditDailyLimit { get; }
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
