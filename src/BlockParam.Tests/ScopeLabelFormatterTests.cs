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
/// single source of truth every render site routes through.
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
        scope.Label.Should().Be("Set all 4 in *.resetButton.elementId");
    }

    [Fact]
    public void Pattern_DbRoot_NoAncestorPath_StillNamesLeaf()
    {
        // DB-root scope has an empty ancestor path: pattern collapses to "*.leaf".
        var scope = Scope("DB21", "", "elementId");

        ScopeLabelFormatter.Pattern(scope).Should().Be("*.elementId");
        scope.Label.Should().Be("Set all 4 in *.elementId");
    }

    [Fact]
    public void CrossDb_FoldsSuffixIntoLeadingWildcard_NoStringAppend()
    {
        // Pre-#143 this rendered "Set all 8 in 'DB21.resetButton — across all
        // selected DBs'". The "— across all selected DBs" append is gone; the
        // leading "*" already says the DB varies.
        var scope = Scope("DB21.resetButton", "resetButton", "elementId",
            matchCount: 8, isCrossDb: true);

        scope.Label.Should().Be("Set all 8 in *.resetButton.elementId");
        scope.Label.Should().NotContain("across all selected DBs");
        scope.Label.Should().NotContain("'");
    }

    [Fact]
    public void MegaScope_EmptyAncestorPath_RendersWildcardLeaf()
    {
        var scope = Scope("All selected DBs", "", "elementId",
            matchCount: 12, isCrossDb: true);

        scope.Label.Should().Be("Set all 12 in *.elementId");
    }

    [Fact]
    public void NoLeaf_ArrayElementScope_PatternIsJustWildcardedPath()
    {
        // Array-element scopes target the elements themselves — no separate
        // leaf field. Pattern is the wildcarded array path, no trailing dot.
        var scope = Scope("DB21.Motors", "Motors", leafName: "", matchCount: 5);

        ScopeLabelFormatter.Pattern(scope).Should().Be("*.Motors");
        scope.Label.Should().Be("Set all 5 in *.Motors");
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
        dbScope.Label.Should().Be($"Set all {dbScope.MatchCount} in *.ModuleId");
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
            s.Label.Should().StartWith("Set all ");
            s.Label.Should().Contain("*.");
            s.Label.Should().NotContain("across all selected DBs");
            s.Label.Should().NotContain("'");
        });
    }
}
