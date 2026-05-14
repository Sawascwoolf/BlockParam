using System.Collections.Generic;
using System.Linq;
using BlockParam.Localization;
using BlockParam.Models;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Focused tests for the tree-shape + flat-list + expand/collapse slice
/// (#80 slice 7a).
/// </summary>
public class MemberTreeViewModelTests
{
    [Fact]
    public void Defaults_EmptyTreeUntilBuildCalled()
    {
        var vm = Build(activeDbs: Array.Empty<ActiveDb>());

        vm.RootMembers.Should().BeEmpty();
        vm.FlatMembers.Should().BeEmpty();
        vm.ModelToVm.Should().BeEmpty();
        vm.ModelToDb.Should().BeEmpty();
        vm.DbToSynthetic.Should().BeEmpty();
        vm.IsRefreshing.Should().BeFalse();
    }

    [Fact]
    public void BuildFromActiveDbs_SingleDb_FlatTopLevelMembersAndIndexedLookups()
    {
        var (db, speed, _) = MakeFlatDb("DB_Flat");
        var vm = Build(activeDbs: new[] { db });

        vm.BuildRootMembersFromActiveDbs();

        // Single-DB shape: top-level members directly at root, no synthetic wrapper.
        vm.RootMembers.Should().HaveCount(2,
            "single-DB session puts the anchor DB's top-level members at root");
        vm.RootMembers.Should().AllSatisfy(r => r.Datatype.Should().NotBe("DB"));
        vm.DbToSynthetic.Should().BeEmpty(
            "single-DB session does not create synthetic group roots");

        // Lookup dicts must cover every member by reference.
        vm.ModelToVm.Should().ContainKey(speed);
        vm.ModelToDb[speed].Should().BeSameAs(db);
    }

    [Fact]
    public void BuildFromActiveDbs_MultiDb_SyntheticGroupPerDb()
    {
        var (dbA, _, _) = MakeFlatDb("DB_A");
        var (dbB, _, _) = MakeFlatDb("DB_B");
        var vm = Build(activeDbs: new[] { dbA, dbB });

        vm.BuildRootMembersFromActiveDbs();

        // Multi-DB shape: one synthetic group root per active DB.
        vm.RootMembers.Should().HaveCount(2);
        vm.RootMembers.Should().AllSatisfy(r => r.Datatype.Should().Be("DB",
            "every multi-DB root is a synthetic group wrapper"));
        vm.DbToSynthetic.Should().HaveCount(2);
        vm.DbToSynthetic[dbA].Should().NotBeNull();
        vm.DbToSynthetic[dbB].Should().NotBeNull();

        // Synthetic group is expanded by default so children are visible.
        vm.RootMembers.Should().AllSatisfy(r => r.IsExpanded.Should().BeTrue());
    }

    [Fact]
    public void BuildFromActiveDbs_MultiDb_NameCollision_PrefixesByPlc()
    {
        var (dbA, _, _) = MakeFlatDb("DB_Foo", plcName: "PLC_1");
        var (dbB, _, _) = MakeFlatDb("DB_Foo", plcName: "PLC_2");
        var vm = Build(
            activeDbs: new[] { dbA, dbB },
            currentPlcName: "PLC_1");

        vm.BuildRootMembersFromActiveDbs();

        // Cross-PLC collision: both names match → both synthetic roots are
        // prefixed with their owning PLC so the user can tell them apart.
        vm.RootMembers.Select(r => r.Name).Should().Equal(
            "PLC_1 / DB_Foo", "PLC_2 / DB_Foo");
    }

    [Fact]
    public void BuildFromActiveDbs_RaisesRootsRebuilt()
    {
        var (db, _, _) = MakeFlatDb("DB_X");
        var vm = Build(activeDbs: new[] { db });

        var rebuiltCount = 0;
        vm.RootsRebuilt += () => rebuiltCount++;

        vm.BuildRootMembersFromActiveDbs();
        rebuiltCount.Should().Be(1);

        // Re-build fires again (host uses this to seed pending edits).
        vm.BuildRootMembersFromActiveDbs();
        rebuiltCount.Should().Be(2);
    }

