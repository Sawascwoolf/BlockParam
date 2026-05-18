using System;
using System.Collections.Generic;
using BlockParam.Diagnostics;
using BlockParam.Models;

namespace BlockParam.Services;

/// <summary>
/// In-process, TIA-session-scoped cache of the project-wide DataBlock
/// enumeration keyed on project scope. Fixes #155 (item 1): the ~8–12s
/// <c>ProjectDiscovery.EnumerateDataBlocks</c> walk over every block in the
/// project (821 in the profiled project) was an instance field on
/// <c>ActiveSetViewModel</c>, which is recreated per dialog open — so the walk
/// re-ran on the first DB-switcher interaction of <em>every</em> open.
///
/// <para>
/// Same lifetime model as #140's <see cref="DbExportCache"/>: one instance per
/// Add-In load, owned by <c>BulkChangeContextMenu</c>, so it survives across
/// dialog opens and dies only when TIA Portal closes. The expensive tree walk
/// runs once per project scope per TIA session; later opens reuse the result.
/// </para>
///
/// <para>
/// Correctness (#155 constraint — discriminator <em>or</em> explicit refresh,
/// never silent staleness): the enumeration list has no cheap Openness
/// change-discriminator (there is no project-level ModifiedDate; hashing a
/// fresh walk would defeat the cache). The valve is therefore the existing
/// explicit refresh affordance — the DB-switcher's Refresh button calls
/// <see cref="Invalidate"/> before forcing a re-enumeration, so a DB added in
/// TIA between opens is one click away, not silently missing. The
/// <c>ShowDialog()</c> modal blocks project edits while a dialog is open, so
/// within a single open the snapshot cannot drift underneath the user.
/// </para>
///
/// <para>
/// Bounded by a small entry cap (<see cref="MaxEntries"/>): in practice exactly
/// one scope is live per session (the open project), but switching projects
/// mid-session must not accumulate unboundedly. Least-recently-used scopes are
/// evicted past the cap; at least one entry is always retained.
/// </para>
/// </summary>
public interface IProjectDbEnumerationCache
{
    /// <summary>
    /// Returns the cached enumeration for <paramref name="scope"/>, or runs
    /// <paramref name="enumerate"/> once, stores it (most-recently-used), and
    /// returns the result. The expensive walk runs at most once per scope per
    /// session until <see cref="Invalidate"/> / <see cref="Clear"/>.
    /// </summary>
    IReadOnlyList<DataBlockSummary> GetOrAdd(
        string scope, Func<IReadOnlyList<DataBlockSummary>> enumerate);

    /// <summary>True if an entry exists for <paramref name="scope"/>.</summary>
    bool HasEntry(string scope);

    /// <summary>
    /// Drops the entry for <paramref name="scope"/> so the next
    /// <see cref="GetOrAdd"/> re-enumerates. Called by the DB-switcher's
    /// explicit Refresh affordance — the cross-open staleness valve.
    /// </summary>
    void Invalidate(string scope);

    /// <summary>Drops every cached scope.</summary>
    void Clear();
}

/// <inheritdoc cref="IProjectDbEnumerationCache"/>
public sealed class ProjectDbEnumerationCache : IProjectDbEnumerationCache
{
    /// <summary>
    /// Hard cap on cached scopes. One project scope is live per session in
    /// practice; the cap only matters when the user switches projects within
    /// one TIA session.
    /// </summary>
    internal const int MaxEntries = 4;

    private readonly object _lock = new object();
    private readonly int _maxEntries;

    // Mutable node class (not a readonly struct) — avoids the partial-trust
    // ldflda-on-readonly-struct-field IL pitfall entirely (CLAUDE.md), and lets
    // the LRU list + index share one object per entry. Mirrors DbExportCache.
    private sealed class CacheItem
    {
        public CacheItem(string scope, IReadOnlyList<DataBlockSummary> list)
        {
            Scope = scope; List = list;
        }
        public string Scope { get; }
        public IReadOnlyList<DataBlockSummary> List { get; set; }
    }

    private readonly LinkedList<CacheItem> _lru = new LinkedList<CacheItem>();
    private readonly Dictionary<string, LinkedListNode<CacheItem>> _index =
        new Dictionary<string, LinkedListNode<CacheItem>>(StringComparer.Ordinal);

    public ProjectDbEnumerationCache() : this(MaxEntries) { }

    // Test seam: inject a tiny cap so eviction is verifiable.
    internal ProjectDbEnumerationCache(int maxEntries)
    {
        _maxEntries = maxEntries;
    }

    public IReadOnlyList<DataBlockSummary> GetOrAdd(
        string scope, Func<IReadOnlyList<DataBlockSummary>> enumerate)
    {
        if (enumerate == null) throw new ArgumentNullException(nameof(enumerate));

        lock (_lock)
        {
            if (_index.TryGetValue(scope, out var hit))
            {
                Touch(hit);
                Log.Information("DB enumeration cache hit (skipping project walk) for scope {Scope}", scope);
                return hit.Value.List;
            }
        }

        // Run the (slow) enumeration OUTSIDE the lock: it calls into TIA
        // Openness on the UI thread and must not serialize behind unrelated
        // cache reads. A duplicate concurrent miss would just enumerate twice
        // and the last writer wins — harmless for an idempotent project walk,
        // and the open path is single-threaded in practice anyway.
        var list = enumerate();

        lock (_lock)
        {
            if (_index.TryGetValue(scope, out var existing))
            {
                existing.Value.List = list;
                Touch(existing);
            }
            else
            {
                var added = _lru.AddFirst(new CacheItem(scope, list));
                _index[scope] = added;
            }

            while (_index.Count > 1 && _index.Count > _maxEntries)
            {
                var lru = _lru.Last;
                if (lru == null) break;
                _lru.RemoveLast();
                _index.Remove(lru.Value.Scope);
            }
        }

        return list;
    }

    public bool HasEntry(string scope)
    {
        lock (_lock)
            return _index.ContainsKey(scope);
    }

    public void Invalidate(string scope)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(scope, out var node))
            {
                _lru.Remove(node);
                _index.Remove(scope);
                Log.Information("DB enumeration cache invalidated for scope {Scope} (explicit refresh)", scope);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _lru.Clear();
            _index.Clear();
        }
    }

    // Caller holds _lock.
    private void Touch(LinkedListNode<CacheItem> node)
    {
        if (!ReferenceEquals(_lru.First, node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
    }
}
