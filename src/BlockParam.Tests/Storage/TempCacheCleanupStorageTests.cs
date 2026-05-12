using System;
using System.Linq;
using BlockParam.Services;
using BlockParam.Services.Storage;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests.Storage;

/// <summary>
/// Mirrors a subset of <see cref="TempCacheCleanupTests"/> through the new
/// in-memory storage seam. Real-disk coverage stays in that suite; this one
/// proves the storage-injecting overload is wired correctly and that stale-vs-fresh
/// classification works without touching the file system.
/// </summary>
public class TempCacheCleanupStorageTests
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    private static StoragePath Root => StoragePath.FromAbsolute(@"C:\temp\BlockParam");

    [Fact]
    public void Missing_root_returns_zero_counts_and_now_plus_maxAge()
    {
        var fs = new InMemoryBlockParamStorage();
        var now = new DateTime(2026, 4, 1, 12, 0, 0);

        var (files, dirs, next) = TempCacheCleanup.Run(fs, Root, MaxAge, now);

        files.Should().Be(0);
        dirs.Should().Be(0);
        next.Should().Be(now + MaxAge);
    }

    [Fact]
    public void Stale_files_are_deleted_and_fresh_files_kept()
    {
        var fs = new InMemoryBlockParamStorage();
        var now = new DateTime(2026, 4, 15, 12, 0, 0);

        var stale = Root / "old.xml";
        var fresh = Root / "new.xml";
        fs.WriteAllText(stale, "");
        fs.WriteAllText(fresh, "");
        fs.SetLastWriteTime(stale, now - TimeSpan.FromDays(30));
        fs.SetLastWriteTime(fresh, now - TimeSpan.FromDays(2));

        var (files, _, _) = TempCacheCleanup.Run(fs, Root, MaxAge, now);

        files.Should().Be(1);
        fs.FileExists(stale).Should().BeFalse();
        fs.FileExists(fresh).Should().BeTrue();
    }

    [Fact]
    public void Now_empty_subdirectories_are_removed_bottom_up()
    {
        var fs = new InMemoryBlockParamStorage();
        var now = new DateTime(2026, 4, 15, 12, 0, 0);

        var leaf = Root / "scope" / "stale.xml";
        fs.WriteAllText(leaf, "");
        fs.SetLastWriteTime(leaf, now - TimeSpan.FromDays(30));

        var (files, dirs, _) = TempCacheCleanup.Run(fs, Root, MaxAge, now);

        files.Should().Be(1);
        dirs.Should().Be(1);
        fs.DirectoryExists(Root / "scope").Should().BeFalse();
        fs.DirectoryExists(Root).Should().BeTrue();
    }

    [Fact]
    public void Non_empty_directories_are_left_alone()
    {
        var fs = new InMemoryBlockParamStorage();
        var now = new DateTime(2026, 4, 15, 12, 0, 0);

        var kept = Root / "scope" / "fresh.xml";
        fs.WriteAllText(kept, "");
        fs.SetLastWriteTime(kept, now - TimeSpan.FromDays(2));

        var (_, dirs, _) = TempCacheCleanup.Run(fs, Root, MaxAge, now);

        dirs.Should().Be(0);
        fs.DirectoryExists(Root / "scope").Should().BeTrue();
    }

    [Fact]
    public void SuggestedNextRun_is_clamped_to_at_least_one_day_ahead()
    {
        var fs = new InMemoryBlockParamStorage();
        var now = new DateTime(2026, 4, 15, 12, 0, 0);

        // A file that is freshly aged-out: oldest+maxAge would be today.
        // Clamp must push it at least 1 day forward.
        var p = Root / "borderline.xml";
        fs.WriteAllText(p, "");
        fs.SetLastWriteTime(p, now - MaxAge - TimeSpan.FromSeconds(1));

        var (_, _, next) = TempCacheCleanup.Run(fs, Root, MaxAge, now);

        next.Should().BeOnOrAfter(now + TimeSpan.FromDays(1));
    }
}