    /// <summary>
    /// The host's <c>SeedVmsFromStore</c> subscribes to <c>RootsRebuilt</c>
    /// and immediately calls <c>Tree.ModelToVm.TryGetValue(node, ...)</c> on
    /// every pending entry. If the slice ever fires the event before the
    /// lookup dictionaries are populated, pending-edit seeding would
    /// silently drop every entry — which is exactly the kind of regression
    /// the slice extraction was supposed to prevent.
    /// </summary>
    [Fact]
    public void BuildFromActiveDbs_RootsRebuilt_FiresAfterLookupsArePopulated()
    {
        var (db, speed, _) = MakeFlatDb("DB_X");
        var vm = Build(activeDbs: new[] { db });

        bool lookupReadyInHandler = false;
        bool speedResolvedInHandler = false;
        vm.RootsRebuilt += () =>
        {
            lookupReadyInHandler = vm.ModelToVm.Count > 0;
            speedResolvedInHandler = vm.FindVmByModel(speed) != null;
        };

        vm.BuildRootMembersFromActiveDbs();

        lookupReadyInHandler.Should().BeTrue(
            "ModelToVm must be populated before the event fires — the host's " +
            "SeedVmsFromStore looks up by MemberNode and would silently drop " +
            "every pending edit if the dict were still empty");
        speedResolvedInHandler.Should().BeTrue(
            "FindVmByModel must work inside the handler for every minted node");
    }

    [Fact]
    public void BuildFromActiveDbs_ClearsPriorLookupsAndRoots()
    {
        var (db1, _, _) = MakeFlatDb("DB_1");
        var (db2, _, _) = MakeFlatDb("DB_2");
        var activeDbs = new List<ActiveDb> { db1 };
        var vm = Build(activeDbs: () => activeDbs);

        vm.BuildRootMembersFromActiveDbs();
        var firstRoot = vm.RootMembers[0];
        var firstModel = firstRoot.Model;
        vm.ModelToVm.Should().ContainKey(firstModel);

        // Swap the active set and rebuild — old VMs/models must be evicted.
        activeDbs.Clear();
        activeDbs.Add(db2);
        vm.BuildRootMembersFromActiveDbs();

        vm.RootMembers.Should().NotContain(firstRoot,
            "rebuild must mint fresh VMs from the new active set");
        vm.ModelToVm.Should().NotContainKey(firstModel,
            "lookups must be cleared so writes don't route to disposed VMs");
    }

    [Fact]
    public void BuildFromActiveDbs_SingleDb_InvokesSubscribeCallbackForEveryMintedVm()
    {
        var (db, _, _) = MakeFlatDb("DB_S");
        var subscribed = new List<MemberNodeViewModel>();
        var vm = Build(activeDbs: new[] { db }, subscribeToVm: subscribed.Add);

        vm.BuildRootMembersFromActiveDbs();

        subscribed.Should().NotBeEmpty(
            "host's StartValueEdited subscription must run on every new VM " +
            "so inline edits in the new tree route into the pending store");
        // Each subscription is for a distinct minted VM.
        subscribed.Distinct().Count().Should().Be(subscribed.Count);
    }

