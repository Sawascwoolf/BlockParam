namespace BlockParam.Services;

/// <summary>
/// Coordinates the "compile inconsistent UDTs and retry export" flow triggered
/// when <c>RefreshStaleUdtCache</c> hits TIA's per-type inconsistency error (#27).
/// Kept separate from <c>BulkChangeContextMenu</c> so the prompt/retry sequencing
/// is testable without TIA Portal.
/// </summary>
public static class InconsistentUdtRetry
{
    /// <summary>
    /// If <paramref name="failed"/> is non-empty, asks the user via <paramref name="askUser"/>.
    /// On yes, invokes <paramref name="tryCompile"/> followed by <paramref name="tryReExport"/>
    /// for each entry individually — never compiling the whole PLC (explicit scope, #27).
    /// Returns the number of UDTs that re-exported successfully.
    /// </summary>
    public static int RetryAfterCompile<T>(
        IReadOnlyList<T> failed,
        Func<T, string> nameOf,
        Func<T, bool> tryCompile,
        Func<T, bool> tryReExport,
        Func<IReadOnlyList<string>, bool> askUser)
    {
        if (failed.Count == 0) return 0;

        var names = failed.Select(nameOf).ToList();
        if (!askUser(names)) return 0;

        int retried = 0;
        foreach (var item in failed)
        {
            if (!tryCompile(item)) continue;
            if (tryReExport(item)) retried++;
        }
        return retried;
    }
}
