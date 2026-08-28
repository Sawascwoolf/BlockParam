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
///
/// <para>
/// <see cref="DailyLimit"/> is deliberately <see cref="int.MaxValue"/> and is
/// never rendered: capture mode also runs under a seeded Pro license (#198,
/// see <see cref="ProLicenseSandbox"/>), so the status bar shows
/// <c>Status_Pro</c> rather than the <c>Status_Remaining</c> counter that
/// would otherwise print the raw number into a marketing frame. Program.cs
/// fails the run if that Pro seed ever stops taking effect.
/// </para>
/// </summary>
internal sealed class UnlimitedUsageTracker : IUsageTracker
{
    public int DailyLimit => int.MaxValue;

    public UsageStatus GetStatus() => new UsageStatus(0, int.MaxValue);

    /// <summary>Always returns true — capture mode has no quota.</summary>
    public bool RecordUsage(int count) => true;
}
