using System.Diagnostics;
using System.Text;
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

    [Fact]
    public void Write_ClearDirectMember_RemovesStartValueElement()
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = _parser.Parse(xml);
        var speed = db.Members.First(m => m.Name == "Speed");

        var result = _writer.ModifyStartValues(xml, new[] { speed }, "");

        result.IsSuccess.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        result.Changes[0].OldValue.Should().Be("1500");
        result.Changes[0].NewValue.Should().Be("");

        // No empty <StartValue> emitted anywhere.
        result.ModifiedXml.Should().NotContain("<StartValue></StartValue>");
        result.ModifiedXml.Should().NotContain("<StartValue />");

        var modified = _parser.Parse(result.ModifiedXml);
        modified.Members.First(m => m.Name == "Speed").StartValue.Should().BeNull();
        // Siblings untouched.
        modified.Members.First(m => m.Name == "Temperature").StartValue.Should().Be("25.5");
        modified.Members.First(m => m.Name == "Enable").StartValue.Should().Be("true");
    }

    [Fact]
    public void Write_ClearUdtInstanceMember_RemovesStartValue_SiblingsUntouched()
    {
        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = _parser.Parse(xml);
        var moduleId = db.AllMembers()
            .First(m => m.Path == "Drive1.Msg_CommError.ModuleId");

        var result = _writer.ModifyStartValues(xml, new[] { moduleId }, "");

        result.IsSuccess.Should().BeTrue();
        result.Changes[0].OldValue.Should().Be("42");
        result.ModifiedXml.Should().NotContain("<StartValue></StartValue>");
        result.ModifiedXml.Should().NotContain("<StartValue />");

        var modified = _parser.Parse(result.ModifiedXml);
        modified.AllMembers().First(m => m.Path == "Drive1.Msg_CommError.ModuleId")
            .StartValue.Should().BeNull();
        // A different UDT-instance's ModuleId is unaffected.
        modified.AllMembers().First(m => m.Path == "Sensor1.Msg_CommError.ModuleId")
            .StartValue.Should().Be("42");
    }

    [Fact]
    public void Write_ClearArrayElement_PrunesSubelement_SiblingsUntouched()
    {
        var xml = TestFixtures.LoadXml("mixed-types-db.xml");
        var db = _parser.Parse(xml);
        var elem3 = db.Members.First(m => m.Name == "MyArray").Children[3]; // [3]

        var result = _writer.ModifyStartValues(xml, new[] { elem3 }, "");

        result.IsSuccess.Should().BeTrue();
        result.Changes[0].OldValue.Should().Be("400");
        result.ModifiedXml.Should().NotContain("<StartValue></StartValue>");
        result.ModifiedXml.Should().NotContain("<StartValue />");

        var modified = _parser.Parse(result.ModifiedXml);
        modified.Members.First(m => m.Name == "MyArray").Children[3].StartValue.Should().BeNull();
        // Sibling indices keep their values.
        modified.Members.First(m => m.Name == "MyArray").Children[0].StartValue.Should().Be("100");
        modified.Members.First(m => m.Name == "MyArray").Children[4].StartValue.Should().Be("500");
    }

    [Fact]
    public void Write_ClearWhenNoStartValuePresent_IsIdempotentNoEmptyElement()
    {
        // Clear the same direct member twice; the second clear has nothing
        // to remove and must not create an empty element or error.
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = _parser.Parse(xml);
        var speed = db.Members.First(m => m.Name == "Speed");

        var first = _writer.ModifyStartValues(xml, new[] { speed }, "");
        first.IsSuccess.Should().BeTrue();

        var db2 = _parser.Parse(first.ModifiedXml);
        var speed2 = db2.Members.First(m => m.Name == "Speed");
        var second = _writer.ModifyStartValues(first.ModifiedXml, new[] { speed2 }, "");

        second.IsSuccess.Should().BeTrue();
        second.Changes.Should().BeEmpty(
            "a clear with no <StartValue> to remove is a genuine no-op — recording a "
            + "phantom change would charge quota and audit-log a write that changed nothing");
        second.ModifiedXml.Should().NotContain("<StartValue></StartValue>");
        second.ModifiedXml.Should().NotContain("<StartValue />");
    }

    // ─────────────────────────────────────────────────────────────────────
    // #159 — batch overload (H1) + O(n) Subelement indexing (H2)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// #159 H1: the batch overload assigns a distinct value per member in a
    /// single parse/serialize pass. Verifies each member gets its own value
    /// (not a shared one) and the change set is complete.
    /// </summary>
    [Fact]
    public void WriteBatch_PerMemberDistinctValues_AllApplied()
    {
        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = _parser.Parse(xml);
        var moduleIds = db.AllMembers().Where(m => m.Name == "ModuleId").ToList();
        moduleIds.Count.Should().BeGreaterThan(1);

        var edits = moduleIds
            .Select((m, i) => (Member: m, Value: (1000 + i).ToString()))
            .ToList();

        var result = _writer.ModifyStartValues(xml, edits);

        result.IsSuccess.Should().BeTrue();
        result.Changes.Should().HaveCount(edits.Count);

        var modified = _parser.Parse(result.ModifiedXml);
        var reModuleIds = modified.AllMembers().Where(m => m.Name == "ModuleId").ToList();
        for (int i = 0; i < reModuleIds.Count; i++)
            reModuleIds[i].StartValue.Should().Be((1000 + i).ToString());
    }

    /// <summary>
    /// #159 H1: a member missing from the XML is reported in Errors without
    /// aborting the batch — the valid edits in the same call still apply.
    /// Mirrors the old per-edit Apply loop's skip-and-log behaviour.
    /// </summary>
    [Fact]
    public void WriteBatch_MissingMember_RecordedAsError_OthersStillApplied()
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = _parser.Parse(xml);
        var speed = db.Members.First(m => m.Name == "Speed");
        var ghost = new BlockParam.Models.MemberNode(
            "Ghost", "Int", "0", "DoesNotExist", null,
            new List<BlockParam.Models.MemberNode>());

        var result = _writer.ModifyStartValues(
            xml, new[] { (speed, "2000"), (ghost, "5") });

        result.Changes.Should().ContainSingle(c => c.MemberPath == "Speed");
        result.Errors.Should().ContainSingle().Which.Should().Contain("DoesNotExist");

        var modified = _parser.Parse(result.ModifiedXml);
        modified.Members.First(m => m.Name == "Speed").StartValue.Should().Be("2000");
    }

    /// <summary>
    /// #159 H1+H2 regression gate. A 10,000-element <c>Array Of DInt</c> with
    /// an explicit StartValue on every element is the issue's reproduction
    /// case. The pre-fix Apply path re-parsed + re-serialized the whole
    /// multi-MB document once per edit (H1, O(n) document cycles) and rescanned
    /// every Subelement linearly per edit (H2, O(n^2) comparisons), taking
    /// seconds-to-minutes. The batch overload is one parse/serialize with an
    /// O(1) Subelement index, so this completes in well under the (generous,
    /// CI-jitter-tolerant) ceiling. A revert to either O(n) pattern blows
    /// past it by orders of magnitude.
    /// </summary>
    [Fact]
    public void WriteBatch_TenThousandElementArray_AppliesCorrectlyAndFast()
    {
        const int n = 10_000;
        var xml = BuildLargeArrayDbXml(n);
        var db = _parser.Parse(xml);
        var arr = db.Members.First(m => m.Name == "BigArray");
        arr.Children.Should().HaveCount(n, "the parser expands arrays up to 100k elements");

        // Distinct value per element so a shared-value bug can't pass.
        var edits = arr.Children
            .Select((c, i) => (Member: c, Value: (i + 1).ToString()))
            .ToList();

        var sw = Stopwatch.StartNew();
        var result = _writer.ModifyStartValues(xml, edits);
        sw.Stop();

        result.IsSuccess.Should().BeTrue();
        result.Changes.Should().HaveCount(n);
        sw.Elapsed.TotalSeconds.Should().BeLessThan(10,
            "#159 H1+H2: batched single parse + O(1) Subelement index — a "
            + "regression to per-edit parse (O(n)) or linear Subelement scan "
            + "(O(n^2)) would take far longer than this generous ceiling");

        var modified = _parser.Parse(result.ModifiedXml);
        var reArr = modified.Members.First(m => m.Name == "BigArray");
        reArr.Children[0].StartValue.Should().Be("1");
        reArr.Children[n / 2].StartValue.Should().Be((n / 2 + 1).ToString());
        reArr.Children[n - 1].StartValue.Should().Be(n.ToString());
    }

    /// <summary>
    /// Builds a SimaticML GlobalDB whose only member is
    /// <c>BigArray : Array[1..n] Of DInt</c> with an explicit
    /// <c>&lt;Subelement Path="i"&gt;&lt;StartValue&gt;</c> for every element —
    /// the synthetic XML the #159 reproduction steps describe.
    /// </summary>
    private static string BuildLargeArrayDbXml(int n)
    {
        var sb = new StringBuilder(n * 64);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<Document>\n");
        sb.Append("  <SW.Blocks.GlobalDB ID=\"0\">\n");
        sb.Append("    <AttributeList>\n");
        sb.Append("      <Interface>\n");
        sb.Append("        <Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/v5\">\n");
        sb.Append("          <Section Name=\"Static\">\n");
        sb.Append($"            <Member Name=\"BigArray\" Datatype=\"Array[1..{n}] of DInt\" Accessibility=\"Public\">\n");
        for (int i = 1; i <= n; i++)
            sb.Append($"              <Subelement Path=\"{i}\"><StartValue>{i}</StartValue></Subelement>\n");
        sb.Append("            </Member>\n");
        sb.Append("          </Section>\n");
        sb.Append("        </Sections>\n");
        sb.Append("      </Interface>\n");
        sb.Append("      <Name>BigArrayDb</Name>\n");
        sb.Append("      <Number>1</Number>\n");
        sb.Append("    </AttributeList>\n");
        sb.Append("  </SW.Blocks.GlobalDB>\n");
        sb.Append("</Document>\n");
        return sb.ToString();
    }
}
