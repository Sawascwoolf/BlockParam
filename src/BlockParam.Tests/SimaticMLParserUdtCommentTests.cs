using BlockParam.Models;
using BlockParam.SimaticML;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// End-to-end tests for the SimaticMLParser + UdtCommentResolver combination —
/// verifies per-DB-instance comment overrides win while UDT-type definitions
/// supply the fallback for leaves without an override (issue #16).
/// </summary>
public class SimaticMLParserUdtCommentTests
{
    private static (DataBlockInfo db, UdtCommentResolver resolver) LoadAll()
    {
        var setpointResolver = new UdtSetPointResolver();
        var commentResolver = new UdtCommentResolver();
        foreach (var (_, xml) in TestFixtures.LoadUdtFixtures())
        {
            setpointResolver.LoadFromXml(xml);
            commentResolver.LoadFromXml(xml);
        }
        var parser = new SimaticMLParser(
            constantResolver: null,
            udtResolver: setpointResolver,
            commentResolver: commentResolver);
        var db = parser.Parse(TestFixtures.LoadXml("DB_ProcessPlant_A1.xml"));
        return (db, commentResolver);
    }

    private static MemberNode Find(DataBlockInfo db, string path)
        => db.AllMembers().First(m => m.Path == path);

    [Fact]
    public void Leaves_inside_udt_ref_use_udt_type_comment_as_fallback()
    {
        var (db, _) = LoadAll();
        // DB_ProcessPlant_A1.xml has no per-instance comment overrides,
        // so these resolve via UDT_ControlValve's type definition.
        Find(db, "units[1].modules[1].valves[1].valveType").Comment
            .Should().Be("1=Ball 2=Butterfly 3=Diaphragm 4=Globe 5=Needle");
        Find(db, "units[1].modules[1].valves[1].positionSetpoint").Comment.Should().Be("%");
        Find(db, "units[1].modules[1].valves[1].deadband").Comment.Should().Be("%");
        // UDT_EquipmentModule.moduleType has a comment in the type def
        Find(db, "units[1].modules[1].moduleType").Comment
            .Should().Be("1=Dosing 2=Heating 3=Stirring 4=Draining");
    }

    [Fact]
    public void Leaves_without_type_comment_remain_null()
    {
        var (db, _) = LoadAll();
        // UDT_ControlValve.valveTag has no comment; fallback yields null.
        Find(db, "units[1].modules[1].valves[1].valveTag").Comment.Should().BeNull();
    }

    [Fact]
    public void Db_instance_override_wins_over_udt_type_comment()
    {
        // v20-tp307.xml carries instance-level comments like "a b" on
        // drive1.communicationError; the UDT type (messageConfig_UDT) defines no
        // comment for that member. The instance text must be returned unchanged.
        var setpointResolver = new UdtSetPointResolver();
        var commentResolver = new UdtCommentResolver();
        foreach (var (_, xml) in TestFixtures.LoadUdtFixtures())
        {
            setpointResolver.LoadFromXml(xml);
            commentResolver.LoadFromXml(xml);
        }
        var parser = new SimaticMLParser(
            constantResolver: null,
            udtResolver: setpointResolver,
            commentResolver: commentResolver);
        var db = parser.Parse(TestFixtures.LoadXml("v20-tp307.xml"));

        Find(db, "drive1").Comment.Should().Be("a");
        Find(db, "drive1.communicationError").Comment.Should().Be("a b");
        Find(db, "drive1.blocked").Comment.Should().Be("a d");
        Find(db, "drive2").Comment.Should().Be("e");
        Find(db, "drive2.blocked").Comment.Should().Be("e d");
        Find(db, "drive2.communicationError").Comment.Should().Be("e b");
    }

    [Fact]
    public void Nested_udt_chain_resolves_through_multiple_types()
    {
        var (db, _) = LoadAll();
        // flowLimits is a UDT ref (UDT_AlarmLimits). The leaves of UDT_AlarmLimits
        // carry no type-level comments — so fallback yields null, NOT the
        // parent's comment (the resolver must chain through the new UDT context).
        Find(db, "units[1].modules[1].valves[1].flowLimits.hiLimit").Comment.Should().BeNull();
    }

    [Fact]
    public void Subelement_comment_override_wins_for_member_inside_array_of_udt()
    {
        // Per-instance comment overrides on a UDT member that lives inside one or
        // more enclosing arrays are written by TIA as
        // <Subelement Path="i,j,..."><Comment>...</Comment></Subelement>
        // on the UDT member template — mirroring how StartValue overrides work.
        // moduleId normally gets its comment from UDT_EquipmentModule's type def
        // (no comment → null), but the override below must take precedence on
        // units[1,2].modules[1].moduleId and NOT leak to units[1,2].modules[2].moduleId.
        var xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <SW.Blocks.GlobalDB ID=""0"">
    <AttributeList>
      <Interface><Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
        <Section Name=""Static"">
          <Member Name=""units"" Datatype=""Array[1..2,2..3] of &quot;UDT_ProcessUnit&quot;"">
            <Sections><Section Name=""None"">
              <Member Name=""modules"" Datatype=""Array[1..3] of &quot;UDT_EquipmentModule&quot;"">
                <Sections><Section Name=""None"">
                  <Member Name=""moduleId"" Datatype=""Int"">
                    <Subelement Path=""1,2,1"">
                      <Comment><MultiLanguageText Lang=""en-GB"">Instance ABCD</MultiLanguageText></Comment>
                    </Subelement>
                  </Member>
                </Section></Sections>
              </Member>
            </Section></Sections>
          </Member>
        </Section>
      </Sections></Interface>
      <Name>DB_Test</Name>
    </AttributeList>
  </SW.Blocks.GlobalDB>
</Document>";

        var setpointResolver = new UdtSetPointResolver();
        var commentResolver = new UdtCommentResolver();
        foreach (var (_, fixtureXml) in TestFixtures.LoadUdtFixtures())
        {
            setpointResolver.LoadFromXml(fixtureXml);
            commentResolver.LoadFromXml(fixtureXml);
        }
        var parser = new SimaticMLParser(
            constantResolver: null,
            udtResolver: setpointResolver,
            commentResolver: commentResolver);
        var db = parser.Parse(xml);

        Find(db, "units[1,2].modules[1].moduleId").Comment.Should().Be("Instance ABCD");
        Find(db, "units[1,2].modules[2].moduleId").Comment.Should().BeNull();
        Find(db, "units[1,3].modules[1].moduleId").Comment.Should().BeNull();
    }

    [Fact]
    public void Parser_without_comment_resolver_preserves_legacy_behavior()
    {
        var setpointResolver = new UdtSetPointResolver();
        foreach (var (_, xml) in TestFixtures.LoadUdtFixtures())
            setpointResolver.LoadFromXml(xml);
        var parser = new SimaticMLParser(constantResolver: null, udtResolver: setpointResolver);
        var db = parser.Parse(TestFixtures.LoadXml("DB_ProcessPlant_A1.xml"));

        // Without the comment resolver, UDT-type comments do not surface.
        Find(db, "units[1].modules[1].valves[1].valveType").Comment.Should().BeNull();
    }
}
