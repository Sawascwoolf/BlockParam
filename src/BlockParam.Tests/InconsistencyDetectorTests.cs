using System;
using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class InconsistencyDetectorTests
{
    [Theory]
    // English — actual TIA Portal phrasing on en-US installs.
    [InlineData("The block is inconsistent and cannot be exported.")]
    [InlineData("Inconsistent block")]
    [InlineData("INCONSISTENT BLOCK")] // case-insensitive match
    // German — actual TIA Portal phrasing on de-DE installs (#51).
    [InlineData("Der Baustein ist inkonsistent und kann nicht exportiert werden.")]
    [InlineData("Inkonsistenter Baustein")]
    [InlineData("INKONSISTENT")]
    public void Matches_OnLocalizedInconsistencyMessage_ReturnsTrue(string message)
    {
        var ex = new InvalidOperationException(message);
        InconsistencyDetector.Matches(ex).Should().BeTrue();
    }

    [Fact]
    public void Matches_WhenInnerExceptionCarriesTheMarker_ReturnsTrue()
    {
        var inner = new InvalidOperationException("Der Baustein ist inkonsistent.");
        var outer = new Exception("Export failed", inner);

        InconsistencyDetector.Matches(outer).Should().BeTrue();
    }

    [Fact]
    public void Matches_WalksFullInnerExceptionChain_NotJustOneLevel()
    {
        // TIA's EngineeringTargetInvocationException can wrap multiple times before
        // the originating "Inconsistent" message is reached.
        var deep = new InvalidOperationException("inkonsistent");
        var middle = new Exception("Wrapper layer", deep);
        var outer = new Exception("Export failed", middle);

        InconsistencyDetector.Matches(outer).Should().BeTrue();
    }

    [Fact]
    public void Matches_OnUnrelatedExportError_ReturnsFalse()
    {
        // Disk-full / permission / not-found should NOT trigger the compile prompt.
        var ex = new System.IO.IOException("Access to the path is denied.");
        InconsistencyDetector.Matches(ex).Should().BeFalse();
    }

    [Fact]
    public void Matches_OnMessageMentioningConsistency_DoesNotFalseMatch()
    {
        // "consistent" alone is not an inconsistency marker — guard against an over-broad match.
        var ex = new InvalidOperationException("Project metadata is consistent.");
        InconsistencyDetector.Matches(ex).Should().BeFalse();
    }

    [Fact]
    public void Matches_OnNullException_ReturnsFalse()
    {
        InconsistencyDetector.Matches(null).Should().BeFalse();
    }

    [Fact]
    public void Matches_OnExceptionWithEmptyMessage_ReturnsFalse()
    {
        var ex = new Exception(string.Empty);
        InconsistencyDetector.Matches(ex).Should().BeFalse();
    }
}
