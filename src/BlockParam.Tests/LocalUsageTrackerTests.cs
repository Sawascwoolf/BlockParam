using FluentAssertions;
using BlockParam.Licensing;
using Xunit;

namespace BlockParam.Tests;

public class LocalUsageTrackerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storagePath;

    public LocalUsageTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BlockParamTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _storagePath = Path.Combine(_tempDir, "usage.dat");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Track_FirstUse_2Remaining()
    {
        var tracker = CreateTracker();

        tracker.RecordUsage().Should().BeTrue();
        var status = tracker.GetStatus();

        status.UsedToday.Should().Be(1);
        status.RemainingToday.Should().Be(2);
        status.IsLimitReached.Should().BeFalse();
    }

    [Fact]
    public void Track_ThirdUse_LimitReached()
    {
        var tracker = CreateTracker();

        tracker.RecordUsage();
        tracker.RecordUsage();
        tracker.RecordUsage().Should().BeTrue();

        var status = tracker.GetStatus();
        status.UsedToday.Should().Be(3);
        status.RemainingToday.Should().Be(0);
        status.IsLimitReached.Should().BeTrue();
    }

    [Fact]
    public void Track_FourthUse_Blocked()
    {
        var tracker = CreateTracker();

        tracker.RecordUsage();
        tracker.RecordUsage();
        tracker.RecordUsage();
        tracker.RecordUsage().Should().BeFalse();

        tracker.GetStatus().UsedToday.Should().Be(3);
    }

    [Fact]
    public void Track_NewDay_ResetCounter()
    {
        var currentDate = new DateTime(2024, 1, 1);
        var tracker = CreateTracker(dateProvider: () => currentDate);

        tracker.RecordUsage();
        tracker.RecordUsage();
        tracker.RecordUsage();
        tracker.GetStatus().IsLimitReached.Should().BeTrue();

        // Simulate next day with a new tracker instance
        currentDate = new DateTime(2024, 1, 2);
        var nextDayTracker = CreateTracker(dateProvider: () => currentDate);

        var status = nextDayTracker.GetStatus();
        status.UsedToday.Should().Be(0);
        status.RemainingToday.Should().Be(3);
    }

    [Fact]
    public void Track_CorruptFile_GracefulReset()
    {
        // Write garbage to the file
        File.WriteAllBytes(_storagePath, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02 });

        var tracker = CreateTracker();
        var status = tracker.GetStatus();

        status.UsedToday.Should().Be(0);
        status.RemainingToday.Should().Be(3);
    }

    [Fact]
    public void Track_MissingFile_CreatesNew()
    {
        File.Exists(_storagePath).Should().BeFalse();

        var tracker = CreateTracker();
        tracker.RecordUsage();

        File.Exists(_storagePath).Should().BeTrue();
    }

    [Fact]
    public void Track_PersistsAcrossInstances()
    {
        var date = new DateTime(2024, 6, 15);

        var tracker1 = CreateTracker(dateProvider: () => date);
        tracker1.RecordUsage();
        tracker1.RecordUsage();

        // New instance, same file, same day
        var tracker2 = CreateTracker(dateProvider: () => date);
        var status = tracker2.GetStatus();

        status.UsedToday.Should().Be(2);
        status.RemainingToday.Should().Be(1);
    }

    private LocalUsageTracker CreateTracker(
        int dailyLimit = 3,
        Func<DateTime>? dateProvider = null)
    {
        return new LocalUsageTracker(_storagePath, dailyLimit, dateProvider: dateProvider);
    }
}
