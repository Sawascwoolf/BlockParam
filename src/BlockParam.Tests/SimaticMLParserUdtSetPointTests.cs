using BlockParam.Models;
using BlockParam.SimaticML;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// End-to-end tests for the SimaticMLParser + UdtSetPointResolver combination
/// against real TIA Portal V20 exports (DB_ProcessPlant_A1 + its referenced UDTs).
/// </summary>
public class SimaticMLParserUdtSetPointTests
{
    private static (DataBlockInfo db, UdtSetPointResolver resolver) LoadAll()
    {
        var resolver = new UdtSetPointResolver();
        foreach (var (_, xml) in TestFixtures.LoadUdtFixtures())
            resolver.LoadFromXml(xml);
        var parser = new SimaticMLParser(constantResolver: null, udtResolver: resolver);
        var db = parser.Parse(TestFixtures.LoadXml("DB_ProcessPlant_A1.xml"));
        return (db, resolver);
    }

    private static MemberNode Find(DataBlockInfo db, string path)
        => db.AllMembers().First(m => m.Path == path);

    [Fact]
    public void Top_level_members_use_db_xml_setpoint()
    {
        var (db, _) = LoadAll();
        Find(db, "plantId").IsSetPoint.Should().BeFalse();     // DB: SetPoint=false
        Find(db, "plantName").IsSetPoint.Should().BeFalse();   // DB: SetPoint=false
        Find(db, "units").IsSetPoint.Should().BeTrue();        // DB: SetPoint=true
    }

    [Fact]
    public void Bare_leaves_inside_array_of_udt_resolve_via_udt_type()
    {
        var (db, _) = LoadAll();
        // units is Array[1..2] of UDT_ProcessUnit; unitId/unitName/recipeId have no
        // AttributeList in the DB XML — resolver must look them up in UDT_ProcessUnit.
        Find(db, "units[1].unitId").IsSetPoint.Should().BeFalse();
        Find(db, "units[1].unitName").IsSetPoint.Should().BeFalse();
        Find(db, "units[1].recipeId").IsSetPoint.Should().BeFalse();
    }

    [Fact]
    public void Nested_udt_instance_flag_comes_from_outer_udt_type()
    {
        var (db, _) = LoadAll();
        // units[i].modules: no DB AttributeList, lives in UDT_ProcessUnit as
        // Array of UDT_EquipmentModule with SetPoint=true at the type level.
        Find(db, "units[1].modules").IsSetPoint.Should().BeTrue();
        Find(db, "units[1].modules[1].valves").IsSetPoint.Should().BeTrue();
        Find(db, "units[1].modules[1].valves[1].flowLimits").IsSetPoint.Should().BeTrue();
        Find(db, "units[1].modules[1].valves[1].pressureLimits").IsSetPoint.Should().BeTrue();
    }

    [Fact]
    public void Leaves_inside_nested_udt_ref_resolve_via_referenced_type()
    {
        var (db, _) = LoadAll();
        // All UDT_AlarmLimits leaves have SetPoint=false in the type def.
        Find(db, "units[1].modules[1].valves[1].flowLimits.hiLimit").IsSetPoint.Should().BeFalse();
        Find(db, "units[1].modules[1].valves[1].flowLimits.enable").IsSetPoint.Should().BeFalse();
        Find(db, "units[1].modules[1].valves[1].pressureLimits.loLoLimit").IsSetPoint.Should().BeFalse();
    }

    [Fact]
    public void No_unresolved_udts_when_all_referenced_types_are_loaded()
    {
        var (db, _) = LoadAll();
        db.UnresolvedUdts.Should().BeEmpty();
    }

    [Fact]
    public void Missing_udt_type_is_recorded_as_unresolved()
    {
        // Load only a subset — omit UDT_AlarmLimits. The DB references it indirectly
        // via UDT_ControlValve.flowLimits/pressureLimits.
        var resolver = new UdtSetPointResolver();
        foreach (var (name, xml) in TestFixtures.LoadUdtFixtures())
        {
            if (name.Contains("AlarmLimits")) continue;
            resolver.LoadFromXml(xml);
        }
        var parser = new SimaticMLParser(constantResolver: null, udtResolver: resolver);
        var db = parser.Parse(TestFixtures.LoadXml("DB_ProcessPlant_A1.xml"));

        db.UnresolvedUdts.Should().Contain("UDT_AlarmLimits");
    }

    [Fact]
    public void Parser_without_resolver_preserves_legacy_behavior()
    {
        // No resolver → bare members inside arrays-of-UDT get IsSetPoint=false,
        // and UnresolvedUdts is empty (we can't detect what's missing without a resolver).
        var parser = new SimaticMLParser();
        var db = parser.Parse(TestFixtures.LoadXml("DB_ProcessPlant_A1.xml"));

        db.UnresolvedUdts.Should().BeEmpty();
        Find(db, "units").IsSetPoint.Should().BeTrue();               // has DB AttributeList
        Find(db, "units[1].unitId").IsSetPoint.Should().BeFalse();    // no DB AttributeList, no resolver
    }
}
