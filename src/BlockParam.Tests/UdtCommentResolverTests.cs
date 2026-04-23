using BlockParam.SimaticML;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class UdtCommentResolverTests
{
    private static UdtCommentResolver LoadAll()
    {
        var resolver = new UdtCommentResolver();
        foreach (var (_, xml) in TestFixtures.LoadUdtFixtures())
            resolver.LoadFromXml(xml);
        return resolver;
    }

    [Fact]
    public void Loads_all_fixtures()
    {
        var r = LoadAll();
        r.HasType("UDT_ControlValve").Should().BeTrue();
        r.HasType("UDT_EquipmentModule").Should().BeTrue();
        r.TypeCount.Should().BeGreaterOrEqualTo(8);
    }

    [Theory]
    [InlineData("UDT_ControlValve", "valveType", "1=Ball 2=Butterfly 3=Diaphragm 4=Globe 5=Needle")]
    [InlineData("UDT_ControlValve", "positionSetpoint", "%")]
    [InlineData("UDT_ControlValve", "deadband", "%")]
    [InlineData("UDT_EquipmentModule", "moduleType", "1=Dosing 2=Heating 3=Stirring 4=Draining")]
    public void Resolves_direct_member_comments(string udt, string member, string expected)
    {
        var r = LoadAll();
        r.TryGetComment(udt, "", member).Should().Be(expected);
    }

    [Fact]
    public void Members_without_comment_return_null()
    {
        var r = LoadAll();
        r.TryGetComment("UDT_ControlValve", "", "valveTag").Should().BeNull();
        r.TryGetComment("UDT_ControlValve", "", "travelTime").Should().BeNull();
    }

    [Fact]
    public void Unknown_type_or_member_returns_null()
    {
        var r = LoadAll();
        r.TryGetComment("nonexistent_UDT", "", "foo").Should().BeNull();
        r.TryGetComment("UDT_ControlValve", "", "nonexistent").Should().BeNull();
    }

    [Fact]
    public void Does_not_recurse_into_udt_ref_inline_expansions()
    {
        // UDT_ControlValve.flowLimits is a UDT ref to UDT_AlarmLimits.
        // Its inline expansion in UDT_ControlValve.xml has no real Comment attrs — we must
        // not descend into them, because the true Comment lives in the referenced UDT.
        var r = LoadAll();
        r.TryGetComment("UDT_ControlValve", "flowLimits", "hiLimit").Should().BeNull();
    }

    [Fact]
    public void Handles_empty_directory_and_missing_directory()
    {
        var r = new UdtCommentResolver();
        r.LoadFromDirectory(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()));
        r.HasTypes.Should().BeFalse();
    }
}
