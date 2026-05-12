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
///   2. <c>PlcPills</c> selection covers every active DB; one pill per PLC;
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
        env.Vm.PlcPills.Select(p => p.PlcName).Should().BeEquivalentTo(
            new[] { "PLC_A", "PLC_B" });
        env.Vm.RootMembers.Should().HaveCount(2);
        env.Vm.RootMembers.Should().AllSatisfy(r => r.Datatype.Should().Be("DB"));
        env.Mbx.AskYesNoCancelCallCount.Should().Be(0, "pure-add does not prompt");
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Remove_DropdownUncheck_TargetHasEdits_PromptStash_CreatesStashEntry()
    {
        // Row 3: two active DBs, the peer has pending edits. Unchecking the
        // peer row prompts; user picks "Stash" (No). Peer drops out of the
        // active set, edits land in the stash dictionary keyed by
        // (PlcName, FolderPath, Name).
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithPeer("nested-struct-db.xml")
            .WithPendingEditsOn("NestedStructDB", count: 1)
            .WithPromptResults(YesNoCancelResult.No)
            .Build();

        env.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        var peerRow = env.Vm.FilteredDataBlockItems.First(i => i.Name == "NestedStructDB");
        peerRow.IsActive = false;

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

        // Verify the last-DB removal is refused at the VM level.
        var anchorDb = env.Vm.AllActiveDbs[0];
        var countBefore = env.Vm.AllActiveDbs.Count;
        env.Vm.RequestRemoveActiveDb(anchorDb); // should be a no-op
        env.Vm.AllActiveDbs.Should().HaveCount(countBefore,
            "the dialog must always have at least one DB");
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
            .WithPeer("nested-struct-db.xml")
            .WithPendingEditsOn("NestedStructDB", count: 1)
            .WithPromptResults(YesNoCancelResult.Cancel)
            .Build();

        var peerLeaf = FindFirstPendingLeaf(env.Vm, "NestedStructDB");
        var pendingValue = peerLeaf.PendingValue;

        var peerDb = env.Vm.AllActiveDbs.First(d => d.Info.Name == "NestedStructDB");
        env.Vm.RequestRemoveActiveDb(peerDb);

        env.Vm.AllActiveDbs.Should().HaveCount(2, "Cancel keeps the peer in place");
        env.Vm.HasStashedDbs.Should().BeFalse("Cancel does not stash");
        peerLeaf.PendingValue.Should().Be(pendingValue, "edit was not lost");
        peerLeaf.IsPendingInlineEdit.Should().BeTrue();
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
            .WithPeer("nested-struct-db.xml")
            .WithPeer("array-db.xml")
            .WithPendingEditsOn("FlatDB", count: 1)
            .WithPendingEditsOn("NestedStructDB", count: 1)
            .WithPromptResults(YesNoCancelResult.Yes, YesNoCancelResult.Yes)
            .Build();

        var targetDb = env.Vm.AllActiveDbs.First(d => d.Info.Name == "ArrayDB");
        env.Vm.SoloActiveDbByReference(targetDb);

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
            .WithPeer("nested-struct-db.xml")  // has edits — prompt fires
            .WithPeer("array-db.xml")          // target — stays
            .WithPendingEditsOn("NestedStructDB", count: 1)
            .WithPromptResults(YesNoCancelResult.Cancel)
            .Build();

        var targetDb = env.Vm.AllActiveDbs.First(d => d.Info.Name == "ArrayDB");
        env.Vm.SoloActiveDbByReference(targetDb);

        env.Vm.AllActiveDbs.Select(d => d.Info.Name).Should().BeEquivalentTo(
            new[] { "NestedStructDB", "ArrayDB" },
            "Cancel on the middle DB aborts the rest of the solo loop");
        env.Vm.HasStashedDbs.Should().BeFalse();
        env.Mbx.AskYesNoCancelCallCount.Should().Be(1);
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Reactivate_StashHeader_PromptAdditive_KeepsOtherActive()
    {
        // Bug spec for #92 — Reactivate must prompt additive vs replace when
        // there's another active DB. Today: ReactivateStashedDb calls
        // ComposeRemoveOthers unconditionally, no top-level prompt fires,
        // every other active DB is silently dropped (or pending-edit-
        // prompted on remove). The user has no way to ask "add the stashed
        // DB to the current session" — every reactivate is destructive.
        //
        // Resolution: when AllActiveDbs.Count >= 2, clicking the stash
        // header fires a Yes/No/Cancel prompt (Yes=Additive, No=Replace,
        // Cancel=abort). This row pins the Additive branch.
        //
        // Setup: 3 DBs total — anchor + ArrayDB peer (no edits) + peer
        // NestedStructDB (with edits). Close NestedStructDB → stash (1st
        // prompt). Reactivate it; [FlatDB, ArrayDB] active at that moment
        // so the new prompt fires (2nd prompt). User picks Yes (Additive).
        // End state: all three active, NestedStructDB's edit restored.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithPeer("nested-struct-db.xml")
            .WithPeer("array-db.xml")
            .WithPendingEditsOn("NestedStructDB", count: 1)
            .WithPromptResults(
                YesNoCancelResult.No,   // 1st: stash NestedStructDB on close
                YesNoCancelResult.Yes)  // 2nd: additive on reactivate
            .Build();

        var pendingLeaf = FindFirstPendingLeaf(env.Vm, "NestedStructDB");
        var pendingValue = pendingLeaf.PendingValue!;

        // Bootstrap: close NestedStructDB to create the stash entry.
        var nestedDb = env.Vm.AllActiveDbs.First(d => d.Info.Name == "NestedStructDB");
        env.Vm.RequestRemoveActiveDb(nestedDb);
        env.Vm.HasStashedDbs.Should().BeTrue("setup: stash created via chip-close");
        env.Vm.AllActiveDbs.Should().HaveCount(2,
            "setup: FlatDB + ArrayDB active before reactivate, ≥2 → prompt fires");

        // Click the stash header. The new additive/replace prompt must fire
        // (count >= 2). User picks Yes = additive.
        var promptsBefore = env.Mbx.AskYesNoCancelCallCount;
        var stash = env.Vm.StashedDbs.Single();
        env.Vm.SwitchToStashedDbCommand.Execute(stash);

        (env.Mbx.AskYesNoCancelCallCount - promptsBefore).Should().Be(1,
            "reactivate with ≥2 active DBs must fire the additive/replace prompt");
        env.Vm.AllActiveDbs.Should().HaveCount(3,
            "Yes = Additive: the previously-active DBs stay, stashed DB added back");
        env.Vm.AllActiveDbs.Select(d => d.Info.Name)
            .Should().BeEquivalentTo(new[] { "FlatDB", "ArrayDB", "NestedStructDB" });
        env.Vm.HasStashedDbs.Should().BeFalse(
            "NestedStructDB's stash entry pops on restore");

        // Stash edits replay onto the live tree.
        var restored = env.Vm.RootMembers
            .SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .FirstOrDefault(n => n.IsLeaf && n.PendingValue == pendingValue);
        restored.Should().NotBeNull("stashed edit must land on the rebuilt tree");
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Reactivate_StashHeader_PromptReplace_DropsOthers()
    {
        // Bug spec for #92 — Replace branch (companion to the Additive row
        // above). Same setup, scripted second prompt = No (Replace). The
        // reactivate must then walk the other active DBs through the usual
        // pending-edit prompt (none have edits here → silent removal) and
        // leave only the stashed DB.
        //
        // Today this happens to produce the same end state as the current
        // (buggy) implementation, but for the wrong reason — no top-level
        // prompt fires today, the others are dropped unconditionally. The
        // failure mode here is the prompt-count assertion: today only 1
        // prompt fires (the initial stash on close), not 2.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithPeer("nested-struct-db.xml")
            .WithPeer("array-db.xml")
            .WithPendingEditsOn("NestedStructDB", count: 1)
            .WithPromptResults(
                YesNoCancelResult.No,   // 1st: stash NestedStructDB on close
                YesNoCancelResult.No)   // 2nd: replace on reactivate
            .Build();

        var pendingLeaf = FindFirstPendingLeaf(env.Vm, "NestedStructDB");
        var pendingValue = pendingLeaf.PendingValue!;

        var nestedDb2 = env.Vm.AllActiveDbs.First(d => d.Info.Name == "NestedStructDB");
        env.Vm.RequestRemoveActiveDb(nestedDb2);
        env.Vm.HasStashedDbs.Should().BeTrue("setup: stash created");

        var promptsBefore = env.Mbx.AskYesNoCancelCallCount;
        var stash = env.Vm.StashedDbs.Single();
        env.Vm.SwitchToStashedDbCommand.Execute(stash);

        (env.Mbx.AskYesNoCancelCallCount - promptsBefore).Should().Be(1,
            "reactivate with ≥2 active DBs must fire the additive/replace prompt " +
            "before any per-DB pending-edit prompts");
        env.Vm.AllActiveDbs.Should().HaveCount(1,
            "No = Replace: others dropped (silently here, no edits)");
        env.Vm.AllActiveDbs[0].Info.Name.Should().Be("NestedStructDB");
        env.Vm.HasStashedDbs.Should().BeFalse();

        var restored = env.Vm.RootMembers
            .SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .FirstOrDefault(n => n.IsLeaf && n.PendingValue == pendingValue);
        restored.Should().NotBeNull("stashed edit lands on the single-DB tree");
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
            .WithPeer("nested-struct-db.xml", plc: "PLC_A")
            .WithPendingEditsOn("NestedStructDB", count: 1)
            .WithPromptResults(
                YesNoCancelResult.No,   // stash NestedStructDB on chip-close
                YesNoCancelResult.No)   // stash FlatDB during reactivate's solo
            .Build();

        // Setup: stash NestedStructDB.
        var nestedDb3 = env.Vm.AllActiveDbs.First(d => d.Info.Name == "NestedStructDB");
        env.Vm.RequestRemoveActiveDb(nestedDb3);
        env.Vm.HasStashedDbs.Should().BeTrue();
        env.Vm.StashedDbs.Should().ContainSingle(s => s.DbName == "NestedStructDB");

        // Stage a pending edit on FlatDB (the still-active anchor) after the
        // peer drop, i.e. while we're in single-DB shape. Use EditableStartValue
        // (production path) so the PendingEditStore is populated — CountPendingEditsForDb
        // reads from the store to decide whether to prompt before remove.
        var anchorLeaf = env.Vm.RootMembers.SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .First(n => n.IsLeaf && !string.IsNullOrEmpty(n.StartValue));
        anchorLeaf.EditableStartValue = anchorLeaf.StartValue == "0" ? "1" : "0";

        // Reactivate the stashed peer via header click.
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
    public void Add_DropdownCheck_AnchorHasEdits_NoPromptFires_PendingEditSurvives()
    {
        // Bug spec for #93 — Dropdown-add must NOT fire the pending-edit
        // prompt. Today AddActiveDbWithPendingEditPrompt loops over every
        // currently-active DB and runs the 3-way Apply/Stash/Cancel prompt
        // to "rescue" pending edits before the tree rebuild orphans them.
        //
        // That loop was a workaround for the orphaning class of bug — fixed
        // properly in 9814a6e by PendingEditStore, which seeds pending
        // values onto fresh VMs after a rebuild. With the store in place,
        // the prompt is unnecessary: a pure-add never needs to consult the
        // user. Resolution: remove the foreach loop.
        //
        // Setup deliberately omits WithPromptResults(...): if any prompt
        // fires today, RecordingFakeMessageBox throws "no scripted
        // response" — that throw IS the red signal that the prompt loop is
        // still in place.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithDropdownPeer("nested-struct-db.xml")
            .WithPendingEditsOn("FlatDB", count: 1)
            .Build();

        var anchorLeafBefore = FindFirstPendingLeaf(env.Vm, "FlatDB");
        var pendingValue = anchorLeafBefore.PendingValue;

        env.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        var peerRow = env.Vm.FilteredDataBlockItems.First(i => i.Name == "NestedStructDB");
        peerRow.IsActive = true;

        env.Mbx.AskYesNoCancelCallCount.Should().Be(0,
            "pure-add no longer consults the user — PendingEditStore preserves " +
            "the anchor's edit across the multi-DB rebuild");
        env.Vm.AllActiveDbs.Should().HaveCount(2,
            "peer added without prompt; anchor stays");
        env.Vm.AllActiveDbs.Select(d => d.Info.Name)
            .Should().BeEquivalentTo(new[] { "FlatDB", "NestedStructDB" });

        // The anchor's pending edit survives the tree rebuild (the VM is
        // a fresh instance now that the tree was rebuilt into multi-DB
        // shape — find it again by walking the new tree).
        var anchorLeafAfter = FindFirstPendingLeaf(env.Vm, "FlatDB");
        anchorLeafAfter.PendingValue.Should().Be(pendingValue,
            "PendingEditStore re-seeds the anchor's pending value onto the rebuilt VM");
        env.Vm.PendingInlineEditCount.Should().Be(1);
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void DropdownRow_SoloClick_AnchorHasEdits_PromptCancel_LeavesActiveSetUnchanged()
    {
        // Row 11b — sibling of Row 11 for the dropdown row-body click
        // (SoloActiveDb path) instead of the checkbox toggle
        // (AddActiveDbWithPendingEditPrompt path). Same scenario, different
        // gesture. Captured from a v1.0.14 user-side TIA reproduction:
        //
        //   single-DB session (anchor with 1 pending inline edit)
        //   → click "+" to open dropdown
        //   → click the BODY of a peer row (Solo, not the checkbox)
        //   → 3-way prompt fires for the anchor's pending edit
        //   → user picks Cancel
        //
        // Pre-fix outcome: active set silently mutated to [anchor, peer]
        // because SoloActiveDb appends the freshly-built target before
        // ComposeRemoveOthers runs, and ComposeRemoveOthers's Cancel branch
        // returns the partial composition. The cascade then rebuilt the
        // tree to multi-DB shape, orphaning the anchor's pending VM, and
        // the next chip-× silently removed the anchor with no prompt and
        // no stash entry — pure data loss.
        //
        // Post-fix expectation: target was newly built + prune cancelled →
        // SoloActiveDb reverts the whole gesture, leaving [anchor] intact
        // with its pending edit on the original (still-live) tree.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithDropdownPeer("nested-struct-db.xml")
            .WithPendingEditsOn("FlatDB", count: 1)
            .WithPromptResults(YesNoCancelResult.Cancel)
            .Build();

        var anchorLeaf = FindFirstPendingLeaf(env.Vm, "FlatDB");
        var pendingValue = anchorLeaf.PendingValue;

        env.Vm.SoloActiveDb(new DataBlockSummary("NestedStructDB", ""));

        env.Vm.AllActiveDbs.Should().HaveCount(1,
            "Solo+Cancel on a newly-built target must revert — no peer added");
        env.Vm.AllActiveDbs[0].Info.Name.Should().Be("FlatDB");
        env.Vm.HasStashedDbs.Should().BeFalse("Cancel does not stash");
        anchorLeaf.PendingValue.Should().Be(pendingValue,
            "tree never rebuilt → anchor's pending VM is still the same instance");
        anchorLeaf.IsPendingInlineEdit.Should().BeTrue();
        env.Mbx.AskYesNoCancelCallCount.Should().Be(1,
            "exactly one prompt fired — the prune walks one DB");
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Remove_ChipCloseAnchor_AnchorHasEdits_PromptKeep_StashesAnchorAndClearsPendingList()
    {
        // Row 12 — chip-× on the anchor with pending edits, user picks Keep.
        // Asserts the anchor's edits land in StashedDbs (not silently dropped)
        // and that PendingEdits drops every entry that referenced the removed
        // DB. Row 3 covers dropdown-uncheck + Stash on a peer; Row 5
        // covers chip-close + Cancel; this row plugs the missing chip-close +
        // Keep + anchor-role corner.
        //
        // Originally written as DropdownCancel_ThenChipCloseAnchor_… stacked
        // on top of the dropdown-add-prompt bug (no prompt fired, set ended
        // up [A, B]). Once the add path prompts correctly, that staged state
        // is unreachable (chip-× on the lone anchor in [A] is refused by the
        // ≥1 invariant), so the test was rewritten to its still-reachable
        // shape — same data-loss class, no dependency on the prior bug.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithPeer("nested-struct-db.xml")
            .WithPendingEditsOn("FlatDB", count: 1)
            .WithPromptResults(YesNoCancelResult.No)   // Keep on chip-close
            .Build();

        var anchorLeaf = FindFirstPendingLeaf(env.Vm, "FlatDB");
        var pendingValue = anchorLeaf.PendingValue;

        var anchorDbToRemove = env.Vm.AllActiveDbs.First(d => d.Info.Name == "FlatDB");
        env.Vm.RequestRemoveActiveDb(anchorDbToRemove);

        env.Vm.AllActiveDbs.Should().ContainSingle()
            .Which.Info.Name.Should().Be("NestedStructDB",
                "Keep on chip-close removes the anchor from the active set");
        env.Vm.HasStashedDbs.Should().BeTrue("Keep moves pending edits into the stash");
        env.Vm.StashedDbs.Should().ContainSingle(s => s.DbName == "FlatDB",
            "anchor's pending edit must land in the stash dictionary, not be silently dropped");
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
            .WithPeer("nested-struct-db.xml", plc: "PLC_B")
            .Build();

        env.Vm.AllActiveDbs[0].Info.Name.Should().Be("FlatDB", "setup: anchor is FlatDB");
        env.Vm.CurrentPlcName.Should().Be("PLC_A", "setup: anchor PLC display is PLC_A");

        var anchorDb2 = env.Vm.AllActiveDbs.First(d => d.Info.Name == "FlatDB");
        env.Vm.RequestRemoveActiveDb(anchorDb2); // 2-DB session — anchor is removable

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
            .WithPeer("nested-struct-db.xml")
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

        var anchorDbManual = env.Vm.AllActiveDbs.First(d => d.Info.Name == "FlatDB");
        env.Vm.RequestRemoveActiveDb(anchorDbManual);

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
    public void PendingEditStore_SurvivesTreeRebuild_AnchorEditKeptAfterPeerRemoved()
    {
        // Regression guard for the orphaning bug fixed by PendingEditStore:
        // Start with 2 active DBs. Stage an edit on the anchor (A). Then remove
        // the peer (B) which has no edits — B's chip-close is silent (no prompt)
        // and triggers BuildRootMembersFromActiveDbs transitioning from multi-DB
        // to single-DB shape. This discards and recreates all MemberNodeViewModels.
        // The store must seed the fresh anchor VM with the original pending value
        // so nothing is lost. Without the store, A's pending edit would be orphaned.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithPeer("nested-struct-db.xml")
            .WithPendingEditsOn("FlatDB", count: 1)
            .Build();

        // Verify setup: anchor has a pending edit, tree is in multi-DB shape.
        env.Vm.AllActiveDbs.Should().HaveCount(2);
        var anchorLeafBefore = FindFirstPendingLeaf(env.Vm, "FlatDB");
        var expectedPendingValue = anchorLeafBefore.PendingValue;
        expectedPendingValue.Should().NotBeNull("setup: anchor has exactly one pending edit");

        // Remove the peer (no pending edits on peer → no prompt).
        var peerDb4 = env.Vm.AllActiveDbs.First(d => d.Info.Name == "NestedStructDB");
        env.Vm.RequestRemoveActiveDb(peerDb4);

        // Tree rebuilt: multi-DB → single-DB (flat) shape, all VMs replaced.
        env.Vm.AllActiveDbs.Should().HaveCount(1, "peer was silently removed");
        env.Mbx.AskYesNoCancelCallCount.Should().Be(0, "no edits on peer → no prompt");
        env.Vm.RootMembers.Should().AllSatisfy(r =>
            r.Datatype.Should().NotBe("DB"), "single-DB shape: no synthetic roots");

        // Find the anchor's leaf in the new flat tree.
        var anchorLeafAfter = FindFirstPendingLeaf(env.Vm, "FlatDB");
        anchorLeafAfter.PendingValue.Should().Be(expectedPendingValue,
            "PendingEditStore must seed the fresh anchor VM with its pending value " +
            "after the tree rebuilt from multi-DB to single-DB shape");
        env.Vm.PendingInlineEditCount.Should().Be(1,
            "exactly one pending edit survives the rebuild");
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void PendingEditStore_SurvivesTreeRebuild_PeerEditKeptAfterCancel()
    {
        // 'Cancel = inert' guarantee extended to the store layer: when the
        // user cancels the remove prompt, the pending edit must not be cleared
        // from the store or from the VM.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithPeer("nested-struct-db.xml")
            .WithPendingEditsOn("NestedStructDB", count: 1)
            .WithPromptResults(YesNoCancelResult.Cancel)
            .Build();

        var peerLeaf = FindFirstPendingLeaf(env.Vm, "NestedStructDB");
        var expectedPendingValue = peerLeaf.PendingValue;

        // Attempt to remove the peer — prompt fires, user cancels.
        var peerDb5 = env.Vm.AllActiveDbs.First(d => d.Info.Name == "NestedStructDB");
        env.Vm.RequestRemoveActiveDb(peerDb5);

        // After cancel: same VMs, same tree shape, store still has the edit.
        env.Vm.AllActiveDbs.Should().HaveCount(2, "Cancel leaves the peer active");
        var peerLeafAfter = FindFirstPendingLeaf(env.Vm, "NestedStructDB");
        peerLeafAfter.PendingValue.Should().Be(expectedPendingValue,
            "store must not clear the pending edit on a cancelled remove gesture");
        env.Vm.PendingInlineEditCount.Should().Be(1,
            "pending count unchanged after cancel");
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Selection_PlainClickInDbA_ClearsPriorSelectionInDbB()
    {
        // Bug spec for #95 — Cross-DB focus row exclusivity. Today
        // MemberNodeViewModel.IsSelected is a plain property with no
        // cross-tree clearing logic, so when the user clicks a leaf in
        // DB A while a leaf in DB B is already selected, both end up
        // IsSelected=true. The dialog then computes scope/preview from
        // an ambiguous selection.
        //
        // Resolution: single global focus row. Setting IsSelected=true on
        // any leaf must clear the prior IsSelected=true leaf anywhere in
        // RootMembers, including in other DBs' synthetic subtrees.
        //
        // Note: the WPF TreeView may carry its own per-tree selection
        // state — this row pins the VM contract regardless. If WPF holds
        // extra state, that's a separate (downstream) fix.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithPeer("nested-struct-db.xml")
            .Build();

        env.Vm.RootMembers.Should().HaveCount(2,
            "setup: multi-DB tree with one synthetic root per DB");

        var dbARoot = env.Vm.RootMembers.First(r => r.Name == "FlatDB");
        var dbBRoot = env.Vm.RootMembers.First(r => r.Name == "NestedStructDB");
        var dbALeaf = new[] { dbARoot }.Concat(dbARoot.AllDescendants())
            .First(n => n.IsLeaf);
        var dbBLeaf = new[] { dbBRoot }.Concat(dbBRoot.AllDescendants())
            .First(n => n.IsLeaf);

        // Select a leaf in DB B first.
        dbBLeaf.IsSelected = true;
        // Then select a leaf in DB A — DB B's selection must clear.
        dbALeaf.IsSelected = true;

        var allLeaves = env.Vm.RootMembers
            .SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .Where(n => n.IsLeaf)
            .ToList();
        allLeaves.Count(n => n.IsSelected).Should().Be(1,
            "only the most recently clicked leaf may carry IsSelected=true");
        dbALeaf.IsSelected.Should().BeTrue("most recent click stays selected");
        dbBLeaf.IsSelected.Should().BeFalse(
            "prior selection in another DB's subtree must clear when a new leaf is selected");
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Title_MultiDb_DoesNotContainAnySingleDbName()
    {
        // Bug spec for #91 — In multi-DB sessions the dialog title still
        // renders one specific DB's name ("BlockParam v1.2.3: PLC_A /
        // FlatDB"), which surfaces an "anchor" privilege the rest of the
        // UI no longer has (#78 peer-DB model). The chip strip is the
        // single source of truth for which DBs are in the session.
        //
        // Resolution: multi-DB sessions render a chip-only header (e.g.
        // version + PLC chips) with no single-DB name. Single-DB sessions
        // keep the current title shape.
        //
        // Today: BuildTitle(version, plcName, dbName) is called with
        // _activeDbs[0].Info.Name even when count > 1 → the anchor name
        // bleeds through.
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml", plc: "PLC_A")
            .WithPeer("nested-struct-db.xml", plc: "PLC_A")
            .Build();

        env.Vm.AllActiveDbs.Should().HaveCount(2, "setup: multi-DB session");
        env.Vm.Title.Should().NotContain("FlatDB",
            "multi-DB title must not surface the anchor DB's name");
        env.Vm.Title.Should().NotContain("NestedStructDB",
            "multi-DB title must not surface any single DB's name");
        AssertInvariants(env.Vm);
    }

    [Fact]
    public void Solo_ChipBody_TitleRefreshesOnCascade()
    {
        // Bug spec for #91 (sibling row) — solo via chip-body click must
        // refresh Title to reflect the post-solo single-DB shape. Today
        // SoloActiveDbByReference assigns State but never updates Title:
        // Title is only rewritten inside RemoveActiveDb's wasAnchor branch.
        // Soloing from [A, B, C] to B leaves Title still showing A.
        //
        // Resolution: every cascade that lands the dialog in single-DB
        // shape must refresh Title (or the multi-DB chip-only header
        // collapses back to a DB-named title).
        var env = new ActiveSetTestBuilder()
            .WithAnchor("flat-db.xml")
            .WithPeer("nested-struct-db.xml")
            .WithPeer("array-db.xml")
            .Build();

        var titleBefore = env.Vm.Title;
        titleBefore.Should().NotContain("NestedStructDB",
            "setup: 3-DB title doesn't already include the soloed name");

        // Solo to NestedStructDB via SoloActiveDbByReference.
        var targetDbSolo = env.Vm.AllActiveDbs.First(d => d.Info.Name == "NestedStructDB");
        env.Vm.SoloActiveDbByReference(targetDbSolo);

        env.Vm.AllActiveDbs.Should().HaveCount(1, "solo collapses to one DB");
        env.Vm.AllActiveDbs[0].Info.Name.Should().Be("NestedStructDB");
        env.Vm.Title.Should().Contain("NestedStructDB",
            "single-DB shape after solo must surface the new lone DB's name " +
            "(or, post-fix, the chip-only header must consistently re-render)");
        env.Vm.Title.Should().NotContain("FlatDB",
            "previous anchor's name must NOT linger after solo");
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

        // (2) pill-row covers the same DB names as the active set
        var pillDbNames = vm.PlcPills
            .SelectMany(p => p.SelectedDbs.OfType<DataBlockListItem>())
            .Select(i => i.Name)
            .ToList();
        var dbsByName = vm.AllActiveDbs.Select(d => d.Info.Name).ToList();
        pillDbNames.Should().BeEquivalentTo(dbsByName,
            "invariant 2: every active DB has a corresponding pill selection entry");

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

        // (5) anchor PLC display matches the anchor pill's PlcName
        if (vm.PlcPills.Count > 0)
        {
            var anchorName = vm.AllActiveDbs[0].Info.Name;
            var anchorPill = vm.PlcPills
                .FirstOrDefault(p => p.SelectedDbs.OfType<DataBlockListItem>()
                    .Any(i => i.Name == anchorName));
            anchorPill?.PlcName.Should().Be(vm.CurrentPlcName,
                "invariant 5: anchor pill's PlcName == CurrentPlcName");
        }

        // (6) PendingEdits only references nodes still reachable in the live
        // tree — removing a DB from the active set must vacate that DB's
        // leaves from the bound "pending changes" inspector list.
        var reachableNodes = vm.RootMembers
            .SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .ToHashSet();
        var orphanedPaths = vm.Pending.PendingEdits
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
            vm.BulkPreview.Entries.Should().BeEmpty(
                "invariant 7: BulkPreview must be empty when no scope is " +
                "selected and not in manual mode");
        }

        // (8) BulkPreview entries and ManualSelectedPaths nodes are reachable
        // from the live tree — same shape as invariant 6 for PendingEdits.
        // Removing a DB from the active set must vacate any preview rows or
        // manual-selection paths that pointed at its leaves (§H3).
        var orphanedPreview = vm.BulkPreview.Entries
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

    /// <summary>
    /// Convenience enum for scripting 3-way prompt responses in tests.
    /// Yes/No/Cancel maps onto the named outcomes of each typed prompt method
    /// (ApplyStashCancel, AddOrReplace, CloseWithStash).
    /// </summary>
    private enum YesNoCancelResult { Yes, No, Cancel }

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

        public ActiveSetTestBuilder WithPeer(string fixture, string plc = "")
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

            var peerDbs = _activeDbs.Skip(1)
                .Select(spec =>
                {
                    var (info, xml, plc) = loaded[spec.Fixture];
                    return new ActiveDb(info, xml,
                        onApply: _ => appliedOrder.Add(info.Name),
                        plcName: plc);
                })
                .ToList();

            // Dropdown enumerates the union of (every active DB + dropdown
            // peers), each with its declared PLC.
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
                additionalActiveDbs: peerDbs,
                buildActiveDbForSummary: BuildForSummary);

            // Stage pending edits per requested DB. Rooted in each DB's
            // synthetic subtree (if multi-DB) or the flat tree (single-DB).
            // Each edit lands on a distinct leaf with a non-empty StartValue.
            //
            // Goes through the production inline-edit path
            // (EditableStartValue setter → StartValueEdited event →
            // OnSingleValueEdited → RefreshPendingAndPreview →
            // RebuildPendingEdits) instead of poking PendingValue directly.
            // The previous direct-assignment shortcut populated _pendingValue
            // on the leaf VM but never fired the event chain that the UI
            // relies on, so the bound PendingEdits collection — which the
            // user actually sees — stayed empty in tests even though the
            // tree thought it had pending edits. That divergence let the
            // dropdown-add-Stash orphaning bug ship in v1.0.13 unflagged.
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
                    leaf.EditableStartValue = leaf.StartValue == "0" ? "1" : "0";
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

        /// <summary>Counts any 3-way prompt call (ApplyStashCancel, AddOrReplace, CloseWithStash).</summary>
        public int AskYesNoCancelCallCount { get; private set; }
        public int AskYesNoCallCount { get; private set; }
        public bool AskYesNo(string message, string title)
        {
            AskYesNoCallCount++;
            return true;
        }
        public void ShowError(string message, string title) { }
        public void ShowInfo(string message, string title) { }

        private YesNoCancelResult Dequeue(string methodName, string message)
        {
            AskYesNoCancelCallCount++;
            if (_results.Count == 0)
                throw new System.InvalidOperationException(
                    $"RecordingFakeMessageBox: {methodName} call #{AskYesNoCancelCallCount} " +
                    $"has no scripted response — extend WithPromptResults(...).\nMessage: {message}");
            return _results.Dequeue();
        }

        public ApplyStashCancelResult AskApplyStashCancel(string message, string title)
        {
            return Dequeue(nameof(AskApplyStashCancel), message) switch
            {
                YesNoCancelResult.Yes => ApplyStashCancelResult.ApplyAndSwitch,
                YesNoCancelResult.No  => ApplyStashCancelResult.StashAndSwitch,
                _                     => ApplyStashCancelResult.Cancel,
            };
        }

        public AddOrReplaceResult AskAddOrReplace(string message, string title)
        {
            return Dequeue(nameof(AskAddOrReplace), message) switch
            {
                YesNoCancelResult.Yes => AddOrReplaceResult.Add,
                YesNoCancelResult.No  => AddOrReplaceResult.Replace,
                _                     => AddOrReplaceResult.Cancel,
            };
        }

        public CloseWithStashResult AskCloseWithStash(string message, string title)
        {
            return Dequeue(nameof(AskCloseWithStash), message) switch
            {
                YesNoCancelResult.Yes => CloseWithStashResult.ApplyActive,
                YesNoCancelResult.No  => CloseWithStashResult.DiscardAll,
                _                     => CloseWithStashResult.Cancel,
            };
        }
    }
}
