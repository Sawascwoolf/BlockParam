using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class SafeFileNameTests
{
    [Theory]
    // Each char from Path.GetInvalidFileNameChars() that customer TIA names plausibly use.
    [InlineData("UDT:Setpoint", "UDT_Setpoint")]
    [InlineData("Valve<1>", "Valve_1_")]
    [InlineData("a|b", "a_b")]
    [InlineData("what?", "what_")]
    [InlineData("star*name", "star_name")]
    [InlineData("with/slash", "with_slash")]
    [InlineData("with\\back", "with_back")]
    [InlineData("quote\"name", "quote_name")]
    public void Sanitize_ReplacesInvalidCharsWithUnderscore(string input, string expected)
    {
        SafeFileName.Sanitize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("PlainName", "PlainName")]
    [InlineData("Name_With_Underscores", "Name_With_Underscores")]
    [InlineData("Name.With.Dots", "Name.With.Dots")]
    [InlineData("Name With Spaces", "Name With Spaces")]
    public void Sanitize_LeavesValidNamesUnchanged(string input, string expected)
    {
        SafeFileName.Sanitize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sanitize_OnNullOrEmpty_ReturnsUnderscore(string? input)
    {
        SafeFileName.Sanitize(input).Should().Be("_");
    }

    [Fact]
    public void Sanitize_OnAllInvalidChars_ReturnsUnderscoresOnly()
    {
        // Real customer scenario: a UDT named entirely from forbidden characters
        // must still produce a usable filename rather than throwing.
        SafeFileName.Sanitize("<>|?*").Should().Be("_____");
    }

    [Theory]
    // Windows rejects trailing dots and spaces in filenames even though they are
    // not in Path.GetInvalidFileNameChars(). Trim them so File.Create succeeds.
    [InlineData("Name.", "Name")]
    [InlineData("Name ", "Name")]
    [InlineData("Name. . .", "Name")]
    public void Sanitize_TrimsTrailingDotsAndSpaces(string input, string expected)
    {
        SafeFileName.Sanitize(input).Should().Be(expected);
    }

    [Fact]
    public void Sanitize_IsDeterministic_SoStalenessChecksKeepWorking()
    {
        // The on-disk UDT cache compares File.GetLastWriteTime(<sanitized>.xml) to
        // type.ModifiedDate; if the same UDT name produced different filenames on
        // different runs we would re-export every click.
        var first = SafeFileName.Sanitize("Setpoint:Limits");
        var second = SafeFileName.Sanitize("Setpoint:Limits");
        first.Should().Be(second);
    }
}
