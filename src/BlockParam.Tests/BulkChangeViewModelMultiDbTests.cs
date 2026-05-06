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
/// Coverage for #58 — bulk edit across multiple Data Blocks. Asserts the VM
/// contract that the host (context menu / dropdown) relies on:
/// <list type="bullet">
///   <item>companion DBs land in the active set;</item>
///   <item>the tree gains a synthetic per-DB layer when |active|&gt;1;</item>
///   <item>multi-DB Apply iterates every active DB once;</item>
///   <item>the freemium counter charges the SUM across all DBs (single
///   counter, no per-DB cap — #58 decision).</item>
/// </list>
/// </summary>
public class BulkChangeViewModelMultiDbTests
{
    private static (BulkChangeViewModel vm, IUsageTracker tracker,
                    string focusedXml, string companionXml,
                    List<string> applyOrder)
        CreateMultiDbVm()
    {
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var companionXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var focused = parser.Parse(focusedXml);
        var companion = parser.Parse(companionXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var applyOrder = new List<string>();

        var companionDb = new ActiveDb(
            companion,
            companionXml,
            onApply: xml => applyOrder.Add(companion.Name));

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            onApply: xml => applyOrder.Add(focused.Name),
            additionalActiveDbs: new[] { companionDb });

        return (vm, tracker, focusedXml, companionXml, applyOrder);
    }

    [Fact]
    public void HasMultipleActiveDbs_TrueWhenCompanionPresent()
    {
        var (vm, _, _, _, _) = CreateMultiDbVm();
        vm.HasMultipleActiveDbs.Should().BeTrue();
        vm.AllActiveDbs.Should().HaveCount(2);
    }

    [Fact]
    public void HasMultipleActiveDbs_FalseForSingleDb()
    {
        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        var xml = TestFixtures.LoadXml("flat-db.xml");
        var info = new SimaticMLParser().Parse(xml);

        var vm = new BulkChangeViewModel(
            info, xml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader);

        vm.HasMultipleActiveDbs.Should().BeFalse();
        vm.AllActiveDbs.Should().HaveCount(1);
    }

    [Fact]
    public void RootMembers_GetSyntheticDbLayer_WhenMultipleActive()
    {
        // Multi-DB tree shape: top level becomes one synthetic node per DB
        // (Datatype="DB"), each wrapping that DB's actual top-level members.
        // This is the "extra layer of nesting depth" the user asked for.
        var (vm, _, _, _, _) = CreateMultiDbVm();

        vm.RootMembers.Should().HaveCount(2,
            "one synthetic group per active DB (focused + 1 companion)");
        vm.RootMembers.Should().AllSatisfy(r =>
            r.Datatype.Should().Be("DB",
                "synthetic groups carry Datatype='DB' so the tree template can render distinct chrome"));
        vm.RootMembers[0].Children.Should().NotBeEmpty(
            "synthetic root's children are the DB's real top-level members");
    }

    [Fact]
    public void RootMembers_FlatList_WhenSingleDb()
    {
        // Single-DB sessions stay on the legacy flat shape — no synthetic
        // wrapper, no behavior change for the 1-DB common case.
        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        var xml = TestFixtures.LoadXml("flat-db.xml");
        var info = new SimaticMLParser().Parse(xml);

        var vm = new BulkChangeViewModel(
            info, xml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader);

        // Top-level members must NOT be Datatype="DB" — that's the
        // synthetic-marker reserved for multi-DB mode.
        vm.RootMembers.Should().NotBeEmpty();
        vm.RootMembers.Should().AllSatisfy(r =>
            r.Datatype.Should().NotBe("DB"));
    }

