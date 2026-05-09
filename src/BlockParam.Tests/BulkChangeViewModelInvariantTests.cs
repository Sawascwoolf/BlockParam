using System.Linq;
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
/// Phase 4 of #78 — locks in current behavior of the active-DB mutation
/// surface before the snapshot refactor lands. Each test pairs a single
/// mutation gesture with the prompt outcome and asserts both the targeted
/// post-state and the cross-cutting <see cref="AssertInvariants"/> set, so
/// any regression in a downstream derived collection (chips, chip-groups,
/// stashed-DBs, RootMembers shape, anchor PLC display) trips the relevant
/// row of the matrix instead of leaking out as a "user noticed in TIA"
/// regression.
///
/// Invariants asserted after every transition:
///   1. <c>AllActiveDbs.Count &gt;= 1</c>;
///   2. <c>ActiveDbChips.Count == AllActiveDbs.Count</c>; PLC grouping matches;
///   3. <c>RootMembers</c> shape matches active-set count
///      (single-DB → flat top-level; multi-DB → exactly N synthetic
///      <c>Datatype="DB"</c> roots, one per active DB);
///   4. <c>HasStashedDbs == (StashedDbs.Count &gt; 0)</c>;
///   5. anchor's chip group's PlcName == <c>CurrentPlcName</c>;
///   6. every <c>PendingEdits</c> entry's <c>Node</c> is reachable from
///      the live <c>RootMembers</c> tree — i.e. removing a DB from the
///      active set must vacate that DB's leaves from the bound list;
///   7. <c>BulkPreview</c> is empty whenever no target is staged
///      (<c>!HasScope &amp;&amp; !IsManualMode</c>) — captures the §B bug
///      symptom "Bulk Preview still shows preview computed from the prior
///      selection" after the selection got cleared by a transition;
///   8. every <c>BulkPreview</c> entry's <c>Node</c> and every
///      <c>ManualSelectedPaths</c> node is reachable from
///      <c>RootMembers</c> — same shape as invariant 6, captures §H3
///      ("removing DB A must clear its manual selections / preview rows");
///   9. <c>NewValue</c> is empty whenever no member is selected and not
///      in manual mode — captures §B "New Value field still populated
///      with the value that was active before the messagebox".
/// </summary>
public class BulkChangeViewModelInvariantTests
{
    // ───────────────────────── transition matrix ─────────────────────────

