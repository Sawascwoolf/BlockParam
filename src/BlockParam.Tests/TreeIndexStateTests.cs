using System;
using System.Collections.Generic;
using System.Linq;
using BlockParam.Localization;
using BlockParam.Models;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Phase 3 of #79 — locks in the cross-cutting invariants of the
/// <see cref="TreeIndexState"/> snapshot owned by
/// <see cref="MemberTreeViewModel"/>. Each test drives a single tree
/// transition (build, add, remove, stash+reactivate, solo) by mutating
/// the active-DB list the slice's factory closes over and re-invoking
/// <see cref="MemberTreeViewModel.BuildRootMembersFromActiveDbs"/>, then
/// asserts every invariant via <see cref="AssertTreeIndexInvariants"/>.
///
/// <para>
/// The shared helper exists so any future #79 follow-up (manual-selection
/// snapshot, picker-filter snapshot) can pick the same shape up: every
/// rebuild observable from outside the slice must satisfy the same five
/// rules regardless of which gesture got us there.
/// </para>
/// </summary>
public class TreeIndexStateTests
{
    [Fact]
    public void SingleDbBuild_NoSyntheticRoots_AllRealNodesIndexed()
    {
        var (db, _, _) = MakeFlatDb("DB_A");
        var vm = Build(activeDbs: new[] { db });

        vm.BuildRootMembersFromActiveDbs();

        AssertTreeIndexInvariants(vm);
        vm.TreeIndex.DbToSynthetic.Should().BeEmpty(
            "single-DB session never mints a synthetic group root");
    }

    [Fact]
    public void MultiDbBuild_OneSyntheticPerDb_ModelToDbCoversEveryRealDescendant()
    {
        var (dbA, _, _) = MakeNestedDb("DB_A");
        var (dbB, _, _) = MakeNestedDb("DB_B");
        var vm = Build(activeDbs: new[] { dbA, dbB });

        vm.BuildRootMembersFromActiveDbs();

        AssertTreeIndexInvariants(vm);
        vm.TreeIndex.DbToSynthetic.Should().HaveCount(2);
        vm.TreeIndex.DbToSynthetic.Keys.Should().BeEquivalentTo(new[] { dbA, dbB },
            "DbToSynthetic keys must reference-equal the live active-DB instances");
    }

    [Fact]
    public void MultiDbAdd_AddingAnotherDb_NewIndexCoversNewDbsNodes()
    {
        var (dbA, _, _) = MakeNestedDb("DB_A");
        var (dbB, speedB, _) = MakeNestedDb("DB_B");
        var activeDbs = new List<ActiveDb> { dbA };
        var vm = Build(activeDbsFactory: () => activeDbs);

        vm.BuildRootMembersFromActiveDbs();

        // Add a second DB — slice goes from single-DB to multi-DB shape.
        activeDbs.Add(dbB);
        vm.BuildRootMembersFromActiveDbs();

        AssertTreeIndexInvariants(vm);
        vm.TreeIndex.ModelToDb.Should().ContainKey(speedB);
        vm.TreeIndex.ModelToDb[speedB].Should().BeSameAs(dbB,
            "newly-added DB's nodes route to that DB instance");
    }

    [Fact]
    public void MultiDbRemoveOne_OrphanCleanup_NoStaleKeys()
    {
        var (dbA, _, _) = MakeNestedDb("DB_A");
        var (dbB, speedB, tempB) = MakeNestedDb("DB_B");
        var activeDbs = new List<ActiveDb> { dbA, dbB };
        var vm = Build(activeDbsFactory: () => activeDbs);

        vm.BuildRootMembersFromActiveDbs();
        vm.TreeIndex.ModelToVm.Should().ContainKey(speedB,
            "setup: dbB's leaf should be indexed before the remove");

        // Remove dbB → slice goes back to single-DB shape.
        activeDbs.Remove(dbB);
        vm.BuildRootMembersFromActiveDbs();

        AssertTreeIndexInvariants(vm);
        vm.TreeIndex.ModelToVm.Should().NotContainKey(speedB,
            "removed DB's nodes must be evicted from the index");
        vm.TreeIndex.ModelToVm.Should().NotContainKey(tempB,
            "every leaf of the removed DB must be evicted, not just the first");
        vm.TreeIndex.ModelToDb.Should().NotContainKey(speedB,
            "ModelToDb keys must mirror ModelToVm keys exactly");
        vm.TreeIndex.DbToSynthetic.Should().NotContainKey(dbB,
            "removed DB must lose its synthetic mapping");
    }

