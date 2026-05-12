using System;
using BlockParam.Services;
using BlockParam.Services.Storage;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests.Storage;

/// <summary>
/// Targets the new <see cref="IBlockParamStorage"/>-aware overloads added in
/// #85. The legacy string-based overload is exercised by
/// <see cref="CacheCleanupScheduleTests"/>; this suite proves the new path
/// runs entirely against an in-memory fake — no real disk involvement.
/// </summary>
public class CacheCleanupScheduleStorageTests
{
    private static StoragePath StateFile =>
        StoragePath.FromAbsolute(@"C:\appdata") / "BlockParam" / "cache-cleanup.txt";

    [Fact]
    public void IsDue_against_in_memory_storage_returns_true_when_missing()
    {
        var fs = new InMemoryBlockParamStorage();
        CacheCleanupSchedule.IsDue(fs, StateFile).Should().BeTrue();
    }

    [Fact]
    public void SetNextRun_persists_to_in_memory_storage_only()
    {
        var fs = new InMemoryBlockParamStorage();
        var now = new DateTime(2026, 4, 1, 12, 0, 0);

        CacheCleanupSchedule.SetNextRun(fs, StateFile, now.AddDays(1));

        fs.FileExists(StateFile).Should().BeTrue();
        CacheCleanupSchedule.IsDue(fs, StateFile, now.AddHours(12)).Should().BeFalse();
        CacheCleanupSchedule.IsDue(fs, StateFile, now.AddDays(2)).Should().BeTrue();
    }

    [Fact]
    public void Corrupt_state_file_is_treated_as_due()
    {
        var fs = new InMemoryBlockParamStorage();
        fs.WriteAllText(StateFile, "not a timestamp");

        CacheCleanupSchedule.IsDue(fs, StateFile).Should().BeTrue();
    }
}
