using System;
using System.Globalization;
using System.IO;
using BlockParam.Diagnostics;

namespace BlockParam.Services;

/// <summary>
/// Persists the next scheduled TEMP-cache cleanup in a small state file so that
/// cleanup runs at most once per <c>TempCacheCleanup.SuggestedNextRun</c>
/// interval across TIA sessions (not every time the Add-In loads).
/// </summary>
public static class CacheCleanupSchedule
{
    public static bool IsDue(string stateFile, DateTime? now = null)
    {
        var reference = now ?? DateTime.Now;
        var next = ReadNextRun(stateFile);
        return !next.HasValue || reference >= next.Value;
    }

    public static void SetNextRun(string stateFile, DateTime nextRun)
    {
        try
        {
            var dir = Path.GetDirectoryName(stateFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(stateFile, nextRun.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            // Non-fatal: cleanup just retries next time the add-in loads.
            // Logged at Warning so a corrupted state file is diagnosable.
            Log.Warning(ex, "CacheCleanupSchedule: could not write {File}", stateFile);
        }
    }

    private static DateTime? ReadNextRun(string stateFile)
    {
        try
        {
            if (!File.Exists(stateFile)) return null;
            var text = File.ReadAllText(stateFile).Trim();
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var dt))
                return dt;
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CacheCleanupSchedule: could not read {File}", stateFile);
            return null;
        }
    }
}
