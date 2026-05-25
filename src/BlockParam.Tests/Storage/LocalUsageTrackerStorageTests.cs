using BlockParam.Licensing;
using BlockParam.Services.Storage;
using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace BlockParam.Tests.Storage;

/// <summary>
/// Regression tests pinning <see cref="LocalUsageTracker"/> to the
/// <see cref="IBlockParamStorage"/> abstraction it migrated onto in #85's
/// follow-up — proves the in-memory path is wired correctly so a future
/// refactor that drops a <c>File.*</c> call back in is caught immediately.
/// Disk-based behavior continues to live in <see cref="LocalUsageTrackerTests"/>.
/// </summary>
public class LocalUsageTrackerStorageTests
{
    private static StoragePath UsagePath =>
        StoragePath.FromAbsolute(@"C:\bp\appdata") / "usage.dat";

    [Fact]
    public void Missing_file_reports_zero_usage_without_writing()
    {
        var fs = new InMemoryBlockParamStorage();
        var tracker = NewTracker(fs);

        var status = tracker.GetStatus();
        status.UsedToday.Should().Be(0);
        status.RemainingToday.Should().Be(3);

        // GetStatus must not create the file — only RecordUsage does.
        fs.FileExists(UsagePath).Should().BeFalse();
    }

    [Fact]
    public void RecordUsage_writes_through_storage_only()
    {
        var fs = new InMemoryBlockParamStorage();
        var tracker = NewTracker(fs);

        tracker.RecordUsage(1).Should().BeTrue();

        fs.FileExists(UsagePath).Should().BeTrue();
        // .tmp file must not linger — Replace() removes the source on success.
        fs.FileExists(StoragePath.FromAbsolute(UsagePath.FullPath + ".tmp"))
            .Should().BeFalse();
    }

    [Fact]
    public void Persists_across_tracker_instances_via_storage()
    {
        var fs = new InMemoryBlockParamStorage();
        var date = new DateTime(2026, 5, 24);

        var w = NewTracker(fs, dailyLimit: 5, dateProvider: () => date);
        w.RecordUsage(2);
        w.RecordUsage(1);

        var r = NewTracker(fs, dailyLimit: 5, dateProvider: () => date);
        r.GetStatus().UsedToday.Should().Be(3);
        r.GetStatus().RemainingToday.Should().Be(2);
    }

    [Fact]
    public void Counter_resets_on_new_day_even_with_persisted_old_count()
    {
        var fs = new InMemoryBlockParamStorage();
        var today = new DateTime(2026, 5, 24);

        var w = NewTracker(fs, dateProvider: () => today);
        w.RecordUsage(2);

        var tomorrow = today.AddDays(1);
        var r = NewTracker(fs, dateProvider: () => tomorrow);
        r.GetStatus().UsedToday.Should().Be(0);
    }

    [Fact]
    public void Corrupt_blob_resets_gracefully_without_throwing()
    {
        var fs = new InMemoryBlockParamStorage();
        // Bypass Obfuscate so the read path falls into the corrupt-file branch.
        fs.WriteAllBytes(UsagePath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00 });

        var tracker = NewTracker(fs);
        tracker.GetStatus().UsedToday.Should().Be(0);
        // And subsequent writes succeed (Replace overwrites the corrupt blob).
        tracker.RecordUsage(1).Should().BeTrue();
        tracker.GetStatus().UsedToday.Should().Be(1);
    }

    [Fact]
    public void Atomic_write_replaces_existing_file_in_place()
    {
        var fs = new InMemoryBlockParamStorage();
        var date = new DateTime(2026, 5, 24);
        var tracker = NewTracker(fs, dateProvider: () => date);

        tracker.RecordUsage(1);
        tracker.RecordUsage(1);

        // After two writes there should be exactly one file at the storage path
        // (no .tmp leftovers, no .bak files).
        fs.EnumerateFiles(UsagePath.Parent).Select(p => p.FileName)
            .Should().BeEquivalentTo("usage.dat");
    }

    [Fact]
    public void Legacy_dual_counter_json_drops_inline_count_field()
    {
        // Mirrors the disk-based test in LocalUsageTrackerTests — confirms the
        // Newtonsoft "ignore unknown property" guarantee survives the
        // storage-abstraction migration unchanged.
        var fs = new InMemoryBlockParamStorage();
        var date = new DateTime(2026, 5, 24);
        var legacy = JsonConvert.SerializeObject(new
        {
            Date = date.ToString("yyyy-MM-dd"),
            Count = 7,
            InlineCount = 99
        });
        fs.WriteAllBytes(UsagePath, Obfuscation.Obfuscate(legacy));

        var tracker = NewTracker(fs, dailyLimit: 200, dateProvider: () => date);
        tracker.GetStatus().UsedToday.Should().Be(7);
    }

    private static LocalUsageTracker NewTracker(
        IBlockParamStorage fs,
        int dailyLimit = 3,
        Func<DateTime>? dateProvider = null)
        => new(fs, UsagePath, dailyLimit, dateProvider);
}
