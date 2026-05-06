using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// #81: <see cref="CompilePromptWorkflow.TryWithRetry"/> is the testable core
/// of the inconsistent-block-then-retry flow. <see cref="BlockExporter"/>
/// itself is a thin TIA-typed wrapper; verifying the sequencing here means we
/// don't need to mock <c>DataBlock</c> just to cover the retry logic.
/// </summary>
public class CompilePromptWorkflowTests
{
    /// <summary>
    /// Realistic TIA Openness exception chain has the "inconsistent" marker
    /// nested inside an outer <c>EngineeringTargetInvocationException</c>; we
    /// reproduce that shape so tests exercise <see cref="InconsistencyDetector"/>'s
    /// chain-walk, not just a top-level message match.
    /// </summary>
    private static Exception InconsistencyException() =>
        new InvalidOperationException("Cannot export",
            new InvalidOperationException("The block is inconsistent and cannot be exported."));

    [Fact]
    public void HappyPath_NoException_ReturnsTrue_DoesNotPromptOrCompile()
    {
        var promptCalled = false;
        var compileCalled = false;
        var exports = 0;

        var ok = CompilePromptWorkflow.TryWithRetry(
            blockName: "DB_Foo",
            exportAction: () => exports++,
            compileAction: () => compileCalled = true,
            askUser: () => { promptCalled = true; return true; });

        ok.Should().BeTrue();
        exports.Should().Be(1);
        promptCalled.Should().BeFalse();
        compileCalled.Should().BeFalse();
    }

    [Fact]
    public void Inconsistency_UserDeclines_ReturnsFalse_DoesNotCompileOrRetry()
    {
        var compileCalled = false;
        var exports = 0;

        var ok = CompilePromptWorkflow.TryWithRetry(
            blockName: "DB_Foo",
            exportAction: () => { exports++; throw InconsistencyException(); },
            compileAction: () => compileCalled = true,
            askUser: () => false);

        ok.Should().BeFalse();
        exports.Should().Be(1);
        compileCalled.Should().BeFalse();
    }

    [Fact]
    public void Inconsistency_UserAccepts_CompilesThenRetriesExport_ReturnsTrue()
    {
        var compileCalled = false;
        var exports = 0;

        var ok = CompilePromptWorkflow.TryWithRetry(
            blockName: "DB_Foo",
            exportAction: () =>
            {
                exports++;
                if (exports == 1) throw InconsistencyException();
            },
            compileAction: () => compileCalled = true,
            askUser: () => true);

        ok.Should().BeTrue();
        compileCalled.Should().BeTrue();
        exports.Should().Be(2);
    }

    /// <summary>
    /// The retry export is run unconditionally after compile — if it throws
    /// (e.g. compile didn't actually fix the inconsistency) the exception
    /// propagates rather than getting swallowed in a second prompt loop.
    /// Spec by design: the user already answered "Yes, compile and retry";
    /// looping on a second failure would be confusing UX.
    /// </summary>
    [Fact]
    public void Inconsistency_RetryStillThrows_ExceptionPropagates()
    {
        var act = () => CompilePromptWorkflow.TryWithRetry(
            blockName: "DB_Foo",
            exportAction: () => throw InconsistencyException(),
            compileAction: () => { },
            askUser: () => true);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void NonInconsistencyError_PropagatesWithoutPrompting()
    {
        var promptCalled = false;

        var act = () => CompilePromptWorkflow.TryWithRetry(
            blockName: "DB_Foo",
            exportAction: () => throw new InvalidOperationException("Read-only library"),
            compileAction: () => { },
            askUser: () => { promptCalled = true; return true; });

        act.Should().Throw<InvalidOperationException>().WithMessage("Read-only library");
        promptCalled.Should().BeFalse();
    }

    /// <summary>
    /// #51: TIA's error message is localized to the UI language, so the
    /// detector matches German "inkonsistent" alongside English. The retry
    /// path must fire on a German install too.
    /// </summary>
    [Fact]
    public void Inconsistency_GermanMessage_TriggersRetryPath()
    {
        var compileCalled = false;
        var exports = 0;

        var ok = CompilePromptWorkflow.TryWithRetry(
            blockName: "DB_Foo",
            exportAction: () =>
            {
                exports++;
                if (exports == 1)
                    throw new InvalidOperationException("Der Baustein ist inkonsistent.");
            },
            compileAction: () => compileCalled = true,
            askUser: () => true);

        ok.Should().BeTrue();
        compileCalled.Should().BeTrue();
    }
}
