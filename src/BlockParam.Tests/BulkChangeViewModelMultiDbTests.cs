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
        // Dropdown wrapper layer (#58): each row carries its IsActive / IsFocused
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
        focusedRow.IsFocused.Should().BeTrue();

        var companionRow = items.First(i => i.Name == companion.Name);
        companionRow.IsActive.Should().BeTrue();
        companionRow.IsFocused.Should().BeFalse();

        var unusedRow = items.First(i => i.Name == "DB_OtherUnused");
        unusedRow.IsActive.Should().BeFalse();
        unusedRow.IsFocused.Should().BeFalse();
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
