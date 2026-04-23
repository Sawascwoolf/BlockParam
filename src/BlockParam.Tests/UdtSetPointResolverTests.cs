using BlockParam.SimaticML;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class UdtSetPointResolverTests
{
    private static UdtSetPointResolver LoadAll()
    {
        var resolver = new UdtSetPointResolver();
        foreach (var (_, xml) in TestFixtures.LoadUdtFixtures())
            resolver.LoadFromXml(xml);
        return resolver;
    }

    [Fact]
    public void Loads_all_fixtures()
    {
        var r = LoadAll();
        r.HasType("messageConfig_UDT").Should().BeTrue();
        r.HasType("UDT_AlarmLimits").Should().BeTrue();
        r.HasType("modul_UDT").Should().BeTrue();
        r.TypeCount.Should().BeGreaterOrEqualTo(8);
    }

    [Theory]
    [InlineData("messageConfig_UDT", "moduleId", true)]
    [InlineData("messageConfig_UDT", "elementId", true)]
    [InlineData("messageConfig_UDT", "actualValue", false)]
    [InlineData("UDT_AlarmLimits", "hiLimit", false)]
    [InlineData("UDT_AlarmLimits", "enable", false)]
    [InlineData("UDT_ControlValve", "flowLimits", true)]
    [InlineData("UDT_ControlValve", "valveTag", false)]
    [InlineData("UDT_ProcessUnit", "modules", true)]
    [InlineData("UDT_ProcessUnit", "unitId", false)]
    public void Resolves_direct_members(string udt, string member, bool expected)
    {
        var r = LoadAll();
        r.TryGetSetPoint(udt, "", member).Should().Be(expected);
    }

    [Fact]
    public void Resolves_members_inside_inline_struct()
    {
        // modul_UDT.drive2 is a Struct; its children carry their own SetPoint
        var r = LoadAll();
        r.TryGetSetPoint("modul_UDT", "drive2", "blocked").Should().Be(true);
        r.TryGetSetPoint("modul_UDT", "drive2", "communicationError").Should().Be(true);
        r.TryGetSetPoint("modul_UDT", "general", "transferFailed").Should().Be(true);
    }

    [Fact]
    public void Does_not_recurse_into_udt_ref_inline_expansions()
    {
        // modul_UDT.drive1 is a UDT ref to driveMessagesConfig_UDT.
        // Its inline expansion in modul_UDT.xml has no SetPoint attrs — we must
        // not descend into them, because the true SetPoint lives in the ref'd UDT.
        var r = LoadAll();
        r.TryGetSetPoint("modul_UDT", "drive1", "communicationError").Should().BeNull();
    }

    [Fact]
    public void Unknown_type_or_member_returns_null()
    {
        var r = LoadAll();
        r.TryGetSetPoint("nonexistent_UDT", "", "foo").Should().BeNull();
        r.TryGetSetPoint("messageConfig_UDT", "", "nonexistent").Should().BeNull();
    }

    [Theory]
    [InlineData("\"UDT_Foo\"", "UDT_Foo")]
    [InlineData("Array[1..5] of \"UDT_Foo\"", "UDT_Foo")]
    [InlineData("Array[0..1, 0..1] of \"UDT_Bar\"", "UDT_Bar")]
    [InlineData("Int", null)]
    [InlineData("Struct", null)]
    [InlineData("Array[1..5] of Int", null)]
    [InlineData("", null)]
    public void ExtractUdtName_covers_common_datatype_forms(string datatype, string? expected)
    {
        UdtSetPointResolver.ExtractUdtName(datatype).Should().Be(expected);
    }

    [Fact]
    public void Handles_empty_directory_and_missing_directory()
    {
        var r = new UdtSetPointResolver();
        r.LoadFromDirectory(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()));
        r.HasTypes.Should().BeFalse();
    }
}
