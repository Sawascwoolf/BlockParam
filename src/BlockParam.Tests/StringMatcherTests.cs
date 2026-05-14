using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Unit tests for <see cref="StringMatcher"/> — the case-insensitive
/// multi-field substring matcher extracted in #83.
/// </summary>
public class StringMatcherTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void EmptyOrWhitespaceFilter_MatchesEverything(string? filter)
    {
        // The "no filter = match all" rule lets callers pass user input
        // straight through without an `if (string.IsNullOrWhiteSpace(...))` guard.
        StringMatcher.MatchesAny(filter, "anything").Should().BeTrue();
        StringMatcher.MatchesAny(filter, "a", "b", "c").Should().BeTrue();
        StringMatcher.MatchesAny(filter).Should().BeTrue();
        StringMatcher.MatchesAny(filter, (string?[]?)null).Should().BeTrue();
    }

    [Fact]
    public void SingleField_Hit_ReturnsTrue()
    {
        StringMatcher.MatchesAny("foo", "barfoobaz").Should().BeTrue();
    }

    [Fact]
    public void SingleField_Miss_ReturnsFalse()
    {
        StringMatcher.MatchesAny("foo", "barbaz").Should().BeFalse();
    }

    [Fact]
    public void MultiField_OneHit_ReturnsTrue()
    {
        StringMatcher.MatchesAny("needle",
            "alpha", "haystack-needle-haystack", "omega").Should().BeTrue();
    }

    [Fact]
    public void MultiField_NoHits_ReturnsFalse()
    {
        StringMatcher.MatchesAny("needle",
            "alpha", "bravo", "charlie").Should().BeFalse();
    }

    [Fact]
    public void AllNullFields_ReturnsFalse()
    {
        // Filter is non-empty so the vacuous-true branch doesn't kick in;
        // all fields are null, so nothing can match — but we must not NRE.
        StringMatcher.MatchesAny("foo", null, null, null).Should().BeFalse();
    }

    [Fact]
    public void MixedNullAndMatchingField_ReturnsTrue()
    {
        // Null fields are skipped, not treated as the empty string.
        StringMatcher.MatchesAny("foo", null, "contains foo", null).Should().BeTrue();
    }

    [Theory]
    [InlineData("FOO", "barfoobaz")]
    [InlineData("foo", "BARFOOBAZ")]
    [InlineData("FoO", "barFOObaz")]
    [InlineData("BAR", "barfoobaz")]
    public void IsCaseInsensitive(string filter, string field)
    {
        StringMatcher.MatchesAny(filter, field).Should().BeTrue();
    }

    [Fact]
    public void NullFieldsArray_WithNonEmptyFilter_ReturnsFalse()
    {
        // Defensive: a caller that forwards a null array shouldn't crash.
        StringMatcher.MatchesAny("foo", (string?[]?)null).Should().BeFalse();
    }

    [Fact]
    public void EmptyFieldsArray_WithNonEmptyFilter_ReturnsFalse()
    {
        StringMatcher.MatchesAny("foo").Should().BeFalse();
    }
}
