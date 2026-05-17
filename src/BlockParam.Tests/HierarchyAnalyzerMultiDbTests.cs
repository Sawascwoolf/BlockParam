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
        // doubles the within-DB match count. The "All selected DBs"
        // mega-scope is *not* expected here: the broadest cross-DB lift
        // already covers every match across both DBs, so emitting it
        // would duplicate an existing scope with a different label.
        // The mega-scope path is exercised separately in
        // <see cref="AnalyzeMulti_NoWithinDbScopes_StillEmitsMegaScope"/>.
        var db1 = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var db2 = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var moduleId = db1.Members[0].Children[0].Children[0];
        moduleId.Name.Should().Be("ModuleId");

        var result = _analyzer.AnalyzeMulti(new[] { db1, db2 }, db1, moduleId);

        // The within-DB analysis on db1 finds 4 ModuleId instances.
        // Cross-DB lifts each scope to include db2's matches as well —
        // doubling the counts.
        result.Scopes.Should().Contain(s => s.MatchCount == 4,
            "within-DB scope: 4 ModuleId in db1");
        result.Scopes.Should().Contain(s => s.MatchCount == 8,
            "cross-DB lift: 4 in db1 + 4 in db2 = 8");
    }

    [Fact]
    public void AnalyzeMulti_NoWithinDbScopes_StillEmitsMegaScope()
    {
        // When the focused DB has the selected member only once (no
        // within-DB scopes to lift, no cross-DB siblings), the
        // "All selected DBs" mega-scope is the only way to reach
        // matching members in the other active DBs — so the analyzer emits
        // it via the combined.Count == 0 branch.
        var db1 = _parser.Parse(TestFixtures.LoadXml("flat-db.xml"));
        var db2 = _parser.Parse(TestFixtures.LoadXml("flat-db.xml"));
        var speed = db1.Members[0];
        speed.Name.Should().Be("Speed");

        var result = _analyzer.AnalyzeMulti(new[] { db1, db2 }, db1, speed);

        result.Scopes.Should().Contain(s =>
            s.AncestorName == "All selected DBs"
            && s.MatchCount == 2);
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
        // (#143: cross-DB classification moved from a magic AncestorName
        // substring to the explicit ScopeLevel.IsCrossDb flag.)
        var withinDbMax = result.Scopes
            .Where(s => !s.IsCrossDb)
            .Max(s => s.MatchCount);

        var crossDbMax = result.Scopes
            .Where(s => s.IsCrossDb)
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
        // (#143: classify via ScopeLevel.IsCrossDb, not an AncestorName substring.)
        var withinDbScopes = result.Scopes
            .Where(s => !s.IsCrossDb)
            .ToList();

        foreach (var w in withinDbScopes)
        {
            result.Scopes
                .Where(s => s.IsCrossDb)
                .Should().NotContain(s => s.MatchCount == w.MatchCount
                                          && s.AncestorPath == w.AncestorPath,
                    "noop cross-DB sibling must be suppressed when the lift adds no matches");
        }
    }

    [Fact]
    public void AnalyzeMulti_2dArraySymmetricInteriorLeaf_BothRowFixAndColFixSurvive()
    {
        // Bug spec for #90 — Two orthogonal 2D dim slices that happen to
        // contain the same number of cells must not collapse to one option.
        //
        // SquareMatrix is Array[0..2, 0..2] of Int (added to array-db.xml
        // for this case; rectangular Matrix[0..1, 0..2] has unique counts
        // per slice and doesn't trigger the bug). Selecting cell [1,2]:
        //   row-fix SquareMatrix[1,*] → 3 elements ([1,0], [1,1], [1,2])
        //   col-fix SquareMatrix[*,2] → 3 elements ([0,2], [1,2], [2,2])
        // Both MatchCount == 3 but target ORTHOGONAL node sets — only the
        // selected cell [1,2] is in both.
        //
        // Today: DeduplicateByMatchCount keys on MatchCount alone and keeps
        // the first scope per count → one slice is silently dropped, the
        // user only sees row-fix OR col-fix in the scope picker.
        // Resolution: dedupe key = (MatchCount, sorted member-path set).
        var db1 = _parser.Parse(TestFixtures.LoadXml("array-db.xml"));
        var db2 = _parser.Parse(TestFixtures.LoadXml("array-db.xml"));
        var square = db1.Members.First(m => m.Name == "SquareMatrix");
        var cell12 = square.Children.First(c => c.Name == "[1,2]");

        var result = _analyzer.AnalyzeMulti(new[] { db1, db2 }, db1, cell12);

        var rowFix = result.Scopes.FirstOrDefault(s => s.AncestorPath == "SquareMatrix[1,*]");
        var colFix = result.Scopes.FirstOrDefault(s => s.AncestorPath == "SquareMatrix[*,2]");

        rowFix.Should().NotBeNull(
            "row-fix slice SquareMatrix[1,*] must survive dedupe — it covers " +
            "cells [1,0], [1,1], [1,2] which col-fix does not");
        colFix.Should().NotBeNull(
            "col-fix slice SquareMatrix[*,2] must survive dedupe — it covers " +
            "cells [0,2], [1,2], [2,2] which row-fix does not");
        rowFix!.MatchCount.Should().Be(3);
        colFix!.MatchCount.Should().Be(3);

        var rowPaths = rowFix.MatchingMembers.Select(m => m.Path).ToHashSet();
        var colPaths = colFix.MatchingMembers.Select(m => m.Path).ToHashSet();
        rowPaths.Should().NotBeEquivalentTo(colPaths,
            "orthogonal slices target different cell sets — collapsing them " +
            "loses real bulk-edit options");
        rowPaths.Intersect(colPaths).Should().ContainSingle()
            .Which.Should().Be("SquareMatrix[1,2]",
                "the slices intersect only at the selected cell");
    }

    [Fact]
    public void AnalyzeMulti_PerAncestorCrossDbScope_EmittedAndDeduped()
    {
        // Real-world structure the analyzer used to miss:
        //   DBn:
        //     estopButton.estopActive.resetZone
        //     resetButton.buttonDefect.resetZone
        // With three DBs, six leaves total. Selecting DB1's
        // estopButton.estopActive.resetZone, the user expects:
        //   - 2 within DB1 (the two resetZones in DB1)
        //   - 3 cross-DB at estopButton.estopActive
        //   - 3 cross-DB at estopButton (collapses with the previous because
        //     only one estopButton child carries a resetZone)
        //   - 6 across all selected DBs (mega-scope)
        // After dedup by match count: counts {2, 3, 6}.
        var dbs = new[] { BuildResetZoneDb("DB1"), BuildResetZoneDb("DB2"), BuildResetZoneDb("DB3") };
        var selected = FindLeaf(dbs[0], "estopButton.estopActive.resetZone");

        var result = _analyzer.AnalyzeMulti(dbs, dbs[0], selected);

        result.Scopes.Select(s => s.MatchCount).OrderBy(n => n).Should().Equal(2, 3, 6);

        // The 3-count scope is the new per-ancestor cross-DB at
        // estopButton.estopActive (or estopButton — they collapse to the
        // same count).
        result.Scopes.Should().Contain(s => s.MatchCount == 3,
            "ancestor cross-DB scope is the missing-mid-level option");
    }

    private static DataBlockInfo BuildResetZoneDb(string name)
    {
        // estopButton (Struct)
        //   estopActive (Struct)
        //     resetZone (Int)
        var estopActiveChildren = new List<MemberNode>();
        var estopActive = new MemberNode(
            "estopActive", "Struct", null, "estopButton.estopActive", null, estopActiveChildren);
        estopActiveChildren.Add(new MemberNode(
            "resetZone", "Int", "0",
            "estopButton.estopActive.resetZone",
            estopActive, Array.Empty<MemberNode>()));

        var estopButtonChildren = new List<MemberNode> { estopActive };
        var estopButton = new MemberNode(
            "estopButton", "Struct", null, "estopButton", null, estopButtonChildren);
        SetParent(estopActive, estopButton);

        // resetButton.buttonDefect.resetZone
        var buttonDefectChildren = new List<MemberNode>();
        var buttonDefect = new MemberNode(
            "buttonDefect", "Struct", null, "resetButton.buttonDefect", null, buttonDefectChildren);
        buttonDefectChildren.Add(new MemberNode(
            "resetZone", "Int", "0",
            "resetButton.buttonDefect.resetZone",
            buttonDefect, Array.Empty<MemberNode>()));

        var resetButtonChildren = new List<MemberNode> { buttonDefect };
        var resetButton = new MemberNode(
            "resetButton", "Struct", null, "resetButton", null, resetButtonChildren);
        SetParent(buttonDefect, resetButton);

        return new DataBlockInfo(
            name: name, number: 0, memoryLayout: "Optimized", blockType: "GlobalDB",
            members: new[] { estopButton, resetButton });
    }

    /// <summary>
    /// MemberNode.Parent has only a getter — the parser builds children with
    /// parent already known, so out-of-order tree construction in tests
    /// requires reaching the backing field via reflection. Acceptable in a
    /// test-only helper; production code never needs this.
    /// </summary>
    private static void SetParent(MemberNode node, MemberNode parent)
    {
        var prop = typeof(MemberNode).GetProperty("Parent")!;
        var backing = typeof(MemberNode).GetField(
            "<Parent>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        backing!.SetValue(node, parent);
    }

    private static MemberNode FindLeaf(DataBlockInfo db, string path)
    {
        foreach (var m in db.AllMembers())
        {
            if (m.Path == path) return m;
        }
        throw new InvalidOperationException($"Leaf '{path}' not found in {db.Name}");
    }
}
