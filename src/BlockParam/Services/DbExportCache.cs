using System;
using System.Collections.Generic;
using System.Globalization;

namespace BlockParam.Services;

/// <summary>
/// In-process, TIA-session-scoped cache of exported SimaticML XML keyed on DB
/// identity. Fixes #140: re-opening an unchanged DB re-ran the slow Openness
/// <c>block.Export</c> + disk read every time. A hit lets
/// <see cref="ActiveDbFactory"/> skip the export + file read and re-parse the
/// cached XML directly.
///
/// <para>
/// Deliberately in-process (no disk I/O — CLAUDE.md storage guardrail). Each
/// entry carries a <em>freshness token</em> derived from the block's Openness
/// <c>ModifiedDate</c> (<see cref="ITiaPortalAdapter.TryGetModifiedToken"/>): a
/// hit requires both the identity key AND a matching token, so an edit made
/// directly in TIA's DB editor between two opens is auto-detected and forces a
/// re-export — the same change-discriminator model the UDT cache already uses
/// (<c>PlcType.ModifiedDate</c>). When the token can't be read the caller
/// disables the cache for that open entirely, so an unreadable timestamp can
/// never serve a stale parse. Our own Apply also <see cref="Invalidate"/>s the
/// affected entry. The cache lives only as long as the Add-In process: closing
/// TIA Portal starts a fresh one.
/// </para>
///
/// <para>
/// Bounded LRU (<see cref="MaxEntries"/>): each entry retains the full export
/// XML (multi-MB for large DBs), so an unbounded map would let a long session
/// that browses many DBs grow without limit. The least-recently-used entry is
/// evicted past the cap; the #140 "re-open the same DB" pattern keeps hot
/// entries resident because every hit refreshes recency.
/// </para>
/// </summary>
public interface IDbExportCache
{
    /// <summary>
    /// Returns the cached export XML for <paramref name="key"/> only if an
    /// entry exists AND its stored freshness token equals
    /// <paramref name="freshnessToken"/> (ordinal). A successful get marks the
    /// entry most-recently-used. A present-but-stale entry returns false (see
    /// <see cref="HasEntry"/> to distinguish stale from cold).
    /// </summary>
    bool TryGet(string key, string freshnessToken, out string xml);

    /// <summary>
    /// Stores <paramref name="xml"/> for <paramref name="key"/> tagged with
    /// <paramref name="freshnessToken"/> (most-recently-used). A later
    /// <see cref="TryGet"/> only hits while the caller presents the same token.
    /// </summary>
    void Set(string key, string freshnessToken, string xml);

    /// <summary>True if any entry (any token) exists for <paramref name="key"/>.</summary>
    bool HasEntry(string key);

    /// <summary>Drops the entry for <paramref name="key"/> (e.g. after an Apply changed the DB).</summary>
    void Invalidate(string key);

    /// <summary>Drops every cached entry.</summary>
    void Clear();
}

/// <inheritdoc cref="IDbExportCache"/>
public sealed class DbExportCache : IDbExportCache
{
    /// <summary>
    /// Max retained exports. ~32 multi-MB XML strings is a sane desktop ceiling;
    /// re-opening within a working set of this size still hits the cache.
    /// </summary>
    internal const int MaxEntries = 32;

    private readonly object _lock = new object();

    // Mutable node class (not a readonly struct) — avoids the partial-trust
    // ldflda-on-readonly-struct-field IL pitfall entirely (CLAUDE.md), and lets
    // the LRU list + index share one object per entry.
    private sealed class CacheItem
    {
        public CacheItem(string key, string token, string xml)
        {
            Key = key; Token = token; Xml = xml;
        }
        public string Key { get; }
        public string Token { get; set; }
        public string Xml { get; set; }
    }

    // Front of _lru == most-recently-used. _index maps key -> its list node so
    // touch/evict/invalidate are all O(1).
    private readonly LinkedList<CacheItem> _lru = new LinkedList<CacheItem>();
    private readonly Dictionary<string, LinkedListNode<CacheItem>> _index =
        new Dictionary<string, LinkedListNode<CacheItem>>();

    /// <summary>
    /// Stable cache key. <paramref name="projectScope"/> isolates parallel TIA
    /// instances / switched projects (a project can host two PLCs with
    /// identically named+numbered DBs); <paramref name="plcName"/> disambiguates
    /// multi-PLC projects; DB name + number identify the block within a PLC.
    /// The <c>\x00</c> separators are collision-free (no segment can contain a
    /// NUL), unlike concatenation alone.
    ///
    /// <para>
    /// Note: callers pass the DB launch path uses <c>displayPlcName</c> ("" on
    /// single-PLC projects) while the in-dialog switcher passes the resolved
    /// PLC name, so on a single-PLC project the same DB can key differently
    /// across those two paths. That is a missed-hit (perf) only, never a
    /// wrong-DB hit: <paramref name="projectScope"/> + name + number stay
    /// correct, and multi-PLC projects (the only place plcName guards
    /// correctness) pass the resolved name on both paths. Tracked for unified
    /// keying in #155.
    /// </para>
    /// </summary>
    public static string KeyFor(string projectScope, string plcName, string dbName, int dbNumber)
        => string.Concat(
            projectScope, "\x00",
            plcName, "\x00",
            dbName, "\x00",
            dbNumber.ToString(CultureInfo.InvariantCulture));

    public bool TryGet(string key, string freshnessToken, out string xml)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(key, out var node) &&
                string.Equals(node.Value.Token, freshnessToken, StringComparison.Ordinal))
            {
                Touch(node);
                xml = node.Value.Xml;
                return true;
            }
        }

        xml = null!;
        return false;
    }

    public void Set(string key, string freshnessToken, string xml)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(key, out var node))
            {
                node.Value.Token = freshnessToken;
                node.Value.Xml = xml;
                Touch(node);
                return;
            }

            var added = _lru.AddFirst(new CacheItem(key, freshnessToken, xml));
            _index[key] = added;

            if (_index.Count > MaxEntries)
            {
                var lru = _lru.Last;
                if (lru != null)
                {
                    _lru.RemoveLast();
                    _index.Remove(lru.Value.Key);
                }
            }
        }
    }

    public bool HasEntry(string key)
    {
        lock (_lock)
            return _index.ContainsKey(key);
    }

    public void Invalidate(string key)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _index.Remove(key);
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
