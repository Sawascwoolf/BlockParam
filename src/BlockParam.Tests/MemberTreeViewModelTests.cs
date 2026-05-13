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
    /// exercises the top-level loop; this test locks the multi-DB
    /// descendant-walk so an accidental "subscribe roots only" regression
    /// would leave nested members unwired for inline editing.
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
        // ("Speed", "Temp") per DB. Multi-DB also adds the synthetic group VM
        // per DB. Subscribe is called for every descendant including the
        // synthetic group itself (which is fine — the synthetic node has no
        // StartValueEdited semantics, the subscription is a no-op).
        subscribed.Should().HaveCountGreaterOrEqualTo(
            /* per DB: synthetic + Group + Speed + Temp = 4, two DBs = 8 */ 8,
            "every descendant in every active DB's synthetic subtree must be wired");
        // Both DB subtrees must contribute — a single-DB regression would
        // produce 4 entries from dbA only.
        subscribed.Select(v => v.Name).Should().Contain("Speed");
        subscribed.Select(v => v.Name).Should().Contain("Temp");
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
    public void FindNodeByPath_LocatesByPathString()
    {
        var (db, _, _) = MakeFlatDb("DB_F");
        var vm = Build(activeDbs: new[] { db });
        vm.BuildRootMembersFromActiveDbs();

        var found = vm.FindNodeByPath("Speed");
        found.Should().NotBeNull();
        found!.Name.Should().Be("Speed");

        vm.FindNodeByPath("DoesNotExist").Should().BeNull();
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