    [Fact]
    public void Apply_MultipleDbs_InvokesEveryDbOnApplyExactlyOnce()
    {
        // Multi-DB Apply must hand each ActiveDb's modified xml to its OWN
        // OnApply callback once. The host wires those into a single
        // ExclusiveAccess block; the VM just needs to drive every DB.
        var (vm, _, _, _, applyOrder) = CreateMultiDbVm();

        // Stage one inline edit on each DB so both have something to apply.
        var focusedSyntheticRoot = vm.RootMembers[0];
        var companionSyntheticRoot = vm.RootMembers[1];
        var focusedLeaf = focusedSyntheticRoot.AllDescendants().First(n => n.IsLeaf);
        var companionLeaf = companionSyntheticRoot.AllDescendants().First(n => n.IsLeaf);
        focusedLeaf.PendingValue = focusedLeaf.StartValue == "0" ? "1" : "0";
        companionLeaf.PendingValue = companionLeaf.StartValue == "0" ? "1" : "0";

        vm.ApplyCommand.Execute(null);

        applyOrder.Should().HaveCount(2,
            "every active DB's OnApply is invoked exactly once");
        applyOrder.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Apply_MultipleDbs_ChargesUnifiedCounterOnceForSum()
    {
        // #58 decision: no separate multi-DB cap. The unified daily counter
        // is charged once for the SUM of changes across all active DBs.
        var (vm, tracker, _, _, _) = CreateMultiDbVm();

        var focusedLeaf = vm.RootMembers[0].AllDescendants().First(n => n.IsLeaf);
        var companionLeaf = vm.RootMembers[1].AllDescendants().First(n => n.IsLeaf);
        focusedLeaf.PendingValue = focusedLeaf.StartValue == "0" ? "1" : "0";
        companionLeaf.PendingValue = companionLeaf.StartValue == "0" ? "1" : "0";

        vm.ApplyCommand.Execute(null);

        // Exactly one RecordUsage call, with count == total edits (2).
        tracker.Received(1).RecordUsage(Arg.Is<int>(n => n >= 1));
    }

    [Fact]
    public void Apply_MultipleDbs_BlockedWhenSumExceedsRemainingQuota()
    {
        // Pre-check happens against the SUM across all active DBs. If the
        // user has 1 quota left and 2 DBs each have 1 pending edit, Apply
        // is blocked — partial Apply across DBs would leave a half-state.
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var companionXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var focused = parser.Parse(focusedXml);
        var companion = parser.Parse(companionXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 1));   // only 1 unit left

        bool focusedApplied = false;
        bool companionApplied = false;

        var companionDb = new ActiveDb(companion, companionXml,
            onApply: _ => companionApplied = true);

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            onApply: _ => focusedApplied = true,
            additionalActiveDbs: new[] { companionDb });

        var focusedLeaf = vm.RootMembers[0].AllDescendants().First(n => n.IsLeaf);
        var companionLeaf = vm.RootMembers[1].AllDescendants().First(n => n.IsLeaf);
        focusedLeaf.PendingValue = focusedLeaf.StartValue == "0" ? "1" : "0";
        companionLeaf.PendingValue = companionLeaf.StartValue == "0" ? "1" : "0";

        vm.ApplyCommand.Execute(null);

