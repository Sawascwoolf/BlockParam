namespace BlockParam.Services;

/// <summary>
/// What <see cref="ActiveDbFactory"/> should do with the export cache on one
/// open. Also the OPEN-TIMING <c>cache=</c> verdict — the single source of
/// truth for that branching so it can be unit-tested without TIA types.
/// </summary>
public enum DbCacheOutcome
{
    /// <summary>Matching fresh entry — reuse cached XML, skip export.</summary>
    Hit,

    /// <summary>No entry for this DB yet — export and cache.</summary>
    Miss,

    /// <summary>An entry exists but its token moved (DB changed) — re-export and replace.</summary>
    Stale,

    /// <summary>ModifiedDate unreadable — cache disabled this open, always export, do not cache.</summary>
    Disabled,

    /// <summary>Caller forced a refresh — export and replace regardless of any entry.</summary>
    Forced,
}

/// <summary>
/// Pure decision policy for the #140 export cache. Kept separate from
/// <see cref="DbExportCache"/> (state) and <see cref="ActiveDbFactory"/> (TIA
/// glue) so the precedence rules have exhaustive unit coverage.
/// </summary>
public static class DbExportCacheDecision
{
    /// <summary>
    /// Precedence (highest first):
    /// <list type="number">
    ///   <item><b>Disabled</b> — token unreadable: never trust the cache.</item>
    ///   <item><b>Forced</b> — explicit refresh: ignore any entry.</item>
    ///   <item><b>Hit</b> — a stored entry's token matches.</item>
    ///   <item><b>Stale</b> — an entry exists but its token differs.</item>
    ///   <item><b>Miss</b> — nothing cached for this DB.</item>
    /// </list>
    /// Only <see cref="DbCacheOutcome.Hit"/> reuses cached XML; every other
    /// outcome re-exports, and only non-<see cref="DbCacheOutcome.Disabled"/>
    /// outcomes are worth storing afterwards (a tokenless entry can never hit).
    /// </summary>
    /// <param name="forceRefresh">Caller asked to bypass the cache.</param>
    /// <param name="tokenReadable">ModifiedDate produced a usable token.</param>
    /// <param name="matchingEntryExists">A stored entry's token equals the current token.</param>
    /// <param name="anyEntryExists">Any entry (any token) exists for the key.</param>
    public static DbCacheOutcome Decide(
        bool forceRefresh,
        bool tokenReadable,
        bool matchingEntryExists,
        bool anyEntryExists)
    {
        if (!tokenReadable) return DbCacheOutcome.Disabled;
        if (forceRefresh) return DbCacheOutcome.Forced;
        if (matchingEntryExists) return DbCacheOutcome.Hit;
        if (anyEntryExists) return DbCacheOutcome.Stale;
        return DbCacheOutcome.Miss;
    }

    /// <summary>The OPEN-TIMING predictor string for an outcome.</summary>
    public static string Predictor(DbCacheOutcome outcome) => outcome switch
    {
        DbCacheOutcome.Hit => "cache=hit",
        DbCacheOutcome.Miss => "cache=miss",
        DbCacheOutcome.Stale => "cache=stale",
        DbCacheOutcome.Disabled => "cache=disabled",
        DbCacheOutcome.Forced => "cache=forced",
        _ => "cache=unknown",
    };
}