    [Fact]
    public void StashAndReactivate_RestoredDbsNodesAreIndexedAgain()
    {
        // Simulates the stash-then-reactivate lifecycle from the slice's
        // perspective: the same ActiveDb reference leaves the active set,
        // then re-enters. The index must reflect both transitions cleanly.
        var (dbA, _, _) = MakeNestedDb("DB_A");
        var (dbB, speedB, _) = MakeNestedDb("DB_B");
        var activeDbs = new List<ActiveDb> { dbA, dbB };
        var vm = Build(activeDbsFactory: () => activeDbs);

        vm.BuildRootMembersFromActiveDbs();
        var dbBVmBeforeStash = vm.TreeIndex.ModelToVm[speedB];

        // Stash (drop) dbB.
        activeDbs.Remove(dbB);
        vm.BuildRootMembersFromActiveDbs();
        AssertTreeIndexInvariants(vm);
        vm.TreeIndex.ModelToVm.Should().NotContainKey(speedB);

        // Reactivate (re-add) dbB.
        activeDbs.Add(dbB);
        vm.BuildRootMembersFromActiveDbs();

        AssertTreeIndexInvariants(vm);
        vm.TreeIndex.ModelToVm.Should().ContainKey(speedB,
            "reactivated DB's leaves must be re-indexed");
        // Same Model, but the VM must be a fresh instance — the slice
        // mints new VMs on every rebuild.
        vm.TreeIndex.ModelToVm[speedB].Should().NotBeSameAs(dbBVmBeforeStash,
            "rebuild mints fresh VMs even when the underlying Model reference is reused");
    }

    [Fact]
    public void SoloPeer_AllOthersGoneFromIndex()
    {
        // "Solo dbB" means: active set becomes [dbB] alone. From the
        // slice's perspective that's an active-set replacement, then
        // rebuild — same shape regardless of which higher-level command
        // triggered it.
        var (dbA, speedA, _) = MakeNestedDb("DB_A");
        var (dbB, speedB, _) = MakeNestedDb("DB_B");
        var (dbC, speedC, _) = MakeNestedDb("DB_C");
        var activeDbs = new List<ActiveDb> { dbA, dbB, dbC };
        var vm = Build(activeDbsFactory: () => activeDbs);

        vm.BuildRootMembersFromActiveDbs();
        AssertTreeIndexInvariants(vm);

        // Solo dbB: drop the other two.
        activeDbs.Clear();
        activeDbs.Add(dbB);
        vm.BuildRootMembersFromActiveDbs();

        AssertTreeIndexInvariants(vm);
        vm.TreeIndex.ModelToVm.Should().ContainKey(speedB,
            "the soloed DB's nodes remain indexed");
        vm.TreeIndex.ModelToVm.Should().NotContainKey(speedA,
            "non-soloed peers must be evicted");
        vm.TreeIndex.ModelToVm.Should().NotContainKey(speedC,
            "every non-soloed peer must be evicted, not just the first");
        vm.TreeIndex.DbToSynthetic.Should().BeEmpty(
            "single-DB shape: solo collapses to flat top-level, no synthetic roots");
    }

    [Fact]
    public void EmptyToSingle_EmptyStartingStateHasEmptyIndex()
    {
        // Slice's default state has the Empty snapshot installed; the
        // first build switches to a real one.
        var vm = Build(activeDbs: Array.Empty<ActiveDb>());

        vm.TreeIndex.Should().BeSameAs(TreeIndexState.Empty,
            "default snapshot is the shared Empty instance");
        vm.TreeIndex.ModelToVm.Should().BeEmpty();
        vm.TreeIndex.ModelToDb.Should().BeEmpty();
        vm.TreeIndex.DbToSynthetic.Should().BeEmpty();
    }

