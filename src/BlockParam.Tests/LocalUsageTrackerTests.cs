using FluentAssertions;
using BlockParam.Licensing;
using Newtonsoft.Json;
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

        tracker.RecordUsage(1).Should().BeTrue();
        var status = tracker.GetStatus();

        status.UsedToday.Should().Be(1);
        status.RemainingToday.Should().Be(2);
        status.IsLimitReached.Should().BeFalse();
    }

    [Fact]
    public void Track_ThirdUse_LimitReached()
    {
        var tracker = CreateTracker();

        tracker.RecordUsage(1);
        tracker.RecordUsage(1);
        tracker.RecordUsage(1).Should().BeTrue();

        var status = tracker.GetStatus();
        status.UsedToday.Should().Be(3);
        status.RemainingToday.Should().Be(0);
        status.IsLimitReached.Should().BeTrue();
    }

    [Fact]
    public void Track_FourthUse_Blocked()
    {
        var tracker = CreateTracker();

        tracker.RecordUsage(1);
        tracker.RecordUsage(1);
        tracker.RecordUsage(1);
        tracker.RecordUsage(1).Should().BeFalse();

        tracker.GetStatus().UsedToday.Should().Be(3);
    }

    [Fact]
    public void Track_NewDay_ResetCounter()
    {
        var currentDate = new DateTime(2024, 1, 1);
        var tracker = CreateTracker(dateProvider: () => currentDate);

        tracker.RecordUsage(1);
        tracker.RecordUsage(1);
        tracker.RecordUsage(1);
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
        tracker.RecordUsage(1);

        File.Exists(_storagePath).Should().BeTrue();
    }

    [Fact]
    public void Track_PersistsAcrossInstances()
    {
        var date = new DateTime(2024, 6, 15);

        var tracker1 = CreateTracker(dateProvider: () => date);
        tracker1.RecordUsage(1);
        tracker1.RecordUsage(1);

        // New instance, same file, same day
        var tracker2 = CreateTracker(dateProvider: () => date);
        var status = tracker2.GetStatus();

        status.UsedToday.Should().Be(2);
        status.RemainingToday.Should().Be(1);
    }

    [Fact]
    public void RecordUsage_BatchFitsUnderCap_Charges()
    {
        var tracker = CreateTracker(dailyLimit: 200);

        tracker.RecordUsage(50).Should().BeTrue();
        tracker.RecordUsage(120).Should().BeTrue();

        tracker.GetStatus().UsedToday.Should().Be(170);
        tracker.GetStatus().RemainingToday.Should().Be(30);
    }

    [Fact]
    public void RecordUsage_BatchOverflowsCap_Atomic_Reject()
    {
        var tracker = CreateTracker(dailyLimit: 200);
        tracker.RecordUsage(180);

        // 30 more would push to 210 — should reject the WHOLE batch, not write 20.
        tracker.RecordUsage(30).Should().BeFalse();
        tracker.GetStatus().UsedToday.Should().Be(180);
        tracker.GetStatus().RemainingToday.Should().Be(20);

        // The remaining 20 still fit and should succeed.
        tracker.RecordUsage(20).Should().BeTrue();
        tracker.GetStatus().IsLimitReached.Should().BeTrue();
    }

    [Fact]
    public void RecordUsage_ZeroOrNegative_NoOp()
    {
        var tracker = CreateTracker();

        tracker.RecordUsage(0).Should().BeTrue();
        tracker.RecordUsage(-5).Should().BeTrue();
        tracker.GetStatus().UsedToday.Should().Be(0);
    }

    [Fact]
    public void Read_LegacyFileWithInlineCount_IgnoresAndKeepsCount()
    {
        // Simulate a saved file from the old dual-counter version.
        var date = new DateTime(2024, 6, 15);
        var legacyJson = JsonConvert.SerializeObject(new
        {
            Date = date.ToString("yyyy-MM-dd"),
            Count = 3,
            InlineCount = 17
        });
        File.WriteAllBytes(_storagePath, Obfuscation.Obfuscate(legacyJson));

        var tracker = CreateTracker(dailyLimit: 200, dateProvider: () => date);
        var status = tracker.GetStatus();

        // Legacy InlineCount is dropped; Count is preserved.
        status.UsedToday.Should().Be(3);
    }

    private LocalUsageTracker CreateTracker(
        int dailyLimit = 3,
        Func<DateTime>? dateProvider = null)
    {
        return new LocalUsageTracker(_storagePath, dailyLimit, dateProvider: dateProvider);
    }
}
