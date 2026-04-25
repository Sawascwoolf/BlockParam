using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BlockParam.Diagnostics;

namespace BlockParam.Services;

/// <summary>
/// Removes stale files and now-empty subdirectories from the BlockParam TEMP
/// cache root and suggests when the next sweep should run, based on the age of
/// the remaining files. Keeps <c>%TEMP%\BlockParam\</c> bounded — without this,
/// orphan per-project scope folders from renamed/deleted projects (#14) would
/// accumulate forever.
/// </summary>
public static class TempCacheCleanup
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(14);
    private static readonly TimeSpan MinNextRunDelay = TimeSpan.FromDays(1);

    /// <summary>
    /// <paramref name="FilesDeleted"/>/<paramref name="DirsDeleted"/>: what was
    /// removed this sweep. <paramref name="SuggestedNextRun"/>: when the next
    /// sweep should run — derived from the oldest remaining file plus
    /// <c>maxAge</c>, clamped to at least 1 day in the future. If the cache is
    /// empty, <c>now + maxAge</c>.
    /// </summary>
    public static (int FilesDeleted, int DirsDeleted, DateTime SuggestedNextRun) Run(
        string rootDir,
        TimeSpan? maxAge = null,
        DateTime? now = null)
    {
        var reference = now ?? DateTime.Now;
        var maxAgeValue = maxAge ?? DefaultMaxAge;

        if (!Directory.Exists(rootDir))
            return (0, 0, reference + maxAgeValue);

        var threshold = reference - maxAgeValue;

        var (files, oldestRemaining) = DeleteStaleFilesAndFindOldest(rootDir, threshold);
        int dirs = DeleteEmptyDirectories(rootDir);

        var nextRun = SuggestNextRun(reference, maxAgeValue, oldestRemaining);
        return (files, dirs, nextRun);
    }

    private static DateTime SuggestNextRun(DateTime now, TimeSpan maxAge, DateTime? oldestRemaining)
    {
        if (!oldestRemaining.HasValue)
            return now + maxAge; // empty cache: wait a full cycle

        // Next file becomes stale at (oldest + maxAge). Never sooner than 1 day.
        var candidate = oldestRemaining.Value + maxAge;
        var earliestAllowed = now + MinNextRunDelay;
        return candidate < earliestAllowed ? earliestAllowed : candidate;
    }

    /// <summary>
    /// Single pass over the file tree: deletes files older than the threshold
    /// and tracks the oldest mtime of everything that stays (including files
    /// we wanted to delete but couldn't — they'll still be there next sweep).
    /// </summary>
    private static (int Deleted, DateTime? OldestRemaining) DeleteStaleFilesAndFindOldest(
        string root, DateTime threshold)
    {
        int count = 0;
        DateTime? oldest = null;

        foreach (var file in SafeList(() => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)))
        {
            DateTime mt;
            try { mt = File.GetLastWriteTime(file); }
            catch (Exception ex)
            {
                Log.Warning(ex, "TempCacheCleanup: could not stat {File}", file);
                continue;
            }

            bool deleted = false;
            if (mt < threshold)
            {
                try
                {
                    File.Delete(file);
                    count++;
                    deleted = true;
                }
                catch (Exception ex)
                {
                    // Best-effort cleanup; locked or read-only files stay.
                    Log.Warning(ex, "TempCacheCleanup: could not delete {File}", file);
                }
            }

            if (!deleted && (!oldest.HasValue || mt < oldest.Value))
                oldest = mt;
        }

        return (count, oldest);
    }

    private static int DeleteEmptyDirectories(string root)
    {
        // Bottom-up so parents are visited after their children are gone.
        var dirs = SafeList(() => Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            .OrderByDescending(d => d.Length)
            .ToList();

        int count = 0;
        foreach (var dir in dirs)
        {
            try
            {
                if (Directory.GetFileSystemEntries(dir).Length == 0)
                {
                    Directory.Delete(dir);
                    count++;
                }
            }
            catch (Exception ex)
            {
                // Best-effort: leave non-empty or otherwise undeletable directories alone.
                Log.Warning(ex, "TempCacheCleanup: could not remove dir {Dir}", dir);
            }
        }
        return count;
    }

    private static List<string> SafeList(Func<IEnumerable<string>> source)
    {
        try { return source().ToList(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "TempCacheCleanup: enumeration failed, skipping");
            return new List<string>();
        }
    }
}
