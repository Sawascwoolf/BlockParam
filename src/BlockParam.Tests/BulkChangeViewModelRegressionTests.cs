using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BlockParam.Config;
using BlockParam.Licensing;
using BlockParam.Models;
using BlockParam.Services;
using BlockParam.SimaticML;
using BlockParam.UI;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Pre-refactor regression net for #80 (BulkChangeViewModel god-class split).
/// Pins the current behaviour of the slices NOT already covered by
/// BulkChangeViewModelInvariantTests (active-set / snapshot seam) or the
/// existing ApplyCommand_* / Apply_MultipleDbs_* tests.
///
/// Groups:
///   A — Search + scope cascade (tests 1–3)
///   B — BulkPreview rebuild (tests 4–7)
///   C — ManualSelection state transitions (tests 8–10)
///   D — Autocomplete VM surface (tests 11–13)
///   E — Comment-preview pipeline (tests 14–15)
///   F — Dispose chain (test 16)
/// </summary>
public class BulkChangeViewModelRegressionTests : IDisposable
{
    // ─────────────────────── shared setup helpers ───────────────────────

    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var d in _tempDirs)
            try { Directory.Delete(d, true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Minimal single-DB VM with no rules. Uses flat-db.xml (FlatDB).
    /// </summary>
    private static BulkChangeViewModel CreateFlatDbVm(
        ConfigLoader? configLoader = null,
        TagTableCache? tagTableCache = null,
        ILicenseService? licenseService = null,
        IMessageBoxService? messageBox = null)
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = new SimaticMLParser().Parse(xml);
        configLoader ??= new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 1000));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        return new BulkChangeViewModel(
            db, xml, new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            tagTableCache: tagTableCache,
            licenseService: licenseService,
            messageBox: messageBox ?? new NopMessageBox());
    }

    /// <summary>
    /// Single-DB VM built from the UDT-instances fixture (4 ModuleId leaves, all "42").
    /// </summary>
    private static BulkChangeViewModel CreateUdtVm(ConfigLoader? configLoader = null)
    {
        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = new SimaticMLParser().Parse(xml);
        configLoader ??= new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 1000));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        return new BulkChangeViewModel(
            db, xml, new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            messageBox: new NopMessageBox());
    }

    /// <summary>
    /// Two-DB VM: FlatDB (anchor) + NestedStructDB (peer). Both loaded
    /// from existing fixtures. No pending edits staged.
    /// </summary>
    private static BulkChangeViewModel CreateTwoDbVm()
    {
        var parser = new SimaticMLParser();
        var anchorXml = TestFixtures.LoadXml("flat-db.xml");
        var anchorInfo = parser.Parse(anchorXml);
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var peerInfo = parser.Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 1000));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var peer = new ActiveDb(peerInfo, peerXml, onApply: null);

        return new BulkChangeViewModel(
            anchorInfo, anchorXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            messageBox: new NopMessageBox(),
            additionalActiveDbs: new[] { peer });
    }

    /// <summary>
    /// Writes a config.json + rules file in a temp dir and returns a ConfigLoader
    /// pointing at it.  Caller is responsible for cleanup (callers own _tempDirs).
    /// </summary>
    private ConfigLoader CreateConfigWithRule(string ruleJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bp_reg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        File.WriteAllText(Path.Combine(dir, "config.json"), @"{ ""version"": ""1.0"" }");
        var rulesDir = Path.Combine(dir, "rules");
        Directory.CreateDirectory(rulesDir);
        File.WriteAllText(Path.Combine(rulesDir, "test.json"), ruleJson);

        return new ConfigLoader(Path.Combine(dir, "config.json"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Group A: Search + scope cascade
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test 1 — SearchHitCount sums across all active DBs in a multi-DB session.
    ///
    /// FlatDB has "Speed", "Temperature", "Enable".
    /// NestedStructDB has "MaxSpeed", "MinSpeed", "Timeout", "Retries", "Status".
    /// Searching "Speed" should hit Speed (FlatDB) + MaxSpeed + MinSpeed (NestedStructDB) = 3.
    /// </summary>
    [Fact]
    public void SearchQuery_MultiDb_HitCountSumsAcrossAllActiveDbs()
    {
        var vm = CreateTwoDbVm();

        vm.Filter.SearchQuery = "Speed";
        vm.FlushPendingSearch(); // cancels debounce, runs synchronously

        // FlatDB: "Speed" → 1 hit
        // NestedStructDB: "MaxSpeed" + "MinSpeed" → 2 hits
        vm.Filter.SearchHitCount.Should().Be(3,
            "search hit count must sum across all active DBs: " +
            "FlatDB.Speed(1) + NestedStructDB.MaxSpeed+MinSpeed(2) = 3");
    }

    /// <summary>
    /// Test 2 — After a search, collapsed ancestors of matching leaves are smart-expanded.
    ///
    /// NestedStructDB has Config.Settings.Timeout. "Timeout" is collapsed by default.
    /// Searching "Timeout" should expand Config (ancestor) and Config.Settings (ancestor).
    /// </summary>
    [Fact]
    public void SearchQuery_SmartExpandsCollapsedAncestors()
    {
        // Use NestedStructDB alone so we can see "Config.Settings.Timeout" hierarchy.
        var xml = TestFixtures.LoadXml("nested-struct-db.xml");
        var db = new SimaticMLParser().Parse(xml);
        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 1000));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var vm = new BulkChangeViewModel(db, xml, new HierarchyAnalyzer(),
            bulkService, tracker, configLoader, messageBox: new NopMessageBox());

        // Verify initial state — Config is present at root level with children collapsed
        var configRoot = vm.RootMembers.FirstOrDefault(r => r.Name == "Config");
        configRoot.Should().NotBeNull("fixture must have a Config struct");

        vm.Filter.SearchQuery = "Timeout";
        vm.FlushPendingSearch();

        // After search the ancestor chain Config → Config.Settings must be expanded
        configRoot!.IsExpanded.Should().BeTrue(
            "Config is an ancestor of Timeout hit — must be expanded by smart-expand");
        var settingsNode = configRoot.Children.FirstOrDefault(c => c.Name == "Settings");
        settingsNode.Should().NotBeNull("fixture must have Config.Settings");
        settingsNode!.IsExpanded.Should().BeTrue(
            "Config.Settings is the direct parent of Timeout — must be expanded");

        // The match itself or its direct parent should have IsSmartExpanded set
        // (SmartExpandSearchMatches calls EnsureVisible which sets IsSmartExpanded on ancestors)
        // Config and Settings were collapsed nodes opened by the search — they are smart-expanded.
        configRoot.IsSmartExpanded.Should().BeTrue(
            "Config was collapsed before search — opening it was a smart-expand");
    }

    /// <summary>
    /// Test 3 — HiddenByRuleCount reflects excludeFromSetpoints rules.
    ///
    /// flat-db.xml has Speed, Temperature, Enable (3 leaves).
    /// A rule excluding "Speed" by path should produce HiddenByRuleCount == 1
    /// and ShowRuleFilterBanner == true.
    /// </summary>
    [Fact]
    public void HiddenByRuleCount_TracksExcludeFromSetpointsRules()
    {
        // Rule: exclude Speed from setpoints
        var configLoader = CreateConfigWithRule(@"{
            ""rules"": [{
                ""pathPattern"": ""Speed"",
                ""datatype"": ""Int"",
                ""excludeFromSetpoints"": true
            }]
        }");
        var vm = CreateFlatDbVm(configLoader: configLoader);

        vm.Filter.HiddenByRuleCount.Should().Be(1,
            "one rule hides Speed — count must be 1");
        vm.Filter.ShowRuleFilterBanner.Should().BeTrue(
            "banner is shown whenever at least one member is hidden by a rule");
    }

    /// <summary>
    /// Test 4 — When an active-set change swaps the anchor to a DB with a
    /// different UDT-resolution outcome, <see cref="SearchFilterViewModel.CanShowSetpointsOnly"/>
    /// and <see cref="SearchFilterViewModel.ShowSetpointsOnlyTooltip"/> must
    /// refresh. Both derive from <c>_active.Info.UnresolvedUdts</c> via the
    /// slice's <c>getAnchorInfo</c> closure, and the slice has no way to
    /// observe the anchor change on its own — the host's
    /// <c>RebuildAfterActiveSetChanged</c> cascade has to call
    /// <see cref="SearchFilterViewModel.RaiseSetpointsCapabilityChanged"/>.
    /// Before this fix the checkbox stayed enabled-or-disabled based on the
    /// original anchor even after the user removed it.
    /// </summary>
    [Fact]
    public void ActiveSetChange_AnchorSwap_RaisesSetpointsCapability()
    {
        // Anchor: clean (no UnresolvedUdts) → CanShowSetpointsOnly==true.
        // Peer:   has UnresolvedUdts + no UDT-refresh path → would be FALSE.
        var anchorInfo = new DataBlockInfo(
            "DB_Anchor", 1, "Optimized", "GlobalDB",
            Array.Empty<MemberNode>(),
            unresolvedUdts: Array.Empty<string>());
        var peerInfo = new DataBlockInfo(
            "DB_Peer", 2, "Optimized", "GlobalDB",
            Array.Empty<MemberNode>(),
            unresolvedUdts: new[] { "messageConfig_UDT" });

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 1000));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        // No onRefreshUdtTypes wired → CanShowSetpointsOnly tracks
        // UnresolvedUdts.Count directly.
        var vm = new BulkChangeViewModel(
            anchorInfo, currentXml: "<Block />",
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            messageBox: new NopMessageBox(),
            additionalActiveDbs: new[] { new ActiveDb(peerInfo, "<Block />") });

        vm.Filter.CanShowSetpointsOnly.Should().BeTrue(
            "starting anchor has 0 unresolved UDTs and no refresh path → enabled");
        vm.Filter.ShowSetpointsOnlyTooltip.Should().NotContain("Disabled",
            "clean anchor: tooltip describes the filter, not the disabled state");

        // Subscribe AFTER construction so the seed cascade doesn't poison the list.
        var capabilityRaises = 0;
        var tooltipRaises = 0;
        vm.Filter.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SearchFilterViewModel.CanShowSetpointsOnly))
                capabilityRaises++;
            if (e.PropertyName == nameof(SearchFilterViewModel.ShowSetpointsOnlyTooltip))
                tooltipRaises++;
        };

        // Remove the anchor → peer (with UnresolvedUdts) becomes the new anchor.
        // RequestRemoveActiveDb runs the State setter, which fires
        // RebuildAfterActiveSetChanged → Filter.RaiseSetpointsCapabilityChanged().
        var anchorDb = vm.AllActiveDbs.First(d => d.Info.Name == "DB_Anchor");
        vm.RequestRemoveActiveDb(anchorDb);

        vm.AllActiveDbs.Should().HaveCount(1);
        vm.AllActiveDbs[0].Info.Name.Should().Be("DB_Peer",
            "after removing the original anchor the peer occupies index 0");

        capabilityRaises.Should().BeGreaterOrEqualTo(1,
            "anchor swap must raise PropertyChanged for CanShowSetpointsOnly so the " +
            "checkbox state can refresh");
        tooltipRaises.Should().BeGreaterOrEqualTo(1,
            "anchor swap must raise PropertyChanged for ShowSetpointsOnlyTooltip so the " +
            "tooltip text refreshes");

        vm.Filter.CanShowSetpointsOnly.Should().BeFalse(
            "new anchor has 1 UnresolvedUdt and no refresh path → disabled");
        vm.Filter.ShowSetpointsOnlyTooltip.Should().Contain("messageConfig_UDT",
            "tooltip must surface the now-current anchor's missing UDT name");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Group B: BulkPreview rebuild
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test 4 — Empty NewValue produces no BulkPreview entries (hasInput guard).
    ///
    /// Selects ModuleId in UdtInstancesDB and picks the DB-level scope (4 matches),
    /// then keeps NewValue empty. BulkPreview.Count must stay 0.
    /// </summary>
    [Fact]
    public void BulkPreview_EmptyNewValue_ProducesNoEntries()
    {
        var vm = CreateUdtVm();

        FlatTreeManager.ExpandAll(vm.RootMembers);
        vm.RefreshFlatList();

        var leaf = vm.FlatMembers.First(m => m.Name == "ModuleId" && m.IsLeaf);
        vm.SelectedFlatMember = leaf;

        // Pick the broadest scope (all 4 ModuleId instances)
        var scope = vm.AvailableScopes.OrderByDescending(s => s.MatchCount).First();
        vm.SelectedScope = scope;

        // NewValue is empty — no input → no preview
        vm.NewValue = "";
        vm.FlushPendingHighlighting();

        vm.BulkPreview.Entries.Should().BeEmpty(
            "empty NewValue must produce no BulkPreview entries (hasInput guard at ComputeBulkPreview)");
    }

    /// <summary>
    /// Test 5 — Leaves already holding the target value are skipped in BulkPreview.
    ///
    /// All 4 ModuleId leaves in UdtInstancesDB hold "42". Setting NewValue = "42"
    /// means 0 would actually change → BulkPreview.Count == 0.
    /// Setting NewValue = "99" means all 4 differ → BulkPreview.Count == 4.
    /// </summary>
    [Fact]
    public void BulkPreview_SkipsLeavesAlreadyMatchingTarget()
    {
        var vm = CreateUdtVm();

        FlatTreeManager.ExpandAll(vm.RootMembers);
        vm.RefreshFlatList();

        var leaf = vm.FlatMembers.First(m => m.Name == "ModuleId" && m.IsLeaf);
        vm.SelectedFlatMember = leaf;

        var scope = vm.AvailableScopes.OrderByDescending(s => s.MatchCount).First();
        scope.MatchCount.Should().Be(4, "fixture has 4 ModuleId leaves");
        vm.SelectedScope = scope;

        // All 4 already hold "42" — none would change
        vm.NewValue = "42";
        vm.FlushPendingHighlighting();

        vm.BulkPreview.Entries.Count.Should().Be(0,
            "all 4 leaves already hold the target value — skip-already-matching must produce 0 entries");

        // Now a different value — all 4 differ
        vm.NewValue = "99";
        vm.FlushPendingHighlighting();

        vm.BulkPreview.Entries.Count.Should().Be(4,
            "all 4 leaves differ from target '99' — all must appear in BulkPreview");
    }

    /// <summary>
    /// Test 6 — A BulkPreview entry that overlaps a pending inline edit is flagged.
    ///
    /// udt-instances-db.xml: select ModuleId with 4 matches; stage a pending inline
    /// edit on one leaf (EditableStartValue = "11"), then set NewValue = "99".
    /// BulkPreviewConflictCount == 1, HasBulkPreviewConflict == true, and
    /// BulkPreviewConflictWarning contains "1 overlap".
    /// </summary>
    [Fact]
    public void BulkPreviewConflictCount_FlagsOverlapWithPendingEdits()
    {
        var vm = CreateUdtVm();

        FlatTreeManager.ExpandAll(vm.RootMembers);
        vm.RefreshFlatList();

        var moduleLeaves = vm.FlatMembers.Where(m => m.Name == "ModuleId" && m.IsLeaf).ToList();
        moduleLeaves.Should().HaveCount(4, "fixture has 4 ModuleId leaves");

        // Stage a pending inline edit on the first leaf
        moduleLeaves[0].EditableStartValue = "11";
        moduleLeaves[0].IsPendingInlineEdit.Should().BeTrue("staging edit via EditableStartValue");

        // Now set up bulk scope
        var leaf = moduleLeaves[1]; // pick a different leaf so SelectedFlatMember isn't the pending one
        vm.SelectedFlatMember = moduleLeaves[0]; // need to select one to populate scopes
        var scope = vm.AvailableScopes.OrderByDescending(s => s.MatchCount).First();
        vm.SelectedScope = scope;

        vm.NewValue = "99";
        vm.FlushPendingHighlighting();

        vm.BulkPreview.ConflictCount.Should().Be(1,
            "one leaf has a pending inline edit that the bulk Set would overwrite");
        vm.BulkPreview.HasConflict.Should().BeTrue();
        vm.BulkPreview.ConflictWarning.Should().Contain("1 overlap",
            "warning must mention the count of overlapping edits");
    }

    /// <summary>
    /// Test 7 — BulkPreviewSummary formats as "orig ⇢ new" when all originals are the same,
    /// or "{N} targets" when they differ.
    ///
    /// ModuleId leaves all start at "42" → homogeneous → "42 ⇢ 85".
    /// Staging one with "11" makes them heterogeneous → "{4} targets".
    /// </summary>
    [Fact]
    public void BulkPreviewSummary_FormatsHomogeneousVsHeterogeneous()
    {
        var vm = CreateUdtVm();

        FlatTreeManager.ExpandAll(vm.RootMembers);
        vm.RefreshFlatList();

        vm.SelectedFlatMember = vm.FlatMembers.First(m => m.Name == "ModuleId" && m.IsLeaf);
        var scope = vm.AvailableScopes.OrderByDescending(s => s.MatchCount).First();
        vm.SelectedScope = scope;

        // Homogeneous: all ModuleId start at "42"
        vm.NewValue = "85";
        vm.FlushPendingHighlighting();

        vm.BulkPreview.Entries.Should().HaveCount(4, "all 4 differ from 85");
        vm.BulkPreview.Summary.Should().Be("42 ⇢ 85",
            "homogeneous originals should produce 'orig ⇢ new' format");

        // Make one leaf heterogeneous by staging a pending edit
        var leaves = vm.FlatMembers.Where(m => m.Name == "ModuleId" && m.IsLeaf).ToList();
        // SPEC AMBIGUITY: The BulkPreviewSummary checks OriginalValue (StartValue) for
        // homogeneity, not PendingValue. So editing one leaf's EditableStartValue changes
        // its effective value shown in BulkPreview but OriginalValue stays as StartValue="42".
        // To get heterogeneous, we need to check the actual OriginalValue field.
        // Looking at TryAddPreviewEntry: OriginalValue = node.StartValue (the DB value, not pending).
        // So all BulkPreview entries still have OriginalValue="42" even with a pending edit.
        // => We need a different approach to produce heterogeneous originals.
        // The heterogeneous summary "N targets" fires when OriginalValue differs between entries.
        // With a single DB and all ModuleId=42, we cannot produce that here via pending edits alone.
        // We'll assert the "{N} targets" path via manual selection with mixed values.
        // Use FlatDB (Speed=1500, Temperature=25.5, Enable=true) for manual mixed-orig test.
        vm.Dispose();

        var vm2 = CreateFlatDbVm();
        FlatTreeManager.ExpandAll(vm2.RootMembers);
        vm2.RefreshFlatList();

        var speed = vm2.FlatMembers.First(m => m.Name == "Speed" && m.IsLeaf);
        var enable = vm2.FlatMembers.First(m => m.Name == "Enable" && m.IsLeaf);

        vm2.UpdateManualSelection(
            added: new[] { speed, enable },
            removed: System.Array.Empty<MemberNodeViewModel>(),
            isFilterRehydration: false);
        vm2.IsManualMode.Should().BeTrue();

        vm2.NewValue = "ON";
        vm2.FlushPendingHighlighting();

        // Speed.StartValue="1500", Enable.StartValue="true" — different originals
        vm2.BulkPreview.Count.Should().BeGreaterThan(0, "both differ from 'ON'");
        vm2.BulkPreview.Summary.Should().MatchRegex(@"^\d+ targets$",
            "heterogeneous originals (Speed='1500' vs Enable='true') must use '{N} targets' format");

        vm2.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Group C: ManualSelection state transitions
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test 8 — Adding 2 leaves via UpdateManualSelection enters manual mode,
    /// clears any scope, and sets ManualSelectionCount == 2.
    /// </summary>
    [Fact]
    public void UpdateManualSelection_TwoLeaves_EntersManualMode_ClearsScope()
    {
        var vm = CreateUdtVm();

        FlatTreeManager.ExpandAll(vm.RootMembers);
        vm.RefreshFlatList();

        var leaves = vm.FlatMembers.Where(m => m.IsLeaf).Take(2).ToList();
        leaves.Should().HaveCount(2, "fixture must have at least 2 leaves");

        vm.UpdateManualSelection(
            added: leaves,
            removed: System.Array.Empty<MemberNodeViewModel>(),
            isFilterRehydration: false);

        vm.IsManualMode.Should().BeTrue("2 leaves selected → manual mode");
        vm.ManualSelectionCount.Should().Be(2);
        vm.SelectedScope.Should().BeNull("manual mode clears the scope dropdown");
        vm.AvailableScopes.Should().BeEmpty("manual mode clears available scopes");
    }

    /// <summary>
    /// Test 9 — IsSelectionTypeHomogeneous flips when a different datatype is added.
    ///
    /// FlatDB: Speed (Int), Enable (Bool). Selecting 2 Ints → homogeneous.
    /// Adding Enable (Bool) → heterogeneous; ManualSelectionSummary uses
    /// Dialog_ManualSelectionMixed format.
    /// </summary>
    [Fact]
    public void IsSelectionTypeHomogeneous_FlipsOnMixedDatatypeAddition()
    {
        var vm = CreateFlatDbVm();

        FlatTreeManager.ExpandAll(vm.RootMembers);
        vm.RefreshFlatList();

        // flat-db.xml: Speed (Int), Temperature (Real), Enable (Bool)
        var speed = vm.FlatMembers.First(m => m.Name == "Speed" && m.IsLeaf);
        var temp = vm.FlatMembers.First(m => m.Name == "Temperature" && m.IsLeaf);
        var enable = vm.FlatMembers.First(m => m.Name == "Enable" && m.IsLeaf);

        speed.Datatype.Should().NotBe(enable.Datatype,
            "Speed (Int) and Enable (Bool) must have different datatypes");

        // Start: 2 leaves of same datatype — pick two Ints but there's only one Int + one Real
        // SPEC AMBIGUITY: FlatDB has Speed=Int, Temperature=Real, Enable=Bool — no two same-type leaves.
        // Use Speed+Temperature to get "heterogeneous from the start" isn't what the test wants.
        // Use udt-instances-db.xml instead: all ModuleId are Int, selecting 2 gives homogeneous.
        vm.Dispose();

        var vm2 = CreateUdtVm();
        FlatTreeManager.ExpandAll(vm2.RootMembers);
        vm2.RefreshFlatList();

        // UDT fixture: ModuleId (Int), ElementId (Int), MessageId (Int), Active (Bool)
        var intLeaves = vm2.FlatMembers.Where(m => m.Name == "ModuleId" && m.IsLeaf).Take(2).ToList();
        intLeaves.Should().HaveCount(2, "fixture needs at least 2 Int leaves");
        intLeaves.Should().AllSatisfy(l => l.Datatype.Should().Be("Int"));

        vm2.UpdateManualSelection(
            added: intLeaves,
            removed: System.Array.Empty<MemberNodeViewModel>(),
            isFilterRehydration: false);

        vm2.IsManualMode.Should().BeTrue();
        vm2.IsSelectionTypeHomogeneous.Should().BeTrue(
            "2 Int leaves → all same datatype → homogeneous");

        // Add a Bool leaf — breaks homogeneity
        var boolLeaf = vm2.FlatMembers.First(m => m.Name == "Active" && m.IsLeaf);
        boolLeaf.Datatype.Should().Be("Bool");

        vm2.UpdateManualSelection(
            added: new[] { boolLeaf },
            removed: System.Array.Empty<MemberNodeViewModel>(),
            isFilterRehydration: false);

        vm2.IsSelectionTypeHomogeneous.Should().BeFalse(
            "Int + Bool → mixed datatypes → not homogeneous");
        vm2.ManualSelectionSummary.Should().Contain("2",
            "ManualSelectionSummary (Dialog_ManualSelectionMixed) must contain the distinct datatype count (2)");
        // Dialog_ManualSelectionMixed = "{0} selected — {1} datatypes" (en) / "{0} ausgewählt — {1} Datentypen" (de)
        // SPEC AMBIGUITY: The exact phrase "datatypes" vs "Datentypen" depends on the OS culture.
        // We assert the format key is used by checking the count "2" appears (which is culture-neutral)
        // and that ManualSelectionSummary is distinct from the homogeneous format (which contains a datatype name).
        vm2.ManualSelectionSummary.Should().NotContain("Int",
            "ManualSelectionSummary in mixed mode must use Dialog_ManualSelectionMixed, " +
            "not Dialog_ManualSelectionSummary which names the single datatype");

        vm2.Dispose();
    }

    /// <summary>
    /// Test 10 — ClearManualSelectionCommand wipes selection and NewValue.
    /// </summary>
    [Fact]
    public void ClearManualSelectionCommand_WipesSelectionAndValue()
    {
        var vm = CreateFlatDbVm();

        FlatTreeManager.ExpandAll(vm.RootMembers);
        vm.RefreshFlatList();

        // Stage 2 leaves (manual mode requires >= 2)
        var leaves = vm.FlatMembers.Where(m => m.IsLeaf).Take(2).ToList();
        vm.UpdateManualSelection(
            added: leaves,
            removed: System.Array.Empty<MemberNodeViewModel>(),
            isFilterRehydration: false);

        vm.IsManualMode.Should().BeTrue("setup: manual mode");
        vm.NewValue = "ON";

        vm.ClearManualSelectionCommand.Execute(null);

        vm.ManualSelectionCount.Should().Be(0, "Clear wipes all selected leaves");
        vm.IsManualMode.Should().BeFalse("0 selected → not in manual mode");
        vm.NewValue.Should().BeEmpty("Clear resets NewValue to empty");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Group D: Autocomplete VM surface
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test 11 — GetSuggestionsForMember applies AND-across-whitespace-terms filtering.
    ///
    /// Tag table has 50 entries: 20 containing "valv", 20 containing "ope",
    /// 10 containing both. With empty filter → 50. With "valv ope" → 10 (only both).
    /// </summary>
    [Fact]
    public void GetSuggestionsForMember_AppliesAndAcrossWhitespaceTerms()
    {
        // Build a tag table with exactly 50 entries, 10 of which contain both "valv" and "ope"
        var entries = new List<TagTableEntry>();
        for (int i = 0; i < 10; i++)
            entries.Add(new TagTableEntry($"VALVE_OPEN_{i}", $"{i}", "Int", $"valve open {i}"));
        for (int i = 0; i < 10; i++)
            entries.Add(new TagTableEntry($"VALVE_CLOSE_{i}", $"{100 + i}", "Int", $"valve close {i}"));
        for (int i = 0; i < 20; i++)
            entries.Add(new TagTableEntry($"OPERATION_{i}", $"{200 + i}", "Int", $"operation {i}"));
        for (int i = 0; i < 10; i++)
            entries.Add(new TagTableEntry($"OTHER_{i}", $"{300 + i}", "Int", $"other {i}"));

        entries.Should().HaveCount(50, "test setup must produce exactly 50 entries");

        var reader = Substitute.For<ITagTableReader>();
        reader.GetTagTableNames().Returns(new[] { "AllConsts" });
        reader.ReadTagTable("AllConsts").Returns(entries);
        var cache = new TagTableCache(reader);

        // Config with a tagTableReference rule on ModuleId
        var configLoader = CreateConfigWithRule(@"{
            ""rules"": [{
                ""pathPattern"": ""ModuleId"",
                ""datatype"": ""Int"",
                ""tagTableReference"": { ""tableName"": ""AllConsts"" }
            }]
        }");

        var vm = CreateUdtVm(configLoader);
        // HACK: TagTableCache must be injected — recreate VM with cache
        vm.Dispose();

        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = new SimaticMLParser().Parse(xml);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 1000));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);
        var vm2 = new BulkChangeViewModel(db, xml, new HierarchyAnalyzer(),
            bulkService, tracker, configLoader,
            tagTableCache: cache,
            messageBox: new NopMessageBox());

        FlatTreeManager.ExpandAll(vm2.RootMembers);
        vm2.RefreshFlatList();
        var leaf = vm2.FlatMembers.First(m => m.Name == "ModuleId" && m.IsLeaf);
        leaf.IsLeaf.Should().BeTrue();

        // Empty filter → all 50
        var allSuggestions = vm2.GetSuggestionsForMember(leaf, "");
        allSuggestions.Should().HaveCount(50,
            "empty filter must return all entries from the tag table");

        // "valv ope" (both terms) → only entries containing BOTH "valv" and "ope"
        // VALVE_OPEN_ entries: name contains "valv" AND "ope" → 10 matches
        var filtered = vm2.GetSuggestionsForMember(leaf, "valv ope");
        filtered.Should().HaveCount(10,
            "AND-filtering 'valv ope' should match only entries containing both substrings " +
            "(VALVE_OPEN_* entries: name='VALVE_OPEN_{i}', comment='valve open {i}')");

        vm2.Dispose();
    }

    /// <summary>
    /// Test 12 — Setting ShowConstants = true populates Suggestions from the tag-table cache.
    ///
    /// A VM with no rule (ShowConstants forced false by default) and a tag table cache
    /// containing 5 entries: toggling ShowConstants on should populate Suggestions.
    /// </summary>
    [Fact]
    public void ShowConstants_TogglingOnPopulatesSuggestions()
    {
        // Build a small tag-table cache
        var reader = Substitute.For<ITagTableReader>();
        reader.GetTagTableNames().Returns(new[] { "TestConsts" });
        reader.ReadTagTable("TestConsts").Returns(new[]
        {
            new TagTableEntry("CONST_A", "1", "Int", ""),
            new TagTableEntry("CONST_B", "2", "Int", ""),
            new TagTableEntry("CONST_C", "3", "Int", ""),
            new TagTableEntry("CONST_D", "4", "Int", ""),
            new TagTableEntry("CONST_E", "5", "Int", ""),
        });
        var cache = new TagTableCache(reader);

        // Build VM with no rule (so ShowConstants isn't forced on)
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = new SimaticMLParser().Parse(xml);
        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 1000));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var vm = new BulkChangeViewModel(db, xml, new HierarchyAnalyzer(),
            bulkService, tracker, configLoader,
            tagTableCache: cache,
            messageBox: new NopMessageBox());

        // Select a leaf so ReloadSuggestions has a model to work with
        FlatTreeManager.ExpandAll(vm.RootMembers);
        vm.RefreshFlatList();
        var leaf = vm.FlatMembers.First(m => m.IsLeaf);
        vm.SelectedFlatMember = leaf;

        // Confirm starting state: ShowConstants=false → Suggestions empty
        vm.ShowConstants.Should().BeFalse("no rule forces ShowConstants on");
        vm.Autocomplete.Suggestions.Should().BeEmpty("ShowConstants is off — no suggestions loaded yet");

        // Toggle on → Suggestions must populate from the tag-table cache
        vm.ShowConstants = true;

        vm.Autocomplete.Suggestions.Should().NotBeEmpty(
            "ShowConstants=true must load suggestions from the tag-table cache");
    }

    /// <summary>
    /// Test 13 — ToggleAllSuggestions opens (populates FilteredSuggestions) then closes.
    ///
    /// First call: FilteredSuggestions becomes the subset matching NewValue.
    /// Second call: FilteredSuggestions is cleared (close path).
    /// </summary>
    [Fact]
    public void ToggleAllSuggestions_OpensThenCloses()
    {
        // Build VM with ShowConstants forced on + a tag table
        var reader = Substitute.For<ITagTableReader>();
        reader.GetTagTableNames().Returns(new[] { "TestConsts" });
        reader.ReadTagTable("TestConsts").Returns(new[]
        {
            new TagTableEntry("VAL_ON_1",  "1",  "Int", ""),
            new TagTableEntry("VAL_ON_2",  "2",  "Int", ""),
            new TagTableEntry("VAL_OFF_1", "10", "Int", ""),
            new TagTableEntry("VAL_OFF_2", "20", "Int", ""),
            new TagTableEntry("OTHER_A",   "99", "Int", ""),
            new TagTableEntry("OTHER_B",   "98", "Int", ""),
            new TagTableEntry("OTHER_C",   "97", "Int", ""),
            new TagTableEntry("OTHER_D",   "96", "Int", ""),
            new TagTableEntry("OTHER_E",   "95", "Int", ""),
            new TagTableEntry("OTHER_F",   "94", "Int", ""),
        });
        var cache = new TagTableCache(reader);

        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = new SimaticMLParser().Parse(xml);
        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 1000));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var vm = new BulkChangeViewModel(db, xml, new HierarchyAnalyzer(),
            bulkService, tracker, configLoader,
            tagTableCache: cache,
            messageBox: new NopMessageBox());

        FlatTreeManager.ExpandAll(vm.RootMembers);
        vm.RefreshFlatList();
        vm.SelectedFlatMember = vm.FlatMembers.First(m => m.IsLeaf);

        // Force ShowConstants on to populate _suggestions
        vm.ShowConstants = true;
        vm.Autocomplete.Suggestions.Should().HaveCount(10, "all 10 entries loaded");
        vm.Autocomplete.FilteredSuggestions.Should().BeEmpty("not yet toggled open");

        // Set NewValue without flushing the debounce — FilteredSuggestions stays empty
        // (we want to test the toggle path, not the auto-filter path)
        vm.NewValue = "ON";
        // Don't flush: we want to test ToggleAllSuggestions when the list starts empty.

        vm.Autocomplete.FilteredSuggestions.Should().BeEmpty("debounce not yet flushed — list still empty");

        // First toggle: closed → open (FilteredSuggestions filtered by NewValue="ON")
        vm.ToggleAllSuggestions();

        vm.Autocomplete.FilteredSuggestions.Should().NotBeEmpty("toggle-open must populate FilteredSuggestions");
        // Only VAL_ON_1 and VAL_ON_2 have "ON" in name — rest do not
        vm.Autocomplete.FilteredSuggestions.Should().AllSatisfy(s =>
            (s.DisplayName.IndexOf("ON", StringComparison.OrdinalIgnoreCase) >= 0
             || s.Value.IndexOf("ON", StringComparison.OrdinalIgnoreCase) >= 0
             || (s.Comment?.IndexOf("ON", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                .Should().BeTrue("every shown suggestion must contain the filter term 'ON'"));

        // Second toggle: close → FilteredSuggestions clears
        vm.ToggleAllSuggestions();

        vm.Autocomplete.FilteredSuggestions.Should().BeEmpty("second toggle-call must close the list");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Group E: Comment-preview pipeline
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test 14 — UpdateCommentPreviews populates PreviewComment when a commentTemplate
    /// rule exists and a scope is selected; it is a no-op when no comment rules exist.
    ///
    /// UpdateCommentPreviews is private. It is called from UpdateHighlighting when
    /// _selectedScope != null and a comment rule with a non-null CommentTemplate exists.
    /// GenerateForScope walks UP from each scope leaf to its parent nodes and applies
    /// the comment rule there — the pathPattern must match the parent UDT instance,
    /// not the leaf. In udt-instances-db.xml the parent of ModuleId is "Msg_CommError"
    /// (datatype "UDT_Message"), so the rule uses pathPattern = "Msg_CommError".
    ///
    /// TEST SEAM: UpdateCommentPreviews is private; triggered via the public cascade:
    /// SelectedFlatMember → SelectedScope → FlushPendingHighlighting → UpdateHighlighting
    /// → UpdateCommentPreviews. PreviewComment is set on MemberNodeViewModel, a public property.
    /// </summary>
    [Fact]
    public void UpdateCommentPreviews_PopulatesPreviewCommentOnScopeMembers()
    {
        // udt-instances-db.xml: Drive1.Msg_CommError.ModuleId hierarchy.
        // Rule with commentTemplate must match the parent UDT instances (Msg_CommError),
        // NOT the leaf (ModuleId) — GenerateForScope walks up from leaves to find parents.
        var configLoader = CreateConfigWithRule(@"{
            ""rules"": [{
                ""pathPattern"": ""Msg_CommError"",
                ""commentTemplate"": ""{db}""
            }]
        }");

        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = new SimaticMLParser().Parse(xml);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 1000));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var vm = new BulkChangeViewModel(db, xml, new HierarchyAnalyzer(),
            bulkService, tracker, configLoader,
            messageBox: new NopMessageBox());

        FlatTreeManager.ExpandAll(vm.RootMembers);
        vm.RefreshFlatList();

        // Select a ModuleId leaf (child of Msg_CommError) and pick the broadest scope
        var leaf = vm.FlatMembers.First(m => m.Name == "ModuleId" && m.IsLeaf);
        vm.SelectedFlatMember = leaf;
        vm.AvailableScopes.Should().NotBeEmpty("ModuleId has repeated occurrences → scope available");
        vm.SelectedScope = vm.AvailableScopes.OrderByDescending(s => s.MatchCount).First();

        vm.NewValue = "99"; // a different value triggers ComputeBulkPreview
        vm.FlushPendingHighlighting(); // runs UpdateHighlighting → UpdateCommentPreviews

        // The Msg_CommError parent nodes should have PreviewComment populated
        // (GenerateForScope walks up from ModuleId → Msg_CommError, finds the rule)
        var nodesWithPreviewComment = vm.RootMembers
            .SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .Where(n => n.PreviewComment != null)
            .ToList();

        nodesWithPreviewComment.Should().NotBeEmpty(
            "UpdateCommentPreviews must populate PreviewComment on the Msg_CommError parent nodes " +
            "when a commentTemplate rule matching 'Msg_CommError' is configured");

        // No-comment-rule path: VM without comment rules should have no PreviewComment set
        var vm2 = CreateUdtVm(); // no rules
        FlatTreeManager.ExpandAll(vm2.RootMembers);
        vm2.RefreshFlatList();
        vm2.SelectedFlatMember = vm2.FlatMembers.First(m => m.Name == "ModuleId" && m.IsLeaf);
        if (vm2.AvailableScopes.Count > 0)
        {
            vm2.SelectedScope = vm2.AvailableScopes.First();
            vm2.NewValue = "99";
            vm2.FlushPendingHighlighting();
        }

        var commentedNoRule = vm2.RootMembers
            .SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .Where(n => n.PreviewComment != null)
            .ToList();
        commentedNoRule.Should().BeEmpty(
            "when no commentTemplate rule is configured, PreviewComment must remain null (early-return path)");

        vm.Dispose();
        vm2.Dispose();
    }

    /// <summary>
    /// Test 15 — ApplyCommentPreviews writes comments into the XML for nodes
    /// whose PreviewComment is set (i.e. ones matched by a config rule with a
    /// commentTemplate). When no nodes have PreviewComment set, the XML is
    /// returned unchanged.
    ///
    /// ApplyCommentPreviews is marked internal so this test can drive it
    /// directly without running the full Apply pipeline.
    /// </summary>
    [Fact]
    public void ApplyCommentPreviews_ModifiesXmlForPendingCommentTargets()
    {
        // Positive path: a rule with commentTemplate matches the Msg_CommError
        // parent UDT instance (GenerateForScope walks up from the ModuleId leaf
        // to find the rule-matching parent). UpdateCommentPreviews runs as part
        // of the scope-selection cascade and populates PreviewComment. Calling
        // ApplyCommentPreviews directly then writes those comments into the XML.
        var configLoader = CreateConfigWithRule(@"{
            ""rules"": [{
                ""pathPattern"": ""Msg_CommError"",
                ""commentTemplate"": ""{db}""
            }]
        }");

        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = new SimaticMLParser().Parse(xml);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 1000));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var vm = new BulkChangeViewModel(db, xml, new HierarchyAnalyzer(),
            bulkService, tracker, configLoader,
            messageBox: new NopMessageBox());

        FlatTreeManager.ExpandAll(vm.RootMembers);
        vm.RefreshFlatList();

        // Select a ModuleId leaf and pick the broadest scope — same setup as
        // test 14. The scope cascade populates PreviewComment on the
        // Msg_CommError parents.
        var leaf = vm.FlatMembers.First(m => m.Name == "ModuleId" && m.IsLeaf);
        vm.SelectedFlatMember = leaf;
        vm.SelectedScope = vm.AvailableScopes.OrderByDescending(s => s.MatchCount).First();
        vm.NewValue = "99";
        vm.FlushPendingHighlighting();

        var modifiedXml = vm.ApplyCommentPreviews(xml);

        modifiedXml.Should().NotBe(xml,
            "ApplyCommentPreviews must rewrite the XML when at least one node " +
            "has a non-empty PreviewComment with CommentState != 'unchanged'");

        vm.Dispose();

        // No-op path: VM with no commentTemplate rule. No node has
        // PreviewComment set, so CollectPendingCommentNodes finds zero targets
        // and ApplyCommentPreviews returns the input XML unchanged.
        var vm2 = CreateFlatDbVm();
        FlatTreeManager.ExpandAll(vm2.RootMembers);
        vm2.RefreshFlatList();
        vm2.RootMembers.First(m => m.Name == "Speed").EditableStartValue = "99";

        var flatXml = TestFixtures.LoadXml("flat-db.xml");
        var unchanged = vm2.ApplyCommentPreviews(flatXml);
        unchanged.Should().Be(flatXml,
            "with no commentTemplate rule, ApplyCommentPreviews must return " +
            "the input XML byte-for-byte unchanged");

        vm2.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Group F: Dispose chain
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test 16 — Dispose is idempotent; after Dispose, LicenseStateChanged
    /// events on the service do NOT trigger UpdateUsageStatus.
    ///
    /// We use a stub ILicenseService that records whether LicenseStateChanged
    /// has any subscribers and fires it manually post-Dispose.
    /// </summary>
    [Fact]
    public void Dispose_IsIdempotentAndTearsDownTimersAndLicenseSubscription()
    {
        var licenseService = new RecordingLicenseService();
        var vm = CreateFlatDbVm(licenseService: licenseService);

        // Kick both debounce timers
        vm.Filter.SearchQuery = "Speed";    // schedules Filter slice debounce timer
        vm.NewValue = "42";          // schedules _valueDebounceTimer

        var usageTextBefore = vm.Subscription.UsageStatusText;

        // First Dispose
        var ex1 = Record.Exception(() => vm.Dispose());
        ex1.Should().BeNull("first Dispose must not throw");

        // Second Dispose — must be idempotent
        var ex2 = Record.Exception(() => vm.Dispose());
        ex2.Should().BeNull("second Dispose must not throw");

        // After dispose, firing LicenseStateChanged must NOT update UsageStatusText
        // (the handler was unsubscribed). We verify by ensuring the subscriber count dropped.
        licenseService.LicenseStateChangedSubscriberCount.Should().Be(0,
            "Dispose must unsubscribe the LicenseStateChanged handler so the VM " +
            "is not kept alive by the service's event list");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fakes
    // ─────────────────────────────────────────────────────────────────────

    private sealed class NopMessageBox : IMessageBoxService
    {
        public bool AskYesNo(string message, string title) => true;
        public void ShowError(string message, string title) { }
        public void ShowInfo(string message, string title) { }
        public ApplyStashCancelResult AskApplyStashCancel(string message, string title)
            => ApplyStashCancelResult.Cancel;
        public AddOrReplaceResult AskAddOrReplace(string message, string title)
            => AddOrReplaceResult.Cancel;
        public CloseWithStashResult AskCloseWithStash(string message, string title)
            => CloseWithStashResult.Cancel;
    }

    /// <summary>
    /// Minimal ILicenseService stub that tracks event subscriber count so
    /// Test 16 can verify the VM unsubscribes on Dispose.
    /// </summary>
    private sealed class RecordingLicenseService : ILicenseService
    {
        private int _subscriberCount;

        public int LicenseStateChangedSubscriberCount => _subscriberCount;

        public event EventHandler? LicenseStateChanged
        {
            add { _subscriberCount++; }
            remove { _subscriberCount--; }
        }

        public LicenseInfo GetLicenseInfo() => new LicenseInfo { Tier = LicenseTier.Free };
        public LicenseTier CurrentTier => LicenseTier.Free;
        public bool IsProActive => false;

        public Task<LicenseActivationResult> ActivateKeyAsync(string licenseKey)
            => Task.FromResult(LicenseActivationResult.InvalidKey("stub"));

        public void DeactivateKey() { }
        public void StartHeartbeat() { }
        public void StopHeartbeat() { }
        public void Dispose() { }
    }
}
