using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using BlockParam.Updates;
using Xunit;

namespace BlockParam.Tests;

public class UpdateCheckServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _cachePath;

    public UpdateCheckServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BlockParamUpdateTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _cachePath = Path.Combine(_tempDir, "update-check.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task CheckAsync_WhenDisabled_ReturnsNull_AndDoesNotFetch()
    {
        var fetcher = new RecordingFetcher();
        var service = Build(
            fetcher,
            current: "v0.3.0",
            settings: new UpdateCheckSettings { Enabled = false });

        var result = await service.CheckAsync();

        result.Should().BeNull();
        fetcher.Calls.Should().Be(0, "disabled service must not hit the network");
    }

    [Fact]
    public async Task CheckAsync_WhenNewerVersion_ReturnsRelease_AndCaches()
    {
        var release = new UpdateInfo { TagName = "v0.4.0", HtmlUrl = "https://x", Body = "notes" };
        var fetcher = new RecordingFetcher(release);
        var service = Build(fetcher, current: "v0.3.0");

        var result = await service.CheckAsync();

        result.Should().NotBeNull();
        result!.TagName.Should().Be("v0.4.0");
        File.Exists(_cachePath).Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_WhenOlderOrEqual_ReturnsNull()
    {
        var release = new UpdateInfo { TagName = "v0.3.0" };
        var fetcher = new RecordingFetcher(release);
        var service = Build(fetcher, current: "v0.3.0");

        (await service.CheckAsync()).Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_StableUserHidesPrereleaseByDefault()
    {
        var release = new UpdateInfo { TagName = "v0.4.0-rc1", PreRelease = true };
        var fetcher = new RecordingFetcher(release);
        var service = Build(fetcher, current: "v0.3.0");

        (await service.CheckAsync()).Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_PrereleaseShownWhenIncludePrereleasesIsTrue()
    {
        var release = new UpdateInfo { TagName = "v0.4.0-rc1", PreRelease = true };
        var fetcher = new RecordingFetcher(release);
        var service = Build(
            fetcher,
            current: "v0.3.0",
            settings: new UpdateCheckSettings { Enabled = true, IncludePrereleases = true });

        (await service.CheckAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task CheckAsync_UsesCacheWithinTtl_NoRefetch()
    {
        var release = new UpdateInfo { TagName = "v0.4.0" };
        var fetcher = new RecordingFetcher(release);
        var clock = new TestClock(DateTime.UtcNow);
        var service = Build(fetcher, current: "v0.3.0", clock: clock);

        // Prime the cache.
        await service.CheckAsync();
        fetcher.Calls.Should().Be(1);

        // Move 1 hour forward — well within the 24 h TTL.
        clock.Now = clock.Now.AddHours(1);
        await service.CheckAsync();
        fetcher.Calls.Should().Be(1, "cache hit must not call the fetcher");
    }

    [Fact]
    public async Task CheckAsync_RefreshesAfterTtlExpires()
    {
        var release = new UpdateInfo { TagName = "v0.4.0" };
        var fetcher = new RecordingFetcher(release);
        var clock = new TestClock(DateTime.UtcNow);
        var service = Build(fetcher, current: "v0.3.0", clock: clock);

        await service.CheckAsync();
        fetcher.Calls.Should().Be(1);

        clock.Now = clock.Now.AddHours(25);
        await service.CheckAsync();
        fetcher.Calls.Should().Be(2, "stale cache must trigger one fresh fetch");
    }

    [Fact]
    public async Task CheckAsync_FetchFailureWithCachedHit_ReturnsCache()
    {
        // Prime cache with a successful fetch.
        var release = new UpdateInfo { TagName = "v0.4.0" };
        var clock = new TestClock(DateTime.UtcNow);
        var fetcher = new RecordingFetcher(release);
        var service = Build(fetcher, current: "v0.3.0", clock: clock);
        await service.CheckAsync();

        // TTL elapses; next fetch fails — should fall back to cache.
        clock.Now = clock.Now.AddHours(25);
        fetcher.NextResult = null;

        var result = await service.CheckAsync();
        result.Should().NotBeNull();
        result!.TagName.Should().Be("v0.4.0");
    }

    [Fact]
    public async Task CheckAsync_FetchFailureNoCache_ReturnsNull_DoesNotCache()
    {
        var fetcher = new RecordingFetcher(null);
        var service = Build(fetcher, current: "v0.3.0");

        (await service.CheckAsync()).Should().BeNull();
        File.Exists(_cachePath).Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsync_FetcherThrows_ReturnsNull_NeverPropagates()
    {
        var fetcher = new ThrowingFetcher();
        var service = Build(fetcher, current: "v0.3.0");

        var act = async () => await service.CheckAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void GetCached_ReturnsCachedReleaseEvenWhenStale()
    {
        // Write a stale cache directly so we don't rely on the fetcher.
        var info = new UpdateInfo { TagName = "v0.4.0" };
        var envelope = new UpdateCheckService.CacheEnvelope
        {
            CheckedAt = DateTime.UtcNow.AddDays(-30),
            Release = info,
        };
        File.WriteAllText(_cachePath,
            Newtonsoft.Json.JsonConvert.SerializeObject(envelope));

        var fetcher = new RecordingFetcher(null);
        var service = Build(fetcher, current: "v0.3.0");

        var cached = service.GetCached();
        cached.Should().NotBeNull();
        cached!.TagName.Should().Be("v0.4.0");
    }

    [Fact]
    public async Task CheckAsync_VersionCompareHandlesRcSuffix()
    {
        // current is rc1, latest stable is the same version without suffix:
        // 0.4.0-rc1 < 0.4.0 → update should surface.
        var release = new UpdateInfo { TagName = "v0.4.0" };
        var fetcher = new RecordingFetcher(release);

        var service = new UpdateCheckService(
            fetcher,
            currentVersion: Parse("v0.4.0-rc1"),
            cachePath: _cachePath,
            readSettings: () => new UpdateCheckSettings());

        (await service.CheckAsync()).Should().NotBeNull();
    }

    private UpdateCheckService Build(
        IReleaseFetcher fetcher,
        string current,
        UpdateCheckSettings? settings = null,
        TestClock? clock = null)
    {
        settings ??= new UpdateCheckSettings();
        clock ??= new TestClock(DateTime.UtcNow);

        return new UpdateCheckService(
            fetcher,
            currentVersion: Parse(current),
            cachePath: _cachePath,
            readSettings: () => settings,
            utcNow: () => clock.Now);
    }

    private static VersionTag Parse(string s)
    {
        VersionTag.TryParse(s, out var t).Should().BeTrue();
        return t;
    }

    private sealed class TestClock
    {
        public DateTime Now;
        public TestClock(DateTime now) { Now = now; }
    }

    private sealed class RecordingFetcher : IReleaseFetcher
    {
        public int Calls;
        public UpdateInfo? NextResult;

        public RecordingFetcher() { NextResult = null; }
        public RecordingFetcher(UpdateInfo? next) { NextResult = next; }

        public Task<UpdateInfo?> FetchLatestAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(NextResult);
        }
    }

    private sealed class ThrowingFetcher : IReleaseFetcher
    {
        public Task<UpdateInfo?> FetchLatestAsync(CancellationToken ct)
            => throw new InvalidOperationException("simulated network blow-up");
    }
}
