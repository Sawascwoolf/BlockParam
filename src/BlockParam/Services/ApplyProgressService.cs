using System;
using System.Threading;
using BlockParam.Localization;
using BlockParam.UI;

namespace BlockParam.Services;

/// <summary>
/// Apply-time progress feedback (#146). The TIA Openness import is
/// synchronous and bound to the TIA process thread — we cannot move it off
/// the dispatcher. Instead, the splash runs on its OWN STA dispatcher (same
/// mechanism as the open-time splash, #125), so the indeterminate
/// progress + "Applying…" status stay painted even while TIA's UI thread
/// is frozen inside <c>Blocks.Import</c>.
///
/// <para>The splash is also <c>Topmost</c>, which addresses the second
/// half of #146: TIA's main window activates during import and would
/// otherwise bury the ownerless BulkChangeDialog. The splash sits above
/// TIA the entire time; after dispose, the dialog itself re-foregrounds
/// via a brief Topmost flip (see <see cref="BulkChangeDialog"/>).</para>
///
/// <para>Tests/DevLauncher inject <see cref="NoOpApplyProgressService"/>;
/// production wires <see cref="WpfApplyProgressService"/> from
/// <c>BulkChangeContextMenu</c>.</para>
/// </summary>
public interface IApplyProgressService
{
    /// <summary>
    /// Begins an Apply progress session. Caller must dispose the returned
    /// handle (e.g. via <c>using</c>) — that dismisses the splash.
    /// </summary>
    IApplyProgressHandle Begin(string initialStatus);
}

/// <summary>
/// Live handle to one Apply progress session. Disposing closes the splash.
/// </summary>
public interface IApplyProgressHandle : IDisposable
{
    /// <summary>Updates the primary status line ("Applying changes to DB_X…").</summary>
    void Report(string status);

    /// <summary>
    /// Shows a success summary on the splash and blocks the caller for
    /// <paramref name="holdMs"/> milliseconds so the user actually sees it
    /// before the splash (and typically the host dialog) closes. The splash
    /// continues to paint during the hold because it lives on its own
    /// dispatcher.
    /// </summary>
    void ShowSummaryAndClose(string summary, int holdMs);
}

/// <summary>
/// Production implementation backed by <see cref="LoadingSplashController"/>
/// (the same cross-thread splash used at dialog open, #125). Every
/// <see cref="Begin"/> call spawns a fresh STA dispatcher thread; dispose
/// tears it back down. The cost (~few ms) is bounded — Apply is a rare,
/// user-initiated gesture.
/// </summary>
public sealed class WpfApplyProgressService : IApplyProgressService
{
    public IApplyProgressHandle Begin(string initialStatus)
    {
        var splash = new LoadingSplashController(Res.Get("Apply_Progress_Title"));
        splash.Show();
        splash.Report(initialStatus);
        return new WpfApplyProgressHandle(splash);
    }

    private sealed class WpfApplyProgressHandle : IApplyProgressHandle
    {
        private readonly LoadingSplashController _splash;
        private int _disposed;

        public WpfApplyProgressHandle(LoadingSplashController splash)
        {
            _splash = splash;
        }

        public void Report(string status)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _splash.Report(status);
        }

        public void ShowSummaryAndClose(string summary, int holdMs)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _splash.Report(summary);
            if (holdMs > 0)
            {
                // Thread.Sleep on the TIA dispatcher is OK here: the splash
                // paints on its own thread, the host dialog is about to
                // close (or already in a stable post-Apply state), and the
                // hold is short enough to be perceived as confirmation
                // rather than a hang.
                Thread.Sleep(holdMs);
            }
            _splash.Close();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _splash.Close();
        }
    }
}

/// <summary>
/// No-op fallback used by unit tests and DevLauncher so Apply paths don't
/// spin up an STA dispatcher window during headless runs.
/// </summary>
public sealed class NoOpApplyProgressService : IApplyProgressService
{
    public IApplyProgressHandle Begin(string initialStatus) => NoOpHandle.Instance;

    private sealed class NoOpHandle : IApplyProgressHandle
    {
        public static readonly NoOpHandle Instance = new();
        public void Report(string status) { }
        public void ShowSummaryAndClose(string summary, int holdMs) { }
        public void Dispose() { }
    }
}
