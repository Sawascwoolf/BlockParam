using System.Collections.Generic;

namespace BlockParam.UI;

/// <summary>
/// Immutable snapshot of the dialog's active-DB set + interlocked state
/// (#78). Every mutation that touches the active set, the per-DB stash
/// dictionary, or the anchor PLC builds a new snapshot and assigns it
/// to <see cref="BulkChangeViewModel.State"/>. The setter is the single
/// source of cascade — <c>RebuildAfterActiveSetChanged</c> /
/// <c>SyncStashedDbsCollection</c> / anchor-display refresh are only
/// called from <c>OnActiveSetChanged</c>, so forgetting to refresh after
/// a mutation is structurally impossible.
///
/// <para>
/// Compound mutations (solo, reactivate-then-solo) build the new snapshot
/// in locals and assign once → exactly one cascade per user gesture,
/// regardless of how many DBs were swapped in or out. Cancellation =
/// don't assign; the dialog stays on the previous snapshot.
/// </para>
///
/// <para>
/// Storage uses plain <see cref="IReadOnlyList{T}"/> /
/// <see cref="IReadOnlyDictionary{TKey,TValue}"/> rather than the
/// <c>System.Collections.Immutable</c> NuGet types so net48 builds don't
/// pull a new transitive dependency. Mutators construct fresh List /
/// Dictionary instances per snapshot — the active set is small (typically
/// 1–3 DBs), so allocation cost is negligible compared to the existing
/// per-rebuild tree-VM construction.
/// </para>
/// </summary>
public sealed class ActiveSetState
{
    public ActiveSetState(
        IReadOnlyList<ActiveDb> dbs,
        IReadOnlyDictionary<string, StashedDbState> stashes,
        string anchorPlcName)
    {
        Dbs = dbs;
        Stashes = stashes;
        AnchorPlcName = anchorPlcName;
    }

    /// <summary>Active DBs in storage order. Index 0 is the anchor.</summary>
    public IReadOnlyList<ActiveDb> Dbs { get; }

    /// <summary>
    /// Per-DB pending-edit stashes. Keyed by
    /// <c>StashKey(summary)</c> = <c>"{PlcName}{FolderPath}{Name}"</c>.
    /// </summary>
    public IReadOnlyDictionary<string, StashedDbState> Stashes { get; }

    /// <summary>
    /// PLC name displayed alongside the anchor DB. Mirrors the host-supplied
    /// <c>currentPlcName</c> on construction; updates as the anchor shifts
    /// during remove / solo so the title-bar PLC label stays accurate.
    /// </summary>
    public string AnchorPlcName { get; }

    /// <summary>Returns a new snapshot with the supplied fields replaced.</summary>
    public ActiveSetState With(
        IReadOnlyList<ActiveDb>? dbs = null,
        IReadOnlyDictionary<string, StashedDbState>? stashes = null,
        string? anchorPlcName = null)
        => new ActiveSetState(
            dbs ?? Dbs,
            stashes ?? Stashes,
            anchorPlcName ?? AnchorPlcName);
}