    /// <summary>
    /// Multi-DB rebuilds go through <c>AddDbGroupRoot</c>, which iterates
    /// every descendant of the synthetic group VM and invokes
    /// <c>_subscribeToVm</c> per node. Single-DB coverage (above) only
    /// exercises the flat top-level + descendant walk on a fixture without
    /// nested children; this test locks the multi-DB descendant-walk so an
    /// accidental "subscribe roots only" regression would leave nested
    /// members unwired for inline editing.
    ///
    /// <para>
    /// Post-#108 the callback is non-recursive by contract — the slice
    /// invokes it once per minted VM, including the synthetic group root
    /// itself. The synthetic root's events never fire (it has no
    /// <c>StartValue</c>), but subscribing it keeps the "one callback per
    /// minted VM" contract uniform between the single-DB and multi-DB
    /// paths.
    /// </para>
    /// </summary>
    [Fact]
    public void BuildFromActiveDbs_MultiDb_InvokesSubscribeCallbackForEveryDescendant()
    {
        var (dbA, _, _) = MakeNestedDb("DB_A");
        var (dbB, _, _) = MakeNestedDb("DB_B");
        var subscribed = new List<MemberNodeViewModel>();
        var vm = Build(activeDbs: new[] { dbA, dbB }, subscribeToVm: subscribed.Add);

        vm.BuildRootMembersFromActiveDbs();

        // MakeNestedDb produces one struct ("Group") with two leaves
        // ("Speed", "Temp") per DB. Per #108's non-recursive contract,
        // AddDbGroupRoot subscribes the synthetic root + every descendant:
        // synthetic + Group + Speed + Temp = 4 per DB. Two DBs = 8 total.
        // An accidental "subscribe roots only" or "subscribe leaves only"
        // regression would shift this number.
        subscribed.Should().HaveCount(8,
            "every minted VM (synthetic root + every descendant) in every " +
            "active DB's subtree must be wired");
        // Both DB subtrees must contribute — a single-DB regression would
        // produce 4 entries from dbA only.
        subscribed.Select(v => v.Name).Where(n => n == "Speed").Should().HaveCount(2,
            "the Speed leaf in each of the two active DBs must be wired");
        subscribed.Select(v => v.Name).Where(n => n == "Temp").Should().HaveCount(2,
            "the Temp leaf in each of the two active DBs must be wired");
    }

    /// <summary>
    /// Regression for #108: the slice's <c>_subscribeToVm</c> callback is
    /// non-recursive by contract — register on the passed node only, do
    /// NOT walk <c>node.Children</c>. A previous host implementation was
    /// itself recursive, and combined with <see cref="MemberTreeViewModel"/>'s
    /// per-descendant loop in <c>AddDbGroupRoot</c> produced one
    /// <c>StartValueEdited</c> / <c>SelectedChanged</c> handler per node
    /// for every ancestor between the root and the leaf — so an inline
    /// edit on a deeply-nested leaf fired N (= depth) sweeps of the tree.
    ///
    /// This test exercises the multi-DB shape with a nested subtree (the
    /// shape that triggered the bug) and asserts each minted VM gets the
    /// callback exactly once. The constructor doc on <see cref="_subscribeToVm"/>
    /// (in production code) is the single source of truth for the contract.
    /// </summary>
    [Fact]
    public void BuildFromActiveDbs_MultiDb_InvokesSubscribeCallbackExactlyOncePerNode()
    {
        var (dbA, _, _) = MakeNestedDb("DB_A");
        var (dbB, _, _) = MakeNestedDb("DB_B");
        var perNodeCount = new Dictionary<MemberNodeViewModel, int>();
        var vm = Build(
            activeDbs: new[] { dbA, dbB },
            subscribeToVm: node =>
            {
                perNodeCount.TryGetValue(node, out var c);
                perNodeCount[node] = c + 1;
            });

        vm.BuildRootMembersFromActiveDbs();

        // No minted VM may receive the callback more than once — a
        // recursive host (the pre-#108 shape) plus the slice's per-
        // descendant loop would produce depth-many entries for non-leaves.
        perNodeCount.Values.Should().AllSatisfy(c => c.Should().Be(1,
            "non-recursive contract: each minted VM is subscribed exactly once"));
    }

    [Fact]
    public void FindVmByModel_ReturnsModelToVmEntry()
    {
        var (db, speed, _) = MakeFlatDb("DB_F");
        var vm = Build(activeDbs: new[] { db });
        vm.BuildRootMembersFromActiveDbs();

        var resolved = vm.FindVmByModel(speed);

        resolved.Should().NotBeNull();
        resolved!.Model.Should().BeSameAs(speed);
    }