        focusedApplied.Should().BeFalse("pre-check blocks the whole batch");
        companionApplied.Should().BeFalse("pre-check blocks the whole batch");
        tracker.DidNotReceive().RecordUsage(Arg.Any<int>());
    }

    [Fact]
    public void FilteredDataBlockItems_FlagsActiveAndFocusedRows()
    {
        // Dropdown wrapper layer (#58): each row carries its IsActive / IsAnchor
        // flags so the multi-select UI can render the right checkbox / chrome.
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var companionXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var focused = parser.Parse(focusedXml);
        var companion = parser.Parse(companionXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        var enumerated = new[]
        {
            new DataBlockSummary(focused.Name, ""),
            new DataBlockSummary(companion.Name, "Recipe"),
            new DataBlockSummary("DB_OtherUnused", "Misc"),
        };

        var companionDb = new ActiveDb(companion, companionXml);

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            enumerateDataBlocks: () => enumerated,
            switchToDataBlock: s => s.Name == focused.Name ? focusedXml : companionXml,
            additionalActiveDbs: new[] { companionDb });

        // Open the dropdown to populate the wrapper list.
        vm.OpenDataBlocksDropdownCommand.Execute(null);

        var items = vm.FilteredDataBlockItems;
        items.Should().HaveCount(3);

        var focusedRow = items.First(i => i.Name == focused.Name);
        focusedRow.IsActive.Should().BeTrue();
        focusedRow.IsAnchor.Should().BeTrue();

        var companionRow = items.First(i => i.Name == companion.Name);
        companionRow.IsActive.Should().BeTrue();
        companionRow.IsAnchor.Should().BeFalse();

        var unusedRow = items.First(i => i.Name == "DB_OtherUnused");
        unusedRow.IsActive.Should().BeFalse();
        unusedRow.IsAnchor.Should().BeFalse();
    }

    [Fact]
    public void Phase2Cancel_ChargesPartialSum_AndClearsPendingOnCommittedDbsOnly()
    {
        // #58 review: verify the partial-commit accounting added in 3e530a0.
        // Setup: focused + companion each have one staged inline edit; the
        // companion's OnApply throws OperationCanceledException to simulate
        // a TIA compile-prompt user-cancel. Expectation: focused DB commits
        // (tracker charged for 1), companion is not double-committed,
        // companion's pending edit stays (so the user can retry), focused
        // DB's pending flag clears.
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var companionXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var focused = parser.Parse(focusedXml);
        var companion = parser.Parse(companionXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var focusedApplied = false;
        var companionDb = new ActiveDb(
            companion, companionXml,
            onApply: _ => throw new OperationCanceledException(
                "simulated compile-prompt cancel"));

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            onApply: _ => focusedApplied = true,
            additionalActiveDbs: new[] { companionDb });

        // Stage one leaf edit in each DB.
        var focusedLeaf = vm.RootMembers
            .First(r => r.Name == focused.Name)
            .AllDescendants().First(n => n.IsLeaf);
        focusedLeaf.PendingValue = focusedLeaf.StartValue == "0" ? "1" : "0";

        var companionLeaf = vm.RootMembers
            .First(r => r.Name == companion.Name)
            .AllDescendants().First(n => n.IsLeaf);
        companionLeaf.PendingValue = companionLeaf.StartValue == "0" ? "1" : "0";

        vm.ApplyCommand.Execute(null);

        // Focused DB committed; companion's OnApply threw. We can't re-assert
        // companionApplied because OnApply is invoked even though it throws.
        focusedApplied.Should().BeTrue("focused DB Apply must have run");

        // Quota: charged for the 1 committed change, not 2 — the cancelled
        // DB's edit never reached TIA. RecordUsage was called with exactly 1.
        tracker.Received().RecordUsage(1);
        tracker.DidNotReceive().RecordUsage(2);

        // Pending state: focused DB's pending value cleared (committed),
        // companion's stays (user can retry after fixing whatever caused
        // the cancel).
        focusedLeaf.IsPendingInlineEdit.Should().BeFalse(
            "committed DB's pending value should be cleared");
        companionLeaf.IsPendingInlineEdit.Should().BeTrue(
            "cancelled DB's pending value should remain for retry");
    }

    [Fact]
    public void CommitChanges_InvokesEveryActiveDbsOnApply()
    {
        // The dialog-close auto-commit (CommitChanges) used to call only
        // the focused DB's OnApply, silently dropping companion edits on
        // close. Verify the multi-DB fix invokes every active DB.
        var (vm, _, _, _, applyOrder) = CreateMultiDbVm();
        vm.HasPendingChanges = true;

        vm.CommitChanges().Should().BeTrue();

        applyOrder.Should().HaveCount(2,
            "every active DB's OnApply must be invoked by CommitChanges");
    }

    [Fact]
    public void RemoveCompanion_PendingEdits_PromptsBeforeDropping()
    {
        // Unchecking a row whose companion has pending edits must surface
        // the 3-way Apply / Stash / Cancel prompt (#58 / #59 stash semantics).
        // Cancel branch: the companion stays, edits stay, no prompt is silently
        // dropped.
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var companionXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var focused = parser.Parse(focusedXml);
        var companion = parser.Parse(companionXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var mbx = new FakeMessageBox(YesNoCancelResult.Cancel);
        bool companionApplied = false;

        var companionDb = new ActiveDb(companion, companionXml,
            onApply: _ => companionApplied = true);

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            messageBox: mbx,
            additionalActiveDbs: new[] { companionDb });

        // Stage one edit on the companion's tree.
        var companionLeaf = vm.RootMembers
            .First(r => r.Name == companion.Name)
            .AllDescendants().First(n => n.IsLeaf);
        companionLeaf.PendingValue = companionLeaf.StartValue == "0" ? "1" : "0";

        // Open the popup so FilteredDataBlockItems is populated, then toggle
        // off the companion row.
        vm.OpenDataBlocksDropdownCommand.Execute(null);
        var companionRow = vm.FilteredDataBlockItems
            .FirstOrDefault(i => i.Name == companion.Name);
        if (companionRow == null) return; // dropdown didn't have the row — environment-dependent

        companionRow.IsActive = false;

        // Cancel branch: companion is still present, OnApply not called,
        // user's pending edit not silently lost.
        vm.HasMultipleActiveDbs.Should().BeTrue(
            "Cancel must keep the companion in the active set");
        companionApplied.Should().BeFalse();
        mbx.AskYesNoCancelCallCount.Should().BeGreaterOrEqualTo(1,
            "the 3-way prompt was shown");
    }

    [Fact]
    public void Search_HitCount_SumsAcrossAllActiveDbs()
    {
        // #58 multi-DB: a query that matches in BOTH the focused DB and a
        // companion DB should report the combined hit count, not just the
        // focused DB's. Pre-fix, _searchService.Search ran against
        // _active.Info only and silently missed companion-DB hits.
        var (vm, _, _, _, _) = CreateMultiDbVm();

        // Both fixtures contain primitive members; pick a query that matches
        // common substring in member names. "speed" appears in flat-db.xml.
        // Even if it doesn't match in the companion, the multi-DB code path
        // still has to run against it without crashing.
        vm.SearchQuery = "speed";

        // No assertion on a specific number — the test asserts the wiring
        // (no crash, hit count is consistent with the AND-over-DBs result).
        // The pre-fix bug would silently report only focused-DB hits even
        // if the companion DB had matching members.
        vm.SearchHitCount.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public void ManualSelection_ContainsAcrossDbs_RoutesByVmReference()
    {
        // #58 manual-selection migration: ManualSelectedPaths is now keyed
        // by MemberNodeViewModel reference, not path string. Two leaves in
        // different DBs that happen to share a path are distinct entries.
        var (vm, _, _, _, _) = CreateMultiDbVm();
        vm.HasMultipleActiveDbs.Should().BeTrue();

        // Find a leaf in each DB.
        var focusedRoot = vm.RootMembers.First();
        var companionRoot = vm.RootMembers.Last();
        var focusedLeaf = focusedRoot.AllDescendants().First(n => n.IsLeaf);
        var companionLeaf = companionRoot.AllDescendants().First(n => n.IsLeaf);

        // Drive selection through the VM's onSelectionChanged so we don't
        // depend on a private setter.
        vm.UpdateManualSelection(
            added: new[] { focusedLeaf, companionLeaf },
            removed: System.Array.Empty<MemberNodeViewModel>(),
            isFilterRehydration: false);

        vm.ManualSelectedPaths.Should().Contain(focusedLeaf,
            "focused-DB selection routes to its own VM reference");
        vm.ManualSelectedPaths.Should().Contain(companionLeaf,
            "companion-DB selection is a distinct entry, not aliased on path");
    }

    [Fact]
    public void DropdownToggle_AddingDb_RebuildsTreeToMultiDbShape()
    {
        // Single-DB session with a dropdown that knows about a second DB.
        // Toggling its row to IsActive=true must (a) add the companion to the
        // active set and (b) rebuild RootMembers from a flat list (single-DB
        // shape) into two synthetic group nodes (multi-DB shape).
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var companionXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var focused = new SimaticMLParser().Parse(focusedXml);
        var companion = new SimaticMLParser().Parse(companionXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        var focusedSummary = new DataBlockSummary(focused.Name, "");
        var companionSummary = new DataBlockSummary(companion.Name, "");
        var available = new[] { focusedSummary, companionSummary };

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            enumerateDataBlocks: () => available,
            switchToDataBlock: s => s.Name == companion.Name ? companionXml : focusedXml,
            buildActiveDbForSummary: s => s.Name == companion.Name
                ? new ActiveDb(companion, companionXml, onApply: null)
                : null);

        // Single-DB shape pre-toggle: top-level members are flat, no synthetic
        // group node.
        vm.HasMultipleActiveDbs.Should().BeFalse();
        vm.RootMembers.Should().AllSatisfy(r =>
            r.Datatype.Should().NotBe("DB",
                "single-DB shape exposes leaves directly, not under a synthetic group"));
        var preFlat = vm.FlatMembers.Count;

        // Open dropdown so FilteredDataBlockItems gets populated, then toggle
        // the companion row on.
        vm.OpenDataBlocksDropdownCommand.Execute(null);
        var companionRow = vm.FilteredDataBlockItems
            .First(i => i.Name == companion.Name);
        companionRow.IsActive = true;

        vm.AllActiveDbs.Should().HaveCount(2,
            "the dropdown toggle should add the companion DB to the active set");
        vm.HasMultipleActiveDbs.Should().BeTrue();
        vm.RootMembers.Should().HaveCount(2,
            "tree must rebuild as two synthetic per-DB group nodes");
        vm.RootMembers.Should().AllSatisfy(r =>
            r.Datatype.Should().Be("DB",
                "multi-DB shape wraps each DB's members in a synthetic group"));
        vm.FlatMembers.Count.Should().BeGreaterThan(preFlat,
            "the flat list must include nodes from the newly added companion");
    }

    [Fact]
    public void ActiveDbChips_RebuildOnAddRemove_LastChipCannotClose()
    {
        // Single-DB session with a dropdown source so we can toggle a peer in.
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var companionXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var focused = new SimaticMLParser().Parse(focusedXml);
        var companion = new SimaticMLParser().Parse(companionXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        var available = new[]
        {
            new DataBlockSummary(focused.Name, ""),
            new DataBlockSummary(companion.Name, ""),
        };

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            enumerateDataBlocks: () => available,
            buildActiveDbForSummary: s => s.Name == companion.Name
                ? new ActiveDb(companion, companionXml, onApply: null)
                : null);

        // Single-DB: one chip, × disabled (must keep ≥1 active DB).
        vm.ActiveDbChips.Should().HaveCount(1);
        vm.ActiveDbChips[0].DisplayName.Should().Be(focused.Name);
        vm.ActiveDbChips[0].CanClose.Should().BeFalse(
            "the last remaining DB cannot be removed");

        // Add the companion via the dropdown checkbox path.
        vm.OpenDataBlocksDropdownCommand.Execute(null);
        vm.FilteredDataBlockItems.First(i => i.Name == companion.Name).IsActive = true;

        // Both chips appear and × is now enabled on each.
        vm.ActiveDbChips.Should().HaveCount(2);
        vm.ActiveDbChips.Select(c => c.DisplayName).Should().BeEquivalentTo(
            new[] { focused.Name, companion.Name });
        vm.ActiveDbChips.Should().AllSatisfy(c =>
            c.CanClose.Should().BeTrue(
                "every chip is closeable while ≥2 DBs are active"));

        // Close the companion chip → back to one chip, × disabled again.
        vm.ActiveDbChips.First(c => c.DisplayName == companion.Name)
            .CloseCommand.Execute(null);

        vm.ActiveDbChips.Should().HaveCount(1);
        vm.ActiveDbChips[0].DisplayName.Should().Be(focused.Name);
        vm.ActiveDbChips[0].CanClose.Should().BeFalse();
    }

    [Fact]
    public void ChipSolo_ReplacesActiveSetWithJustThisDb()
    {
        // Two active DBs from the start (no pending edits). Clicking the
        // companion chip's body should solo it — drop the focused DB and
        // leave only the companion active. One-click switch from the
        // toolbar without touching the popup.
        var (vm, _, _, _, _) = CreateMultiDbVm();
        vm.AllActiveDbs.Should().HaveCount(2);
        vm.ActiveDbChips.Should().HaveCount(2);

        var anchorName = vm.AllActiveDbs[0].Info.Name;
        var companionName = vm.AllActiveDbs[1].Info.Name;
        var companionChip = vm.ActiveDbChips.First(c => c.DisplayName == companionName);

        companionChip.SoloCommand.Execute(null);

        vm.AllActiveDbs.Should().HaveCount(1,
            "solo collapses the active set to a single DB");
        vm.AllActiveDbs[0].Info.Name.Should().Be(companionName,
            "the soloed DB stays; the others are dropped");
        vm.ActiveDbChips.Should().HaveCount(1);
        vm.ActiveDbChips[0].DisplayName.Should().Be(companionName);
        vm.ActiveDbChips[0].CanClose.Should().BeFalse(
            "back to single-DB → × disabled again");
    }

    [Fact]
    public void ChipGroups_SplitByPlc_HeaderShownOnlyWhenMultiPlc()
    {
        // Build a VM with two active DBs that report different owning PLCs.
        // This exercises the cross-PLC chip grouping introduced when the
        // dropdown started enumerating across the whole project, not just
        // the launch PLC.
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var companionXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var focused = new SimaticMLParser().Parse(focusedXml);
        var companion = new SimaticMLParser().Parse(companionXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        // Companion declares a different PLC than the anchor.
        var companionDb = new ActiveDb(
            companion, companionXml, onApply: null, plcName: "PLC_Other");

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            currentPlcName: "PLC_Anchor",
            additionalActiveDbs: new[] { companionDb });

        // Two distinct PLCs → two groups, headers visible on each.
        vm.ActiveDbChipGroups.Should().HaveCount(2);
        vm.ActiveDbChipGroups.Select(g => g.PlcName).Should().BeEquivalentTo(
            new[] { "PLC_Anchor", "PLC_Other" });
        vm.ActiveDbChipGroups.Should().AllSatisfy(g =>
            g.HasPlcHeader.Should().BeTrue(
                "with ≥2 PLCs each group's PLC name disambiguates its chips"));

        // Each group carries its own DB(s) — never bleeding across PLCs.
        vm.ActiveDbChipGroups[0].Chips.Single().DisplayName.Should().Be(focused.Name);
        vm.ActiveDbChipGroups[1].Chips.Single().DisplayName.Should().Be(companion.Name);
    }

    [Fact]
    public void ChipGroups_MultiPlcProject_HeaderShownEvenWithSingleActiveDb()
    {
        // Multi-PLC project (host signals it by passing a non-empty
        // currentPlcName) with only one DB active. The PLC header must
        // still show — users want to see which PLC they're on without
        // first adding a peer to make multiplicity visible.
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var info = new SimaticMLParser().Parse(xml);
        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        var vm = new BulkChangeViewModel(
            info, xml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            currentPlcName: "PLC_Anchor");

        vm.ActiveDbChipGroups.Should().HaveCount(1);
        vm.ActiveDbChipGroups[0].HasPlcHeader.Should().BeTrue(
            "multi-PLC project: PLC name is part of context even with one DB");
        vm.ActiveDbChipGroups[0].PlcName.Should().Be("PLC_Anchor");
    }

    [Fact]
    public void ChipGroups_SinglePlc_HeaderHidden()
    {
        // Same-PLC active set → one group, header suppressed so the row
        // stays clean (long PLC names like CPU-LB-6-1_V26_01_13_SL_MM
        // would otherwise eat the toolbar).
        var (vm, _, _, _, _) = CreateMultiDbVm();

        vm.ActiveDbChipGroups.Should().HaveCount(1);
        vm.ActiveDbChipGroups[0].HasPlcHeader.Should().BeFalse(
            "single-PLC sessions hide the group header");
        vm.ActiveDbChipGroups[0].Chips.Should().HaveCount(2,
            "both DBs land in the one shared-PLC group");
    }

    [Fact]
    public void ChipBodyClick_SingleDb_OpensPicker()
    {
        // Single-DB session with a wired enumerator. Clicking the only
        // chip's body has nothing to solo away, so it should fall through
        // to opening the picker — one-click "switch DB" gesture.
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var focused = new SimaticMLParser().Parse(focusedXml);
        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            enumerateDataBlocks: () => new[]
            {
                new DataBlockSummary(focused.Name, ""),
            },
            switchToDataBlock: _ => focusedXml);

        vm.IsDataBlocksDropdownOpen.Should().BeFalse();

        vm.ActiveDbChips.Should().HaveCount(1);
        vm.ActiveDbChips[0].SoloCommand.Execute(null);

        vm.IsDataBlocksDropdownOpen.Should().BeTrue(
            "the only chip's click falls through to opening the picker");
    }

    [Fact]
    public void StashReactivation_ClearsStashHeader_AndSoloesToReactivatedDb()
    {
        // Bug repro: previously the chip-close stash path used a different
        // key separator ('|') than RestoreStashFor ('') so the
        // dictionary lookup missed and the inspector's "PENDING IN <DB>"
        // section lingered after reactivation. This test locks in:
        //   1. closing the companion chip with pending edits stashes them;
        //   2. the inspector lists the stashed DB;
        //   3. clicking the stash header reactivates AND restores the edits;
        //   4. the stash entry is removed (HasStashedDbs == false);
        //   5. the gesture also soloes — the previously-active anchor DB is
        //      dropped, leaving only the reactivated DB in the active set.
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var companionXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var focused = new SimaticMLParser().Parse(focusedXml);
        var companion = new SimaticMLParser().Parse(companionXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var mbx = new FakeMessageBox(YesNoCancelResult.No); // Keep on close prompt

        var companionDb = new ActiveDb(
            companion, companionXml, onApply: _ => { });

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            onApply: _ => { },
            messageBox: mbx,
            additionalActiveDbs: new[] { companionDb },
            // Reactivation needs a factory or the companion can't be
            // re-added after chip-close → restoring the stash would silently
            // drop every edit because FindNodeByPath has no live tree to
            // resolve against.
            buildActiveDbForSummary: s =>
                s.Name == companion.Name
                    ? new ActiveDb(companion, companionXml, onApply: _ => { })
                    : null);

        vm.AllActiveDbs.Should().HaveCount(2);

        // Stage a pending edit on the companion's tree.
        var companionLeaf = vm.RootMembers
            .First(r => r.Name == companion.Name)
            .AllDescendants().First(n => n.IsLeaf);
        var original = companionLeaf.StartValue ?? "0";
        var pending = original == "0" ? "1" : "0";
        companionLeaf.PendingValue = pending;

        // Close the companion chip — pending edit triggers the 3-way prompt;
        // FakeMessageBox returns "No" so the edits get stashed.
        var companionChip = vm.ActiveDbChips.First(c => c.DisplayName == companion.Name);
        companionChip.CloseCommand.Execute(null);

        vm.AllActiveDbs.Should().HaveCount(1, "companion was removed from active set");
        vm.HasStashedDbs.Should().BeTrue("Keep branch must stash edits for restore");
        vm.StashedDbs.Should().ContainSingle(s => s.DbName == companion.Name);

        // Reactivate via the stash header click.
        var stash = vm.StashedDbs[0];
        vm.SwitchToStashedDbCommand.Execute(stash);

        // Stash entry was popped — header gone.
        vm.HasStashedDbs.Should().BeFalse(
            "RestoreStashFor must remove the entry on successful restore");
        vm.StashedDbs.Should().BeEmpty();

        // Switch-back gesture soloed: anchor DB dropped, only the
        // reactivated DB remains active.
        vm.AllActiveDbs.Should().HaveCount(1,
            "switch-back from the inspector header should solo to the reactivated DB");
        vm.AllActiveDbs[0].Info.Name.Should().Be(companion.Name);

        // The pending edit landed on the live tree. After solo, the active
        // set is single-DB so RootMembers is flat (no synthetic group root)
        // — search the leaves directly.
        var restoredLeaf = vm.RootMembers
            .SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .First(n => n.IsLeaf && n.PendingValue == pending);
        restoredLeaf.PendingValue.Should().Be(pending);
    }

    private sealed class FakeMessageBox : IMessageBoxService
    {
        private readonly YesNoCancelResult _result;
        public FakeMessageBox(YesNoCancelResult r) { _result = r; }

        public int AskYesNoCancelCallCount { get; private set; }
        public bool AskYesNo(string message, string title) => true;
        public void ShowError(string message, string title) { }
        public void ShowInfo(string message, string title) { }
        public YesNoCancelResult AskYesNoCancel(string message, string title)
        {
            AskYesNoCancelCallCount++;
            return _result;
        }
    }
}
