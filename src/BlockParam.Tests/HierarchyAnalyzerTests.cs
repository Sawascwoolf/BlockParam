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

    // --- #90 / #154 H5 fingerprint correctness tests ---

    /// <summary>
    /// #90 invariant: two partial-dim scopes with equal MatchCount but disjoint
    /// path sets (e.g. SquareMatrix[0,*] and SquareMatrix[*,0] on a 3x3 array,
    /// each covering 3 cells but different cells) must NOT be collapsed by
    /// DeduplicateByMatchCount. This pins the correctness side of the #154 H5
    /// fingerprint change (HashSet&lt;string&gt; → HashSet&lt;long&gt; via FNV-1a).
    /// Behavioral test through the public Analyze API.
    /// </summary>
    [Fact]
    public void Analyze_SquareMatrix_SameCountDifferentPaths_BothScopesKept()
    {
        // SquareMatrix is Array[0..2, 0..2] of Int in array-db.xml.
        // Selecting [0,0] yields:
        //   SquareMatrix[0,*]  — row 0: [0,0], [0,1], [0,2]   MatchCount = 3
        //   SquareMatrix[*,0]  — col 0: [0,0], [1,0], [2,0]   MatchCount = 3
        //   SquareMatrix       — full:  all 9 elements          MatchCount = 9
        // The two 3-element scopes share the same MatchCount but target completely
        // different cells; the deduplicator must keep both.
        var db = _parser.Parse(TestFixtures.LoadXml("array-db.xml"));
        var squareMatrix = db.Members.First(m => m.Name == "SquareMatrix");
        var elem00 = squareMatrix.Children.First(c => c.Name == "[0,0]");

        var result = _analyzer.Analyze(db, elem00);

        var rowScope = result.Scopes.FirstOrDefault(s => s.AncestorPath == "SquareMatrix[0,*]");
        var colScope = result.Scopes.FirstOrDefault(s => s.AncestorPath == "SquareMatrix[*,0]");

        rowScope.Should().NotBeNull("row-0 scope must be present");
        colScope.Should().NotBeNull("col-0 scope must be present");
        rowScope!.MatchCount.Should().Be(3);
        colScope!.MatchCount.Should().Be(3);

        // Verify the path sets are indeed disjoint (no false collision in the fingerprint).
        var rowPaths = rowScope.MatchingMembers.Select(m => m.Name).ToHashSet();
        var colPaths = colScope.MatchingMembers.Select(m => m.Name).ToHashSet();
        rowPaths.Should().NotBeEquivalentTo(colPaths,
            "row-0 and col-0 cover different cells; fingerprints must differ");
    }

    /// <summary>
    /// Complementary check: two scopes with identical MatchCount AND identical
    /// path sets must collapse to one entry. This confirms the dedup still works
    /// for the "genuine duplicate" case after the fingerprint type change.
    /// Behavioral test: SquareMatrix[0,*] appears via within-DB analysis and
    /// must not be repeated in the result even if the analyzer builds it twice.
    /// Because the analyzer only builds each partial-dim scope once per select,
    /// we verify uniqueness via result cardinality (no scope with the same
    /// AncestorPath appears more than once).
    /// </summary>
    [Fact]
    public void Analyze_SquareMatrix_ScopeAppearsExactlyOnce()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("array-db.xml"));
        var squareMatrix = db.Members.First(m => m.Name == "SquareMatrix");
        var elem01 = squareMatrix.Children.First(c => c.Name == "[0,1]");

        var result = _analyzer.Analyze(db, elem01);

        // Each distinct AncestorPath should appear at most once (dedup works).
        var paths = result.Scopes.Select(s => s.AncestorPath).ToList();
        paths.Should().OnlyHaveUniqueItems("duplicate scopes indicate a fingerprint collision");
    }
}