    [Fact]
    public void FindVmByModel_StaleModel_ReturnsNull()
    {
        var (db, _, _) = MakeFlatDb("DB_F");
        var (_, otherSpeed, _) = MakeFlatDb("DB_Other");
        var vm = Build(activeDbs: new[] { db });
        vm.BuildRootMembersFromActiveDbs();

        // Model from a DB that isn't in the active set — must not resolve.
        vm.FindVmByModel(otherSpeed).Should().BeNull();
    }

    [Fact]
    public void FindActiveDbForModel_ResolvesOwningDb()
    {
        var (db, speed, _) = MakeFlatDb("DB_F");
        var vm = Build(activeDbs: new[] { db });
        vm.BuildRootMembersFromActiveDbs();

        vm.FindActiveDbForModel(speed).Should().BeSameAs(db);
    }

    [Fact]
    public void FindNodeByPathInDb_SingleDb_LocatesByPathString()
    {
        // Single-DB shape (no synthetic root): the in-db lookup still
        // works because the slice falls back to walking RootMembers when
        // owner is the sole active DB (#82).
        var (db, _, _) = MakeFlatDb("DB_F");
        var vm = Build(activeDbs: new[] { db });
        vm.BuildRootMembersFromActiveDbs();

        var found = vm.FindNodeByPathInDb("Speed", db);
        found.Should().NotBeNull();
        found!.Name.Should().Be("Speed");

        vm.FindNodeByPathInDb("DoesNotExist", db).Should().BeNull();
    }

    [Fact]
    public void FindNodeByPathInDb_RestrictsToOwningDb()
    {
        var (dbA, _, _) = MakeFlatDb("DB_A");
        var (dbB, _, _) = MakeFlatDb("DB_B");
        var vm = Build(activeDbs: new[] { dbA, dbB });
        vm.BuildRootMembersFromActiveDbs();

        var foundInA = vm.FindNodeByPathInDb("Speed", dbA);
        foundInA.Should().NotBeNull();
        // Reference points into dbA's subtree, not dbB's.
        vm.FindActiveDbForModel(foundInA!.Model).Should().BeSameAs(dbA);

        var foundInB = vm.FindNodeByPathInDb("Speed", dbB);
        foundInB.Should().NotBeNull();
        vm.FindActiveDbForModel(foundInB!.Model).Should().BeSameAs(dbB);
        foundInB.Should().NotBeSameAs(foundInA,
            "same path string in two DBs must resolve to distinct VMs");
    }

    [Fact]
    public void FindNodeByPathInDb_NotInActiveSet_ReturnsNull()
    {
        // The single-DB fallback only resolves when owner IS the sole
        // active DB. An owner from a different fixture must NOT silently
        // fall through to the first root in RootMembers (#82).
        var (dbA, _, _) = MakeFlatDb("DB_A");
        var (dbStranger, _, _) = MakeFlatDb("DB_Stranger");
        var vm = Build(activeDbs: new[] { dbA });
        vm.BuildRootMembersFromActiveDbs();

        vm.FindNodeByPathInDb("Speed", dbStranger).Should().BeNull(
            "stranger DB is not in the active set — fall-through would " +
            "be exactly the cross-DB aliasing the in-db variant exists to prevent");
    }

    [Fact]
    public void RebuildFlatList_PopulatesFlatMembers()
    {
        var (db, _, _) = MakeFlatDb("DB_F");
        var vm = Build(activeDbs: new[] { db });
        vm.BuildRootMembersFromActiveDbs();

        vm.RebuildFlatList();

        vm.FlatMembers.Should().NotBeEmpty();
        vm.FlatMembers.Should().Contain(m => m.Name == "Speed");
    }

