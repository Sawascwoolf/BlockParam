using FluentAssertions;
using BlockParam.SimaticML;
using Xunit;

namespace BlockParam.Tests;

public class SimaticMLParserTests
{
    private readonly SimaticMLParser _parser = new();

    [Fact]
    public void Parse_Collects_All_MultiLanguageText_Variants_Into_Comments_Dict()
    {
        var xml = TestFixtures.LoadXml("inline-rules-db.xml");
        var db = _parser.Parse(xml);

        var moduleId = db.Members.Single(m => m.Name == "moduleId");
        moduleId.Comments.Should().ContainKey("en-GB");
        moduleId.Comments.Should().ContainKey("de-DE");
        moduleId.Comments["en-GB"].Should().Contain("{bp_varTable=MOD_}");
        moduleId.Comments["de-DE"].Should().Be("Modul-ID");
    }

    [Fact]
    public void Parse_FlatDb_ReturnsCorrectMemberCount()
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = _parser.Parse(xml);

        db.Name.Should().Be("FlatDB");
        db.Number.Should().Be(1);
        db.MemoryLayout.Should().Be("Optimized");
        db.BlockType.Should().Be("GlobalDB");
        db.Members.Should().HaveCount(3);
        db.Members[0].Name.Should().Be("Speed");
        db.Members[0].StartValue.Should().Be("1500");
        db.Members[1].Name.Should().Be("Temperature");
        db.Members[1].StartValue.Should().Be("25.5");
        db.Members[2].Name.Should().Be("Enable");
        db.Members[2].StartValue.Should().Be("true");
    }

    [Fact]
    public void Parse_NestedStruct_BuildsCorrectTree()
    {
        var xml = TestFixtures.LoadXml("nested-struct-db.xml");
        var db = _parser.Parse(xml);

        db.Members.Should().HaveCount(2); // Config + Status
        var config = db.Members[0];
        config.Name.Should().Be("Config");
        config.IsStruct.Should().BeTrue();
        config.Children.Should().HaveCount(3); // MaxSpeed, MinSpeed, Settings

        var settings = config.Children[2];
        settings.Name.Should().Be("Settings");
        settings.IsStruct.Should().BeTrue();
        settings.Children.Should().HaveCount(2);
        settings.Children[0].Name.Should().Be("Timeout");
        settings.Children[0].StartValue.Should().Be("5000");
        settings.Children[0].Parent.Should().BeSameAs(settings);
    }

    [Fact]
    public void Parse_UdtInstances_DetectedCorrectly()
    {
        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = _parser.Parse(xml);

        db.Members.Should().HaveCount(2); // Drive1 + Sensor1
        var drive1 = db.Members[0];
        drive1.Name.Should().Be("Drive1");
        drive1.IsUdtInstance.Should().BeTrue();
        drive1.IsStruct.Should().BeFalse();

        var msgCommError = drive1.Children[0];
        msgCommError.Name.Should().Be("Msg_CommError");
        msgCommError.IsUdtInstance.Should().BeTrue();
        msgCommError.Children.Should().HaveCount(4);
    }

    [Fact]
    public void Parse_AllDataTypes_StartValuesPreserved()
    {
        var xml = TestFixtures.LoadXml("mixed-types-db.xml");
        var db = _parser.Parse(xml);

        db.Members.Should().HaveCount(9);
        db.Members.First(m => m.Name == "MyBool").StartValue.Should().Be("true");
        db.Members.First(m => m.Name == "MyInt").StartValue.Should().Be("42");
        db.Members.First(m => m.Name == "MyDInt").StartValue.Should().Be("100000");
        db.Members.First(m => m.Name == "MyReal").StartValue.Should().Be("3.14");
        db.Members.First(m => m.Name == "MyLReal").StartValue.Should().Be("3.14159265358979");
        db.Members.First(m => m.Name == "MyString").StartValue.Should().Be("'Hello World'");
        db.Members.First(m => m.Name == "MyTime").StartValue.Should().Be("T#5s");
        db.Members.First(m => m.Name == "MyDate").StartValue.Should().Be("D#2024-01-01");
    }

    [Fact]
    public void Parse_ArrayMember_RecognizedAsArray()
    {
        var xml = TestFixtures.LoadXml("mixed-types-db.xml");
        var db = _parser.Parse(xml);

        var array = db.Members.First(m => m.Name == "MyArray");
        array.IsArray.Should().BeTrue();
        array.Datatype.Should().Be("Array[0..4] of Int");
    }

    [Fact]
    public void Parse_PrimitiveArray_ExpandedIntoPerIndexChildren()
    {
        var xml = TestFixtures.LoadXml("mixed-types-db.xml");
        var db = _parser.Parse(xml);

        var array = db.Members.First(m => m.Name == "MyArray");
        array.Children.Should().HaveCount(5);
        array.Children[0].Name.Should().Be("[0]");
        array.Children[0].Path.Should().Be("MyArray[0]");
        array.Children[0].IsArrayElement.Should().BeTrue();
        array.Children[0].StartValue.Should().Be("100");
        array.Children[4].Name.Should().Be("[4]");
        array.Children[4].StartValue.Should().Be("500");
    }

    [Fact]
    public void Parse_ArrayNonZeroLowerBound_StartsAtCorrectIndex()
    {
        var xml = TestFixtures.LoadXml("array-db.xml");
        var db = _parser.Parse(xml);

        var arr = db.Members.First(m => m.Name == "PrimitiveArray");
        arr.Children.Should().HaveCount(4);
        arr.Children[0].Name.Should().Be("[5]");
        arr.Children[0].StartValue.Should().Be("50");
        arr.Children[3].Name.Should().Be("[8]");
        arr.Children[3].StartValue.Should().Be("80");
    }

    [Fact]
    public void Parse_ArraySymbolicBound_ResolvedViaConstantResolver()
    {
        var xml = TestFixtures.LoadXml("array-db.xml");
        var resolver = new StubResolver(("MAX_VALVES", 3));
        var parser = new SimaticMLParser(resolver);
        var db = parser.Parse(xml);

        var arr = db.Members.First(m => m.Name == "ValveCount");
        arr.UnresolvedBound.Should().BeNull();
        arr.Children.Should().HaveCount(3);
        arr.Children[0].Name.Should().Be("[1]");
        arr.Children[0].StartValue.Should().Be("10");
        arr.Children[2].Name.Should().Be("[3]");
        arr.Children[2].StartValue.Should().Be("30");
    }

    [Fact]
    public void Parse_ArraySymbolicBoundWithoutResolver_SurfacedAsUnresolved()
    {
        var xml = TestFixtures.LoadXml("array-db.xml");
        var db = _parser.Parse(xml); // no resolver

        var arr = db.Members.First(m => m.Name == "ValveCount");
        arr.UnresolvedBound.Should().Be("MAX_VALVES");
        arr.Children.Should().BeEmpty();
        arr.IsArray.Should().BeTrue();
    }

    [Fact]
    public void Parse_ArrayUnknownConstant_EvenWithResolver_Unresolved()
    {
        var xml = TestFixtures.LoadXml("array-db.xml");
        var resolver = new StubResolver(("MAX_VALVES", 3));
        var parser = new SimaticMLParser(resolver);
        var db = parser.Parse(xml);

        var arr = db.Members.First(m => m.Name == "UnknownBound");
        arr.UnresolvedBound.Should().Be("UNKNOWN_CONST");
        arr.Children.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ArrayAboveSizeCap_CollapsedWithSizeMarker()
    {
        // Use a synthetic DB with a huge array bound resolved via a stub resolver;
        // the parser must refuse to materialise per-index nodes and instead set
        // UnresolvedBound to a "(too large: …)" marker.
        const string xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <SW.Blocks.GlobalDB ID=""0"">
    <AttributeList>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Static"">
            <Member Name=""Huge"" Datatype=""Array[0..HUGE] of Int"" Accessibility=""Public"" />
          </Section>
        </Sections>
      </Interface>
      <MemoryLayout>Optimized</MemoryLayout>
      <Name>HugeDB</Name>
      <Number>1</Number>
      <ProgrammingLanguage>DB</ProgrammingLanguage>
    </AttributeList>
  </SW.Blocks.GlobalDB>
</Document>";
        var parser = new SimaticMLParser(new StubResolver(("HUGE", 1_000_000)));
        var db = parser.Parse(xml);

        var arr = db.Members.First(m => m.Name == "Huge");
        arr.IsArray.Should().BeTrue();
        arr.Children.Should().BeEmpty();
        arr.UnresolvedBound.Should().StartWith("(too large:");
    }

    [Fact]
    public void Parse_MultiDimArray_ExpandedCartesian()
    {
        var xml = TestFixtures.LoadXml("array-db.xml");
        var db = _parser.Parse(xml);

        var matrix = db.Members.First(m => m.Name == "Matrix");
        matrix.Children.Should().HaveCount(6); // 2 * 3

        matrix.Children[0].Name.Should().Be("[0,0]");
        matrix.Children[0].Path.Should().Be("Matrix[0,0]");
        matrix.Children[0].StartValue.Should().Be("11");
        matrix.Children[5].Name.Should().Be("[1,2]");
        matrix.Children[5].StartValue.Should().Be("23");
    }

    private sealed class StubResolver : BlockParam.Services.IConstantResolver
    {
        private readonly Dictionary<string, int> _map;
        public StubResolver(params (string name, int value)[] entries)
        {
            _map = entries.ToDictionary(e => e.name, e => e.value, StringComparer.OrdinalIgnoreCase);
        }
        public bool TryResolve(string name, out int value) => _map.TryGetValue(name, out value);
    }

    [Fact]
    public void Parse_DeepNesting_CorrectDepthAndPaths()
    {
        var xml = TestFixtures.LoadXml("deep-nesting-db.xml");
        var db = _parser.Parse(xml);

        var allMembers = db.AllMembers().ToList();
        var deepValue = allMembers.First(m => m.Name == "DeepValue");

        deepValue.Path.Should().Be("Level1.Level2.Level3.Level4.DeepValue");
        deepValue.Depth.Should().Be(4);
        deepValue.StartValue.Should().Be("999");
    }

    [Fact]
    public void Parse_StandardMemoryLayout_Works()
    {
        var xml = TestFixtures.LoadXml("standard-memory-db.xml");
        var db = _parser.Parse(xml);

        db.MemoryLayout.Should().Be("Standard");
        db.Members.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_MalformedXml_ThrowsMeaningfulError()
    {
        var malformed = "<not valid xml";

        var act = () => _parser.Parse(malformed);

        act.Should().Throw<SimaticMLParseException>()
            .WithMessage("*Failed to parse*");
    }

    [Fact]
    public void Parse_NonDbBlock_ThrowsUnsupported()
    {
        var fbXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <SW.Blocks.FB ID=""0"">
    <AttributeList>
      <Name>TestFB</Name>
    </AttributeList>
  </SW.Blocks.FB>
</Document>";

        var act = () => _parser.Parse(fbXml);

        act.Should().Throw<SimaticMLParseException>()
            .WithMessage("*Unsupported block type*FB*");
    }

    [Fact]
    public void MemberNode_Depth_CalculatedCorrectly()
    {
        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = _parser.Parse(xml);

        // Drive1 (depth 0) > Msg_CommError (depth 1) > ModuleId (depth 2)
        var drive1 = db.Members[0];
        drive1.Depth.Should().Be(0);

        var msg = drive1.Children[0];
        msg.Depth.Should().Be(1);

        var moduleId = msg.Children[0];
        moduleId.Depth.Should().Be(2);
    }

    [Fact]
    public void MemberNode_Path_ReflectsFullHierarchy()
    {
        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = _parser.Parse(xml);

        var moduleId = db.Members[0].Children[0].Children[0];
        moduleId.Path.Should().Be("Drive1.Msg_CommError.ModuleId");
    }

    [Fact]
    public void Parse_AllMembers_EnumeratesEntireTree()
    {
        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = _parser.Parse(xml);

        var allMembers = db.AllMembers().ToList();
        // 2 elements * (1 element + 2 messages * (1 msg + 4 vars)) = 2 + 2*(2 + 2*4) = 2 + 20 = 22
        // Actually: Drive1, Msg_CommError, ModuleId, ElementId, MessageId, Active,
        //           Msg_Overtemp, ModuleId, ElementId, MessageId, Active,
        //           Sensor1, Msg_CommError, ModuleId, ElementId, MessageId, Active,
        //           Msg_Overtemp, ModuleId, ElementId, MessageId, Active = 22
        allMembers.Should().HaveCount(22);
    }

    [Fact]
    public void Parse_LeafNodes_IdentifiedCorrectly()
    {
        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = _parser.Parse(xml);

        var drive1 = db.Members[0];
        drive1.IsLeaf.Should().BeFalse();

        var moduleId = drive1.Children[0].Children[0];
        moduleId.IsLeaf.Should().BeTrue();
    }

    // ===== V20 real export tests =====

    [Fact]
    public void Parse_V20_GlobalDB_NestedUdtSections()
    {
        var xml = TestFixtures.LoadXml("v20-tp307.xml");
        var db = _parser.Parse(xml);

        db.Name.Should().Be("TP307");
        db.BlockType.Should().Be("GlobalDB");
        db.Members.Should().HaveCount(3); // drive1, drive2, general

        // drive1 is "driveMessagesConfig_UDT" → children in Sections/Section[@Name="None"]
        var drive1 = db.Members[0];
        drive1.Name.Should().Be("drive1");
        drive1.Datatype.Should().Contain("driveMessagesConfig_UDT");
        drive1.Children.Should().HaveCount(2); // communicationError, blocked

        // Each child is "messageConfig_UDT" with its own nested Sections
        var commError = drive1.Children[0];
        commError.Name.Should().Be("communicationError");
        commError.Datatype.Should().Contain("messageConfig_UDT");
        commError.Children.Should().HaveCount(5); // moduleId, elementId, messageId, bmkId, actualValue

        commError.Children[0].Name.Should().Be("moduleId");
        commError.Children[0].IsLeaf.Should().BeTrue();

        // drive2 is Struct → children as direct Member elements
        var drive2 = db.Members[1];
        drive2.Name.Should().Be("drive2");
        drive2.Datatype.Should().Be("Struct");
        drive2.Children.Should().HaveCount(2); // blocked, communicationError
        drive2.Children[0].Children.Should().HaveCount(5); // nested UDT members
    }

    [Fact]
    public void Parse_V20_ConstantName_ReadAsStartValue()
    {
        var xml = TestFixtures.LoadXml("v20-tp307.xml");
        var db = _parser.Parse(xml);

        var moduleId = db.AllMembers().First(m => m.Path == "drive1.communicationError.moduleId");
        moduleId.StartValue.Should().Be("MOD_TP307");
    }

    [Fact]
    public void Parse_V20_LiteralStartValue_StillWorks()
    {
        var xml = TestFixtures.LoadXml("v20-tp307.xml");
        var db = _parser.Parse(xml);

        var elementId = db.AllMembers().First(m => m.Path == "drive1.communicationError.elementId");
        elementId.StartValue.Should().Be("ELE_DRIVE_1");
    }

    [Fact]
    public void Parse_V20_NoStartValue_ReturnsNull()
    {
        var xml = TestFixtures.LoadXml("v20-tp307.xml");
        var db = _parser.Parse(xml);

        var messageId = db.AllMembers().First(m => m.Path == "drive1.communicationError.messageId");
        messageId.StartValue.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Parse_V20_MemberComment_Parsed()
    {
        var xml = TestFixtures.LoadXml("v20-tp307.xml");
        var db = _parser.Parse(xml);

        var drive1 = db.Members.First(m => m.Name == "drive1");
        drive1.Comment.Should().Be("a");

        var commError = db.AllMembers().First(m => m.Path == "drive1.communicationError");
        commError.Comment.Should().Be("a b");

        var blocked = db.AllMembers().First(m => m.Path == "drive1.blocked");
        blocked.Comment.Should().Be("a d");
    }

    [Fact]
    public void Parse_V20_MemberWithoutComment_ReturnsNull()
    {
        var xml = TestFixtures.LoadXml("v20-tp307.xml");
        var db = _parser.Parse(xml);

        var moduleId = db.AllMembers().First(m => m.Path == "drive1.communicationError.moduleId");
        moduleId.Comment.Should().BeNull();
    }

    [Fact]
    public void Parse_V20_InstanceDB_Supported()
    {
        var xml = TestFixtures.LoadXml("v20-tp308-instancedb.xml");
        var db = _parser.Parse(xml);

        db.Name.Should().Be("TP308_ModulUDT");
        db.BlockType.Should().Be("InstanceDB");
        db.Members.Should().HaveCount(3); // drive1, drive2, general

        // Full tree depth: drive1 → communicationError → moduleId
        var allMembers = db.AllMembers().ToList();
        allMembers.Should().Contain(m => m.Path == "drive1.communicationError.moduleId");
        allMembers.Should().Contain(m => m.Path == "drive2.blocked.moduleId");
        allMembers.Should().Contain(m => m.Path == "general.transferFailed.moduleId");
    }
}
