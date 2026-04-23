using System;
using System.IO;
using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class TempCacheCleanupTests : IDisposable
{
    private readonly string _root;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    public TempCacheCleanupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "BlockParamTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void MissingRoot_ReturnsZero_AndSuggestsFullCycle()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        var now = new DateTime(2026, 4, 18, 12, 0, 0);

        var (files, dirs, next) = TempCacheCleanup.Run(missing, MaxAge, now);

        files.Should().Be(0);
        dirs.Should().Be(0);
        next.Should().Be(now + MaxAge);
    }

    [Fact]
    public void DeletesFilesOlderThanThreshold_KeepsFresh()
    {
        var now = DateTime.Now;
        var old = CreateFile("old.xml", writeTime: now.AddDays(-20));
        var fresh = CreateFile("fresh.xml", writeTime: now.AddDays(-5));

        var (files, _, _) = TempCacheCleanup.Run(_root, MaxAge, now);

        files.Should().Be(1);
        File.Exists(old).Should().BeFalse();
        File.Exists(fresh).Should().BeTrue();
    }

    [Fact]
    public void RemovesEmptyScopeDirectoriesBottomUp()
    {
        var now = DateTime.Now;
        var scope = Path.Combine(_root, "TagTables", "abc123");
        Directory.CreateDirectory(scope);
        CreateFile(Path.Combine("TagTables", "abc123", "table.xml"), now.AddDays(-30));

        var (files, dirs, _) = TempCacheCleanup.Run(_root, MaxAge, now);

        files.Should().Be(1);
        dirs.Should().Be(2);
        Directory.Exists(scope).Should().BeFalse();
    }

    [Fact]
    public void KeepsDirectoryWithFreshFile()
    {
        var now = DateTime.Now;
        var scope = Path.Combine(_root, "TagTables", "proj");
        Directory.CreateDirectory(scope);
        CreateFile(Path.Combine("TagTables", "proj", "old.xml"), now.AddDays(-30));
        var fresh = CreateFile(Path.Combine("TagTables", "proj", "fresh.xml"), now.AddDays(-1));

        var (files, dirs, _) = TempCacheCleanup.Run(_root, MaxAge, now);

        files.Should().Be(1);
        dirs.Should().Be(0);
        File.Exists(fresh).Should().BeTrue();
    }

    [Fact]
    public void DoesNotDeleteRoot()
    {
        CreateFile("ancient.xml", DateTime.Now.AddDays(-365));

        TempCacheCleanup.Run(_root, MaxAge);

        Directory.Exists(_root).Should().BeTrue();
    }

    [Fact]
    public void SuggestedNextRun_IsOldestFilePlusMaxAge_WhenFilesRemain()
    {
        var now = new DateTime(2026, 4, 18, 12, 0, 0);
        CreateFile("a.xml", now.AddDays(-3));
        CreateFile("b.xml", now.AddDays(-10)); // oldest; stale in 4 days

        var (_, _, next) = TempCacheCleanup.Run(_root, MaxAge, now);

        next.Should().Be(now.AddDays(-10) + MaxAge);
    }

    [Fact]
    public void SuggestedNextRun_ClampedToAtLeastOneDayFromNow()
    {
        var now = new DateTime(2026, 4, 18, 12, 0, 0);
        // File written 13d23h ago → becomes stale in 1 hour. Clamp lifts next run to ≥ now+1d.
        CreateFile("almostStale.xml", now - MaxAge + TimeSpan.FromHours(1));

        var (_, _, next) = TempCacheCleanup.Run(_root, MaxAge, now);

        next.Should().Be(now.AddDays(1));
    }

    [Fact]
    public void SuggestedNextRun_OnEmptyCache_IsNowPlusMaxAge()
    {
        var now = new DateTime(2026, 4, 18, 12, 0, 0);

        var (_, _, next) = TempCacheCleanup.Run(_root, MaxAge, now);

        next.Should().Be(now + MaxAge);
    }

    [Fact]
    public void SuggestedNextRun_AfterDeletion_IgnoresRemovedFiles()
    {
        var now = new DateTime(2026, 4, 18, 12, 0, 0);
        CreateFile("old.xml", now.AddDays(-30));       // will be deleted
        CreateFile("kept.xml", now.AddDays(-2));       // oldest remaining

        var (_, _, next) = TempCacheCleanup.Run(_root, MaxAge, now);

        next.Should().Be(now.AddDays(-2) + MaxAge);
    }

    private string CreateFile(string relativePath, DateTime writeTime)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
        File.SetLastWriteTime(full, writeTime);
        return full;
    }
}