    [Fact]
    public void Add_DropdownCheck_MultiPlc_TargetOnDifferentPlc_BuildsMultiDbTree()
    {
        // Row 1: 1 active anchor on PLC_A, dropdown row for a peer on PLC_B.
        // Toggling the row to IsActive=true must (a) add it to the active set,
        // (b) split chip groups by PLC, (c) rebuild the tree to multi-DB shape.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml", plc: "PLC_A")
            .WithDropdownPeer("nested-struct-db.xml", plc: "PLC_B")
            .Build();

        env.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        var peerRow = env.Vm.FilteredDataBlockItems.First(i => i.Name == "NestedStructDB");
        peerRow.IsActive = true;

        env.Vm.AllActiveDbs.Should().HaveCount(2);
        env.Vm.ActiveDbChipGroups.Select(g => g.PlcName).Should().BeEquivalentTo(
            new[] { "PLC_A", "PLC_B" });
        env.Vm.ActiveDbChipGroups.Should().AllSatisfy(g =>
            g.HasPlcHeader.Should().BeTrue("two distinct PLCs disambiguate via header"));
        env.Vm.RootMembers.Should().HaveCount(2);
        env.Vm.RootMembers.Should().AllSatisfy(r => r.Datatype.Should().Be("DB"));
        env.Mbx.AskYesNoCancelCallCount.Should().Be(0, "pure-add does not prompt");
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Remove_DropdownUncheck_TargetHasEdits_PromptStash_CreatesStashEntry()
    {
        // Row 3: two active DBs, companion has pending edits. Unchecking the
        // companion row prompts; user picks "Stash" (No). Companion drops out
        // of the active set, edits land in the stash dictionary keyed by
        // (PlcName, FolderPath, Name).
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithCompanion("nested-struct-db.xml")
            .WithPendingEditsOn("NestedStructDB", count: 1)
            .WithPromptResults(YesNoCancelResult.No)
            .Build();

        env.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        var companionRow = env.Vm.FilteredDataBlockItems.First(i => i.Name == "NestedStructDB");
        companionRow.IsActive = false;

        env.Vm.AllActiveDbs.Should().HaveCount(1);
        env.Vm.AllActiveDbs[0].Info.Name.Should().Be("FlatDB");
        env.Vm.HasStashedDbs.Should().BeTrue();
        env.Vm.StashedDbs.Should().ContainSingle(s => s.DbName == "NestedStructDB");
        env.Mbx.AskYesNoCancelCallCount.Should().Be(1);
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Remove_ChipClose_LastRemainingDb_Refused_NoError()
    {
        // Row 4: single-DB session — chip × is bound disabled (CanClose=false).
        // The legitimate gesture cannot reach RemoveActiveDb. Verify the chip
        // exposes that disabled state and that no message-box prompts fire.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .Build();

        env.Vm.ActiveDbChips.Should().HaveCount(1);
        env.Vm.ActiveDbChips[0].CanClose.Should().BeFalse(
            "the dialog must always have at least one DB");
        env.Vm.ActiveDbChips[0].CloseCommand.CanExecute(null).Should().BeFalse();
        env.Vm.AllActiveDbs.Should().HaveCount(1);
        env.Mbx.AskYesNoCancelCallCount.Should().Be(0);
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Remove_ChipClose_TargetHasEdits_PromptCancel_LeavesEverythingPending()
    {
        // Row 5: two active DBs, target has edits, user cancels the prompt.
        // The active set is unchanged; the pending edit is still pending; no
        // stash entry was created. Cancel must be inert.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithCompanion("nested-struct-db.xml")
            .WithPendingEditsOn("NestedStructDB", count: 1)
            .WithPromptResults(YesNoCancelResult.Cancel)
            .Build();

        var companionLeaf = FindFirstPendingLeaf(env.Vm, "NestedStructDB");
        var pendingValue = companionLeaf.PendingValue;

        var companionChip = env.Vm.ActiveDbChips.First(c => c.DisplayName == "NestedStructDB");
        companionChip.CloseCommand.Execute(null);

        env.Vm.AllActiveDbs.Should().HaveCount(2, "Cancel keeps the companion in place");
        env.Vm.HasStashedDbs.Should().BeFalse("Cancel does not stash");
        companionLeaf.PendingValue.Should().Be(pendingValue, "edit was not lost");
        companionLeaf.IsPendingInlineEdit.Should().BeTrue();
        env.Mbx.AskYesNoCancelCallCount.Should().Be(1);
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Solo_3Active_TwoOthersHaveEdits_PromptApplyApply_BothCommittedOnce()
    {
        // Row 6: 3 active DBs. The two non-target DBs each have one pending
        // edit. User clicks the target chip's body to solo; both prompts get
        // "Yes" (Apply). Each non-target DB's OnApply fires exactly once and
        // it's removed from the active set. The target stays.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithCompanion("nested-struct-db.xml")
            .WithCompanion("array-db.xml")
            .WithPendingEditsOn("FlatDB", count: 1)
            .WithPendingEditsOn("NestedStructDB", count: 1)
            .WithPromptResults(YesNoCancelResult.Yes, YesNoCancelResult.Yes)
            .Build();

        var targetChip = env.Vm.ActiveDbChips.First(c => c.DisplayName == "ArrayDB");
        targetChip.SoloCommand.Execute(null);

        env.Vm.AllActiveDbs.Should().HaveCount(1);
        env.Vm.AllActiveDbs[0].Info.Name.Should().Be("ArrayDB");
        env.AppliedOrder.Should().BeEquivalentTo(
            new[] { "FlatDB", "NestedStructDB" },
            o => o.WithoutStrictOrdering(),
            "every non-target DB with pending edits committed exactly once");
        env.Mbx.AskYesNoCancelCallCount.Should().Be(2);
        env.Vm.HasStashedDbs.Should().BeFalse("Apply branch does not stash");
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Solo_3Active_MiddleHasEdits_PromptCancel_LeavesPartialSet()
    {
        // Row 7: 3 active DBs, only the middle one has pending edits. Solo
        // walks the others in order, removing the first silently (no edits =
        // no prompt), then prompting for the middle. User picks Cancel: the
        // remove-loop bails. Active set is {target, middle}.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")              // no edits — silent remove
            .WithCompanion("nested-struct-db.xml")  // has edits — prompt fires
            .WithCompanion("array-db.xml")          // target — stays
            .WithPendingEditsOn("NestedStructDB", count: 1)
            .WithPromptResults(YesNoCancelResult.Cancel)
            .Build();

        var targetChip = env.Vm.ActiveDbChips.First(c => c.DisplayName == "ArrayDB");
        targetChip.SoloCommand.Execute(null);

        env.Vm.AllActiveDbs.Select(d => d.Info.Name).Should().BeEquivalentTo(
            new[] { "NestedStructDB", "ArrayDB" },
            "Cancel on the middle DB aborts the rest of the solo loop");
        env.Vm.HasStashedDbs.Should().BeFalse();
        env.Mbx.AskYesNoCancelCallCount.Should().Be(1);
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Reactivate_StashHeader_OneOtherActiveNoEdits_SoloesAndRestores()
    {
        // Row 8 (simplified to one other for fixture economy): a stashed DB
        // exists and one unrelated DB is active with no edits. Clicking the
        // stash header re-adds the stashed DB, soloes (silently drops the
        // other since no edits → no prompt), and restores the stash's edits
        // onto the new live tree. The stash entry pops.
        //
        // We bootstrap the stash by closing a companion's chip with edits
        // pending and the message-box returning Stash (No), then asserting
        // the stash exists, *then* exercising the reactivate gesture.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithCompanion("nested-struct-db.xml")
            .WithPendingEditsOn("NestedStructDB", count: 1)
            // First prompt = stash on close; second prompt would fire if the
            // anchor had edits during reactivate (it doesn't, so unused).
            .WithPromptResults(YesNoCancelResult.No)
            .Build();

        var pendingLeaf = FindFirstPendingLeaf(env.Vm, "NestedStructDB");
        var pendingValue = pendingLeaf.PendingValue!;

        // Close the companion to create the stash.
        env.Vm.ActiveDbChips.First(c => c.DisplayName == "NestedStructDB")
            .CloseCommand.Execute(null);
        env.Vm.HasStashedDbs.Should().BeTrue("setup: stash created via chip-close");

        // Reactivate via the stash header.
        var stash = env.Vm.StashedDbs.Single();
        env.Vm.SwitchToStashedDbCommand.Execute(stash);

        env.Vm.AllActiveDbs.Should().HaveCount(1, "solo collapses to just the reactivated DB");
        env.Vm.AllActiveDbs[0].Info.Name.Should().Be("NestedStructDB");
        env.Vm.HasStashedDbs.Should().BeFalse("RestoreStashFor pops the entry");

        // The stashed pending value made it back onto a live leaf.
        var restored = env.Vm.RootMembers
            .SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .FirstOrDefault(n => n.IsLeaf && n.PendingValue == pendingValue);
        restored.Should().NotBeNull("stash edits must replay onto the rebuilt tree");
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Reactivate_StashHeader_OtherActiveHasEdits_PromptStash_TwoStashEntries()
    {
        // Row 9 — verifies #78 Phase 1's WalkDbTopLevels fix: the helpers
        // that gate the 3-way prompt (CountPendingEditsForDb) and stash
        // capture (StashPendingEditsForDb) now fall back to RootMembers
        // when the live tree is in single-DB shape, so the reactivate-then-
        // solo walk correctly prompts on the still-anchored DB's pending
        // edits instead of silently dropping them.
        //
        // Scenario: stash A exists; while reactivating A, the still-active
        // anchor has edits. The reactivate gesture's solo step prompts and
        // the user picks Stash (No). End state: only A active, exactly one
        // stash entry — A's stash popped on restore, anchor's was just
        // created.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml", plc: "PLC_A")
            .WithCompanion("nested-struct-db.xml", plc: "PLC_A")
            .WithPendingEditsOn("NestedStructDB", count: 1)
            .WithPromptResults(
                YesNoCancelResult.No,   // stash NestedStructDB on chip-close
                YesNoCancelResult.No)   // stash FlatDB during reactivate's solo
            .Build();

        // Setup: stash NestedStructDB.
        env.Vm.ActiveDbChips.First(c => c.DisplayName == "NestedStructDB")
            .CloseCommand.Execute(null);
        env.Vm.HasStashedDbs.Should().BeTrue();
        env.Vm.StashedDbs.Should().ContainSingle(s => s.DbName == "NestedStructDB");

        // Stage a pending edit on FlatDB (the still-active anchor) after the
        // companion drop, i.e. while we're in single-DB shape.
        var anchorLeaf = env.Vm.RootMembers.SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .First(n => n.IsLeaf && !string.IsNullOrEmpty(n.StartValue));
        anchorLeaf.PendingValue = anchorLeaf.StartValue == "0" ? "1" : "0";

        // Reactivate the stashed companion via header click.
        var promptsBefore = env.Mbx.AskYesNoCancelCallCount;
        var stash = env.Vm.StashedDbs.Single();
        env.Vm.SwitchToStashedDbCommand.Execute(stash);

        env.Vm.AllActiveDbs.Should().HaveCount(1);
        env.Vm.AllActiveDbs[0].Info.Name.Should().Be("NestedStructDB");
        env.Vm.HasStashedDbs.Should().BeTrue("anchor's edits stashed during reactivate");
        env.Vm.StashedDbs.Should().ContainSingle(s => s.DbName == "FlatDB",
            "exactly one stash remains — NestedStructDB's popped on restore, FlatDB's was just created");
        (env.Mbx.AskYesNoCancelCallCount - promptsBefore).Should().Be(1,
            "reactivate's solo step must prompt for the anchor's pending edits");
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void DropdownRow_ToggleToAdd_AnchorHasEdits_PromptCancel_LeavesActiveSetUnchanged()
    {
        // Row 11 — captures observed bug on PR #74 §B repro:
        // Single-DB session (anchor A with pending edits). User opens the
        // switcher dropdown and toggles peer B's IsActive=true. A 3-way
        // prompt fires for A's pending edits. User picks Cancel.
        //
        // Expected: active set stays [A]; B is NOT added; tree shape and
        // anchor's pending edit are byte-identical to the pre-toggle state.
        // Reported behaviour: active set ends up [A, B] — the dropdown
        // toggle mutates _activeDbs before the prompt resolves and Cancel
        // doesn't roll the mutation back.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithDropdownPeer("nested-struct-db.xml")
            .WithPendingEditsOn("FlatDB", count: 1)
            .WithPromptResults(YesNoCancelResult.Cancel)
            .Build();

        var anchorLeaf = FindFirstPendingLeaf(env.Vm, "FlatDB");
        var pendingValue = anchorLeaf.PendingValue;

        env.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        var peerRow = env.Vm.FilteredDataBlockItems.First(i => i.Name == "NestedStructDB");
        peerRow.IsActive = true;

        env.Vm.AllActiveDbs.Should().HaveCount(1, "Cancel must leave the active set untouched");
        env.Vm.AllActiveDbs[0].Info.Name.Should().Be("FlatDB");
        env.Vm.HasStashedDbs.Should().BeFalse("Cancel does not stash");
        anchorLeaf.PendingValue.Should().Be(pendingValue, "anchor edit survives Cancel");
        anchorLeaf.IsPendingInlineEdit.Should().BeTrue();
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void DropdownCancel_ThenChipCloseAnchor_PromptKeep_StashesAnchorAndClearsPendingList()
    {
        // Row 12 — captures observed bug on PR #74 §B, stacked on top of
        // Row 11's bug-1 state.
        //
        // Sequence (matches the user-reported repro):
        //   1. Single-DB session: anchor A (FlatDB) with 1 pending edit.
        //   2. Open dropdown, toggle peer B (NestedStructDB) IsActive=true.
        //      3-way prompt fires; user picks Cancel.
        //      → Per Row 11, the active set ends up [A, B] (bug 1).
        //   3. User then chip-×'s A. 3-way prompt fires; user picks Keep
        //      (No / Stash).
        //
        // Expected: A leaves the active set, A's pending edit moves into
        // StashedDbs, and PendingEdits drops every entry that referenced A.
        //
        // Observed VM-layer behaviour: A leaves the active set (good), but
        // its pending edits are SILENTLY DROPPED — not migrated to the
        // stash (HasStashedDbs == False) and not retained anywhere. The
        // user-visible "pending edits still showing" is then a downstream
        // UI binding that holds a stale snapshot of the inspector list;
        // the VM-layer data-loss is the root cause.
        //
        // Note: when bug 1 is fixed (Cancel leaves the active set as [A]),
        // step 3 becomes a chip-× on the only active DB, which is disabled
        // (Row 4). At that point this test should be re-evaluated — the
        // bug it captures may disappear as a consequence of fixing #1.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithDropdownPeer("nested-struct-db.xml")
            .WithPendingEditsOn("FlatDB", count: 1)
            .WithPromptResults(
                YesNoCancelResult.Cancel,   // Cancel the switch / add prompt
                YesNoCancelResult.No)       // Then Keep on chip-close
            .Build();

        var anchorLeaf = FindFirstPendingLeaf(env.Vm, "FlatDB");
        var pendingValue = anchorLeaf.PendingValue;

        // Step 2 — dropdown toggle + Cancel (bug 1 trigger).
        env.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        var peerRow = env.Vm.FilteredDataBlockItems.First(i => i.Name == "NestedStructDB");
        peerRow.IsActive = true;

        // Step 3 — chip-× A. The state is corrupted from step 2 ([A, B]
        // instead of [A]); A is still the anchor in this corrupted set.
        var anchorChip = env.Vm.ActiveDbChips.First(c => c.DisplayName == "FlatDB");
        anchorChip.CloseCommand.Execute(null);

        env.Vm.AllActiveDbs.Should().ContainSingle()
            .Which.Info.Name.Should().Be("NestedStructDB",
                "Keep on chip-close removes A from the active set");
        // The data-loss assertion: A's edits must end up SOMEWHERE — either
        // in the stash (Keep) or applied (would not be Keep). Currently
        // they're dropped on the floor.
        env.Vm.HasStashedDbs.Should().BeTrue("Keep moves pending edits into the stash");
        env.Vm.StashedDbs.Should().ContainSingle(s => s.DbName == "FlatDB",
            "A's pending edit must land in the stash dictionary, not be silently dropped");
        env.Vm.PendingEdits.Should().BeEmpty(
            "removed DB's pending leaves must vacate the bound PendingEdits list");
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Remove_ChipCloseAnchor_CrossPlc_PeerBecomesAnchor_PlcDisplayRotates()
    {
        // Row 14 — §H2 + §I: chip-× the anchor in a 2-DB cross-PLC session
        // with no edits. Three things must rotate atomically:
        //   - AllActiveDbs[0] becomes the surviving peer
        //   - CurrentDataBlockName tracks _activeDbs[0]
        //   - CurrentPlcName flips PLC_A → PLC_B (the §I checklist item)
        //
        // The cross-PLC angle matters because TryComputeRemove (~line 1665)
        // only rewrites AnchorPlcName when the removed DB *was* the anchor;
        // a regression that drops that special-case would leave the dialog
        // title showing PLC_A even after the anchor moved to PLC_B's DB.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml", plc: "PLC_A")
            .WithCompanion("nested-struct-db.xml", plc: "PLC_B")
            .Build();

        env.Vm.AllActiveDbs[0].Info.Name.Should().Be("FlatDB", "setup: anchor is FlatDB");
        env.Vm.CurrentPlcName.Should().Be("PLC_A", "setup: anchor PLC display is PLC_A");

        var anchorChip = env.Vm.ActiveDbChips.First(c => c.DisplayName == "FlatDB");
        anchorChip.CanClose.Should().BeTrue("2-DB session — anchor is removable");
        anchorChip.CloseCommand.Execute(null);

        env.Vm.AllActiveDbs.Should().HaveCount(1);
        env.Vm.AllActiveDbs[0].Info.Name.Should().Be("NestedStructDB",
            "the surviving peer rotates into the anchor slot");
        env.Vm.CurrentDataBlockName.Should().Be("NestedStructDB",
            "CurrentDataBlockName tracks _activeDbs[0]");
        env.Vm.CurrentPlcName.Should().Be("PLC_B",
            "anchor PLC display follows the new anchor (TryComputeRemove " +
            "rewrites AnchorPlcName; State setter propagates to _currentPlcName)");
        env.Mbx.AskYesNoCancelCallCount.Should().Be(0, "no edits → no prompt");
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Remove_ChipClose_AnchorHadManualSelection_VacatedAndPeerSelectionStartsClean()
    {
        // Row 15 — §H3 (full scenario): anchor A has 2 manually-selected
        // leaves; user chip-×s A; user then manually-selects 2 leaves in DB B.
        // Both halves of the §H3 checklist:
        //   1. Removing A drops its manual paths.
        //   2. Then selecting 2 in B leaves ManualSelectionCount == 2,
        //      not 4 — A's old selections do NOT contribute.
        //
        // Without the second half, a regression that "kept" A's paths but
        // hid them from invariant 8b (e.g. via a "soft remove" flag)
        // wouldn't trip until the user noticed the count was off.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithCompanion("nested-struct-db.xml")
            .Build();

        // Multi-DB shape: pick 2 leaves from FlatDB's synthetic subtree.
        var anchorRoot = env.Vm.RootMembers.First(r => r.Name == "FlatDB");
        var anchorLeaves = new[] { anchorRoot }
            .Concat(anchorRoot.AllDescendants())
            .Where(n => n.IsLeaf)
            .Take(2)
            .ToList();
        anchorLeaves.Should().HaveCount(2, "fixture should provide ≥ 2 leaves on FlatDB");

        env.Vm.UpdateManualSelection(
            added: anchorLeaves,
            removed: System.Array.Empty<MemberNodeViewModel>(),
            isFilterRehydration: false);
        env.Vm.IsManualMode.Should().BeTrue("setup: 2 leaves selected → manual mode");
        env.Vm.ManualSelectionCount.Should().Be(2);

        var anchorChip = env.Vm.ActiveDbChips.First(c => c.DisplayName == "FlatDB");
        anchorChip.CloseCommand.Execute(null);

        env.Vm.AllActiveDbs.Should().HaveCount(1);
        env.Vm.AllActiveDbs[0].Info.Name.Should().Be("NestedStructDB");
        env.Vm.ManualSelectedPaths.Should().BeEmpty(
            "removing the anchor must drop its manually-selected leaves " +
            "(RebuildAfterActiveSetChanged line 1449 _manualSelectedPaths.Clear)");
        env.Vm.IsManualMode.Should().BeFalse("0 manual paths → not in manual mode");

        // Now the second half: select 2 leaves in the survivor (now flat tree).
        var peerLeaves = env.Vm.RootMembers
            .SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .Where(n => n.IsLeaf)
            .Take(2)
            .ToList();
        peerLeaves.Should().HaveCount(2, "single-DB tree should still have ≥ 2 leaves");
        env.Vm.UpdateManualSelection(
            added: peerLeaves,
            removed: System.Array.Empty<MemberNodeViewModel>(),
            isFilterRehydration: false);

        env.Vm.IsManualMode.Should().BeTrue("now 2 leaves selected in B");
        env.Vm.ManualSelectionCount.Should().Be(2,
            "B's count is 2, not 4 — A's old selections did not bleed back in");
        env.Vm.ManualSelectedPaths.Should().BeEquivalentTo(peerLeaves,
            "ManualSelectedPaths holds B's nodes only — none of A's nodes survived");
        env.Mbx.AskYesNoCancelCallCount.Should().Be(0, "no pending edits → no prompt");
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Switch_LegacyApi_OneActiveWithEdits_PromptStash_OriginalStashedRestoredOnReturn()
    {
        // Row 10: legacy SwitchToDataBlock — single-DB session, target = a
        // different DB. The current DB has pending edits. User picks Stash
        // (No). Switch lands on target; the original is in the stash. Switch
        // back to the original: stash pops and edits are restored onto the
        // re-loaded tree.
        var flatXml = TestFixtures.LoadXml("flat-db.xml");
        var nestedXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var flat = new SimaticMLParser().Parse(flatXml);
        var nested = new SimaticMLParser().Parse(nestedXml);

        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithDropdownPeer("nested-struct-db.xml")
            .WithPromptResults(
                YesNoCancelResult.No,   // stash FlatDB before switch
                YesNoCancelResult.No)   // stash NestedStructDB on switch back
            .Build();

        // Stage one edit on FlatDB.
        var flatLeaf = env.Vm.RootMembers.SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .First(n => n.IsLeaf && !string.IsNullOrEmpty(n.StartValue));
        var pendingValue = flatLeaf.StartValue == "0" ? "1" : "0";
        flatLeaf.PendingValue = pendingValue;
        var pendingPath = flatLeaf.Path;

        // Switch away to NestedStructDB.
        var nestedSummary = new DataBlockSummary("NestedStructDB", "");
        env.Vm.SwitchToDataBlock(nestedSummary).Should().BeTrue();

        env.Vm.AllActiveDbs[0].Info.Name.Should().Be("NestedStructDB");
        env.Vm.HasStashedDbs.Should().BeTrue("FlatDB stashed on switch out");
        env.Vm.StashedDbs.Should().ContainSingle(s => s.DbName == "FlatDB");

        // Stage an unrelated edit on NestedStructDB so the return-switch also
        // exercises the stash branch (verifies the legacy path's symmetry).
        var nestedLeaf = env.Vm.RootMembers.SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .First(n => n.IsLeaf && !string.IsNullOrEmpty(n.StartValue));
        nestedLeaf.PendingValue = nestedLeaf.StartValue == "0" ? "1" : "0";

        // Switch back to FlatDB.
        var flatSummary = new DataBlockSummary("FlatDB", "");
        env.Vm.SwitchToDataBlock(flatSummary).Should().BeTrue();

        env.Vm.AllActiveDbs[0].Info.Name.Should().Be("FlatDB");
        env.Vm.HasStashedDbs.Should().BeTrue("NestedStructDB now stashed");
        env.Vm.StashedDbs.Should().ContainSingle(s => s.DbName == "NestedStructDB");

        // Verify the original FlatDB edit was restored.
        var restored = env.Vm.RootMembers.SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .FirstOrDefault(n => n.IsLeaf && n.Path == pendingPath);
        restored.Should().NotBeNull();
        restored!.PendingValue.Should().Be(pendingValue);
        AssertInvariants(env.Vm);
    }

    // ───────────────────────── invariant assertion ─────────────────────────

    /// <summary>
    /// Five-property cross-cut applied after every transition. A test that
    /// fails here (without failing its targeted assertion) means the
    /// derived collections drifted from <c>AllActiveDbs</c> — exactly the
    /// bug class #78's snapshot refactor exists to prevent.
    /// </summary>
    private static void AssertInvariants(BulkChangeViewModel vm)
    {
        // (1) at least one DB always active
        vm.AllActiveDbs.Should().NotBeEmpty("invariant 1: AllActiveDbs.Count >= 1");

        // (2) chips count and PLC grouping match active set
        vm.ActiveDbChips.Should().HaveCount(vm.AllActiveDbs.Count,
            "invariant 2a: ActiveDbChips.Count == AllActiveDbs.Count");
        var chipsByName = vm.ActiveDbChips.Select(c => c.DisplayName).ToList();
        var dbsByName = vm.AllActiveDbs.Select(d => d.Info.Name).ToList();
        chipsByName.Should().BeEquivalentTo(dbsByName,
            "invariant 2b: every active DB has a corresponding chip");
        var groupedChipNames = vm.ActiveDbChipGroups.SelectMany(g => g.Chips).Select(c => c.DisplayName).ToList();
        groupedChipNames.Should().BeEquivalentTo(chipsByName,
            "invariant 2c: PLC grouping covers every chip exactly once");

        // (3) tree shape matches active-set count
        if (vm.AllActiveDbs.Count == 1)
        {
            vm.RootMembers.Should().AllSatisfy(r => r.Datatype.Should().NotBe("DB"),
                "invariant 3 (single-DB): top-level members are flat, no synthetic group");
        }
        else
        {
            vm.RootMembers.Should().HaveCount(vm.AllActiveDbs.Count,
                "invariant 3 (multi-DB): one synthetic root per active DB");
            vm.RootMembers.Should().AllSatisfy(r => r.Datatype.Should().Be("DB",
                "invariant 3 (multi-DB): every root is a synthetic Datatype='DB' group"));
        }

        // (4) stash-flag matches stash list
        vm.HasStashedDbs.Should().Be(vm.StashedDbs.Count > 0,
            "invariant 4: HasStashedDbs == (StashedDbs.Count > 0)");

        // (5) anchor PLC display matches the anchor's chip group
        if (vm.ActiveDbChipGroups.Count > 0)
        {
            var anchorName = vm.AllActiveDbs[0].Info.Name;
            var anchorGroup = vm.ActiveDbChipGroups
                .First(g => g.Chips.Any(c => c.DisplayName == anchorName));
            anchorGroup.PlcName.Should().Be(vm.CurrentPlcName,
                "invariant 5: anchor's chip-group PLC name == CurrentPlcName");
        }

        // (6) PendingEdits only references nodes still reachable in the live
        // tree — removing a DB from the active set must vacate that DB's
        // leaves from the bound "pending changes" inspector list.
        var reachableNodes = vm.RootMembers
            .SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .ToHashSet();
        var orphanedPaths = vm.PendingEdits
            .Where(e => !reachableNodes.Contains(e.Node))
            .Select(e => e.Path)
            .ToList();
        orphanedPaths.Should().BeEmpty(
            "invariant 6: PendingEdits drops nodes whose DB left the active set");

        // (7) BulkPreview is empty when there is no staged target (no scope
        // selected and not in manual mode). Captures §B bug: the prior
        // selection's preview lingered after the 3-way Cancel cleared the
        // selection. ComputeBulkPreview's hasInput check enforces this; if
        // a transition path forgets to call it, the stale rows survive.
        if (!vm.HasScope && !vm.IsManualMode)
        {
            vm.BulkPreview.Should().BeEmpty(
                "invariant 7: BulkPreview must be empty when no scope is " +
                "selected and not in manual mode");
        }

        // (8) BulkPreview entries and ManualSelectedPaths nodes are reachable
        // from the live tree — same shape as invariant 6 for PendingEdits.
        // Removing a DB from the active set must vacate any preview rows or
        // manual-selection paths that pointed at its leaves (§H3).
        var orphanedPreview = vm.BulkPreview
            .Where(e => !reachableNodes.Contains(e.Node))
            .Select(e => e.Path)
            .ToList();
        orphanedPreview.Should().BeEmpty(
            "invariant 8a: BulkPreview drops nodes whose DB left the active set");
        var orphanedManual = vm.ManualSelectedPaths
            .Where(n => !reachableNodes.Contains(n))
            .Select(n => n.Path)
            .ToList();
        orphanedManual.Should().BeEmpty(
            "invariant 8b: ManualSelectedPaths drops nodes whose DB left the active set");

        // (9) NewValue is cleared when no member is selected and not in
        // manual mode. Captures §B bug: the field was still showing the
        // value typed before the 3-way prompt fired even though the
        // selection got cleared. OnMemberSelected(null) clears _newValue
        // — any new path that lands the dialog in "no selection" without
        // routing through OnMemberSelected leaves the bug in.
        //
        // Note: this guards the VM-state side of the §B symptom only. If
        // the field is still rendered with a stale value while the VM
        // reports NewValue=="", the bug is downstream (XAML binding /
        // dialog code-behind), not VM state — needs a manual / WPF-level
        // walkthrough to catch.
        if (!vm.HasSelection && !vm.IsManualMode)
        {
            vm.NewValue.Should().BeEmpty(
                "invariant 9: NewValue must be empty when nothing is " +
                "selected and not in manual mode");
        }
    }

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>
    /// First leaf inside the named DB's synthetic subtree (or the flat tree,
    /// if single-DB) with a pending value. Use after the builder staged
    /// edits via <see cref="ActiveSetTestBuilder.WithPendingEditsOn"/>.
    /// </summary>
    private static MemberNodeViewModel FindFirstPendingLeaf(BulkChangeViewModel vm, string dbName)
    {
        var roots = vm.RootMembers.Count > 0 && vm.RootMembers[0].Datatype == "DB"
            ? vm.RootMembers.Where(r => r.Name == dbName)
            : vm.RootMembers;
        return roots.SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .First(n => n.IsLeaf && n.IsPendingInlineEdit);
    }

    // ───────────────────────── builder + fakes ─────────────────────────

    private sealed class ActiveSetTestBuilder
    {
        private readonly List<DbSpec> _activeDbs = new();
        private readonly List<DbSpec> _dropdownPeers = new();
        private readonly Dictionary<string, int> _pendingEditCounts = new();
        private readonly Queue<YesNoCancelResult> _promptResults = new();
        private string _anchorPlc = "";

        public ActiveSetTestBuilder WithAnchor(string fixture, string plc = "")
        {
            if (_activeDbs.Count > 0) throw new System.InvalidOperationException("anchor already set");
            _anchorPlc = plc;
            _activeDbs.Add(new DbSpec(fixture, plc, IsAnchor: true));
            return this;
        }

        public ActiveSetTestBuilder WithCompanion(string fixture, string plc = "")
        {
            _activeDbs.Add(new DbSpec(fixture, plc, IsAnchor: false));
            return this;
        }

        /// <summary>
        /// Adds the fixture to the dropdown's enumerator without putting it in
        /// the active set, so the test can simulate the user toggling its row.
        /// </summary>
        public ActiveSetTestBuilder WithDropdownPeer(string fixture, string plc = "")
        {
            _dropdownPeers.Add(new DbSpec(fixture, plc, IsAnchor: false));
            return this;
        }

        public ActiveSetTestBuilder WithPendingEditsOn(string dbName, int count = 1)
        {
            _pendingEditCounts[dbName] = count;
            return this;
        }

        public ActiveSetTestBuilder WithPromptResults(params YesNoCancelResult[] results)
        {
            foreach (var r in results) _promptResults.Enqueue(r);
            return this;
        }

        public TestEnv Build()
        {
            if (_activeDbs.Count == 0)
                throw new System.InvalidOperationException("call WithAnchor first");

            var parser = new SimaticMLParser();
            var loaded = new Dictionary<string, (DataBlockInfo info, string xml, string plc)>();
            foreach (var spec in _activeDbs.Concat(_dropdownPeers))
            {
                if (loaded.ContainsKey(spec.Fixture)) continue;
                var xml = TestFixtures.LoadXml(spec.Fixture);
                var info = parser.Parse(xml);
                loaded[spec.Fixture] = (info, xml, spec.Plc);
            }

            var configLoader = new ConfigLoader(null);
            var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
            var tracker = Substitute.For<IUsageTracker>();
            tracker.GetStatus().Returns(new UsageStatus(0, 1000));
            tracker.RecordUsage(Arg.Any<int>()).Returns(true);

            var mbx = new RecordingFakeMessageBox(_promptResults);
            var appliedOrder = new List<string>();

            var anchorSpec = _activeDbs[0];
            var (anchorInfo, anchorXml, _) = loaded[anchorSpec.Fixture];

            var companionDbs = _activeDbs.Skip(1)
                .Select(spec =>
                {
                    var (info, xml, plc) = loaded[spec.Fixture];
                    return new ActiveDb(info, xml,
                        onApply: _ => appliedOrder.Add(info.Name),
                        plcName: plc);
                })
                .ToList();

            // Dropdown enumerates the union of (active anchor + active companions
            // + dropdown peers), each with its declared PLC.
            var summaries = _activeDbs.Concat(_dropdownPeers)
                .Select(spec =>
                {
                    var info = loaded[spec.Fixture].info;
                    return new DataBlockSummary(info.Name, "", plcName: spec.Plc);
                })
                .ToList();

            // buildActiveDbForSummary lets the dropdown / reactivate paths
            // re-add a peer as a writable ActiveDb so OnApply (and stash
            // restore) work end to end — same shape host-side.
            ActiveDb? BuildForSummary(DataBlockSummary s)
            {
                var spec = _activeDbs.Concat(_dropdownPeers)
                    .FirstOrDefault(d => loaded[d.Fixture].info.Name == s.Name);
                if (spec == null) return null;
                var (info, xml, plc) = loaded[spec.Fixture];
                return new ActiveDb(info, xml,
                    onApply: _ => appliedOrder.Add(info.Name),
                    plcName: plc);
            }

            string SwitchTo(DataBlockSummary s)
            {
                var spec = _activeDbs.Concat(_dropdownPeers)
                    .First(d => loaded[d.Fixture].info.Name == s.Name);
                return loaded[spec.Fixture].xml;
            }

            var vm = new BulkChangeViewModel(
                anchorInfo, anchorXml,
                new HierarchyAnalyzer(), bulkService, tracker, configLoader,
                onApply: _ => appliedOrder.Add(anchorInfo.Name),
                messageBox: mbx,
                enumerateDataBlocks: () => summaries,
                switchToDataBlock: SwitchTo,
                currentPlcName: _anchorPlc,
                additionalActiveDbs: companionDbs,
                buildActiveDbForSummary: BuildForSummary);

            // Stage pending edits per requested DB. Rooted in each DB's
            // synthetic subtree (if multi-DB) or the flat tree (single-DB).
            // Each edit lands on a distinct leaf with a non-empty StartValue.
            foreach (var kvp in _pendingEditCounts)
            {
                var dbName = kvp.Key;
                var count = kvp.Value;
                var roots = vm.RootMembers.Count > 0 && vm.RootMembers[0].Datatype == "DB"
                    ? vm.RootMembers.Where(r => r.Name == dbName).ToList()
                    : (loaded.Values.Any(v => v.info.Name == dbName)
                        ? vm.RootMembers.ToList()
                        : new List<MemberNodeViewModel>());
                if (roots.Count == 0)
                    throw new System.InvalidOperationException(
                        $"WithPendingEditsOn: DB '{dbName}' is not in the active set at build time");

                var leaves = roots.SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
                    .Where(n => n.IsLeaf && !string.IsNullOrEmpty(n.StartValue))
                    .Take(count)
                    .ToList();
                if (leaves.Count < count)
                    throw new System.InvalidOperationException(
                        $"WithPendingEditsOn: DB '{dbName}' has only {leaves.Count} primitive leaves with start values, requested {count}");
                foreach (var leaf in leaves)
                    leaf.PendingValue = leaf.StartValue == "0" ? "1" : "0";
            }

            return new TestEnv(vm, mbx, appliedOrder);
        }

        private sealed record DbSpec(string Fixture, string Plc, bool IsAnchor);
    }

    private sealed record TestEnv(
        BulkChangeViewModel Vm,
        RecordingFakeMessageBox Mbx,
        List<string> AppliedOrder);

    private sealed class RecordingFakeMessageBox : IMessageBoxService
    {
        private readonly Queue<YesNoCancelResult> _results;

        public RecordingFakeMessageBox(Queue<YesNoCancelResult> results)
        {
            _results = results;
        }

        public int AskYesNoCancelCallCount { get; private set; }
        public int AskYesNoCallCount { get; private set; }
        public bool AskYesNo(string message, string title)
        {
            AskYesNoCallCount++;
            return true;
        }
        public void ShowError(string message, string title) { }
        public void ShowInfo(string message, string title) { }
        public YesNoCancelResult AskYesNoCancel(string message, string title)
        {
            AskYesNoCancelCallCount++;
            if (_results.Count == 0)
                throw new System.InvalidOperationException(
                    $"RecordingFakeMessageBox: AskYesNoCancel call #{AskYesNoCancelCallCount} " +
                    $"has no scripted response — extend WithPromptResults(...).\nMessage: {message}");
            return _results.Dequeue();
        }
    }
}
