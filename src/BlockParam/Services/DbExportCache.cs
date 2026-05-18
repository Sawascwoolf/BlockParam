using System;
using System.Collections.Generic;

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
/// </summary>
public interface IDbExportCache
{
    /// <summary>
    /// Returns the cached export XML for <paramref name="key"/> only if an
    /// entry exists AND its stored freshness token equals
    /// <paramref name="freshnessToken"/> (ordinal). A present-but-stale entry
    /// returns false (see <see cref="HasEntry"/> to distinguish stale from cold).
    /// </summary>
    bool TryGet(string key, string freshnessToken, out string xml);

    /// <summary>
    /// Stores <paramref name="xml"/> for <paramref name="key"/> tagged with
    /// <paramref name="freshnessToken"/>. A later <see cref="TryGet"/> only
    /// hits while the caller presents the same token.
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
    private readonly object _lock = new object();
    private readonly Dictionary<string, Entry> _byKey =
        new Dictionary<string, Entry>();

    private readonly struct Entry
    {
        public Entry(string token, string xml) { Token = token; Xml = xml; }
        public string Token { get; }
        public string Xml { get; }
    }

    /// <summary>
    /// Stable cache key. <paramref name="projectScope"/> isolates parallel TIA
    /// instances / switched projects (a project can host two PLCs with
    /// identically named+numbered DBs); <paramref name="plcName"/> disambiguates
    /// multi-PLC projects; DB name + number identify the block within a PLC.
    /// The <c>\x00</c> separators are collision-free (no segment can contain a
    /// NUL), unlike concatenation alone.
    /// </summary>
    public static string KeyFor(string projectScope, string plcName, string dbName, int dbNumber)
        => string.Concat(
            projectScope, "\x00",
            plcName, "\x00",
            dbName, "\x00",
            dbNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public bool TryGet(string key, string freshnessToken, out string xml)
    {
        lock (_lock)
        {
            if (_byKey.TryGetValue(key, out var entry) &&
                string.Equals(entry.Token, freshnessToken, StringComparison.Ordinal))
            {
                xml = entry.Xml;
                return true;
            }
        }

        xml = null!;
        return false;
    }

    public void Set(string key, string freshnessToken, string xml)
    {
        lock (_lock)
            _byKey[key] = new Entry(freshnessToken, xml);
    }

    public bool HasEntry(string key)
    {
        lock (_lock)
            return _byKey.ContainsKey(key);
    }

    public void Invalidate(string key)
    {
        lock (_lock)
            _byKey.Remove(key);
    }

    public void Clear()
    {
        lock (_lock)
            _byKey.Clear();
    }
}
