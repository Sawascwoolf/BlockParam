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
/// Bounded by a <b>total-XML-size budget</b> (<see cref="MaxTotalChars"/>),
/// not just an entry count: a single 17k-member DB exports to multi-MB XML
/// (~tens of MB as a UTF-16 string), so an entry-count cap could still pin
/// hundreds of MB inside an already memory-hungry TIA process. The
/// least-recently-used entries are evicted until both the size budget and a
/// small hard entry cap (<see cref="MaxEntries"/>) hold; the #140 "re-open the
/// same DB" pattern keeps hot entries resident because every hit refreshes
/// recency. At least one entry is always retained (a lone DB larger than the
/// whole budget is still worth caching for its own re-open).
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
    /// <paramref name="freshnessToken"/> (most-recently-used), then evicts
    /// least-recently-used entries until within the size + entry bounds. A
    /// later <see cref="TryGet"/> only hits while the caller presents the same
    /// token.
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
    /// Primary bound: total characters of cached XML. ~24M chars ≈ 48 MB of
    /// UTF-16 — a modest, predictable ceiling for an add-in inside TIA's
    /// process regardless of how large or how many DBs get opened.
    /// </summary>
    internal const long MaxTotalChars = 24_000_000;

    /// <summary>Secondary hard cap so tiny DBs can't pin unbounded entries.</summary>
    internal const int MaxEntries = 12;

    private readonly object _lock = new object();
    private readonly long _maxTotalChars;
    private readonly int _maxEntries;
    private long _totalChars;

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

    public DbExportCache() : this(MaxEntries, MaxTotalChars) { }

    // Test seam: inject tiny bounds so size-eviction is verifiable without
    // allocating 48 MB strings.
    internal DbExportCache(int maxEntries, long maxTotalChars)
    {
        _maxEntries = maxEntries;
        _maxTotalChars = maxTotalChars;
    }

    /// <summary>
    /// Stable cache key. <paramref name="projectScope"/> isolates parallel TIA
    /// instances / switched projects (a project can host two PLCs with
    /// identically named+numbered DBs); <paramref name="plcName"/> disambiguates
    /// multi-PLC projects; DB name + number identify the block within a PLC.
    /// The <c>\x00</c> separators are collision-free (no segment can contain a
    /// NUL), unlike concatenation alone.
    ///
    /// <para>
    /// Note: the DB launch path uses <c>displayPlcName</c> ("" on single-PLC
    /// projects) while the in-dialog switcher passes the resolved PLC name, so
    /// on a single-PLC project the same DB can key differently across those two
    /// paths. That is a missed-hit (perf) only, never a wrong-DB hit:
    /// <paramref name="projectScope"/> + name + number stay correct, and
    /// multi-PLC projects (the only place plcName guards correctness) pass the
    /// resolved name on both paths. Tracked for unified keying in #155.
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
                _totalChars -= node.Value.Xml.Length;
                node.Value.Token = freshnessToken;
                node.Value.Xml = xml;
                _totalChars += xml.Length;
                Touch(node);
            }
            else
            {
                var added = _lru.AddFirst(new CacheItem(key, freshnessToken, xml));
                _index[key] = added;
                _totalChars += xml.Length;
            }

            // Evict LRU (tail) until within both bounds, but never drop the
            // last entry — a lone over-budget DB is still worth its re-open.
            while (_index.Count > 1 &&
                   (_index.Count > _maxEntries || _totalChars > _maxTotalChars))
            {
                var lru = _lru.Last;
                if (lru == null) break;
                _totalChars -= lru.Value.Xml.Length;
                _lru.RemoveLast();
                _index.Remove(lru.Value.Key);
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
                _totalChars -= node.Value.Xml.Length;
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
            _totalChars = 0;
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