    [Fact]
    public void RebuildFlatList_InvokesInsideRefreshScopeWhileIsRefreshingIsTrue()
    {
        var (db, _, _) = MakeFlatDb("DB_F");
        var vm = Build(activeDbs: new[] { db });
        vm.BuildRootMembersFromActiveDbs();

        bool wasTrueInsideCallback = false;
        int flatCountInsideCallback = -1;
        vm.RebuildFlatList(insideRefreshScope: () =>
        {
            wasTrueInsideCallback = vm.IsRefreshing;
            flatCountInsideCallback = vm.FlatMembers.Count;
        });

        wasTrueInsideCallback.Should().BeTrue(
            "the callback runs after the flat list rebuild but before IsRefreshing " +
            "clears — host uses this scope to restore selection without re-entering " +
            "OnMemberSelected");
        flatCountInsideCallback.Should().BeGreaterThan(0,
            "the doc comment promises the flat list is *already* rebuilt when the " +
            "callback fires, so selection-restore code can look up the new VM by " +
            "path against FlatMembers");
        vm.IsRefreshing.Should().BeFalse(
            "the flag clears once the callback returns");
    }

    [Fact]
    public void RebuildFlatList_CallbackThrows_StillClearsIsRefreshing()
    {
        var (db, _, _) = MakeFlatDb("DB_F");
        var vm = Build(activeDbs: new[] { db });
        vm.BuildRootMembersFromActiveDbs();

        var act = () => vm.RebuildFlatList(insideRefreshScope: () =>
            throw new InvalidOperationException("callback boom"));

        // Whatever the callback does, the slice must not leak IsRefreshing=true —
        // otherwise the SelectedFlatMember setter on the host stays permanently
        // gated against re-entry.
        act.Should().Throw<InvalidOperationException>();
        vm.IsRefreshing.Should().BeFalse(
            "try/finally must clear IsRefreshing even when the callback throws");
    }

    [Fact]
    public void RebuildFlatList_ReEntry_IsSilentlyIgnored()
    {
        var (db, _, _) = MakeFlatDb("DB_F");
        var vm = Build(activeDbs: new[] { db });
        vm.BuildRootMembersFromActiveDbs();

        int innerInvocations = 0;
        vm.RebuildFlatList(insideRefreshScope: () =>
        {
            // Re-enter from within the scope — the second call must be a no-op
            // because IsRefreshing is true.
            vm.RebuildFlatList(insideRefreshScope: () => innerInvocations++);
        });

        innerInvocations.Should().Be(0,
            "re-entry while IsRefreshing must short-circuit before the inner callback runs");
    }

    [Fact]
    public void ToggleExpand_FlipsIsExpandedAndRebuildsFlatList()
    {
        var (dbA, _, _) = MakeNestedDb("DB_N");
        var vm = Build(activeDbs: new[] { dbA });
        vm.BuildRootMembersFromActiveDbs();
        vm.RebuildFlatList();

        var nestedRoot = vm.RootMembers[0];
        nestedRoot.IsExpanded.Should().BeFalse("single-DB roots start collapsed");

        var beforeCount = vm.FlatMembers.Count;
        vm.ToggleExpand(nestedRoot);

        nestedRoot.IsExpanded.Should().BeTrue();
        vm.FlatMembers.Count.Should().BeGreaterThan(beforeCount,
            "expanding a root must reveal its children in the flat list");
    }

    [Fact]
    public void ExpandAllCommand_ExpandsEveryRoot()
    {
        var (dbA, _, _) = MakeNestedDb("DB_N");
        var vm = Build(activeDbs: new[] { dbA });
        vm.BuildRootMembersFromActiveDbs();

        vm.ExpandAllCommand.Execute(null);

        vm.RootMembers.Should().AllSatisfy(r => r.IsExpanded.Should().BeTrue());
    }

