using System;
using System.IO;
using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class CacheCleanupScheduleTests : IDisposable
{
    private readonly string _dir;
    private readonly string _stateFile;

    public CacheCleanupScheduleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "BlockParamTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _stateFile = Path.Combine(_dir, "cache-cleanup.txt");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void IsDue_WhenStateFileMissing_ReturnsTrue()
    {
        CacheCleanupSchedule.IsDue(_stateFile).Should().BeTrue();
    }

    [Fact]
    public void IsDue_WhenNowIsBeforeNextRun_ReturnsFalse()
    {
        var now = new DateTime(2026, 4, 1, 12, 0, 0);
        CacheCleanupSchedule.SetNextRun(_stateFile, now.AddDays(1));

        CacheCleanupSchedule.IsDue(_stateFile, now.AddHours(12)).Should().BeFalse();
    }

    [Fact]
    public void IsDue_WhenNowIsAfterNextRun_ReturnsTrue()
    {
        var now = new DateTime(2026, 4, 1, 12, 0, 0);
        CacheCleanupSchedule.SetNextRun(_stateFile, now.AddDays(1));

        CacheCleanupSchedule.IsDue(_stateFile, now.AddDays(2)).Should().BeTrue();
    }

    [Fact]
    public void IsDue_WhenStateFileCorrupted_ReturnsTrue()
    {
        File.WriteAllText(_stateFile, "not a date");

        CacheCleanupSchedule.IsDue(_stateFile).Should().BeTrue();
    }

    [Fact]
    public void SetNextRun_CreatesParentDirectory()
    {
        var nested = Path.Combine(_dir, "nested", "sub", "cache-cleanup.txt");

        CacheCleanupSchedule.SetNextRun(nested, DateTime.Now.AddDays(1));

        File.Exists(nested).Should().BeTrue();
    }

    [Fact]
    public void SetNextRun_RoundtripsTimestamp()
    {
        var nextRun = new DateTime(2026, 4, 19, 10, 30, 0, DateTimeKind.Local);
        CacheCleanupSchedule.SetNextRun(_stateFile, nextRun);

        CacheCleanupSchedule.IsDue(_stateFile, nextRun.AddSeconds(-1)).Should().BeFalse();
        CacheCleanupSchedule.IsDue(_stateFile, nextRun).Should().BeTrue();
    }
}
