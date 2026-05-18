using System;
using System.Collections.Generic;
using BlockParam.Diagnostics;

namespace BlockParam.Services;

/// <summary>
/// A TIA-session-scoped "run this expensive thing at most once per project
/// scope" gate. Fixes #155 items 2 and 3, which share the same shape: a costly
/// per-PLC walk that was re-run on <em>every</em> dialog open even though its
/// on-disk output (the per-project XML cache) already existed and nothing had
/// changed.
///
/// <list type="bullet">
///   <item><b>Tag-table export</b> (~4.6s/open): <c>TagTableExporter.Export</c>
///   wipes + re-exports every tag table from Openness on first tag-table need
///   of each dialog. The XML it produces is already on disk from a prior open.</item>
///   <item><b>UDT cache validation</b> (~2.7s/open): <c>UdtCacheRefresher.Refresh</c>
///   walks all UDTs (583 in the profiled project) checking ModifiedDate vs
///   file mtime, unconditionally on every open even when 0 are stale.</item>
/// </list>
///
/// <para>
/// Same lifetime model as #140's <see cref="DbExportCache"/>: one gate instance
/// per Add-In load, owned by <c>BulkChangeContextMenu</c>, surviving across
/// dialog opens and dying only when TIA Portal closes. The first open per
/// scope runs the action; subsequent opens skip it.
/// </para>
///
/// <para>
/// Correctness (#155 constraint — discriminator <em>or</em> explicit refresh,
/// never silent staleness): the gated walks are themselves the
/// change-discriminators (the tag-table re-export and the UDT ModifiedDate
/// freshness scan). Skipping them across opens means a tag-table / UDT edited
/// in TIA mid-session would not be picked up automatically — exactly the same
/// bar #140 already accepted for the DB export cache (a UDT change does not
/// bump the owning DB's ModifiedDate either). The valve is the existing
/// explicit refresh affordances: the dialog's "Refresh constants" /
/// "Refresh UDT types" actions call <see cref="Invalidate"/> before forcing
/// the walk, so a mid-session change is one click away, not silently stale.
/// The <c>ShowDialog()</c> modal blocks TIA edits while a dialog is open.
/// </para>
///
/// <para>
/// One reusable focused service, instantiated once per concern (one gate for
/// tag-table export, one for UDT validation) — keeps the #80/#81 hotspot seams
/// intact without a bespoke class per bottleneck.
/// </para>
/// </summary>
public interface ISessionScopeGate
{
    /// <summary>
    /// Runs <paramref name="action"/> and marks <paramref name="scope"/> done
    /// only if it has not already run (and succeeded) for that scope this
    /// session. Returns true if <paramref name="action"/> was invoked, false if
    /// it was skipped because the scope was already satisfied. If
    /// <paramref name="action"/> throws, the scope is left UN-satisfied so the
    /// next open retries — a failed export/validation must never be cached as
    /// "done".
    /// </summary>
    bool RunOnce(string scope, Action action);

    /// <summary>True if <paramref name="scope"/> has been satisfied this session.</summary>
    bool HasRun(string scope);

    /// <summary>
    /// Clears the satisfied mark for <paramref name="scope"/> so the next
    /// <see cref="RunOnce"/> re-runs the action. Called by the explicit refresh
    /// affordance — the cross-open staleness valve.
    /// </summary>
    void Invalidate(string scope);

    /// <summary>Clears every satisfied scope.</summary>
    void Clear();
}

/// <inheritdoc cref="ISessionScopeGate"/>
public sealed class SessionScopeGate : ISessionScopeGate
{
    private readonly string _label;
    private readonly object _lock = new object();
    private readonly HashSet<string> _done = new HashSet<string>(StringComparer.Ordinal);

    /// <param name="label">
    /// Short tag for log lines (e.g. "tag-table export", "UDT validation") so
    /// the two gate instances are distinguishable in the open-path log.
    /// </param>
    public SessionScopeGate(string label)
    {
        _label = label ?? "session-gate";
    }

    public bool RunOnce(string scope, Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));

        lock (_lock)
        {
            if (_done.Contains(scope))
            {
                Log.Information("{Label}: skipped (session-cached for scope {Scope})", _label, scope);
                return false;
            }
        }

        // Run OUTSIDE the lock: the action calls into TIA Openness on the UI
        // thread and may itself prompt the user (UDT compile prompt). The open
        // path is single-threaded in practice; a hypothetical duplicate
        // concurrent first-run would just do the idempotent walk twice.
        action();

        lock (_lock)
        {
            _done.Add(scope);
        }
        return true;
    }

    public bool HasRun(string scope)
    {
        lock (_lock)
            return _done.Contains(scope);
    }

    public void Invalidate(string scope)
    {
        lock (_lock)
        {
            if (_done.Remove(scope))
                Log.Information("{Label}: invalidated for scope {Scope} (explicit refresh)", _label, scope);
        }
    }

    public void Clear()
    {
        lock (_lock)
            _done.Clear();
    }
}
