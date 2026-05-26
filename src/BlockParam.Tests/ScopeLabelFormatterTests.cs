using System.Globalization;
using System.Threading;
using BlockParam.Models;
using BlockParam.Services;
using BlockParam.SimaticML;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// #143: the bulk-scope label must state the <em>pattern</em> the scope
/// matches (with a <c>*</c> wildcard for the varying instance/DB segment)
/// and name the leaf field being written — not a single concrete
/// DB-qualified example path. <see cref="ScopeLabelFormatter"/> is the
/// single source of truth every dropdown render site routes through.
///
/// #174: the dropdown wording deliberately differs from the Set button
/// caption — dropdown shows the in-scope total via Scope_DropdownItem
/// ("N member(s) in pattern"); the button shows the will-change count
/// via MenuTitle_SetAll ("Set all N in pattern"). They previously shared
/// MenuTitle_SetAll and contradicted each other once some members already
/// held the target value.
/// </summary>
public class ScopeLabelFormatterTests
{
    private readonly SimaticMLParser _parser = new();
    private readonly HierarchyAnalyzer _analyzer = new();

    public ScopeLabelFormatterTests()
    {
        // Labels go through Strings.resx — pin culture so en assertions are
        // stable regardless of the runner's OS language.
        Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    private static ScopeLevel Scope(
        string ancestorName, string ancestorPath, string leafName,
        int matchCount = 4, bool isCrossDb = false)
    {
        var members = Enumerable.Range(0, matchCount)
            .Select(i => new MemberNode(
                leafName, "Bool", null, $"x{i}.{leafName}", null, Array.Empty<MemberNode>()))
            .ToList();
        return new ScopeLevel(ancestorName, ancestorPath, 0, members, leafName, isCrossDb);
    }

    [Fact]
    public void Pattern_WithinDb_WildcardsLeadingSegment_AndAppendsLeaf()
    {
        // Concrete AncestorName "DB21.resetButton" → pattern "*.resetButton.elementId".
        var scope = Scope("DB21.resetButton", "resetButton", "elementId");

        ScopeLabelFormatter.Pattern(scope).Should().Be("*.resetButton.elementId");
        scope.Label.Should().Be("4 member(s) in *.resetButton.elementId");
    }

    [Fact]
    public void Pattern_DbRoot_NoAncestorPath_StillNamesLeaf()
    {
        // DB-root scope has an empty ancestor path: pattern collapses to "*.leaf".
        var scope = Scope("DB21", "", "elementId");

        ScopeLabelFormatter.Pattern(scope).Should().Be("*.elementId");
        scope.Label.Should().Be("4 member(s) in *.elementId");
    }

    [Fact]
    public void CrossDb_FoldsSuffixIntoLeadingWildcard_NoStringAppend()
    {
        // Pre-#143 this rendered "Set all 8 in 'DB21.resetButton — across all
        // selected DBs'". The "— across all selected DBs" append is gone; the
        // leading "*" already says the DB varies.
        var scope = Scope("DB21.resetButton", "resetButton", "elementId",
            matchCount: 8, isCrossDb: true);

        scope.Label.Should().Be("8 member(s) in *.resetButton.elementId");
        scope.Label.Should().NotContain("across all selected DBs");
        scope.Label.Should().NotContain("'");
    }

    [Fact]
    public void MegaScope_EmptyAncestorPath_RendersWildcardLeaf()
    {
        var scope = Scope("All selected DBs", "", "elementId",
            matchCount: 12, isCrossDb: true);

        scope.Label.Should().Be("12 member(s) in *.elementId");
    }

    [Fact]
    public void NoLeaf_ArrayElementScope_PatternIsJustWildcardedPath()
    {
        // Array-element scopes target the elements themselves — no separate
        // leaf field. Pattern is the wildcarded array path, no trailing dot.
        var scope = Scope("DB21.Motors", "Motors", leafName: "", matchCount: 5);

        ScopeLabelFormatter.Pattern(scope).Should().Be("*.Motors");
        scope.Label.Should().Be("5 member(s) in *.Motors");
    }

    [Fact]
    public void DropdownLabel_DoesNotUseTheSetVerb_174()
    {
        // The dropdown describes the scope (a thing to pick), not the action
        // (a thing to do). The "Set" verb belongs to the button next to it,
        // which advertises the will-change count. Sharing "Set all N" caused
        // the two adjacent counters to contradict each other once some
        // members already held the target value (#174).
        var scope = Scope("DB21.resetButton", "resetButton", "elementId", matchCount: 48);
        scope.Label.Should().NotStartWith("Set ",
            "the dropdown is not the action — the Set button is");
        scope.Label.Should().StartWith("48 ",
            "dropdown always shows MatchCount, never CountWouldChangeMembers");
    }

    [Fact]
    public void ToString_RoutesThroughTheSameFormatter()
    {
        var scope = Scope("DB21.resetButton", "resetButton", "elementId");
        scope.ToString().Should().Be(scope.Label);
    }

    [Fact]
    public void Analyzer_WithinDbScope_LabelNamesWildcardAndLeaf()
    {
        // Integration: a real analysis must thread the leaf name through so
        // the label states the field being written.
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var moduleId = db.Members[0].Children[0].Children[0];
        moduleId.Name.Should().Be("ModuleId");

        var result = _analyzer.Analyze(db, moduleId);

        var dbScope = result.Scopes.First(s => s.AncestorName == "UdtInstancesDB");
        dbScope.LeafName.Should().Be("ModuleId");
        dbScope.Label.Should().Be($"{dbScope.MatchCount} member(s) in *.ModuleId");
        dbScope.Label.Should().NotContain("UdtInstancesDB",
            "the label states the pattern, not the concrete DB name");
    }

    [Fact]
    public void Analyzer_CrossDbLift_IsCrossDbFlaggedAndLabelHasNoSuffix()
    {
        var db1 = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var db2 = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var moduleId = db1.Members[0].Children[0].Children[0];

        var result = _analyzer.AnalyzeMulti(new[] { db1, db2 }, db1, moduleId);

        var crossDb = result.Scopes.Where(s => s.IsCrossDb).ToList();
        crossDb.Should().NotBeEmpty("two identical DBs must produce cross-DB lifts");
        crossDb.Should().AllSatisfy(s =>
        {
            s.Label.Should().Contain(" member(s) in *.");
            s.Label.Should().NotContain("across all selected DBs");
            s.Label.Should().NotContain("'");
        });
    }
}
