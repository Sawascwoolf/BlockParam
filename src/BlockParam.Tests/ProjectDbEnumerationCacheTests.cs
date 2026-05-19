using System;
using System.Collections.Generic;
using BlockParam.Models;
using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// #155 item 1 — the project DB enumeration must run at most once per scope per
/// TIA session, with the explicit-refresh Invalidate as the staleness valve.
/// </summary>
public class ProjectDbEnumerationCacheTests
{
    private static IReadOnlyList<DataBlockSummary> Db(params string[] names)
    {
        var list = new List<DataBlockSummary>();
        foreach (var n in names) list.Add(new DataBlockSummary(n, ""));
        return list;
    }

    [Fact]
    public void GetOrAdd_FirstCall_RunsEnumeratorAndReturnsResult()
    {
        var cache = new ProjectDbEnumerationCache();
        int calls = 0;

        var result = cache.GetOrAdd("scopeA", () => { calls++; return Db("DB1", "DB2"); });

        calls.Should().Be(1);
        result.Should().HaveCount(2);
        cache.HasEntry("scopeA").Should().BeTrue();
    }

    [Fact]
    public void GetOrAdd_SecondCallSameScope_DoesNotReRunEnumerator()
    {
        var cache = new ProjectDbEnumerationCache();
        int calls = 0;
        Func<IReadOnlyList<DataBlockSummary>> enumerate = () => { calls++; return Db("DB1"); };

        var first = cache.GetOrAdd("scopeA", enumerate);
        var second = cache.GetOrAdd("scopeA", enumerate);

        calls.Should().Be(1, "the expensive project walk is session-cached");
        second.Should().BeSameAs(first, "the cached list instance is returned");
    }

    [Fact]
    public void GetOrAdd_DifferentScopes_EnumerateIndependently()
    {
        var cache = new ProjectDbEnumerationCache();

        var a = cache.GetOrAdd("scopeA", () => Db("A"));
        var b = cache.GetOrAdd("scopeB", () => Db("B1", "B2"));

        a.Should().HaveCount(1);
        b.Should().HaveCount(2);
        cache.HasEntry("scopeA").Should().BeTrue();
        cache.HasEntry("scopeB").Should().BeTrue();
    }

    [Fact]
    public void Invalidate_ForcesReEnumerationOnNextGet()
    {
        var cache = new ProjectDbEnumerationCache();
        int calls = 0;
        Func<IReadOnlyList<DataBlockSummary>> enumerate = () => { calls++; return Db($"v{calls}"); };

        cache.GetOrAdd("scopeA", enumerate);
        cache.Invalidate("scopeA");
        var after = cache.GetOrAdd("scopeA", enumerate);

        calls.Should().Be(2, "explicit refresh busts the session cache");
        after[0].Name.Should().Be("v2");
    }

    [Fact]
    public void Invalidate_UnknownScope_DoesNotThrow()
    {
        var cache = new ProjectDbEnumerationCache();

        var act = () => cache.Invalidate("ghost");

        act.Should().NotThrow();
    }

    [Fact]
    public void Clear_DropsEveryScope()
    {
        var cache = new ProjectDbEnumerationCache();
        cache.GetOrAdd("scopeA", () => Db("A"));
        cache.GetOrAdd("scopeB", () => Db("B"));

        cache.Clear();

        cache.HasEntry("scopeA").Should().BeFalse();
        cache.HasEntry("scopeB").Should().BeFalse();
    }

    [Fact]
    public void GetOrAdd_NullEnumerator_Throws()
    {
        var cache = new ProjectDbEnumerationCache();

        var act = () => cache.GetOrAdd("scopeA", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Bounded LRU eviction (project-switch guard) ──────────────────────────

    [Fact]
    public void GetOrAdd_PastEntryCap_EvictsLeastRecentlyUsedScope()
    {
        var cache = new ProjectDbEnumerationCache(maxEntries: 2);

        cache.GetOrAdd("s0", () => Db("0"));
        cache.GetOrAdd("s1", () => Db("1"));
        cache.GetOrAdd("s2", () => Db("2")); // count 3 > 2 → evict s0 (LRU)

        cache.HasEntry("s0").Should().BeFalse("s0 was least-recently-used");
        cache.HasEntry("s1").Should().BeTrue();
        cache.HasEntry("s2").Should().BeTrue();
    }

    [Fact]
    public void GetOrAdd_Hit_RefreshesRecency_SoScopeSurvivesEviction()
    {
        var cache = new ProjectDbEnumerationCache(maxEntries: 2);
        cache.GetOrAdd("s0", () => Db("0"));
        cache.GetOrAdd("s1", () => Db("1"));

        // Touch s0 so s1 becomes the eviction victim.
        cache.GetOrAdd("s0", () => Db("ignored"));
        cache.GetOrAdd("s2", () => Db("2"));

        cache.HasEntry("s0").Should().BeTrue("a hit marks it most-recently-used");
        cache.HasEntry("s1").Should().BeFalse("s1 became least-recently-used");
        cache.HasEntry("s2").Should().BeTrue();
    }
}
