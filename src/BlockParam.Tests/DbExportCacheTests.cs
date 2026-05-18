using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class DbExportCacheTests
{
    private const string Tok = "2026-05-18T10:00:00.0000000Z";

    // ── KeyFor ──────────────────────────────────────────────────────────────

    [Fact]
    public void KeyFor_SameInputs_IsStable()
    {
        var k1 = DbExportCache.KeyFor("proj1", "PLC_1", "DB_Valves", 10);
        var k2 = DbExportCache.KeyFor("proj1", "PLC_1", "DB_Valves", 10);

        k1.Should().Be(k2);
    }

    [Fact]
    public void KeyFor_DifferentProjectScope_ProducesDifferentKey()
    {
        DbExportCache.KeyFor("scopeA", "PLC_1", "DB_Valves", 10)
            .Should().NotBe(DbExportCache.KeyFor("scopeB", "PLC_1", "DB_Valves", 10));
    }

    [Fact]
    public void KeyFor_DifferentPlcName_ProducesDifferentKey()
    {
        DbExportCache.KeyFor("proj1", "PLC_1", "DB_Valves", 10)
            .Should().NotBe(DbExportCache.KeyFor("proj1", "PLC_2", "DB_Valves", 10));
    }

    [Fact]
    public void KeyFor_DifferentDbName_ProducesDifferentKey()
    {
        DbExportCache.KeyFor("proj1", "PLC_1", "DB_Valves", 10)
            .Should().NotBe(DbExportCache.KeyFor("proj1", "PLC_1", "DB_Pumps", 10));
    }

    [Fact]
    public void KeyFor_DifferentDbNumber_ProducesDifferentKey()
    {
        DbExportCache.KeyFor("proj1", "PLC_1", "DB_Valves", 10)
            .Should().NotBe(DbExportCache.KeyFor("proj1", "PLC_1", "DB_Valves", 11));
    }

    [Fact]
    public void KeyFor_SegmentBoundaryShift_DoesNotCollide()
    {
        // Without a separator, ("ab","c",..) and ("a","bc",..) would collide.
        DbExportCache.KeyFor("ab", "c", "DB", 1)
            .Should().NotBe(DbExportCache.KeyFor("a", "bc", "DB", 1));
    }

    // ── TryGet on empty cache ────────────────────────────────────────────────

    [Fact]
    public void TryGet_MissingKey_ReturnsFalse()
    {
        var cache = new DbExportCache();

        var found = cache.TryGet("no-such-key", Tok, out var xml);

        found.Should().BeFalse();
        xml.Should().BeNull();
    }

    [Fact]
    public void HasEntry_MissingKey_IsFalse()
    {
        new DbExportCache().HasEntry("nope").Should().BeFalse();
    }

    // ── Set + TryGet (token gating) ──────────────────────────────────────────

    [Fact]
    public void Set_ThenTryGetWithSameToken_ReturnsStoredXml()
    {
        var cache = new DbExportCache();
        const string expectedXml = "<Document><DB name=\"DB_Valves\"/></Document>";

        cache.Set("key1", Tok, expectedXml);
        var found = cache.TryGet("key1", Tok, out var xml);

        found.Should().BeTrue();
        xml.Should().Be(expectedXml);
    }

    [Fact]
    public void TryGet_TokenMismatch_IsMiss_ButEntryStillExists()
    {
        var cache = new DbExportCache();
        cache.Set("key1", "token-old", "<old/>");

        cache.TryGet("key1", "token-new", out var xml).Should().BeFalse();
        xml.Should().BeNull();
        // Distinguishes "stale" (key present, token moved) from a cold miss.
        cache.HasEntry("key1").Should().BeTrue();
    }

    [Fact]
    public void Set_RefreshedToken_Overwrites_AndNewTokenHits()
    {
        var cache = new DbExportCache();

        cache.Set("key1", "t1", "<first/>");
        cache.Set("key1", "t2", "<second/>");

        cache.TryGet("key1", "t1", out _).Should().BeFalse();
        cache.TryGet("key1", "t2", out var xml).Should().BeTrue();
        xml.Should().Be("<second/>");
    }

    // ── Invalidate ───────────────────────────────────────────────────────────

    [Fact]
    public void Invalidate_RemovesTargetKey_LeavesOthers()
    {
        var cache = new DbExportCache();
        cache.Set("key1", Tok, "<xml1/>");
        cache.Set("key2", Tok, "<xml2/>");

        cache.Invalidate("key1");

        cache.TryGet("key1", Tok, out _).Should().BeFalse();
        cache.HasEntry("key1").Should().BeFalse();
        cache.TryGet("key2", Tok, out var xml2).Should().BeTrue();
        xml2.Should().Be("<xml2/>");
    }

    [Fact]
    public void Invalidate_NonExistentKey_DoesNotThrow()
    {
        var cache = new DbExportCache();

        var act = () => cache.Invalidate("ghost-key");

        act.Should().NotThrow();
    }

    // ── Clear ────────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_EmptiesAllEntries()
    {
        var cache = new DbExportCache();
        cache.Set("key1", Tok, "<xml1/>");
        cache.Set("key2", Tok, "<xml2/>");

        cache.Clear();

        cache.TryGet("key1", Tok, out _).Should().BeFalse();
        cache.TryGet("key2", Tok, out _).Should().BeFalse();
        cache.HasEntry("key1").Should().BeFalse();
    }

    // ── Bounded LRU eviction ─────────────────────────────────────────────────

    [Fact]
    public void Set_PastCap_EvictsLeastRecentlyUsed()
    {
        var cache = new DbExportCache();
        for (int i = 0; i < DbExportCache.MaxEntries; i++)
            cache.Set($"k{i}", Tok, $"<x{i}/>");

        // One past the cap evicts the oldest (k0), keeps the rest + the new one.
        cache.Set("kNew", Tok, "<new/>");

        cache.HasEntry("k0").Should().BeFalse("k0 was least-recently-used");
        cache.HasEntry("k1").Should().BeTrue();
        cache.HasEntry($"k{DbExportCache.MaxEntries - 1}").Should().BeTrue();
        cache.TryGet("kNew", Tok, out var nx).Should().BeTrue();
        nx.Should().Be("<new/>");
    }

    [Fact]
    public void TryGet_Hit_RefreshesRecency_SoEntrySurvivesEviction()
    {
        var cache = new DbExportCache();
        for (int i = 0; i < DbExportCache.MaxEntries; i++)
            cache.Set($"k{i}", Tok, $"<x{i}/>");

        // Touch k0 so it is no longer the LRU; k1 becomes the eviction victim.
        cache.TryGet("k0", Tok, out _).Should().BeTrue();
        cache.Set("kNew", Tok, "<new/>");

        cache.HasEntry("k0").Should().BeTrue("a successful get marks it MRU");
        cache.HasEntry("k1").Should().BeFalse("k1 became the least-recently-used");
    }

    [Fact]
    public void Set_ExistingKey_DoesNotGrowCount_NorEvict()
    {
        var cache = new DbExportCache();
        for (int i = 0; i < DbExportCache.MaxEntries; i++)
            cache.Set($"k{i}", Tok, $"<x{i}/>");

        // Re-Set an existing key at capacity: update in place, evict nothing.
        cache.Set("k0", "t2", "<x0v2/>");

        cache.HasEntry($"k{DbExportCache.MaxEntries - 1}").Should().BeTrue();
        cache.TryGet("k0", "t2", out var x0).Should().BeTrue();
        x0.Should().Be("<x0v2/>");
    }
}