    [Fact]
    public void BuildIsAtomic_RootsRebuiltHandlerSeesConsistentSnapshot()
    {
        // The slice's RootsRebuilt event fires after both RootMembers and
        // _treeIndex are installed. Any handler — including the host's
        // pending-edit seeder — must observe a consistent snapshot where
        // every reachable node is indexed (#79 atomicity guarantee).
        var (dbA, _, _) = MakeNestedDb("DB_A");
        var (dbB, _, _) = MakeNestedDb("DB_B");
        var vm = Build(activeDbs: new[] { dbA, dbB });

        bool sawConsistentState = false;
        vm.RootsRebuilt += () =>
        {
            // Inside the handler, capture the live snapshot + reachable
            // real nodes (skipping the synthetic DB roots, which are
            // bookkeeping only) and assert every one is indexed — exactly
            // as the host's SeedVmsFromStore does after this event fires.
            var syntheticModels = new HashSet<MemberNode>(
                vm.TreeIndex.DbToSynthetic.Values.Select(v => v.Model));
            var reachableReal = vm.RootMembers
                .SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
                .Where(n => !syntheticModels.Contains(n.Model))
                .ToList();
            sawConsistentState = reachableReal.All(n => vm.TreeIndex.ModelToVm.ContainsKey(n.Model));
        };

        vm.BuildRootMembersFromActiveDbs();

        sawConsistentState.Should().BeTrue(
            "RootsRebuilt must fire only after the index covers every reachable real node");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Invariant helper
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cross-cutting invariants on <see cref="MemberTreeViewModel.TreeIndex"/>
    /// that must hold after every rebuild (#79). Mirrors the
    /// <c>AssertInvariants(vm)</c> pattern in
    /// <c>BulkChangeViewModelInvariantTests</c> but scoped to the
    /// tree-rebuild snapshot.
    ///
    /// <list type="number">
    ///   <item>Every reachable non-synthetic node is keyed in
    ///   <see cref="TreeIndexState.ModelToVm"/>.</item>
    ///   <item>Single-DB shape: no synthetic group roots; multi-DB shape:
    ///   exactly N synthetic <c>Datatype="DB"</c> roots (N = active-DB
    ///   count); <see cref="TreeIndexState.DbToSynthetic"/> count == N.</item>
    ///   <item>Multi-DB shape: every real descendant has
    ///   <see cref="TreeIndexState.ModelToDb"/> pointing to the owning
    ///   <see cref="ActiveDb"/> (by reference, never by name — #82).</item>
    ///   <item>No orphan keys: every key in
    ///   <see cref="TreeIndexState.ModelToVm"/> corresponds to a reachable
    ///   real node; no stale entries from a prior tree.</item>
    ///   <item><see cref="TreeIndexState.ModelToVm"/> and
    ///   <see cref="TreeIndexState.ModelToDb"/> have identical key sets —
    ///   they're built together and must stay in lockstep.</item>
    /// </list>
    /// </summary>
    private static void AssertTreeIndexInvariants(MemberTreeViewModel vm)
    {
        var index = vm.TreeIndex;
        var rootsList = vm.RootMembers.ToList();
        var reachable = rootsList
            .SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .ToList();

        // The synthetic group roots created for multi-DB shape carry a
        // Model with Datatype="DB" and are NOT in ModelToVm — they're
        // bookkeeping only. Filter them out for the reachable-node sweep.
        // MemberNode uses default (reference) equality so HashSet works.
        var syntheticModels = new HashSet<MemberNode>(
            index.DbToSynthetic.Values.Select(v => v.Model));
        var reachableReal = reachable
            .Where(n => !syntheticModels.Contains(n.Model))
            .ToList();

        // (1) every reachable real node is keyed in ModelToVm
        foreach (var node in reachableReal)
        {
            index.ModelToVm.Should().ContainKey(node.Model,
                $"invariant 1: every reachable real node ('{node.Path}') must be indexed");
            index.ModelToVm[node.Model].Should().BeSameAs(node,
                "invariant 1: ModelToVm must point at the live VM for the model");
        }

        // (2) tree shape matches active-set count
        if (index.DbToSynthetic.Count == 0)
        {
            // Single-DB (or empty): no synthetic group roots in RootMembers.
            rootsList.Should().AllSatisfy(r => r.Datatype.Should().NotBe("DB",
                "invariant 2 (single-DB): top-level members are flat, no synthetic group"));
        }
        else
        {
            // Multi-DB: exactly one synthetic root per ActiveDb mapped.
            rootsList.Should().HaveCount(index.DbToSynthetic.Count,
                "invariant 2 (multi-DB): one synthetic root per active DB");
            rootsList.Should().AllSatisfy(r => r.Datatype.Should().Be("DB",
                "invariant 2 (multi-DB): every root is a synthetic Datatype='DB' group"));
            index.DbToSynthetic.Values.Should().BeEquivalentTo(rootsList,
                "invariant 2 (multi-DB): DbToSynthetic values are exactly the RootMembers (order-independent — dict enumeration order is implementation-defined)");
        }

        // (3) multi-DB: every real descendant routes to its owning DB
        if (index.DbToSynthetic.Count > 0)
        {
            foreach (var kvp in index.DbToSynthetic)
            {
                var owningDb = kvp.Key;
                var syntheticVm = kvp.Value;
                foreach (var realDescendant in syntheticVm.AllDescendants())
                {
                    if (ReferenceEquals(realDescendant.Model, syntheticVm.Model)) continue;
                    index.ModelToDb.Should().ContainKey(realDescendant.Model,
                        $"invariant 3: real descendant '{realDescendant.Path}' must be in ModelToDb");
                    index.ModelToDb[realDescendant.Model].Should().BeSameAs(owningDb,
                        "invariant 3: ModelToDb routes to the synthetic root's owning ActiveDb by reference (#82)");
                }
            }
        }

        // (4) no orphan keys — every ModelToVm key is one of the
        // currently reachable real nodes.
        var reachableRealModels = new HashSet<MemberNode>(reachableReal.Select(n => n.Model));
        index.ModelToVm.Keys.Should().AllSatisfy(k =>
            reachableRealModels.Contains(k).Should().BeTrue(
                "invariant 4: no orphan keys in ModelToVm — every key " +
                "must correspond to a reachable real node in the current tree"));

        // (5) ModelToVm and ModelToDb have matching key sets
        index.ModelToDb.Keys.Should().BeEquivalentTo(index.ModelToVm.Keys,
            "invariant 5: ModelToVm and ModelToDb are built in lockstep");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fixtures (mirror MemberTreeViewModelTests so tests stay slice-local)
    // ─────────────────────────────────────────────────────────────────────

    private static MemberTreeViewModel Build(
        IReadOnlyList<ActiveDb>? activeDbs = null,
        Func<IReadOnlyList<ActiveDb>>? activeDbsFactory = null,
        string currentPlcName = "") =>
        new MemberTreeViewModel(
            getActiveDbs: activeDbsFactory ?? (() => activeDbs ?? Array.Empty<ActiveDb>()),
            getCurrentPlcName: () => currentPlcName,
            commentLanguagePolicy: new CommentLanguagePolicy(null, null, new[] { "en-GB" }),
            subscribeToVm: _ => { });

    private static (ActiveDb db, MemberNode group, MemberNode leaf) MakeNestedDb(string name)
    {
        var leafA = new MemberNode("Speed", "Int", "0", "Group.Speed", null, Array.Empty<MemberNode>());
        var leafB = new MemberNode("Temp", "Int", "0", "Group.Temp", null, Array.Empty<MemberNode>());
        var group = new MemberNode("Group", "Struct", null, "Group", null, new[] { leafA, leafB });
        var info = new DataBlockInfo(name, 1, "Optimized", "GlobalDB", new[] { group });
        return (new ActiveDb(info, $"<Block name='{name}' />"), leafA, leafB);
    }

    private static (ActiveDb db, MemberNode speed, MemberNode enableFlag) MakeFlatDb(
        string name, string plcName = "")
    {
        var speed = new MemberNode("Speed", "Int", "0", "Speed", null, Array.Empty<MemberNode>());
        var enableFlag = new MemberNode("Enable", "Bool", "false", "Enable", null, Array.Empty<MemberNode>());
        var info = new DataBlockInfo(name, 1, "Optimized", "GlobalDB", new[] { speed, enableFlag });
        return (new ActiveDb(info, $"<Block name='{name}' />", onApply: null, plcName: plcName),
                speed, enableFlag);
    }
}
