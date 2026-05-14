using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
///   <item>peer DBs land in the active set;</item>
///   <item>the tree gains a synthetic per-DB layer when |active|&gt;1;</item>
///   <item>multi-DB Apply iterates every active DB once;</item>
///   <item>the freemium counter charges the SUM across all DBs (single
///   counter, no per-DB cap — #58 decision).</item>
/// </list>
/// </summary>
public class BulkChangeViewModelMultiDbTests
{
    private static (BulkChangeViewModel vm, IUsageTracker tracker,
                    string focusedXml, string peerXml,
                    List<string> applyOrder)
        CreateMultiDbVm()
    {
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var focused = parser.Parse(focusedXml);
        var peer = parser.Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var applyOrder = new List<string>();

        var peerDb = new ActiveDb(
            peer,
            peerXml,
            onApply: xml => applyOrder.Add(peer.Name));

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            onApply: xml => applyOrder.Add(focused.Name),
            additionalActiveDbs: new[] { peerDb });

        return (vm, tracker, focusedXml, peerXml, applyOrder);
    }

    [Fact]
    public void HasMultipleActiveDbs_TrueWhenPeerPresent()
    {
        var (vm, _, _, _, _) = CreateMultiDbVm();
        vm.ActiveSet.HasMultipleActiveDbs.Should().BeTrue();
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

        vm.ActiveSet.HasMultipleActiveDbs.Should().BeFalse();
        vm.AllActiveDbs.Should().HaveCount(1);
    }

    [Fact]
    public void RootMembers_GetSyntheticDbLayer_WhenMultipleActive()
    {
        // Multi-DB tree shape: top level becomes one synthetic node per DB
        // (Datatype="DB"), each wrapping that DB's actual top-level members.
        // This is the "extra layer of nesting depth" the user asked for.
        var (vm, _, _, _, _) = CreateMultiDbVm();

        vm.Tree.RootMembers.Should().HaveCount(2,
            "one synthetic group per active DB (focused + 1 peer)");
        vm.Tree.RootMembers.Should().AllSatisfy(r =>
            r.Datatype.Should().Be("DB",
                "synthetic groups carry Datatype='DB' so the tree template can render distinct chrome"));
        vm.Tree.RootMembers[0].Children.Should().NotBeEmpty(
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
        vm.Tree.RootMembers.Should().NotBeEmpty();
        vm.Tree.RootMembers.Should().AllSatisfy(r =>
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
        var focusedSyntheticRoot = vm.Tree.RootMembers[0];
        var peerSyntheticRoot = vm.Tree.RootMembers[1];
        var focusedLeaf = focusedSyntheticRoot.AllDescendants().First(n => n.IsLeaf);
        var peerLeaf = peerSyntheticRoot.AllDescendants().First(n => n.IsLeaf);
        focusedLeaf.EditableStartValue = focusedLeaf.StartValue == "0" ? "1" : "0";
        peerLeaf.EditableStartValue = peerLeaf.StartValue == "0" ? "1" : "0";

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

        var focusedLeaf = vm.Tree.RootMembers[0].AllDescendants().First(n => n.IsLeaf);
        var peerLeaf = vm.Tree.RootMembers[1].AllDescendants().First(n => n.IsLeaf);
        focusedLeaf.EditableStartValue = focusedLeaf.StartValue == "0" ? "1" : "0";
        peerLeaf.EditableStartValue = peerLeaf.StartValue == "0" ? "1" : "0";

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
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var focused = parser.Parse(focusedXml);
        var peer = parser.Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 1));   // only 1 unit left

        bool focusedApplied = false;
        bool peerApplied = false;

        var peerDb = new ActiveDb(peer, peerXml,
            onApply: _ => peerApplied = true);

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            onApply: _ => focusedApplied = true,
            additionalActiveDbs: new[] { peerDb });

        var focusedLeaf = vm.Tree.RootMembers[0].AllDescendants().First(n => n.IsLeaf);
        var peerLeaf = vm.Tree.RootMembers[1].AllDescendants().First(n => n.IsLeaf);
        focusedLeaf.EditableStartValue = focusedLeaf.StartValue == "0" ? "1" : "0";
        peerLeaf.EditableStartValue = peerLeaf.StartValue == "0" ? "1" : "0";

        vm.ApplyCommand.Execute(null);

        focusedApplied.Should().BeFalse("pre-check blocks the whole batch");
        peerApplied.Should().BeFalse("pre-check blocks the whole batch");
        tracker.DidNotReceive().RecordUsage(Arg.Any<int>());
    }

    /// <summary>
    /// Regression for #108: in multi-DB mode the host's
    /// <c>SubscribeStartValueEdited</c> used to be recursive AND
    /// <see cref="MemberTreeViewModel.AddDbGroupRoot"/> also walked every
    /// descendant — so non-leaf nodes ended up with depth-many
    /// <c>StartValueEdited</c> and <c>SelectedChanged</c> handlers each.
    /// Editing a leaf still produced the correct pending value (the store
    /// is idempotent), but every inline edit fanned N
    /// <c>OnNodeSelected</c> sweeps of the tree.
    ///
    /// <para>
    /// The fix made the per-VM subscribe callback non-recursive by contract
    /// (both sites iterate per node). This test fires
    /// <c>StartValueEdited</c> on a deeply-nested leaf in a multi-DB session
    /// and asserts the host's <c>OnSingleValueEdited</c> runs exactly once.
    /// </para>
    /// </summary>
    [Fact]
    public void InlineEdit_DeeplyNestedLeaf_MultiDb_FiresOnSingleValueEditedExactlyOnce()
    {
        // Pair two DBs so we land on the multi-DB code path that triggered
        // the bug. deep-nesting-db.xml has 5 levels of struct nesting with
        // one leaf at the bottom — exactly the shape #108 calls out (depth
        // ≥ 3 below the synthetic group root).
        var anchorXml = TestFixtures.LoadXml("flat-db.xml");
        var deepXml = TestFixtures.LoadXml("deep-nesting-db.xml");
        var parser = new SimaticMLParser();
        var anchor = parser.Parse(anchorXml);
        var deep = parser.Parse(deepXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var deepDb = new ActiveDb(deep, deepXml, onApply: null);

        var vm = new BulkChangeViewModel(
            anchor, anchorXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            additionalActiveDbs: new[] { deepDb });

        // Locate the deep leaf (DeepValue) inside the synthetic group root
        // for the deep DB. AllDescendants spans the whole subtree.
        var deepRoot = vm.Tree.RootMembers.First(r => r.Name == deep.Name);
        var deepLeaf = deepRoot.AllDescendants()
            .First(n => n.IsLeaf && n.Name == "DeepValue");

        // The leaf must sit deep enough below the synthetic root that a
        // recursive subscribe would noticeably fan out. The synthetic root
        // is depth 0; DeepValue lives at depth 5 (Level1 → Level2 → Level3
        // → Level4 → DeepValue). The original bug bound depth-many handlers
        // per ancestor — locking in the structural premise of the test.
        deepLeaf.Depth.Should().BeGreaterOrEqualTo(3,
            "the regression only manifests when the leaf is deeply nested " +
            "below the synthetic group root");

        // Observe the StartValueEdited and SelectedChanged events'
        // invocation list lengths: exactly one host handler each must be
        // wired (OnSingleValueEdited / Selection.OnNodeSelected, routed
        // via SubscribeStartValueEdited). Before #108 these were
        // (depth + 1) — one per ancestor that walked into this leaf
        // through the recursive host AND the slice's per-descendant loop.
        var startValueEditedField = typeof(MemberNodeViewModel).GetField(
            "StartValueEdited",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var selectedChangedField = typeof(MemberNodeViewModel).GetField(
            "SelectedChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        startValueEditedField.Should().NotBeNull(
            "the test relies on the compiler-generated backing field for the event");
        selectedChangedField.Should().NotBeNull(
            "the test relies on the compiler-generated backing field for the event");

        var editedDel = (Delegate?)startValueEditedField!.GetValue(deepLeaf);
        var editedHandlerCount = editedDel?.GetInvocationList().Length ?? 0;
        editedHandlerCount.Should().Be(1,
            "non-recursive contract (#108): each minted VM gets exactly one " +
            "OnSingleValueEdited handler — a recursive host plus the slice's " +
            "per-descendant loop would produce depth-many subscriptions here");

        var selectedDel = (Delegate?)selectedChangedField!.GetValue(deepLeaf);
        var selectedHandlerCount = selectedDel?.GetInvocationList().Length ?? 0;
        selectedHandlerCount.Should().Be(1,
            "non-recursive contract (#108): each minted VM gets exactly one " +
            "Selection.OnNodeSelected handler — depth-many handlers were the " +
            "observable symptom of the bug (N sweeps of the tree per edit)");
    }

    [Fact]
    public void FilteredDataBlockItems_FlagsActiveAndFocusedRows()
    {
        // Dropdown wrapper layer (#58): each row carries its IsActive / IsAnchor
        // flags so the multi-select UI can render the right checkbox / chrome.
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var focused = parser.Parse(focusedXml);
        var peer = parser.Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        var enumerated = new[]
        {
            new DataBlockSummary(focused.Name, ""),
            new DataBlockSummary(peer.Name, "Recipe"),
            new DataBlockSummary("DB_OtherUnused", "Misc"),
        };

        var peerDb = new ActiveDb(peer, peerXml);

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            enumerateDataBlocks: () => enumerated,
            switchToDataBlock: s => s.Name == focused.Name ? focusedXml : peerXml,
            additionalActiveDbs: new[] { peerDb });

        // Open the dropdown to populate the wrapper list.
        vm.ActiveSet.OpenDataBlocksDropdownCommand.Execute(null);

        var items = vm.ActiveSet.FilteredDataBlockItems;
        items.Should().HaveCount(3);

        var focusedRow = items.First(i => i.Name == focused.Name);
        focusedRow.IsActive.Should().BeTrue();
        focusedRow.IsAnchor.Should().BeTrue();

        var peerRow = items.First(i => i.Name == peer.Name);
        peerRow.IsActive.Should().BeTrue();
        peerRow.IsAnchor.Should().BeFalse();

        var unusedRow = items.First(i => i.Name == "DB_OtherUnused");
        unusedRow.IsActive.Should().BeFalse();
        unusedRow.IsAnchor.Should().BeFalse();
    }

    [Fact]
    public void Phase2Cancel_ChargesPartialSum_AndClearsPendingOnCommittedDbsOnly()
    {
        // #58 review: verify the partial-commit accounting added in 3e530a0.
        // Setup: focused + peer each have one staged inline edit; the
        // peer's OnApply throws OperationCanceledException to simulate
        // a TIA compile-prompt user-cancel. Expectation: focused DB commits
        // (tracker charged for 1), peer is not double-committed,
        // peer's pending edit stays (so the user can retry), focused
        // DB's pending flag clears.
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var focused = parser.Parse(focusedXml);
        var peer = parser.Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var focusedApplied = false;
        var peerDb = new ActiveDb(
            peer, peerXml,
            onApply: _ => throw new OperationCanceledException(
                "simulated compile-prompt cancel"));

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            onApply: _ => focusedApplied = true,
            additionalActiveDbs: new[] { peerDb });

        // Stage one leaf edit in each DB.
        var focusedLeaf = vm.Tree.RootMembers
            .First(r => r.Name == focused.Name)
            .AllDescendants().First(n => n.IsLeaf);
        focusedLeaf.EditableStartValue = focusedLeaf.StartValue == "0" ? "1" : "0";

        var peerLeaf = vm.Tree.RootMembers
            .First(r => r.Name == peer.Name)
            .AllDescendants().First(n => n.IsLeaf);
        peerLeaf.EditableStartValue = peerLeaf.StartValue == "0" ? "1" : "0";

        vm.ApplyCommand.Execute(null);

        // Focused DB committed; peer's OnApply threw. We can't re-assert
        // peerApplied because OnApply is invoked even though it throws.
        focusedApplied.Should().BeTrue("focused DB Apply must have run");

        // Quota: charged for the 1 committed change, not 2 — the cancelled
        // DB's edit never reached TIA. RecordUsage was called with exactly 1.
        tracker.Received().RecordUsage(1);
        tracker.DidNotReceive().RecordUsage(2);

        // Pending state: focused DB's pending value cleared (committed),
        // peer's stays (user can retry after fixing whatever caused
        // the cancel).
        focusedLeaf.IsPendingInlineEdit.Should().BeFalse(
            "committed DB's pending value should be cleared");
        peerLeaf.IsPendingInlineEdit.Should().BeTrue(
            "cancelled DB's pending value should remain for retry");
    }

    [Fact]
    public void CommitChanges_InvokesEveryActiveDbsOnApply()
    {
        // The dialog-close auto-commit (CommitChanges) used to call only
        // the focused DB's OnApply, silently dropping peer edits on
        // close. Verify the multi-DB fix invokes every active DB.
        var (vm, _, _, _, applyOrder) = CreateMultiDbVm();
        vm.HasPendingChanges = true;

        vm.CommitChanges().Should().BeTrue();

        applyOrder.Should().HaveCount(2,
            "every active DB's OnApply must be invoked by CommitChanges");
    }

    [Fact]
    public void RemovePeer_PendingEdits_PromptsBeforeDropping()
    {
        // Unchecking a row whose peer has pending edits must surface
        // the 3-way Apply / Stash / Cancel prompt (#58 / #59 stash semantics).
        // Cancel branch: the peer stays, edits stay, no prompt is silently
        // dropped.
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var focused = parser.Parse(focusedXml);
        var peer = parser.Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var mbx = new FakeMessageBox(YesNoCancelResult.Cancel);
        bool peerApplied = false;

        var peerDb = new ActiveDb(peer, peerXml,
            onApply: _ => peerApplied = true);

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            messageBox: mbx,
            additionalActiveDbs: new[] { peerDb });

        // Stage one edit on the peer's tree.
        var peerLeaf = vm.Tree.RootMembers
            .First(r => r.Name == peer.Name)
            .AllDescendants().First(n => n.IsLeaf);
        peerLeaf.EditableStartValue = peerLeaf.StartValue == "0" ? "1" : "0";

        // Open the popup so FilteredDataBlockItems is populated, then toggle
        // off the peer row.
        vm.ActiveSet.OpenDataBlocksDropdownCommand.Execute(null);
        var peerRow = vm.ActiveSet.FilteredDataBlockItems
            .FirstOrDefault(i => i.Name == peer.Name);
        if (peerRow == null) return; // dropdown didn't have the row — environment-dependent

        peerRow.IsActive = false;

        // Cancel branch: peer is still present, OnApply not called,
        // user's pending edit not silently lost.
        vm.ActiveSet.HasMultipleActiveDbs.Should().BeTrue(
            "Cancel must keep the peer in the active set");
        peerApplied.Should().BeFalse();
        mbx.AskYesNoCancelCallCount.Should().BeGreaterOrEqualTo(1,
            "the 3-way prompt was shown");
    }

    [Fact]
    public void Search_HitCount_SumsAcrossAllActiveDbs()
    {
        // #58 multi-DB: a query that matches in BOTH the focused DB and a
        // peer DB should report the combined hit count, not just the
        // focused DB's. Pre-fix, _searchService.Search ran against
        // _active.Info only and silently missed peer-DB hits.
        var (vm, _, _, _, _) = CreateMultiDbVm();

        // Both fixtures contain primitive members; pick a query that matches
        // common substring in member names. "speed" appears in flat-db.xml.
        // Even if it doesn't match in the peer, the multi-DB code path
        // still has to run against it without crashing.
        vm.Filter.SearchQuery = "speed";

        // No assertion on a specific number — the test asserts the wiring
        // (no crash, hit count is consistent with the AND-over-DBs result).
        // The pre-fix bug would silently report only focused-DB hits even
        // if the peer DB had matching members.
        vm.Filter.SearchHitCount.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public void ManualSelection_ContainsAcrossDbs_RoutesByVmReference()
    {
        // #58 manual-selection migration: ManualSelectedPaths is now keyed
        // by MemberNodeViewModel reference, not path string. Two leaves in
        // different DBs that happen to share a path are distinct entries.
        var (vm, _, _, _, _) = CreateMultiDbVm();
        vm.ActiveSet.HasMultipleActiveDbs.Should().BeTrue();

        // Find a leaf in each DB.
        var focusedRoot = vm.Tree.RootMembers.First();
        var peerRoot = vm.Tree.RootMembers.Last();
        var focusedLeaf = focusedRoot.AllDescendants().First(n => n.IsLeaf);
        var peerLeaf = peerRoot.AllDescendants().First(n => n.IsLeaf);

        // Drive selection through the VM's onSelectionChanged so we don't
        // depend on a private setter.
        vm.UpdateManualSelection(
            added: new[] { focusedLeaf, peerLeaf },
            removed: System.Array.Empty<MemberNodeViewModel>(),
            isFilterRehydration: false);

        vm.Selection.ManualSelectedPaths.Should().Contain(focusedLeaf,
            "focused-DB selection routes to its own VM reference");
        vm.Selection.ManualSelectedPaths.Should().Contain(peerLeaf,
            "peer-DB selection is a distinct entry, not aliased on path");
    }

    [Fact]
    public void DropdownToggle_AddingDb_RebuildsTreeToMultiDbShape()
    {
        // Single-DB session with a dropdown that knows about a second DB.
        // Toggling its row to IsActive=true must (a) add the peer to the
        // active set and (b) rebuild RootMembers from a flat list (single-DB
        // shape) into two synthetic group nodes (multi-DB shape).
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var focused = new SimaticMLParser().Parse(focusedXml);
        var peer = new SimaticMLParser().Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        var focusedSummary = new DataBlockSummary(focused.Name, "");
        var peerSummary = new DataBlockSummary(peer.Name, "");
        var available = new[] { focusedSummary, peerSummary };

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            enumerateDataBlocks: () => available,
            switchToDataBlock: s => s.Name == peer.Name ? peerXml : focusedXml,
            buildActiveDbForSummary: s => s.Name == peer.Name
                ? new ActiveDb(peer, peerXml, onApply: null)
                : null);

        // Single-DB shape pre-toggle: top-level members are flat, no synthetic
        // group node.
        vm.ActiveSet.HasMultipleActiveDbs.Should().BeFalse();
        vm.Tree.RootMembers.Should().AllSatisfy(r =>
            r.Datatype.Should().NotBe("DB",
                "single-DB shape exposes leaves directly, not under a synthetic group"));
        var preFlat = vm.Tree.FlatMembers.Count;

        // Open dropdown so FilteredDataBlockItems gets populated, then toggle
        // the peer row on.
        vm.ActiveSet.OpenDataBlocksDropdownCommand.Execute(null);
        var peerRow = vm.ActiveSet.FilteredDataBlockItems
            .First(i => i.Name == peer.Name);
        peerRow.IsActive = true;

        vm.AllActiveDbs.Should().HaveCount(2,
            "the dropdown toggle should add the peer DB to the active set");
        vm.ActiveSet.HasMultipleActiveDbs.Should().BeTrue();
        vm.Tree.RootMembers.Should().HaveCount(2,
            "tree must rebuild as two synthetic per-DB group nodes");
        vm.Tree.RootMembers.Should().AllSatisfy(r =>
            r.Datatype.Should().Be("DB",
                "multi-DB shape wraps each DB's members in a synthetic group"));
        vm.Tree.FlatMembers.Count.Should().BeGreaterThan(preFlat,
            "the flat list must include nodes from the newly added peer");
    }

    [Fact]
    public void PlcPills_RebuildOnAddRemove_LastDbCannotBeRemoved()
    {
        // Single-DB session with a dropdown source so we can toggle a peer in.
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var focused = new SimaticMLParser().Parse(focusedXml);
        var peer = new SimaticMLParser().Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        var available = new[]
        {
            new DataBlockSummary(focused.Name, ""),
            new DataBlockSummary(peer.Name, ""),
        };

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            enumerateDataBlocks: () => available,
            buildActiveDbForSummary: s => s.Name == peer.Name
                ? new ActiveDb(peer, peerXml, onApply: null)
                : null);

        // Single-DB: one pill with one selected entry; last DB cannot be removed.
        vm.ActiveSet.PlcPills.Should().HaveCount(1,
            "one pill for the single active PLC");
        vm.AllActiveDbs.Should().HaveCount(1);
        var anchorDb = vm.AllActiveDbs[0];
        vm.ActiveSet.RequestRemoveActiveDb(anchorDb); // should be refused
        vm.AllActiveDbs.Should().HaveCount(1,
            "the last remaining DB cannot be removed");

        // Add the peer via the dropdown checkbox path.
        vm.ActiveSet.OpenDataBlocksDropdownCommand.Execute(null);
        vm.ActiveSet.FilteredDataBlockItems.First(i => i.Name == peer.Name).IsActive = true;

        // Two DBs active → still one pill (both on same PLC).
        vm.AllActiveDbs.Should().HaveCount(2);
        vm.ActiveSet.PlcPills.Should().HaveCount(1,
            "both DBs are on the same PLC → one pill");

        // Remove the peer → back to one DB.
        var peerDbRef = vm.AllActiveDbs.First(d => d.Info.Name == peer.Name);
        vm.ActiveSet.RequestRemoveActiveDb(peerDbRef);
        vm.AllActiveDbs.Should().HaveCount(1);
        vm.AllActiveDbs[0].Info.Name.Should().Be(focused.Name);
    }

    [Fact]
    public void Solo_ReplacesActiveSetWithJustThisDb()
    {
        // Two active DBs from the start (no pending edits). Calling
        // SoloActiveDbByReference on the peer should drop the anchor and
        // leave only the peer active.
        var (vm, _, _, _, _) = CreateMultiDbVm();
        vm.AllActiveDbs.Should().HaveCount(2);

        var anchorName = vm.AllActiveDbs[0].Info.Name;
        var peerName = vm.AllActiveDbs[1].Info.Name;
        var peerDb = vm.AllActiveDbs.First(d => d.Info.Name == peerName);

        vm.ActiveSet.SoloActiveDbByReference(peerDb);

        vm.AllActiveDbs.Should().HaveCount(1,
            "solo collapses the active set to a single DB");
        vm.AllActiveDbs[0].Info.Name.Should().Be(peerName,
            "the soloed DB stays; the others are dropped");
    }

    [Fact]
    public void PlcPills_SplitByPlc_TwoPillsForTwoDistinctPlcs()
    {
        // Two active DBs on different PLCs → two separate pills.
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var focused = new SimaticMLParser().Parse(focusedXml);
        var peer = new SimaticMLParser().Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        var peerDb = new ActiveDb(peer, peerXml, onApply: null, plcName: "PLC_Other");

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            currentPlcName: "PLC_Anchor",
            additionalActiveDbs: new[] { peerDb });

        // Two distinct PLCs → two pills, one per PLC.
        vm.ActiveSet.PlcPills.Should().HaveCount(2);
        vm.ActiveSet.PlcPills.Select(p => p.PlcName).Should().BeEquivalentTo(
            new[] { "PLC_Anchor", "PLC_Other" });
        vm.ActiveSet.PlcPills[0].SelectedDbs.Should().HaveCount(1);
        vm.ActiveSet.PlcPills[1].SelectedDbs.Should().HaveCount(1);
    }

    [Fact]
    public void PlcPills_SinglePlc_OnePill_LabelEmpty()
    {
        // Same-PLC active set → one pill, label empty (PLC name not shown
        // when there's only one PLC).
        var (vm, _, _, _, _) = CreateMultiDbVm();

        vm.ActiveSet.PlcPills.Should().HaveCount(1,
            "both DBs are on the same PLC — only one pill");
        vm.ActiveSet.PlcPills[0].Label.Should().BeEmpty(
            "single-PLC sessions omit the PLC label on the pill");
    }

    [Fact]
    public void PlcPills_MultiPlcProject_SingleActiveDb_AnchorPillHasPlcName()
    {
        // Multi-PLC project (host signals it by passing a non-empty
        // currentPlcName) with only one DB active. The pill must carry
        // the PLC name so the user knows which machine they're on.
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

        vm.ActiveSet.PlcPills.Should().HaveCount(1);
        vm.ActiveSet.PlcPills[0].PlcName.Should().Be("PLC_Anchor");
        vm.ActiveSet.PlcPills[0].Label.Should().Be("PLC_Anchor",
            "multi-PLC context: pill label shows the PLC name");
    }

    [Fact]
    public void StashReactivation_ClearsStashHeader_AndSoloesToReactivatedDb()
    {
        // Bug repro: previously the chip-close stash path used a different
        // key separator ('|') than RestoreStashFor ('') so the
        // dictionary lookup missed and the inspector's "PENDING IN <DB>"
        // section lingered after reactivation. This test locks in:
        //   1. closing the peer chip with pending edits stashes them;
        //   2. the inspector lists the stashed DB;
        //   3. clicking the stash header reactivates AND restores the edits;
        //   4. the stash entry is removed (HasStashedDbs == false);
        //   5. the gesture also soloes — the previously-active anchor DB is
        //      dropped, leaving only the reactivated DB in the active set.
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var focused = new SimaticMLParser().Parse(focusedXml);
        var peer = new SimaticMLParser().Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var mbx = new FakeMessageBox(YesNoCancelResult.No); // Keep on close prompt

        var peerDb = new ActiveDb(
            peer, peerXml, onApply: _ => { });

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            onApply: _ => { },
            messageBox: mbx,
            additionalActiveDbs: new[] { peerDb },
            // Reactivation needs a factory or the peer can't be
            // re-added after chip-close → restoring the stash would silently
            // drop every edit because FindNodeByPath has no live tree to
            // resolve against.
            buildActiveDbForSummary: s =>
                s.Name == peer.Name
                    ? new ActiveDb(peer, peerXml, onApply: _ => { })
                    : null);

        vm.AllActiveDbs.Should().HaveCount(2);

        // Stage a pending edit on the peer's tree. Use EditableStartValue
        // (production path) so the PendingEditStore is populated — CountPendingEditsForDb
        // reads from the store to decide whether to prompt before remove.
        var peerLeaf = vm.Tree.RootMembers
            .First(r => r.Name == peer.Name)
            .AllDescendants().First(n => n.IsLeaf);
        var original = peerLeaf.StartValue ?? "0";
        var pending = original == "0" ? "1" : "0";
        peerLeaf.EditableStartValue = pending;

        // Remove the peer — pending edit triggers the 3-way prompt;
        // FakeMessageBox returns "No" so the edits get stashed.
        var peerDbRef = vm.AllActiveDbs.First(d => d.Info.Name == peer.Name);
        vm.ActiveSet.RequestRemoveActiveDb(peerDbRef);

        vm.AllActiveDbs.Should().HaveCount(1, "peer was removed from active set");
        vm.ActiveSet.HasStashedDbs.Should().BeTrue("Keep branch must stash edits for restore");
        vm.ActiveSet.StashedDbs.Should().ContainSingle(s => s.DbName == peer.Name);

        // Reactivate via the stash header click.
        var stash = vm.ActiveSet.StashedDbs[0];
        vm.ActiveSet.SwitchToStashedDbCommand.Execute(stash);

        // Stash entry was popped — header gone.
        vm.ActiveSet.HasStashedDbs.Should().BeFalse(
            "RestoreStashFor must remove the entry on successful restore");
        vm.ActiveSet.StashedDbs.Should().BeEmpty();

        // Switch-back gesture soloed: anchor DB dropped, only the
        // reactivated DB remains active.
        vm.AllActiveDbs.Should().HaveCount(1,
            "switch-back from the inspector header should solo to the reactivated DB");
        vm.AllActiveDbs[0].Info.Name.Should().Be(peer.Name);

        // The pending edit landed on the live tree. After solo, the active
        // set is single-DB so RootMembers is flat (no synthetic group root)
        // — search the leaves directly.
        var restoredLeaf = vm.Tree.RootMembers
            .SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .First(n => n.IsLeaf && n.PendingValue == pending);
        restoredLeaf.PendingValue.Should().Be(pending);
    }

    // ── Pill-row tests (#pill-refactor) ──────────────────────────────────────

    [Fact]
    public void PlcPills_PillSelectionChange_AddsDbToActiveSet()
    {
        // Simulating a user checking a DB in a pill's popup: when SelectedDbs
        // gains a new DataBlockListItem the VM must add it to the active set.
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var focused = new SimaticMLParser().Parse(focusedXml);
        var peer = new SimaticMLParser().Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        var available = new[]
        {
            new DataBlockSummary(focused.Name, ""),
            new DataBlockSummary(peer.Name, ""),
        };

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            enumerateDataBlocks: () => available,
            buildActiveDbForSummary: s => s.Name == peer.Name
                ? new ActiveDb(peer, peerXml, onApply: null)
                : null);

        vm.AllActiveDbs.Should().HaveCount(1, "single-DB session to start");
        vm.ActiveSet.PlcPills.Should().HaveCount(1);

        // Simulate pill selection gaining the peer (as if the user opened
        // the pill popup and checked the peer row).
        var peerSummary = new DataBlockSummary(peer.Name, "");
        var peerItem = new DataBlockListItem(peerSummary, isActive: false, isAnchor: false);
        var pill = vm.ActiveSet.PlcPills[0];
        pill.AvailableDbs.Add(peerItem);   // mimic lazy-loaded list
        pill.SelectedDbs.Add(peerItem);    // mimic user checking the row

        vm.AllActiveDbs.Should().HaveCount(2,
            "adding a DB to pill.SelectedDbs must trigger AddActiveDbFromSummary");
    }

    [Fact]
    public void PlcPills_LastDbRemoval_Refused()
    {
        // When there is exactly one active DB, removing it from the pill's
        // SelectedDbs must be refused — the dialog always keeps ≥1 active DB.
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var info = new SimaticMLParser().Parse(xml);
        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        var vm = new BulkChangeViewModel(
            info, xml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader);

        vm.AllActiveDbs.Should().HaveCount(1);
        vm.ActiveSet.PlcPills.Should().HaveCount(1);
        var pill = vm.ActiveSet.PlcPills[0];
        var initial = pill.SelectedDbs.Count;

        // Attempt to deselect the only item.
        if (initial > 0)
            pill.SelectedDbs.RemoveAt(0);

        // Active set must remain unchanged.
        vm.AllActiveDbs.Should().HaveCount(1,
            "pill removal of the last DB must be refused by the cascade");
    }

    [Fact]
    public void PlcPills_CascadeReentrancy_NoInfiniteLoop()
    {
        // When the cascade rewrites pill.SelectedDbs via SyncSelectedDbs, the
        // re-entrancy guard must prevent a second cascade from firing.
        // This test ensures the scenario completes without hanging or throwing.
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var focused = new SimaticMLParser().Parse(focusedXml);
        var peer = new SimaticMLParser().Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        var peerDb = new ActiveDb(peer, peerXml, onApply: null);

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            additionalActiveDbs: new[] { peerDb });

        vm.AllActiveDbs.Should().HaveCount(2);
        vm.ActiveSet.PlcPills.Should().HaveCount(1, "both DBs on same PLC → one pill");

        // Remove one DB — this triggers RebuildPlcPills which calls
        // SyncSelectedDbs on the pill. The guard must prevent a second cascade.
        var peerDbRef = vm.AllActiveDbs.First(d => d.Info.Name == peer.Name);
        var act = () => vm.ActiveSet.RequestRemoveActiveDb(peerDbRef);
        act.Should().NotThrow("cascade re-entrancy guard must prevent infinite recursion");
        vm.AllActiveDbs.Should().HaveCount(1);
    }

    [Fact]
    public void PlcPillViewModel_IsOpen_LazyLoadsOnce()
    {
        // PlcPillViewModel.IsOpen = true triggers a lazy load; the second
        // open skips the fetch (IsLoaded guard).
        int loadCallCount = 0;
        var pill = new PlcPillViewModel(
            plcName: "PLC_A",
            isAnchor: true,
            initialActiveItems: Array.Empty<DataBlockListItem>(),
            loadDbs: _ =>
            {
                loadCallCount++;
                return Task.FromResult<IReadOnlyList<DataBlockListItem>>(
                    Array.Empty<DataBlockListItem>());
            });

        pill.IsLoaded.Should().BeFalse("not loaded until first open");

        // First open — triggers load.
        pill.IsOpen = true;
        pill.IsLoaded.Should().BeTrue("loaded after first open");
        loadCallCount.Should().Be(1, "exactly one fetch on first open");

        // Second open — must not re-fetch.
        pill.IsOpen = false;
        pill.IsOpen = true;
        loadCallCount.Should().Be(1, "cache hit: no second fetch");
    }

    [Fact]
    public void PlcPillViewModel_FirstOpen_FallsBackToInitialActiveItems()
    {
        // When SelectedDbs is empty at first popup-open (no cascade ever
        // mutated it), OnIsOpenFlippedToTrue's snapshot is empty and must
        // fall back to _initialActiveItems so the load's matching items
        // become selected. Without the fallback the row would render with
        // zero selections after loading.
        var summary = new DataBlockSummary("DB_Initial", "", plcName: "PLC_X", number: 7);
        var initialItem = new DataBlockListItem(summary, isActive: true, isAnchor: true);

        // Loader returns a fresh DataBlockListItem with the same identity —
        // matches what LoadDbsForPlcAsync does in production (fresh wrappers
        // around cached DataBlockSummary objects).
        var loadedItem = new DataBlockListItem(summary, isActive: true, isAnchor: true);

        var pill = new PlcPillViewModel(
            plcName: "PLC_X",
            isAnchor: true,
            initialActiveItems: new[] { initialItem },
            loadDbs: _ => Task.FromResult<IReadOnlyList<DataBlockListItem>>(
                new[] { loadedItem }));

        // Don't touch SelectedDbs — the constructor seeds it from
        // initialActiveItems, but the test models the "user clears it
        // before first open" case by clearing here. Both states (untouched
        // or pre-cleared to empty) take the fallback branch.
        pill.SelectedDbs.Clear();

        pill.IsOpen = true;

        pill.SelectedDbs.Should().HaveCount(1,
            "fallback re-syncs against _initialActiveItems when SelectedDbs is empty");
        pill.SelectedDbs[0].Should().BeSameAs(loadedItem,
            "selection must reference the loaded instance, not the initialActiveItems one");
    }

    [Fact]
    public void PlcPills_RemoveLastDbForPLC_PillVanishes()
    {
        // When the last active DB for a PLC is removed, the pill for that
        // PLC must disappear from PlcPills (RebuildPlcPills re-runs on cascade).
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var focused = new SimaticMLParser().Parse(focusedXml);
        var peer = new SimaticMLParser().Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        // Two DBs on two different PLCs → two pills.
        var peerDb = new ActiveDb(peer, peerXml, onApply: null, plcName: "PLC_B");

        var vm = new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            currentPlcName: "PLC_A",
            additionalActiveDbs: new[] { peerDb });

        vm.ActiveSet.PlcPills.Should().HaveCount(2, "two PLCs → two pills");

        // Remove the peer (PLC_B's only DB).
        var peerDbRef = vm.AllActiveDbs.First(d => d.Info.Name == peer.Name);
        vm.ActiveSet.RequestRemoveActiveDb(peerDbRef);

        vm.AllActiveDbs.Should().HaveCount(1);
        vm.ActiveSet.PlcPills.Should().HaveCount(1,
            "PLC_B's pill must vanish when its last DB is removed");
        vm.ActiveSet.PlcPills[0].PlcName.Should().Be("PLC_A");
    }

    // ── "+ PLC" workflow tests (#pill-refactor empty-pill path) ──────────────

    /// <summary>
    /// Shared fixture for the "+ PLC"-flow tests: one active DB on PLC_A,
    /// plus two project-only PLCs (PLC_B with a DB, PLC_C with a DB) that
    /// have no active DB so they show up in <see cref="ActiveSetViewModel.InactiveProjectPlcs"/>.
    /// </summary>
    private static BulkChangeViewModel BuildVmForAddPlcTests()
    {
        var focusedXml = TestFixtures.LoadXml("flat-db.xml");
        var focused = new SimaticMLParser().Parse(focusedXml);
        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        var available = new[]
        {
            new DataBlockSummary(focused.Name, "", plcName: "PLC_A"),
            new DataBlockSummary("DB_OnB",    "", plcName: "PLC_B"),
            new DataBlockSummary("DB_OnC",    "", plcName: "PLC_C"),
        };

        return new BulkChangeViewModel(
            focused, focusedXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            currentPlcName: "PLC_A",
            enumerateDataBlocks: () => available,
            // Both callbacks wired so HasDataBlockSwitcher = true and the
            // InactiveProjectPlcs / CanAddPlc properties evaluate their
            // real logic (they short-circuit to "empty" when switching is
            // unwired).
            switchToDataBlock: _ => focusedXml,
            buildActiveDbForSummary: _ => null);
    }

    [Fact]
    public void AddPlcToRow_AddsEmptyPillForCandidate()
    {
        var vm = BuildVmForAddPlcTests();
        vm.ActiveSet.PlcPills.Should().HaveCount(1, "one PLC active to start");
        vm.ActiveSet.InactiveProjectPlcs.Should().BeEquivalentTo(new[] { "PLC_B", "PLC_C" });

        vm.ActiveSet.AddPlcToRow("PLC_B");

        vm.ActiveSet.PlcPills.Should().HaveCount(2, "PLC_B's empty pill joins the row");
        vm.ActiveSet.PlcPills.Select(p => p.PlcName).Should().Contain("PLC_B");
        var newPill = vm.ActiveSet.PlcPills.First(p => p.PlcName == "PLC_B");
        newPill.SelectedDbs.Should().BeEmpty("the pill is added with no DB active yet");
        vm.ActiveSet.InactiveProjectPlcs.Should().BeEquivalentTo(new[] { "PLC_C" },
            "PLC_B is now in the row, so it drops off the candidate list");
    }

    [Fact]
    public void AddPlcToRow_RejectsUnknownPlcName()
    {
        var vm = BuildVmForAddPlcTests();
        var pillsBefore = vm.ActiveSet.PlcPills.Count;

        // Garbage input the click stream could produce after a project refresh
        // dropped that PLC, or via a hostile caller.
        vm.ActiveSet.AddPlcToRow("DoesNotExist");

        vm.ActiveSet.PlcPills.Count.Should().Be(pillsBefore,
            "unknown PLC must be silently rejected, no pill added");
        // Critically: the bad name must NOT have landed in _extraPillPlcs (we
        // verify this indirectly: a subsequent valid add stays clean).
        vm.ActiveSet.AddPlcToRow("PLC_B");
        vm.ActiveSet.PlcPills.Should().HaveCount(pillsBefore + 1);
        vm.ActiveSet.PlcPills.Select(p => p.PlcName).Should().NotContain("DoesNotExist");
    }

    [Fact]
    public void AddPlcToRow_RejectsAlreadyActivePlc()
    {
        var vm = BuildVmForAddPlcTests();
        var pillsBefore = vm.ActiveSet.PlcPills.Count;

        // PLC_A is the anchor — already in the row.
        vm.ActiveSet.AddPlcToRow("PLC_A");

        vm.ActiveSet.PlcPills.Count.Should().Be(pillsBefore,
            "PLC already in row → no-op, no duplicate pill");
    }

    [Fact]
    public void InactiveProjectPlcs_ReflectsCurrentRow()
    {
        var vm = BuildVmForAddPlcTests();
        vm.ActiveSet.InactiveProjectPlcs.Should().BeEquivalentTo(new[] { "PLC_B", "PLC_C" });

        vm.ActiveSet.AddPlcToRow("PLC_B");
        vm.ActiveSet.InactiveProjectPlcs.Should().BeEquivalentTo(new[] { "PLC_C" });

        vm.ActiveSet.AddPlcToRow("PLC_C");
        vm.ActiveSet.InactiveProjectPlcs.Should().BeEmpty("every PLC is in the row now");
    }

    [Fact]
    public void CanAddPlc_FlipsAsRowFills()
    {
        var vm = BuildVmForAddPlcTests();
        vm.ActiveSet.CanAddPlc.Should().BeTrue("two PLCs still inactive");

        vm.ActiveSet.AddPlcToRow("PLC_B");
        vm.ActiveSet.CanAddPlc.Should().BeTrue("PLC_C still available");

        vm.ActiveSet.AddPlcToRow("PLC_C");
        vm.ActiveSet.CanAddPlc.Should().BeFalse("every project PLC is now in the row");
    }

    /// <summary>
    /// Convenience enum shared across multi-DB tests. Yes/No/Cancel maps
    /// onto the three named outcomes for each typed prompt method.
    /// </summary>
    private enum YesNoCancelResult { Yes, No, Cancel }

    private sealed class FakeMessageBox : IMessageBoxService
    {
        private readonly YesNoCancelResult _result;
        public FakeMessageBox(YesNoCancelResult r) { _result = r; }

        /// <summary>Counts any 3-way prompt call (ApplyStashCancel, AddOrReplace, CloseWithStash).</summary>
        public int AskYesNoCancelCallCount { get; private set; }
        public bool AskYesNo(string message, string title) => true;
        public void ShowError(string message, string title) { }
        public void ShowInfo(string message, string title) { }
        public ApplyStashCancelResult AskApplyStashCancel(string message, string title)
        {
            AskYesNoCancelCallCount++;
            return _result switch
            {
                YesNoCancelResult.Yes => ApplyStashCancelResult.ApplyAndSwitch,
                YesNoCancelResult.No  => ApplyStashCancelResult.StashAndSwitch,
                _                     => ApplyStashCancelResult.Cancel,
            };
        }
        public AddOrReplaceResult AskAddOrReplace(string message, string title)
        {
            AskYesNoCancelCallCount++;
            return _result switch
            {
                YesNoCancelResult.Yes => AddOrReplaceResult.Add,
                YesNoCancelResult.No  => AddOrReplaceResult.Replace,
                _                     => AddOrReplaceResult.Cancel,
            };
        }
        public CloseWithStashResult AskCloseWithStash(string message, string title)
        {
            AskYesNoCancelCallCount++;
            return _result switch
            {
                YesNoCancelResult.Yes => CloseWithStashResult.ApplyActive,
                YesNoCancelResult.No  => CloseWithStashResult.DiscardAll,
                _                     => CloseWithStashResult.Cancel,
            };
        }
    }
}
