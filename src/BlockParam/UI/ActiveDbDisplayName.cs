using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockParam.UI;

/// <summary>
/// Single source of truth for the collision-safe DB display string used
/// across the dialog (#82). Two PLCs can each host a DB with the same name
/// (e.g. both have <c>DB_Foo</c>); when that happens the name alone is
/// ambiguous, so the string is prefixed with the owning PLC.
///
/// <para>
/// Extracted from <see cref="MemberTreeViewModel"/>'s multi-DB tree builder
/// (the synthetic group-root label logic) so the Pending Edits row label
/// (#145) renders <b>exactly</b> the same string as the tree's DB group
/// header — there is intentionally only one formatter. Do not duplicate
/// this rule; call this helper.
/// </para>
///
/// <para>
/// The collision lookup depends only on the active-DB set, so a caller that
/// formats many DBs (the tree builder loop, the per-row pending resolver)
/// builds one <see cref="ActiveDbDisplayName"/> instance and reuses it: the
/// name-count map is computed once in the constructor (O(dbs)) rather than
/// rebuilt per call. Each <see cref="Resolve"/> is O(dbs), not O(1) — it
/// runs a <see cref="object.ReferenceEquals"/> scan over the active-DB list
/// to recover the index for the anchor-PLC fallback. With the realistic
/// handful of active DBs this is negligible; if the active set ever grows
/// large, pass the index in and drop the scan.
/// </para>
/// </summary>
internal sealed class ActiveDbDisplayName
{
    private readonly IReadOnlyList<ActiveDb> _dbs;
    private readonly Dictionary<string, int> _nameCounts;
    private readonly string _anchorPlcFallback;

    internal ActiveDbDisplayName(
        IReadOnlyList<ActiveDb> allActiveDbs,
        string anchorPlcFallback)
    {
        _dbs = allActiveDbs;
        _anchorPlcFallback = anchorPlcFallback;
        _nameCounts = allActiveDbs
            .GroupBy(d => d.Info.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves the display string for <paramref name="db"/> within the
    /// active-DB set this instance was built for. When another active DB
    /// shares the same <see cref="Models.DataBlockInfo.Name"/> the result is
    /// <c>"{plc} / {name}"</c> (PLC taken from <see cref="ActiveDb.PlcName"/>,
    /// falling back to the constructor's anchor-PLC value for the index-0 DB
    /// when the host seeded the anchor with an empty PLC name); otherwise it
    /// is the bare DB name.
    /// </summary>
    internal string Resolve(ActiveDb db)
    {
        // #82: every ActiveDb (including the anchor) carries its own PlcName,
        // so the prefix lookup is uniform — no index-0 special case. The
        // anchor fallback is kept for back-compat with hosts that may still
        // seed the anchor with an empty PlcName; prefer db.PlcName when set.
        int index = -1;
        for (int i = 0; i < _dbs.Count; i++)
        {
            if (ReferenceEquals(_dbs[i], db)) { index = i; break; }
        }

        var plc = !string.IsNullOrEmpty(db.PlcName)
            ? db.PlcName
            : (index == 0 ? _anchorPlcFallback : "");

        bool collides = _nameCounts.TryGetValue(db.Info.Name, out var c) && c > 1;
        return collides && !string.IsNullOrEmpty(plc)
            ? $"{plc} / {db.Info.Name}"
            : db.Info.Name;
    }

    /// <summary>
    /// Builds the per-node DB-label resolver the Pending Edits slice (#145)
    /// threads into <c>PendingEditsViewModel.Rebuild</c>. The returned
    /// delegate maps a tree node to its owning-DB display string via
    /// <see cref="MemberTreeViewModel.FindActiveDbForModel"/> (the #82
    /// model→ActiveDb index — never a path scan).
    ///
    /// <para>
    /// Conditional display (the recommended #145 design): when one or zero
    /// DBs are active the resolver returns <see cref="string.Empty"/> so
    /// single-DB rows stay unlabeled (no needless noise); the qualifier only
    /// appears once <c>allActiveDbs.Count &gt; 1</c>. Centralised here so the
    /// gate isn't re-implemented in the BulkChangeViewModel god class (#80).
    /// The collision map is built once here, not per pending row.
    /// </para>
    /// </summary>
    internal static Func<MemberNodeViewModel, string> ResolverFor(
        MemberTreeViewModel tree,
        IReadOnlyList<ActiveDb> allActiveDbs,
        string anchorPlcFallback)
    {
        // Single-/zero-DB session: never qualify. Returning a constant-empty
        // delegate keeps the per-row hot path allocation-free.
        if (allActiveDbs.Count <= 1)
            return _ => string.Empty;

        var formatter = new ActiveDbDisplayName(allActiveDbs, anchorPlcFallback);
        return node =>
        {
            var owner = tree.FindActiveDbForModel(node.Model);
            return owner == null ? string.Empty : formatter.Resolve(owner);
        };
    }
}
