using System;
using System.Collections.Generic;
using System.Linq;
using BlockParam.Models;
using BlockParam.UI;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Focused tests for the active-set mutators that moved off the host VM
/// into <see cref="ActiveSetViewModel"/> in #80 slice 8b. The slice now
/// owns:
///
/// <list type="bullet">
///   <item>Add / Solo / Remove / Reactivate mutator paths;</item>
///   <item>DB-switcher dropdown state + commands;</item>
///   <item>PLC-pill row + rebuild + "+ PLC" affordance state.</item>
/// </list>
///
/// Host-side side effects (apply-in-place on remove, replay stashed edits
/// onto the live tree) are still host territory — they're wired through
/// callbacks. These tests stub those callbacks with NSubstitute / inline
/// delegates so the snapshot composition can be exercised in isolation.
///
/// The slice-8a tests in <see cref="ActiveSetViewModelTests"/> use the
/// no-arg-ish <c>new ActiveSetViewModel(initial)</c> ctor and validate
/// the snapshot-container contract; this file uses the richer ctor and
/// validates the mutators. Both ctors share the same state container.
/// </summary>
public class ActiveSetViewModelMutatorTests
{
    [Fact]
    public void AddActiveDbFromSummary_AppendsBuiltDbAndRaisesStateChanged()
    {
        var anchor = Db("Anchor");
        var added = Db("Added");
        var harness = new Harness(Snap(anchor))
            .WithBuildActiveDbForSummary(s => string.Equals(s.Name, "Added") ? added : null);

        ActiveSetState? captOld = null, captNew = null;
        harness.Vm.StateChanged += (o, n) => { captOld = o; captNew = n; };

        harness.Vm.AddActiveDbFromSummary(new DataBlockSummary("Added", ""));

        captOld.Should().NotBeNull();
        captNew.Should().NotBeNull();
        captNew!.Dbs.Should().HaveCount(2);
        captNew.Dbs.Last().Should().BeSameAs(added);
        harness.Vm.State.Dbs.Should().HaveCount(2);
        harness.Vm.HasMultipleActiveDbs.Should().BeTrue();
    }

    [Fact]
    public void AddActiveDbFromSummary_BuilderReturnsNull_NoStateChange()
    {
        var anchor = Db("Anchor");
        var harness = new Harness(Snap(anchor))
            .WithBuildActiveDbForSummary(_ => null);

        int events = 0;
        harness.Vm.StateChanged += (_, _) => events++;

        harness.Vm.AddActiveDbFromSummary(new DataBlockSummary("NotBuildable", ""));

        events.Should().Be(0);
        harness.Vm.State.Dbs.Should().HaveCount(1);
    }

    [Fact]
    public void SoloActiveDb_TargetAlreadyActive_PrunesPeers()
    {
        var a = Db("A");
        var b = Db("B");
        var c = Db("C");
        var harness = new Harness(Snap(a, b, c));

        // No pending edits → PromptForPendingEditsOnRemove takes the NoEdits
        // fast path and no message-box call is needed.
        harness.Vm.SoloActiveDb(new DataBlockSummary("B", ""));

        harness.Vm.State.Dbs.Should().ContainSingle().Which.Should().BeSameAs(b);
    }

    [Fact]
    public void SoloActiveDb_TargetNotInSet_BuildsThenSolos()
    {
        var a = Db("A");
        var b = Db("B");
        var harness = new Harness(Snap(a))
            .WithBuildActiveDbForSummary(s => string.Equals(s.Name, "B") ? b : null);

        harness.Vm.SoloActiveDb(new DataBlockSummary("B", ""));

        harness.Vm.State.Dbs.Should().ContainSingle().Which.Should().BeSameAs(b);
    }

    [Fact]
    public void SoloActiveDbByReference_AlreadyOnly_NoOp()
    {
        var a = Db("A");
        var harness = new Harness(Snap(a));
        int events = 0;
        harness.Vm.StateChanged += (_, _) => events++;

        harness.Vm.SoloActiveDbByReference(a);

        events.Should().Be(0);
        harness.Vm.State.Dbs.Should().ContainSingle();
    }

