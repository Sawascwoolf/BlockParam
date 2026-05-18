using System;
using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// #155 items 2 &amp; 3 — the gated walk (tag-table re-export / UDT freshness
/// scan) must run at most once per scope per TIA session, must NOT cache a
/// failed run as "done", and must re-run after the explicit-refresh Invalidate.
/// </summary>
public class SessionScopeGateTests
{
    [Fact]
    public void RunOnce_FirstCall_RunsActionAndReportsRan()
    {
        var gate = new SessionScopeGate("test");
        int runs = 0;

        var ran = gate.RunOnce("scopeA", () => runs++);

        ran.Should().BeTrue();
        runs.Should().Be(1);
        gate.HasRun("scopeA").Should().BeTrue();
    }

    [Fact]
    public void RunOnce_SecondCallSameScope_SkipsActionAndReportsSkipped()
    {
        var gate = new SessionScopeGate("test");
        int runs = 0;

        gate.RunOnce("scopeA", () => runs++);
        var ranAgain = gate.RunOnce("scopeA", () => runs++);

        ranAgain.Should().BeFalse("already satisfied this session");
        runs.Should().Be(1, "the expensive walk is not repeated");
    }

    [Fact]
    public void RunOnce_DifferentScopes_RunIndependently()
    {
        var gate = new SessionScopeGate("test");
        int runs = 0;

        gate.RunOnce("scopeA", () => runs++).Should().BeTrue();
        gate.RunOnce("scopeB", () => runs++).Should().BeTrue();

        runs.Should().Be(2);
    }

    [Fact]
    public void RunOnce_ActionThrows_ScopeStaysUnsatisfied_SoNextOpenRetries()
    {
        var gate = new SessionScopeGate("test");

        var firstAttempt = () => gate.RunOnce("scopeA", () => throw new InvalidOperationException("boom"));
        firstAttempt.Should().Throw<InvalidOperationException>();

        gate.HasRun("scopeA").Should().BeFalse("a failed walk must never be cached as done");

        int runs = 0;
        gate.RunOnce("scopeA", () => runs++).Should().BeTrue("the next open retries");
        runs.Should().Be(1);
    }

    [Fact]
    public void Invalidate_ReRunsActionOnNextRunOnce()
    {
        var gate = new SessionScopeGate("test");
        int runs = 0;

        gate.RunOnce("scopeA", () => runs++);
        gate.Invalidate("scopeA");
        var ran = gate.RunOnce("scopeA", () => runs++);

        ran.Should().BeTrue("explicit refresh busts the session gate");
        runs.Should().Be(2);
    }

    [Fact]
    public void Invalidate_UnknownScope_DoesNotThrow()
    {
        var gate = new SessionScopeGate("test");

        var act = () => gate.Invalidate("ghost");

        act.Should().NotThrow();
    }

    [Fact]
    public void Clear_ResetsEveryScope()
    {
        var gate = new SessionScopeGate("test");
        gate.RunOnce("scopeA", () => { });
        gate.RunOnce("scopeB", () => { });

        gate.Clear();

        gate.HasRun("scopeA").Should().BeFalse();
        gate.HasRun("scopeB").Should().BeFalse();
    }

    [Fact]
    public void RunOnce_NullAction_Throws()
    {
        var gate = new SessionScopeGate("test");

        var act = () => gate.RunOnce("scopeA", null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
