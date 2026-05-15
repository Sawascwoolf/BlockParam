using BlockParam.Licensing;

namespace BlockParam.DevLauncher;

/// <summary>
/// No-op <see cref="IUsageTracker"/> for capture-script mode (#96).
/// Always grants quota so <c>ApplyCommand</c> stays enabled across
/// repeated capture runs — the daily counter in the real
/// <see cref="LocalUsageTracker"/> is never touched.
///
/// <para>
/// Injected only when a capture plan is active; interactive DevLauncher
/// sessions and the shipped Add-In always use the real tracker.
/// </para>
/// </summary>
internal sealed class UnlimitedUsageTracker : IUsageTracker
{
    public int DailyLimit => int.MaxValue;

    public UsageStatus GetStatus() => new UsageStatus(0, int.MaxValue);

    /// <summary>Always returns true — capture mode has no quota.</summary>
    public bool RecordUsage(int count) => true;
}
