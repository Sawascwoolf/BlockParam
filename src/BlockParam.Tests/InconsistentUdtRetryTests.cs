using System.Collections.Generic;
using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class InconsistentUdtRetryTests
{
    private sealed class FakeUdt
    {
        public string Name { get; }
        public bool CompileSucceeds { get; set; } = true;
        public bool ReExportSucceeds { get; set; } = true;
        public bool CompileCalled { get; set; }
        public bool ReExportCalled { get; set; }

        public FakeUdt(string name) { Name = name; }
    }

    [Fact]
    public void RetryAfterCompile_WhenListEmpty_DoesNotPromptOrRetry()
    {
        var promptCalled = false;

        var retried = InconsistentUdtRetry.RetryAfterCompile<FakeUdt>(
            failed: new List<FakeUdt>(),
            nameOf: u => u.Name,
            tryCompile: _ => true,
            tryReExport: _ => true,
            askUser: _ => { promptCalled = true; return true; });

        retried.Should().Be(0);
        promptCalled.Should().BeFalse();
    }

    [Fact]
    public void RetryAfterCompile_WhenUserDeclines_DoesNotCompileOrReExport()
    {
        var udts = new List<FakeUdt>
        {
            new FakeUdt("UDT_ControlValve"),
            new FakeUdt("UDT_EquipmentModule"),
        };

        var retried = InconsistentUdtRetry.RetryAfterCompile(
            failed: udts,
            nameOf: u => u.Name,
            tryCompile: u => { u.CompileCalled = true; return true; },
            tryReExport: u => { u.ReExportCalled = true; return true; },
            askUser: _ => false);

        retried.Should().Be(0);
        udts.Should().OnlyContain(u => !u.CompileCalled && !u.ReExportCalled);
    }

    [Fact]
    public void RetryAfterCompile_PassesCollectedNamesToPrompt_InOrder()
    {
        var udts = new List<FakeUdt>
        {
            new FakeUdt("UDT_AlarmLimits"),
            new FakeUdt("UDT_ControlValve"),
            new FakeUdt("UDT_ProcessUnit"),
        };
        IReadOnlyList<string>? seen = null;

        InconsistentUdtRetry.RetryAfterCompile(
            failed: udts,
            nameOf: u => u.Name,
            tryCompile: _ => true,
            tryReExport: _ => true,
            askUser: names => { seen = names; return false; });

        seen.Should().Equal("UDT_AlarmLimits", "UDT_ControlValve", "UDT_ProcessUnit");
    }

    [Fact]
    public void RetryAfterCompile_OnYes_CompilesAndReExportsEachIndividually()
    {
        var udts = new List<FakeUdt>
        {
            new FakeUdt("UDT_A"),
            new FakeUdt("UDT_B"),
        };

        var retried = InconsistentUdtRetry.RetryAfterCompile(
            failed: udts,
            nameOf: u => u.Name,
            tryCompile: u => { u.CompileCalled = true; return true; },
            tryReExport: u => { u.ReExportCalled = true; return true; },
            askUser: _ => true);

        retried.Should().Be(2);
        udts.Should().OnlyContain(u => u.CompileCalled && u.ReExportCalled);
    }

    [Fact]
    public void RetryAfterCompile_WhenCompileFails_SkipsReExportForThatUdt()
    {
        var udts = new List<FakeUdt>
        {
            new FakeUdt("UDT_Good"),
            new FakeUdt("UDT_Bad") { CompileSucceeds = false },
        };

        var retried = InconsistentUdtRetry.RetryAfterCompile(
            failed: udts,
            nameOf: u => u.Name,
            tryCompile: u => { u.CompileCalled = true; return u.CompileSucceeds; },
            tryReExport: u => { u.ReExportCalled = true; return u.ReExportSucceeds; },
            askUser: _ => true);

        retried.Should().Be(1);
        udts[0].ReExportCalled.Should().BeTrue();
        udts[1].CompileCalled.Should().BeTrue();
        udts[1].ReExportCalled.Should().BeFalse();
    }

    [Fact]
    public void RetryAfterCompile_WhenReExportFailsAfterCompile_CountsOnlySuccess()
    {
        var udts = new List<FakeUdt>
        {
            new FakeUdt("UDT_Succeeds"),
            new FakeUdt("UDT_StillBroken") { ReExportSucceeds = false },
        };

        var retried = InconsistentUdtRetry.RetryAfterCompile(
            failed: udts,
            nameOf: u => u.Name,
            tryCompile: _ => true,
            tryReExport: u => u.ReExportSucceeds,
            askUser: _ => true);

        retried.Should().Be(1);
    }
}
