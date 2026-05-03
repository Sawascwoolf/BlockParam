using FluentAssertions;
using BlockParam.Updates;
using Xunit;

namespace BlockParam.Tests;

public class VersionTagTests
{
    [Theory]
    [InlineData("v0.4.0", 0, 4, 0, "")]
    [InlineData("0.4.0", 0, 4, 0, "")]
    [InlineData("V1.2.3", 1, 2, 3, "")]
    [InlineData("v0.4.0-rc1", 0, 4, 0, "rc1")]
    [InlineData("0.4.0-beta.2", 0, 4, 0, "beta.2")]
    [InlineData("1.0.0+build.42", 1, 0, 0, "")]
    [InlineData("1.0.0-rc1+build.7", 1, 0, 0, "rc1")]
    [InlineData("v1", 1, 0, 0, "")]
    [InlineData("v1.2", 1, 2, 0, "")]
    public void TryParse_AcceptsCommonForms(string input, int major, int minor, int patch, string pre)
    {
        VersionTag.TryParse(input, out var tag).Should().BeTrue();
        tag.Major.Should().Be(major);
        tag.Minor.Should().Be(minor);
        tag.Patch.Should().Be(patch);
        tag.PreRelease.Should().Be(pre);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("v.")]
    [InlineData("vX.Y.Z")]
    [InlineData("1.2.3.4")]
    public void TryParse_RejectsGarbage(string? input)
    {
        VersionTag.TryParse(input, out _).Should().BeFalse();
    }

    [Fact]
    public void Compare_NewerPatchWins()
    {
        Parse("v0.4.0").CompareTo(Parse("v0.3.9")).Should().BePositive();
    }

    [Fact]
    public void Compare_NewerMinorWins()
    {
        Parse("v0.4.0").CompareTo(Parse("v0.3.99")).Should().BePositive();
    }

    [Fact]
    public void Compare_PreReleaseSortsBeforeStable()
    {
        // Per SemVer 2.0: 0.4.0-rc1 < 0.4.0
        Parse("v0.4.0-rc1").CompareTo(Parse("v0.4.0")).Should().BeNegative();
    }

    [Fact]
    public void Compare_PreReleaseOrdering_NumericalIds()
    {
        // rc1 < rc2 (numerical ids compared numerically)
        Parse("v0.4.0-rc1").CompareTo(Parse("v0.4.0-rc2")).Should().BeNegative();
    }

    [Fact]
    public void Compare_PreReleaseOrdering_DotSeparated()
    {
        // beta.2 < beta.10  — numeric segments compared as numbers
        Parse("v1.0.0-beta.2").CompareTo(Parse("v1.0.0-beta.10")).Should().BeNegative();
    }

    [Fact]
    public void Compare_StableEqualsPlain()
    {
        Parse("v1.2.3").CompareTo(Parse("1.2.3")).Should().Be(0);
    }

    [Fact]
    public void FromSystemVersion_Roundtrip()
    {
        var tag = VersionTag.FromSystemVersion(new System.Version(1, 2, 3, 0));
        tag.Major.Should().Be(1);
        tag.Minor.Should().Be(2);
        tag.Patch.Should().Be(3);
        tag.PreRelease.Should().BeEmpty();
        tag.IsPreRelease.Should().BeFalse();
    }

    private static VersionTag Parse(string s)
    {
        VersionTag.TryParse(s, out var t).Should().BeTrue($"input '{s}' must parse");
        return t;
    }
}
