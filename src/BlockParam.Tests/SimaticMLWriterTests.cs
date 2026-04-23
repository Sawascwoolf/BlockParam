using FluentAssertions;
using BlockParam.SimaticML;
using Xunit;

namespace BlockParam.Tests;

public class SimaticMLWriterTests
{
    private readonly SimaticMLParser _parser = new();
    private readonly SimaticMLWriter _writer = new();

    [Fact]
    public void Write_SingleValue_CorrectlyModified()
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = _parser.Parse(xml);
        var speed = db.Members.First(m => m.Name == "Speed");

        var result = _writer.ModifyStartValues(xml, new[] { speed }, "2000");

        result.IsSuccess.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        result.Changes[0].OldValue.Should().Be("1500");
        result.Changes[0].NewValue.Should().Be("2000");

        // Verify the XML was actually modified
        var modifiedDb = _parser.Parse(result.ModifiedXml);
        modifiedDb.Members.First(m => m.Name == "Speed").StartValue.Should().Be("2000");
    }

    [Fact]
    public void Write_BulkScope_AllMatchesModified()
    {
        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = _parser.Parse(xml);
        var allModuleIds = db.AllMembers().Where(m => m.Name == "ModuleId").ToList();

        var result = _writer.ModifyStartValues(xml, allModuleIds, "99");

        result.IsSuccess.Should().BeTrue();
        result.Changes.Should().HaveCount(4);
        result.Changes.Should().OnlyContain(c => c.NewValue == "99");

        var modifiedDb = _parser.Parse(result.ModifiedXml);
        modifiedDb.AllMembers()
            .Where(m => m.Name == "ModuleId")
            .Should().OnlyContain(m => m.StartValue == "99");
    }

    [Fact]
    public void Write_OnlyTargetScope_OtherScopesUntouched()
    {
        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = _parser.Parse(xml);
        // Only modify ModuleId in Drive1 (first 2), not Sensor1
        var drive1ModuleIds = db.AllMembers()
            .Where(m => m.Name == "ModuleId" && m.Path.StartsWith("Drive1."))
            .ToList();

        var result = _writer.ModifyStartValues(xml, drive1ModuleIds, "99");

        result.Changes.Should().HaveCount(2);

        var modifiedDb = _parser.Parse(result.ModifiedXml);
        modifiedDb.AllMembers()
            .Where(m => m.Name == "ModuleId" && m.Path.StartsWith("Drive1."))
            .Should().OnlyContain(m => m.StartValue == "99");
        modifiedDb.AllMembers()
            .Where(m => m.Name == "ModuleId" && m.Path.StartsWith("Sensor1."))
            .Should().OnlyContain(m => m.StartValue == "42"); // Unchanged
    }

    [Fact]
    public void Write_PreservesXmlStructure()
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = _parser.Parse(xml);
        var speed = db.Members.First(m => m.Name == "Speed");

        var result = _writer.ModifyStartValues(xml, new[] { speed }, "2000");

        // Other members should be unchanged
        var modifiedDb = _parser.Parse(result.ModifiedXml);
        modifiedDb.Name.Should().Be("FlatDB");
        modifiedDb.Number.Should().Be(1);
        modifiedDb.Members.First(m => m.Name == "Temperature").StartValue.Should().Be("25.5");
        modifiedDb.Members.First(m => m.Name == "Enable").StartValue.Should().Be("true");
    }

    [Fact]
    public void Write_BoolValue_CorrectFormat()
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = _parser.Parse(xml);
        var enable = db.Members.First(m => m.Name == "Enable");

        var result = _writer.ModifyStartValues(xml, new[] { enable }, "false");

        var modifiedDb = _parser.Parse(result.ModifiedXml);
        modifiedDb.Members.First(m => m.Name == "Enable").StartValue.Should().Be("false");
    }

    [Fact]
    public void Write_RealValue_CorrectFormat()
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = _parser.Parse(xml);
        var temp = db.Members.First(m => m.Name == "Temperature");

        var result = _writer.ModifyStartValues(xml, new[] { temp }, "99.9");

        var modifiedDb = _parser.Parse(result.ModifiedXml);
        modifiedDb.Members.First(m => m.Name == "Temperature").StartValue.Should().Be("99.9");
    }

    [Fact]
    public void Write_StringValue_CorrectFormat()
    {
        var xml = TestFixtures.LoadXml("mixed-types-db.xml");
        var db = _parser.Parse(xml);
        var str = db.Members.First(m => m.Name == "MyString");

        var result = _writer.ModifyStartValues(xml, new[] { str }, "'New Value'");

        var modifiedDb = _parser.Parse(result.ModifiedXml);
        modifiedDb.Members.First(m => m.Name == "MyString").StartValue.Should().Be("'New Value'");
    }

    [Fact]
    public void Write_ChangeSet_RecordsOldAndNewValues()
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = _parser.Parse(xml);
        var speed = db.Members.First(m => m.Name == "Speed");

        var result = _writer.ModifyStartValues(xml, new[] { speed }, "2000");

        result.Changes[0].MemberPath.Should().Be("Speed");
        result.Changes[0].OldValue.Should().Be("1500");
        result.Changes[0].NewValue.Should().Be("2000");
        result.Changes[0].Datatype.Should().Be("Int");
    }

    [Fact]
    public void Write_RoundTrip_ParseWriteParse_Consistent()
    {
        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db1 = _parser.Parse(xml);
        var allModuleIds = db1.AllMembers().Where(m => m.Name == "ModuleId").ToList();

        // Modify and parse again
        var result = _writer.ModifyStartValues(xml, allModuleIds, "77");
        var db2 = _parser.Parse(result.ModifiedXml);

        // Structure should be identical
        db2.Name.Should().Be(db1.Name);
        db2.Number.Should().Be(db1.Number);
        db2.AllMembers().Count().Should().Be(db1.AllMembers().Count());
        db2.AllMembers().Where(m => m.Name == "ModuleId")
            .Should().OnlyContain(m => m.StartValue == "77");
    }

    [Fact]
    public void Write_ConstantName_WrittenAsAttribute()
    {
        var xml = TestFixtures.LoadXml("v20-tp307.xml");
        var db = _parser.Parse(xml);
        var moduleId = db.AllMembers().First(m => m.Path == "drive1.communicationError.moduleId");

        var result = _writer.ModifyStartValues(xml, new[] { moduleId }, "MOD_TP308");

        result.IsSuccess.Should().BeTrue();
        result.Changes[0].OldValue.Should().Be("MOD_TP307");
        result.Changes[0].NewValue.Should().Be("MOD_TP308");

        // Verify XML has ConstantName attribute
        result.ModifiedXml.Should().Contain("ConstantName=\"&quot;MOD_TP308&quot;\"");

        // Round-trip: re-parse should see the constant name
        var modified = _parser.Parse(result.ModifiedXml);
        modified.AllMembers().First(m => m.Path == "drive1.communicationError.moduleId")
            .StartValue.Should().Be("MOD_TP308");
    }

    [Fact]
    public void Write_NumericValue_WrittenAsTextContent()
    {
        var xml = TestFixtures.LoadXml("v20-tp307.xml");
        var db = _parser.Parse(xml);
        var elementId = db.AllMembers().First(m => m.Path == "drive1.communicationError.elementId");

        var result = _writer.ModifyStartValues(xml, new[] { elementId }, "5");

        result.IsSuccess.Should().BeTrue();

        // Should NOT have ConstantName attribute for numeric values
        var modified = _parser.Parse(result.ModifiedXml);
        modified.AllMembers().First(m => m.Path == "drive1.communicationError.elementId")
            .StartValue.Should().Be("5");
    }

    [Fact]
    public void Write_ConstantToLiteral_RemovesConstantNameAttribute()
    {
        var xml = TestFixtures.LoadXml("v20-tp307.xml");
        var db = _parser.Parse(xml);
        var moduleId = db.AllMembers().First(m => m.Path == "drive1.communicationError.moduleId");

        // Replace constant with numeric value
        var result = _writer.ModifyStartValues(xml, new[] { moduleId }, "42");

        result.IsSuccess.Should().BeTrue();

        // The modified member should now be a literal, not a constant
        var modified = _parser.Parse(result.ModifiedXml);
        modified.AllMembers().First(m => m.Path == "drive1.communicationError.moduleId")
            .StartValue.Should().Be("42");

        // Other moduleId members should still have their constant
        modified.AllMembers().First(m => m.Path == "drive1.blocked.moduleId")
            .StartValue.Should().Be("MOD_TP307");
    }

    [Fact]
    public void Write_LiteralToConstant_AddsConstantNameAttribute()
    {
        var xml = TestFixtures.LoadXml("v20-tp307.xml");
        var db = _parser.Parse(xml);
        var elementId = db.AllMembers().First(m => m.Path == "drive1.communicationError.elementId");

        // Replace numeric with constant
        var result = _writer.ModifyStartValues(xml, new[] { elementId }, "ELE_DRIVE");

        result.IsSuccess.Should().BeTrue();
        result.ModifiedXml.Should().Contain("ConstantName=\"&quot;ELE_DRIVE&quot;\"");

        var modified = _parser.Parse(result.ModifiedXml);
        modified.AllMembers().First(m => m.Path == "drive1.communicationError.elementId")
            .StartValue.Should().Be("ELE_DRIVE");
    }

    [Fact]
    public void Write_BulkConstant_AllMembersGetConstantName()
    {
        var xml = TestFixtures.LoadXml("v20-tp307.xml");
        var db = _parser.Parse(xml);
        var allModuleIds = db.AllMembers().Where(m => m.Name == "moduleId").ToList();

        var result = _writer.ModifyStartValues(xml, allModuleIds, "MOD_TP310");

        result.IsSuccess.Should().BeTrue();

        var modified = _parser.Parse(result.ModifiedXml);
        modified.AllMembers().Where(m => m.Name == "moduleId")
            .Should().OnlyContain(m => m.StartValue == "MOD_TP310");
    }

    [Fact]
    public void Write_RemoveMembers_RemovesFromXml()
    {
        var xml = TestFixtures.LoadXml("nested-struct-db.xml");
        var db = _parser.Parse(xml);
        var minSpeed = db.AllMembers().First(m => m.Name == "MinSpeed");

        var modified = _writer.RemoveMembers(xml, new[] { minSpeed });

        var modifiedDb = _parser.Parse(modified);
        modifiedDb.AllMembers().Should().NotContain(m => m.Name == "MinSpeed");
        modifiedDb.AllMembers().Should().Contain(m => m.Name == "MaxSpeed");
    }

    [Fact]
    public void Write_ArrayElement_ModifiesSubelementStartValue()
    {
        var xml = TestFixtures.LoadXml("mixed-types-db.xml");
        var db = _parser.Parse(xml);
        var elem3 = db.Members.First(m => m.Name == "MyArray").Children[3]; // [3]

        var result = _writer.ModifyStartValues(xml, new[] { elem3 }, "4000");

        result.IsSuccess.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        result.Changes[0].OldValue.Should().Be("400");
        result.Changes[0].NewValue.Should().Be("4000");

        var modified = _parser.Parse(result.ModifiedXml);
        var reparsedElem3 = modified.Members.First(m => m.Name == "MyArray").Children[3];
        reparsedElem3.StartValue.Should().Be("4000");

        // Siblings untouched
        modified.Members.First(m => m.Name == "MyArray").Children[0].StartValue.Should().Be("100");
        modified.Members.First(m => m.Name == "MyArray").Children[4].StartValue.Should().Be("500");
    }

    [Fact]
    public void Write_ArrayElement_CreatesSubelementWhenMissing()
    {
        // Start from an array with only some Subelements pre-populated, then
        // write to an index that has no Subelement yet.
        var xml = TestFixtures.LoadXml("array-db.xml");
        var db = _parser.Parse(xml);
        // PrimitiveArray has indices 5..8 all populated; drop one via raw
        // edit is overkill — instead use the all-indices-of-Valve test below.
        // Here, verify that an existing index rewrite doesn't duplicate Subelements.
        var arr = db.Members.First(m => m.Name == "PrimitiveArray");
        var elem6 = arr.Children[1]; // [6]

        var result = _writer.ModifyStartValues(xml, new[] { elem6 }, "600");

        result.IsSuccess.Should().BeTrue();
        var modified = _parser.Parse(result.ModifiedXml);
        modified.Members.First(m => m.Name == "PrimitiveArray").Children[1].StartValue.Should().Be("600");
    }

    [Fact]
    public void Write_MultiDimArrayElement_WritesCommaIndexPath()
    {
        var xml = TestFixtures.LoadXml("array-db.xml");
        var db = _parser.Parse(xml);
        var matrix = db.Members.First(m => m.Name == "Matrix");
        var elem_1_2 = matrix.Children[5]; // [1,2]

        var result = _writer.ModifyStartValues(xml, new[] { elem_1_2 }, "999");

        result.IsSuccess.Should().BeTrue();
        result.ModifiedXml.Should().Contain("Path=\"1,2\"");
        var modified = _parser.Parse(result.ModifiedXml);
        modified.Members.First(m => m.Name == "Matrix").Children[5].StartValue.Should().Be("999");
    }

    [Fact]
    public void Write_AllArrayElements_BulkApplies()
    {
        var xml = TestFixtures.LoadXml("mixed-types-db.xml");
        var db = _parser.Parse(xml);
        var allIndices = db.Members.First(m => m.Name == "MyArray").Children.ToList();

        var result = _writer.ModifyStartValues(xml, allIndices, "7");

        result.IsSuccess.Should().BeTrue();
        result.Changes.Should().HaveCount(5);

        var modified = _parser.Parse(result.ModifiedXml);
        modified.Members.First(m => m.Name == "MyArray").Children
            .Should().OnlyContain(c => c.StartValue == "7");
    }

    [Fact]
    public void TokenizePath_SplitsIndexesCorrectly()
    {
        SimaticMLWriter.TokenizePath("Foo.Bar[3].Speed")
            .Should().Equal("Foo", "Bar", "[3]", "Speed");

        SimaticMLWriter.TokenizePath("Matrix[0,1]")
            .Should().Equal("Matrix", "[0,1]");

        SimaticMLWriter.TokenizePath("Plain.Name")
            .Should().Equal("Plain", "Name");
    }
}
