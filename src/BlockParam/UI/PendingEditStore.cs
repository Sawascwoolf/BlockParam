using System.Collections.Generic;
using System.Linq;
using BlockParam.Models;

namespace BlockParam.UI;

/// <summary>
/// Session-scoped, in-memory store for pending inline-edit values, keyed by
/// <see cref="MemberNode"/> reference.
///
/// <para>
/// <b>Identity rule.</b> Member identity is <c>MemberNode</c> reference
/// equality (CLAUDE.md guardrail #82). Two leaf nodes in different active
/// DBs can share the same <c>Path</c> string (e.g. both DBs expose
/// <c>Config.Speed</c>), but each has a distinct <c>MemberNode</c> instance
/// — the one parsed from its own DB's XML. Using reference equality avoids
/// the path-string aliasing that would conflate them.
/// </para>
///
/// <para>
/// <b>Survival across tree rebuilds.</b> <see cref="ActiveDb.Info"/> (and
/// therefore its <see cref="DataBlockInfo.Members"/> graph) lives for the
/// duration of an <see cref="ActiveDb"/> instance. Active-set transitions
/// (solo, reactivate, chip-×) swap <c>MemberNodeViewModel</c> objects but
/// preserve the underlying <c>ActiveDb</c> and its <c>MemberNode</c> graph.
/// Pending-edit keys stored here therefore survive
/// <c>BuildRootMembersFromActiveDbs</c> rebuilds, and fresh VMs can seed
/// their <c>PendingValue</c> by reading from this store at construction time.
/// </para>
///
/// <para>
/// <b>Scope of a pending edit.</b> An entry lives in the store from the
/// moment the user types a value in the inline cell until one of: (a) the
/// value is applied and a <c>RefreshTree</c> re-parses the DB (producing new
/// <c>MemberNode</c> instances — old keys become dead), (b) the edit is
/// explicitly discarded, or (c) the owning <see cref="ActiveDb"/> leaves the
/// active set via stash or remove. Callers are responsible for calling
/// <see cref="ClearForNodes"/> / <see cref="ClearAll"/> at those lifecycle
/// boundaries.
/// </para>
/// </summary>
public class PendingEditStore
{
    private readonly Dictionary<MemberNode, string> _values = new();

    // ── Write operations ──────────────────────────────────────────────────

    /// <summary>
    /// Records or updates a pending inline-edit value for <paramref name="node"/>.
    /// </summary>
    public void Set(MemberNode node, string pendingValue) =>
        _values[node] = pendingValue;

    /// <summary>
    /// Removes the pending entry for <paramref name="node"/>. No-op if none exists.
    /// </summary>
    public void Clear(MemberNode node) =>
        _values.Remove(node);

    /// <summary>
    /// Removes all pending entries for the nodes belonging to
    /// <paramref name="db"/>, identified by looking them up in
    /// <paramref name="modelToDb"/>. Used when a DB is successfully applied or
    /// removed from the active set without stashing.
    /// </summary>
    public void ClearForDb(ActiveDb db, IReadOnlyDictionary<MemberNode, ActiveDb> modelToDb)
    {
        var toRemove = _values.Keys
            .Where(n => modelToDb.TryGetValue(n, out var owner) && ReferenceEquals(owner, db))
            .ToList();
        foreach (var n in toRemove) _values.Remove(n);
    }

    /// <summary>Removes all pending entries. Called on apply-all / full refresh.</summary>
    public void ClearAll() => _values.Clear();

    // ── Read operations ───────────────────────────────────────────────────

    /// <summary>
    /// Returns the pending value for <paramref name="node"/>, or null when no
    /// edit is staged.
    /// </summary>
    public string? Get(MemberNode node) =>
        _values.TryGetValue(node, out var v) ? v : null;

    /// <summary>True when a pending edit exists for <paramref name="node"/>.</summary>
    public bool HasPending(MemberNode node) =>
        _values.ContainsKey(node);

    /// <summary>
    /// Number of pending entries that belong to <paramref name="db"/>, using
    /// <paramref name="modelToDb"/> to resolve ownership.
    /// </summary>
    public int CountForDb(ActiveDb db, IReadOnlyDictionary<MemberNode, ActiveDb> modelToDb) =>
        _values.Keys.Count(n =>
            modelToDb.TryGetValue(n, out var owner) && ReferenceEquals(owner, db));

    /// <summary>
    /// Enumerates (node, pendingValue) pairs for nodes that belong to
    /// <paramref name="db"/>. Used by stash-capture and per-DB apply paths.
    /// </summary>
    public IEnumerable<(MemberNode Node, string PendingValue)> GetForDb(
        ActiveDb db,
        IReadOnlyDictionary<MemberNode, ActiveDb> modelToDb)
    {
        foreach (var kv in _values)
        {
            if (modelToDb.TryGetValue(kv.Key, out var owner) && ReferenceEquals(owner, db))
                yield return (kv.Key, kv.Value);
        }
    }

    /// <summary>
    /// All pending entries as (node, pendingValue) pairs. Used by
    /// single-DB apply and RebuildPendingEdits paths that don't need
    /// per-DB routing.
    /// </summary>
    public IEnumerable<(MemberNode Node, string PendingValue)> GetAll()
    {
        foreach (var kv in _values)
            yield return (kv.Key, kv.Value);
    }

    /// <summary>Total number of pending entries across all DBs.</summary>
    public int Count => _values.Count;
}
