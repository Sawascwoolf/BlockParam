using BlockParam.Models;
using BlockParam.Services;
using BlockParam.SimaticML;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Coverage for HierarchyAnalyzer.AnalyzeMulti (#58 cross-DB scopes).
///
/// Lift rule: every within-DB scope gets a cross-DB sibling whose
/// MatchingMembers is the same path strings matched in every active DB.
/// Plus an "All selected DBs" mega-scope that includes every same-name
/// match across every DB. DBs that don't have the path contribute zero
/// matches and are silently skipped.
/// </summary>
public class HierarchyAnalyzerMultiDbTests
{
    private readonly SimaticMLParser _parser = new();
    private readonly HierarchyAnalyzer _analyzer = new();

    [Fact]
    public void AnalyzeMulti_SingleDbActive_DegradesToLegacy()
    {
        // |active|==1 should behave identically to Analyze(): no cross-DB
        // siblings, no mega-scope. Keeps the single-DB UX byte-for-byte
        // unchanged when nothing was added to the active set.
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var moduleId = db.Members[0].Children[0].Children[0];

        var multi = _analyzer.AnalyzeMulti(new[] { db }, db, moduleId);
        var legacy = _analyzer.Analyze(db, moduleId);

        multi.Scopes.Should().HaveCount(legacy.Scopes.Count,
            "single-DB AnalyzeMulti must match the legacy Analyze count");
        multi.Scopes.Select(s => s.MatchCount)
            .Should().Equal(legacy.Scopes.Select(s => s.MatchCount));
    }

    [Fact]
    public void AnalyzeMulti_TwoDbsSamePaths_EmitsCrossDbSiblings()
    {
        // Two copies of the same DB structure → every cross-DB sibling
        // doubles the within-DB match count, plus a mega-scope.
        var db1 = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var db2 = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var moduleId = db1.Members[0].Children[0].Children[0];
        moduleId.Name.Should().Be("ModuleId");

        var result = _analyzer.AnalyzeMulti(new[] { db1, db2 }, db1, moduleId);

        // The within-DB analysis on db1 finds 4 ModuleId instances.
        // Cross-DB lifts each scope to include db2's matches as well —
        // doubling the counts. Plus an All-selected-DBs mega-scope.
        result.Scopes.Should().Contain(s => s.MatchCount == 4,
            "within-DB scope: 4 ModuleId in db1");
        result.Scopes.Should().Contain(s => s.MatchCount == 8,
            "cross-DB lift: 4 in db1 + 4 in db2 = 8");
        result.Scopes.Should().Contain(s =>
            s.AncestorName == "All selected DBs"
            && s.MatchCount == 8);
    }

    [Fact]
    public void AnalyzeMulti_OneDbMissingPaths_SilentlySkipped()
    {
        // db1 has UDT instances; db2 is a flat DB with no matching paths.
        // Cross-DB siblings should NOT include db2 in their match set
        // (no false positives); mega-scope likewise contains only db1's
        // members. No exception, no warning — silent skip per #58 decision.
        var db1 = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var db2 = _parser.Parse(TestFixtures.LoadXml("flat-db.xml"));
        var moduleId = db1.Members[0].Children[0].Children[0];

        var result = _analyzer.AnalyzeMulti(new[] { db1, db2 }, db1, moduleId);

        // No cross-DB scope should have a match count larger than the
        // db1-only counts: db2 contributes nothing.
        var withinDbMax = result.Scopes
            .Where(s => s.AncestorName != "All selected DBs"
                        && !s.AncestorName.Contains("across all selected DBs"))
            .Max(s => s.MatchCount);

        var crossDbMax = result.Scopes
            .Where(s => s.AncestorName.Contains("across all selected DBs")
                        || s.AncestorName == "All selected DBs")
            .DefaultIfEmpty()
            .Max(s => s?.MatchCount ?? 0);

        crossDbMax.Should().BeLessOrEqualTo(withinDbMax,
            "db2 has none of the lifted paths → cross-DB lifts equal the within-DB matches");
    }

    [Fact]
    public void AnalyzeMulti_CrossDbSiblings_KeepDatatypeFilter()
    {
        // Two DBs with identical structure: cross-DB lift must still
        // honour the same Datatype check the within-DB analysis uses, so
        // a member with the same name but a different type doesn't get
        // pulled in.
        var db1 = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var db2 = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var moduleId = db1.Members[0].Children[0].Children[0];
        var datatype = moduleId.Datatype;

        var result = _analyzer.AnalyzeMulti(new[] { db1, db2 }, db1, moduleId);

        foreach (var scope in result.Scopes)
        {
            scope.MatchingMembers.Should().AllSatisfy(m =>
                m.Datatype.Should().Be(datatype));
        }
    }

    [Fact]
    public void AnalyzeMulti_NoCrossDbSiblingWhenWidthUnchanged()
    {
        // If the cross-DB lift's match count equals the within-DB match
        // count, no point showing it — it's the same set just with a
        // different label. AnalyzeMulti suppresses it.
        var db1 = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var db2 = _parser.Parse(TestFixtures.LoadXml("flat-db.xml"));   // no UDT paths
        var moduleId = db1.Members[0].Children[0].Children[0];

        var result = _analyzer.AnalyzeMulti(new[] { db1, db2 }, db1, moduleId);

        // For each within-DB scope, there must NOT be a cross-DB sibling
        // with the same MatchCount — that would be a noop sibling.
        var withinDbScopes = result.Scopes
            .Where(s => !s.AncestorName.Contains("across all selected DBs")
                        && s.AncestorName != "All selected DBs")
            .ToList();

        foreach (var w in withinDbScopes)
        {
            result.Scopes
                .Where(s => s.AncestorName.Contains("across all selected DBs"))
                .Should().NotContain(s => s.MatchCount == w.MatchCount
                                          && s.AncestorPath == w.AncestorPath,
                    "noop cross-DB sibling must be suppressed when the lift adds no matches");
        }
    }
}
