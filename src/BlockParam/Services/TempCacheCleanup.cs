using System;
using System.Collections.Generic;
using System.Linq;
using BlockParam.Diagnostics;
using BlockParam.Services.Storage;

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
        DateTime? now = null) =>
        Run(FileSystemBlockParamStorage.Instance, StoragePath.FromAbsolute(rootDir), maxAge, now);

    public static (int FilesDeleted, int DirsDeleted, DateTime SuggestedNextRun) Run(
        IBlockParamStorage storage,
        StoragePath rootDir,
        TimeSpan? maxAge = null,
        DateTime? now = null)
    {
        var reference = now ?? DateTime.Now;
        var maxAgeValue = maxAge ?? DefaultMaxAge;

        if (!storage.DirectoryExists(rootDir))
            return (0, 0, reference + maxAgeValue);

        var threshold = reference - maxAgeValue;

        var (files, oldestRemaining) = DeleteStaleFilesAndFindOldest(storage, rootDir, threshold);
        int dirs = DeleteEmptyDirectories(storage, rootDir);

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
        IBlockParamStorage storage, StoragePath root, DateTime threshold)
    {
        int count = 0;
        DateTime? oldest = null;

        foreach (var file in SafeList(() => storage.EnumerateFiles(root, "*", recursive: true)))
        {
            DateTime mt;
            try { mt = storage.GetLastWriteTime(file); }
            catch (Exception ex)
            {
                Log.Warning(ex, "TempCacheCleanup: could not stat {File}", file.FullPath);
                continue;
            }

            bool deleted = false;
            if (mt < threshold)
            {
                try
                {
                    storage.DeleteFile(file);
                    count++;
                    deleted = true;
                }
                catch (Exception ex)
                {
                    // Best-effort cleanup; locked or read-only files stay.
                    Log.Warning(ex, "TempCacheCleanup: could not delete {File}", file.FullPath);
                }
            }

            if (!deleted && (!oldest.HasValue || mt < oldest.Value))
                oldest = mt;
        }

        return (count, oldest);
    }

    private static int DeleteEmptyDirectories(IBlockParamStorage storage, StoragePath root)
    {
        // Bottom-up so parents are visited after their children are gone.
        var dirs = SafeList(() => storage.EnumerateDirectories(root, "*", recursive: true))
            .OrderByDescending(d => d.FullPath.Length)
            .ToList();

        int count = 0;
        foreach (var dir in dirs)
        {
            try
            {
                if (!storage.HasAnyEntries(dir))
                {
                    storage.DeleteDirectory(dir);
                    count++;
                }
            }
            catch (Exception ex)
            {
                // Best-effort: leave non-empty or otherwise undeletable directories alone.
                Log.Warning(ex, "TempCacheCleanup: could not remove dir {Dir}", dir.FullPath);
            }
        }
        return count;
    }

    private static List<StoragePath> SafeList(Func<IEnumerable<StoragePath>> source)
    {
        try { return source().ToList(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "TempCacheCleanup: enumeration failed, skipping");
            return new List<StoragePath>();
        }
    }
}