    [Fact]
    public void RequestRemoveActiveDb_LastDb_Refused()
    {
        var a = Db("A");
        var harness = new Harness(Snap(a));
        int events = 0;
        harness.Vm.StateChanged += (_, _) => events++;

        harness.Vm.RequestRemoveActiveDb(a);

        events.Should().Be(0);
        harness.Vm.State.Dbs.Should().ContainSingle();
    }

    [Fact]
    public void RequestRemoveActiveDb_TwoDbs_NoPendingEdits_RemovesWithoutPrompt()
    {
        var a = Db("A");
        var b = Db("B");
        var mbx = Substitute.For<IMessageBoxService>();
        var harness = new Harness(Snap(a, b)).WithMessageBox(mbx);

        harness.Vm.RequestRemoveActiveDb(a);

        harness.Vm.State.Dbs.Should().ContainSingle().Which.Should().BeSameAs(b);
        // No prompt because no pending edits exist for the DB being removed.
        mbx.DidNotReceive().AskApplyStashCancel(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public void RequestRemoveActiveDb_WithPendingEdits_PromptStash_InstallsSnapshotWithStash()
    {
        var a = Db("A");
        var b = Db("B");
        var node = new MemberNode(
            name: "Leaf", datatype: "Int", startValue: "0", path: "Leaf",
            parent: null, children: Array.Empty<MemberNode>());
        var modelToDb = new Dictionary<MemberNode, ActiveDb> { [node] = a };
        var store = new PendingEditStore();
        store.Set(node, "42");

        var mbx = Substitute.For<IMessageBoxService>();
        mbx.AskApplyStashCancel(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ApplyStashCancelResult.StashAndSwitch);

        var harness = new Harness(Snap(a, b))
            .WithMessageBox(mbx)
            .WithPendingStore(store, () => modelToDb);

        harness.Vm.RequestRemoveActiveDb(a);

        mbx.Received(1).AskApplyStashCancel(Arg.Any<string>(), Arg.Any<string>());
        harness.Vm.State.Dbs.Should().ContainSingle().Which.Should().BeSameAs(b);
        harness.Vm.State.Stashes.Should().HaveCount(1);
        harness.Vm.StashedDbs.Should().ContainSingle()
            .Which.DbName.Should().Be("A");
        // Store entries for the stashed DB are evicted so the live tree
        // doesn't keep the stale pending state.
        store.Count.Should().Be(0);
    }

    [Fact]
    public void RequestRemoveActiveDb_WithPendingEdits_PromptApply_CallsTryApplyCallback()
    {
        var a = Db("A");
        var b = Db("B");
        var node = new MemberNode(
            name: "Leaf", datatype: "Int", startValue: "0", path: "Leaf",
            parent: null, children: Array.Empty<MemberNode>());
        var modelToDb = new Dictionary<MemberNode, ActiveDb> { [node] = a };
        var store = new PendingEditStore();
        store.Set(node, "42");

        var mbx = Substitute.For<IMessageBoxService>();
        mbx.AskApplyStashCancel(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ApplyStashCancelResult.ApplyAndSwitch);

        var applyCallback = Substitute.For<Func<ActiveDb, bool>>();
        applyCallback(a).Returns(true);

        var harness = new Harness(Snap(a, b))
            .WithMessageBox(mbx)
            .WithPendingStore(store, () => modelToDb)
            .WithTryApply(applyCallback);

        harness.Vm.RequestRemoveActiveDb(a);

        applyCallback.Received(1)(a);
        harness.Vm.State.Dbs.Should().ContainSingle().Which.Should().BeSameAs(b);
        harness.Vm.State.Stashes.Should().BeEmpty(
            "Apply path commits + removes; no stash created");
    }

    [Fact]
    public void RequestRemoveActiveDb_WithPendingEdits_PromptCancel_LeavesStateUntouched()
    {
        var a = Db("A");
        var b = Db("B");
        var node = new MemberNode(
            name: "Leaf", datatype: "Int", startValue: "0", path: "Leaf",
            parent: null, children: Array.Empty<MemberNode>());
        var modelToDb = new Dictionary<MemberNode, ActiveDb> { [node] = a };
        var store = new PendingEditStore();
        store.Set(node, "42");

        var mbx = Substitute.For<IMessageBoxService>();
        mbx.AskApplyStashCancel(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ApplyStashCancelResult.Cancel);

        var harness = new Harness(Snap(a, b))
            .WithMessageBox(mbx)
            .WithPendingStore(store, () => modelToDb);

        var originalSnap = harness.Vm.State;
        int events = 0;
        harness.Vm.StateChanged += (_, _) => events++;

        harness.Vm.RequestRemoveActiveDb(a);

        events.Should().Be(0);
        harness.Vm.State.Should().BeSameAs(originalSnap);
        store.Count.Should().Be(1, "Cancel path preserves pending edits");
    }

    [Fact]
    public void ReactivateStashedDb_SingleActiveDb_NoPromptFires()
    {
        // current.Dbs.Count < 2 → additive/replace prompt is skipped.
        var anchor = Db("Anchor");
        var stashedDb = Db("Stashed");
        var stash = Stash("Stashed");
        var initial = new ActiveSetState(
            new[] { anchor },
            new Dictionary<string, StashedDbState> { [KeyFor(stash.Summary)] = stash },
            "");

        var mbx = Substitute.For<IMessageBoxService>();
        var restoreCallback = Substitute.For<Func<StashedDbState, ActiveDb?, (int, int)>>();
        restoreCallback(Arg.Any<StashedDbState>(), Arg.Any<ActiveDb?>()).Returns((0, 0));

        var harness = new Harness(initial)
            .WithMessageBox(mbx)
            .WithBuildActiveDbForSummary(s => string.Equals(s.Name, "Stashed") ? stashedDb : null)
            .WithRestoreStashOntoLive(restoreCallback);

        harness.Vm.ReactivateStashedDb(stash);

        mbx.DidNotReceive().AskAddOrReplace(Arg.Any<string>(), Arg.Any<string>());
        // Replace branch with single active → fresh DB appended, anchor pruned
        // (no pending edits → NoEdits fast path), stash popped.
        harness.Vm.State.Dbs.Should().ContainSingle().Which.Info.Name.Should().Be("Stashed");
        harness.Vm.State.Stashes.Should().BeEmpty();
        restoreCallback.Received(1)(stash, Arg.Any<ActiveDb?>());
    }

    [Fact]
    public void ReactivateStashedDb_TwoActiveDbs_AddDecision_AppendsKeepsAll()
    {
        var anchor = Db("Anchor");
        var peer = Db("Peer");
        var stashedDb = Db("Stashed");
        var stash = Stash("Stashed");
        var initial = new ActiveSetState(
            new[] { anchor, peer },
            new Dictionary<string, StashedDbState> { [KeyFor(stash.Summary)] = stash },
            "");

        var mbx = Substitute.For<IMessageBoxService>();
        mbx.AskAddOrReplace(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AddOrReplaceResult.Add);

        var restoreCallback = Substitute.For<Func<StashedDbState, ActiveDb?, (int, int)>>();
        restoreCallback(Arg.Any<StashedDbState>(), Arg.Any<ActiveDb?>()).Returns((0, 0));

        var harness = new Harness(initial)
            .WithMessageBox(mbx)
            .WithBuildActiveDbForSummary(s => string.Equals(s.Name, "Stashed") ? stashedDb : null)
            .WithRestoreStashOntoLive(restoreCallback);

        harness.Vm.ReactivateStashedDb(stash);

        mbx.Received(1).AskAddOrReplace(Arg.Any<string>(), Arg.Any<string>());
        harness.Vm.State.Dbs.Select(d => d.Info.Name)
            .Should().BeEquivalentTo(new[] { "Anchor", "Peer", "Stashed" });
        harness.Vm.State.Stashes.Should().BeEmpty();
        // Scoped restore: the additive branch hands the just-built DB so peer
        // DBs with colliding member paths don't absorb the stashed edits.
        restoreCallback.Received(1)(stash, stashedDb);
    }

    [Fact]
    public void ReactivateStashedDb_TwoActiveDbs_CancelDecision_NoStateChange()
    {
        var anchor = Db("Anchor");
        var peer = Db("Peer");
        var stash = Stash("Stashed");
        var initial = new ActiveSetState(
            new[] { anchor, peer },
            new Dictionary<string, StashedDbState> { [KeyFor(stash.Summary)] = stash },
            "");

        var mbx = Substitute.For<IMessageBoxService>();
        mbx.AskAddOrReplace(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AddOrReplaceResult.Cancel);

        var harness = new Harness(initial).WithMessageBox(mbx);
        var originalSnap = harness.Vm.State;
        int events = 0;
        harness.Vm.StateChanged += (_, _) => events++;

        harness.Vm.ReactivateStashedDb(stash);

        events.Should().Be(0);
        harness.Vm.State.Should().BeSameAs(originalSnap);
    }

    [Fact]
    public void DataBlockSwitcher_OpenDropdown_LoadsAndFiltersAvailableList()
    {
        var available = new[]
        {
            new DataBlockSummary("Alpha", ""),
            new DataBlockSummary("Beta", ""),
        };
        var harness = new Harness(Snap(Db("Anchor")))
            .WithEnumerateDataBlocks(() => available)
            .WithSwitchToDataBlock(_ => "<Block/>");

        harness.Vm.OpenDataBlocksDropdownCommand.Execute(null);

        harness.Vm.IsDataBlocksDropdownOpen.Should().BeTrue();
        harness.Vm.FilteredDataBlocks.Should().HaveCount(2);
        harness.Vm.HasDataBlockSwitcher.Should().BeTrue();
    }

    [Fact]
    public void DataBlockSwitcher_FilterText_FiltersList()
    {
        var available = new[]
        {
            new DataBlockSummary("DB_Sensors", ""),
            new DataBlockSummary("DB_Valves", ""),
        };
        var harness = new Harness(Snap(Db("Anchor")))
            .WithEnumerateDataBlocks(() => available)
            .WithSwitchToDataBlock(_ => "<Block/>");

        harness.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        harness.Vm.DataBlockSearchText = "Sensors";

        harness.Vm.FilteredDataBlocks.Should().ContainSingle()
            .Which.Name.Should().Be("DB_Sensors");
    }

    [Fact]
    public void DataBlockSwitcher_Refresh_ReEnumeratesAndCachesAgain()
    {
        int callCount = 0;
        var harness = new Harness(Snap(Db("Anchor")))
            .WithEnumerateDataBlocks(() =>
            {
                callCount++;
                return new[] { new DataBlockSummary("X", "") };
            })
            .WithSwitchToDataBlock(_ => "<Block/>");

        harness.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        harness.Vm.OpenDataBlocksDropdownCommand.Execute(null); // cache hit
        callCount.Should().Be(1);

        harness.Vm.RefreshDataBlocksCommand.Execute(null);
        callCount.Should().Be(2, "Refresh forces re-enumeration");
    }

    [Fact]
    public void PlcPills_RebuildAfterSetState_OnePillPerActivePlc()
    {
        var anchor = Db("DB_A", plc: "PLC_1");
        var peer = Db("DB_B", plc: "PLC_2");
        var harness = new Harness(new ActiveSetState(
            new[] { anchor, peer },
            new Dictionary<string, StashedDbState>(),
            anchorPlcName: "PLC_1"));

        harness.Vm.RebuildPlcPills();

        harness.Vm.PlcPills.Should().HaveCount(2);
        harness.Vm.PlcPills.Select(p => p.PlcName)
            .Should().BeEquivalentTo(new[] { "PLC_1", "PLC_2" });
    }

    [Fact]
    public void NoIsSameSummaryReferenceInSlice()
    {
        // Dead-code guard: the helper had zero callers in src/ and is dropped
        // in this slice. Catches a future re-introduction (and points the
        // author at this test for the rationale).
        var sliceSource = System.IO.File.ReadAllText(
            System.IO.Path.Combine(
                FindRepoRoot(),
                "src", "BlockParam", "UI", "ActiveSetViewModel.cs"));
        sliceSource.Should().NotContain("IsSameSummary");
    }

    private static string FindRepoRoot()
    {
        // Tests run from BlockParam.Tests\bin\<config>\<tfm>; the repo root
        // is up four levels. Walk up looking for the .csproj marker so the
        // test stays robust across CI vs local layouts.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir != null; i++)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "BlockParam.sln")))
                return dir;
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    // ---------- helpers ----------

    private static ActiveSetState Snap(params ActiveDb[] dbs)
        => new ActiveSetState(
            dbs,
            new Dictionary<string, StashedDbState>(),
            "");

    private static ActiveDb Db(string name, string plc = "")
    {
        var info = new DataBlockInfo(name, 1, "Optimized", "GlobalDB", Array.Empty<MemberNode>());
        return new ActiveDb(info, $"<Block name='{name}' />", onApply: null, plcName: plc);
    }

    private static StashedDbState Stash(string name, string folder = "")
    {
        var summary = new DataBlockSummary(name, folder, plcName: "", number: 1);
        return new StashedDbState(summary, Array.Empty<StashedEditEntry>());
    }

    /// <summary>
    /// Mirrors <c>ActiveSetViewModel.StashKey</c> (kept private). The
    /// stash dictionary key the slice computes for a summary is
    /// <c>{PlcName}{FolderPath}{Name}</c>. Tests that
    /// pre-populate a stash entry by key need this so the slice can find
    /// + pop the entry on <c>ReactivateStashedDb</c>.
    /// </summary>
    private static string KeyFor(DataBlockSummary s) =>
        $"{s.PlcName}{s.FolderPath}{s.Name}";

    /// <summary>
    /// Builder-style harness so each test only wires the callbacks it needs.
    /// Default config = pure state-container behavior (matches slice 8a).
    /// Each <c>WithX</c> call rewires the slice with a richer set of deps
    /// — the slice has no mutator state outside <see cref="ActiveSetState"/>,
    /// so reconstructing per gesture is cheap and keeps the tests focused.
    /// </summary>
    private class Harness
    {
        private readonly ActiveSetState _initial;
        private IMessageBoxService? _messageBox;
        private PendingEditStore? _pendingEditStore;
        private Func<IReadOnlyDictionary<MemberNode, ActiveDb>>? _getModelToDb;
        private Func<DataBlockSummary, ActiveDb?>? _build;
        private Func<IReadOnlyList<DataBlockSummary>>? _enumerate;
        private Func<DataBlockSummary, string>? _switch;
        private Func<ActiveDb, bool>? _tryApply;
        private Func<StashedDbState, ActiveDb?, (int, int)>? _restore;
        private ActiveSetViewModel? _vm;

        public Harness(ActiveSetState initial) { _initial = initial; }

        public ActiveSetViewModel Vm => _vm ??= new ActiveSetViewModel(
            _initial,
            messageBox: _messageBox,
            pendingEditStore: _pendingEditStore,
            getModelToDb: _getModelToDb,
            getStartValueForNode: _ => null,
            buildActiveDbForSummary: _build,
            enumerateDataBlocks: _enumerate,
            switchToDataBlock: _switch,
            tryApplyActiveDbInPlace: _tryApply,
            restoreStashOntoLive: _restore,
            setStatus: _ => { },
            getPendingCount: () => _pendingEditStore?.Count ?? 0,
            dispatcher: null);

        public Harness WithMessageBox(IMessageBoxService mbx) { _messageBox = mbx; return this; }
        public Harness WithPendingStore(PendingEditStore store, Func<IReadOnlyDictionary<MemberNode, ActiveDb>> getModelToDb)
        { _pendingEditStore = store; _getModelToDb = getModelToDb; return this; }
        public Harness WithBuildActiveDbForSummary(Func<DataBlockSummary, ActiveDb?> build) { _build = build; return this; }
        public Harness WithEnumerateDataBlocks(Func<IReadOnlyList<DataBlockSummary>> enumerate) { _enumerate = enumerate; return this; }
        public Harness WithSwitchToDataBlock(Func<DataBlockSummary, string> sw) { _switch = sw; return this; }
        public Harness WithTryApply(Func<ActiveDb, bool> tryApply) { _tryApply = tryApply; return this; }
        public Harness WithRestoreStashOntoLive(Func<StashedDbState, ActiveDb?, (int, int)> restore) { _restore = restore; return this; }
    }
}