    [Fact]
    public void CollapseAllCommand_CollapsesEveryRoot()
    {
        var (dbA, _, _) = MakeNestedDb("DB_N");
        var vm = Build(activeDbs: new[] { dbA });
        vm.BuildRootMembersFromActiveDbs();
        vm.ExpandAllCommand.Execute(null);

        vm.CollapseAllCommand.Execute(null);

        vm.RootMembers.Should().AllSatisfy(r => r.IsExpanded.Should().BeFalse());
    }

    [Fact]
    public void ExpandAllChildren_OnGroupRoot_ExpandsEveryDescendant()
    {
        var (dbA, _, _) = MakeNestedDb("DB_N");
        var vm = Build(activeDbs: new[] { dbA });
        vm.BuildRootMembersFromActiveDbs();

        var root = vm.RootMembers[0];
        vm.ExpandAllChildren(root);

        foreach (var d in root.AllDescendants())
        {
            if (d.HasChildren) d.IsExpanded.Should().BeTrue();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fixtures
    // ─────────────────────────────────────────────────────────────────────

    private static MemberTreeViewModel Build(
        IReadOnlyList<ActiveDb>? activeDbs = null,
        Func<IReadOnlyList<ActiveDb>>? activeDbsFactory = null,
        string currentPlcName = "",
        Action<MemberNodeViewModel>? subscribeToVm = null) =>
        new MemberTreeViewModel(
            getActiveDbs: activeDbsFactory ?? (() => activeDbs ?? Array.Empty<ActiveDb>()),
            getCurrentPlcName: () => currentPlcName,
            commentLanguagePolicy: new CommentLanguagePolicy(null, null, new[] { "en-GB" }),
            subscribeToVm: subscribeToVm ?? (_ => { }));

    /// <summary>Overload accepting a live factory so tests can swap the
    /// active set between calls without rebuilding the slice.</summary>
    private static MemberTreeViewModel Build(
        Func<IReadOnlyList<ActiveDb>> activeDbs,
        string currentPlcName = "",
        Action<MemberNodeViewModel>? subscribeToVm = null) =>
        new MemberTreeViewModel(
            getActiveDbs: activeDbs,
            getCurrentPlcName: () => currentPlcName,
            commentLanguagePolicy: new CommentLanguagePolicy(null, null, new[] { "en-GB" }),
            subscribeToVm: subscribeToVm ?? (_ => { }));

    /// <summary>
    /// Two top-level members, one of which is a leaf "Speed". Fully usable
    /// as an <see cref="ActiveDb"/>.
    /// </summary>
    private static (ActiveDb db, MemberNode speed, MemberNode enableFlag) MakeFlatDb(
        string name, string plcName = "")
    {
        var speed = new MemberNode("Speed", "Int", "0", "Speed", null, Array.Empty<MemberNode>());
        var enableFlag = new MemberNode("Enable", "Bool", "false", "Enable", null, Array.Empty<MemberNode>());
        var info = new DataBlockInfo(name, 1, "Optimized", "GlobalDB", new[] { speed, enableFlag });
        return (new ActiveDb(info, $"<Block name='{name}' />", onApply: null, plcName: plcName),
                speed, enableFlag);
    }

    /// <summary>
    /// One nested struct member with two children. Used by expand/collapse
    /// tests that need a real ancestor-with-descendants shape.
    /// </summary>
    private static (ActiveDb db, MemberNode group, MemberNode leaf) MakeNestedDb(string name)
    {
        var leafA = new MemberNode("Speed", "Int", "0", "Group.Speed", null, Array.Empty<MemberNode>());
        var leafB = new MemberNode("Temp", "Int", "0", "Group.Temp", null, Array.Empty<MemberNode>());
        var group = new MemberNode("Group", "Struct", null, "Group", null, new[] { leafA, leafB });
        var info = new DataBlockInfo(name, 1, "Optimized", "GlobalDB", new[] { group });
        return (new ActiveDb(info, $"<Block name='{name}' />"), group, leafA);
    }
}
