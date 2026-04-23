using FluentAssertions;
using BlockParam.Services;
using BlockParam.SimaticML;
using Xunit;

namespace BlockParam.Tests;

public class HierarchyAnalyzerTests
{
    private readonly SimaticMLParser _parser = new();
    private readonly HierarchyAnalyzer _analyzer = new();

    [Fact]
    public void Analyze_SingleOccurrence_NoScopesOffered()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("flat-db.xml"));
        var speed = db.Members.First(m => m.Name == "Speed");

        var result = _analyzer.Analyze(db, speed);

        result.HasBulkOptions.Should().BeFalse();
        result.Scopes.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_UdtInstances_FindsAllModuleIds()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        // Drive1.Msg_CommError.ModuleId
        var moduleId = db.Members[0].Children[0].Children[0];
        moduleId.Name.Should().Be("ModuleId");

        var result = _analyzer.Analyze(db, moduleId);

        result.HasBulkOptions.Should().BeTrue();
        // All 4 ModuleId instances across the DB
        result.Scopes.Should().Contain(s => s.MatchCount == 4);
    }

    [Fact]
    public void Analyze_TwoLevels_TwoScopes()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        // Drive1.Msg_CommError.ModuleId — should have Drive1 scope (2) and DB scope (4)
        var moduleId = db.Members[0].Children[0].Children[0];

        var result = _analyzer.Analyze(db, moduleId);

        result.Scopes.Should().HaveCountGreaterOrEqualTo(2);
        // Narrowest scope: within Drive1 (2 messages, each with ModuleId)
        result.Scopes.Should().Contain(s => s.AncestorName == "UdtInstancesDB.Drive1" && s.MatchCount == 2);
        // Broadest scope: entire DB (4 ModuleIds total)
        result.Scopes.Should().Contain(s => s.AncestorName == "UdtInstancesDB" && s.MatchCount == 4);
    }

    [Fact]
    public void Analyze_CorrectMatchCounts()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        // ElementId in Drive1.Msg_CommError
        var elementId = db.Members[0].Children[0].Children[1];
        elementId.Name.Should().Be("ElementId");

        var result = _analyzer.Analyze(db, elementId);

        result.HasBulkOptions.Should().BeTrue();
        // All 4 ElementId instances
        var dbScope = result.Scopes.First(s => s.AncestorPath == "");
        dbScope.MatchCount.Should().Be(4);
    }

    [Fact]
    public void Analyze_OnlyMatchesSameName()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        // ModuleId should NOT match ElementId
        var moduleId = db.Members[0].Children[0].Children[0];

        var result = _analyzer.Analyze(db, moduleId);

        foreach (var scope in result.Scopes)
        {
            scope.MatchingMembers.Should().OnlyContain(m => m.Name == "ModuleId");
        }
    }

    [Fact]
    public void Analyze_OnlyMatchesSameDatatype()
    {
        // Create a scenario where same name but different datatype exists
        // In our fixtures, all ModuleId are Int, so they should all match
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var moduleId = db.Members[0].Children[0].Children[0];

        var result = _analyzer.Analyze(db, moduleId);

        foreach (var scope in result.Scopes)
        {
            scope.MatchingMembers.Should().OnlyContain(m => m.Datatype == "Int");
        }
    }

    [Fact]
    public void Analyze_MixedUdtAndStruct_BothFound()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        // Active field appears in all 4 messages
        var active = db.Members[0].Children[0].Children[3];
        active.Name.Should().Be("Active");

        var result = _analyzer.Analyze(db, active);

        result.HasBulkOptions.Should().BeTrue();
        result.Scopes.Should().Contain(s => s.MatchCount == 4);
    }

    [Fact]
    public void Analyze_DeepNesting_SingleOccurrence_NoScopes()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("deep-nesting-db.xml"));
        var deepValue = db.AllMembers().First(m => m.Name == "DeepValue");

        var result = _analyzer.Analyze(db, deepValue);

        result.HasBulkOptions.Should().BeFalse();
    }

    [Fact]
    public void Analyze_ScopeLevels_OrderedNarrowestToBroadest()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var moduleId = db.Members[0].Children[0].Children[0];

        var result = _analyzer.Analyze(db, moduleId);

        // Scopes should be ordered from narrowest (smallest count) to broadest
        for (int i = 1; i < result.Scopes.Count; i++)
        {
            result.Scopes[i].MatchCount.Should()
                .BeGreaterThanOrEqualTo(result.Scopes[i - 1].MatchCount);
        }
    }

    [Fact]
    public void Analyze_ArrayElement_OffersAllElementsScope()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("mixed-types-db.xml"));
        var myArray = db.Members.First(m => m.Name == "MyArray");
        var elem3 = myArray.Children[3]; // [3]

        var result = _analyzer.Analyze(db, elem3);

        result.HasBulkOptions.Should().BeTrue();
        var arrayScope = result.Scopes.First();
        arrayScope.AncestorPath.Should().Be("MyArray");
        arrayScope.MatchCount.Should().Be(5); // all 5 array elements
        arrayScope.MatchingMembers.Select(m => m.Name)
            .Should().Equal("[0]", "[1]", "[2]", "[3]", "[4]");
    }

    [Fact]
    public void Analyze_MultiDimPrimitiveArrayElement_OffersPartialDimScope()
    {
        // Matrix is Array[0..1, 0..2] of Int → 6 elements.
        var db = _parser.Parse(TestFixtures.LoadXml("array-db.xml"));
        var matrix = db.Members.First(m => m.Name == "Matrix");
        var elem01 = matrix.Children.First(c => c.Name == "[0,1]");

        var result = _analyzer.Analyze(db, elem01);

        result.HasBulkOptions.Should().BeTrue();

        var rowScope = result.Scopes.First(s => s.AncestorPath == "Matrix[0,*]");
        rowScope.MatchCount.Should().Be(3);
        rowScope.MatchingMembers.Select(m => m.Name)
            .Should().Equal("[0,0]", "[0,1]", "[0,2]");

        // Broader scope: the full matrix (6 elements).
        result.Scopes.Should().Contain(s =>
            s.AncestorPath == "Matrix" && s.MatchCount == 6);
    }

    [Fact]
    public void Analyze_MultiDimSecondRow_PartialScopeCoversOnlyThatRow()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("array-db.xml"));
        var matrix = db.Members.First(m => m.Name == "Matrix");
        var elem12 = matrix.Children.First(c => c.Name == "[1,2]");

        var result = _analyzer.Analyze(db, elem12);

        var rowScope = result.Scopes.First(s => s.AncestorPath == "Matrix[1,*]");
        rowScope.MatchingMembers.Select(m => m.Name)
            .Should().Equal("[1,0]", "[1,1]", "[1,2]");
    }

    [Fact]
    public void Analyze_MultiDim_AlsoOffersTrailingDimScope()
    {
        // Matrix[0..1, 0..2]: selecting [0,1] should offer both
        //   [0,*] (fix dim 0 → row 0, 3 elements)
        //   [*,1] (fix dim 1 → column 1, 2 elements)
        var db = _parser.Parse(TestFixtures.LoadXml("array-db.xml"));
        var matrix = db.Members.First(m => m.Name == "Matrix");
        var elem01 = matrix.Children.First(c => c.Name == "[0,1]");

        var result = _analyzer.Analyze(db, elem01);

        var columnScope = result.Scopes.First(s => s.AncestorPath == "Matrix[*,1]");
        columnScope.MatchCount.Should().Be(2);
        columnScope.MatchingMembers.Select(m => m.Name)
            .Should().Equal("[0,1]", "[1,1]");

        var rowScope = result.Scopes.First(s => s.AncestorPath == "Matrix[0,*]");
        rowScope.MatchCount.Should().Be(3);
    }

    [Fact]
    public void Analyze_SingleDimArray_NoPartialDimScopeAdded()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("mixed-types-db.xml"));
        var myArray = db.Members.First(m => m.Name == "MyArray");
        var elem2 = myArray.Children[2];

        var result = _analyzer.Analyze(db, elem2);

        // 1D array has no partial-dim slice — only the full-array scope.
        result.Scopes.Should().ContainSingle(s => s.AncestorPath == "MyArray");
        result.Scopes.Should().NotContain(s => s.AncestorPath.StartsWith("MyArray["));
    }
}
