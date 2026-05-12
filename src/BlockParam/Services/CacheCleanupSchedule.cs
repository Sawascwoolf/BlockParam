using System;
using System.Globalization;
using BlockParam.Diagnostics;
using BlockParam.Services.Storage;

namespace BlockParam.Services;

/// <summary>
/// Persists the next scheduled TEMP-cache cleanup in a small state file so that
/// cleanup runs at most once per <c>TempCacheCleanup.SuggestedNextRun</c>
/// interval across TIA sessions (not every time the Add-In loads).
/// </summary>
public static class CacheCleanupSchedule
{
    public static bool IsDue(string stateFile, DateTime? now = null) =>
        IsDue(FileSystemBlockParamStorage.Instance, StoragePath.FromAbsolute(stateFile), now);

    public static bool IsDue(IBlockParamStorage storage, StoragePath stateFile, DateTime? now = null)
    {
        var reference = now ?? DateTime.Now;
        var next = ReadNextRun(storage, stateFile);
        return !next.HasValue || reference >= next.Value;
    }

    public static void SetNextRun(string stateFile, DateTime nextRun) =>
        SetNextRun(FileSystemBlockParamStorage.Instance, StoragePath.FromAbsolute(stateFile), nextRun);

    public static void SetNextRun(IBlockParamStorage storage, StoragePath stateFile, DateTime nextRun)
    {
        try
        {
            storage.WriteAllText(stateFile, nextRun.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            // Non-fatal: cleanup just retries next time the add-in loads.
            // Logged at Warning so a corrupted state file is diagnosable.
            Log.Warning(ex, "CacheCleanupSchedule: could not write {File}", stateFile.FullPath);
        }
    }

    private static DateTime? ReadNextRun(IBlockParamStorage storage, StoragePath stateFile)
    {
        try
        {
            if (!storage.FileExists(stateFile)) return null;
            var text = storage.ReadAllText(stateFile).Trim();
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var dt))
                return dt;
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CacheCleanupSchedule: could not read {File}", stateFile.FullPath);
            return null;
        }
    }
}
