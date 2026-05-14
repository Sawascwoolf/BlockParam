using System.Collections.Generic;
using System.Linq;
using BlockParam.Models;
using BlockParam.Services;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Focused tests for the selection + scope + manual-selection slice
/// (#80 slice 7b).
/// </summary>
public class SelectionScopeViewModelTests
{
    [Fact]
    public void Defaults_EmptySelectionAndScope()
    {
        var (vm, _, _) = Build();

        vm.SelectedFlatMember.Should().BeNull();
        vm.SelectedScope.Should().BeNull();
        vm.AvailableScopes.Should().BeEmpty();
        vm.ManualSelectedPaths.Should().BeEmpty();
        vm.IsManualMode.Should().BeFalse();
        vm.ManualSelectionCount.Should().Be(0);
        vm.HasSelection.Should().BeFalse();
        vm.HasScope.Should().BeFalse();
        vm.CanEdit.Should().BeFalse();
        vm.IsSelectionTypeHomogeneous.Should().BeTrue();
        vm.ManualSelectionSummary.Should().BeEmpty();
        vm.SelectedMemberDisplay.Should().NotBeNullOrEmpty(
            "should show the localized 'click to select' placeholder when nothing is selected");
    }

    [Fact]
    public void SelectedFlatMember_Setter_RaisesPropertyChangedAndMemberChangedEvent()
    {
        var (vm, tree, _) = BuildWithSingleDb();
        var leafVm = tree.RootMembers.First(r => r.IsLeaf);

        var memberChangedFires = new List<MemberNodeViewModel?>();
        var changedProps = new List<string?>();
        vm.MemberChanged += v => memberChangedFires.Add(v);
        vm.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        vm.SelectedFlatMember = leafVm;

        memberChangedFires.Should().ContainSingle().Which.Should().BeSameAs(leafVm,
            "the slice fires MemberChanged so the host can run OnMemberSelected");
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.SelectedFlatMember));
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.HasSelection));
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.SelectedMemberDisplay));
    }

    [Fact]
    public void SelectedFlatMember_SetterSuppressed_WhenTreeIsRefreshing()
    {
        var (vm, tree, _) = BuildWithSingleDb();
        var leafVm = tree.RootMembers.First(r => r.IsLeaf);

        // Drive Tree into refreshing state and assign during the scope.
        bool setterRanDuringRefresh = false;
        tree.RebuildFlatList(insideRefreshScope: () =>
        {
            // Tree.IsRefreshing == true here. SelectedFlatMember setter
            // must short-circuit (existing OnMemberSelected pipeline must
            // not run on a half-refreshed flat list).
            vm.SelectedFlatMember = leafVm;
            setterRanDuringRefresh = true;
        });

        setterRanDuringRefresh.Should().BeTrue();
        vm.SelectedFlatMember.Should().BeNull(
            "the setter must be a no-op while Tree.IsRefreshing is true to avoid " +
            "racing the flat-list rebuild");
    }

    [Fact]
    public void SetSelectedFlatMemberSilent_RaisesPropertyChangedButNotMemberChanged()
    {
        var (vm, tree, _) = BuildWithSingleDb();
        var leafVm = tree.RootMembers.First(r => r.IsLeaf);

        var memberChangedFires = 0;
        var changedProps = new List<string?>();
        vm.MemberChanged += _ => memberChangedFires++;
        vm.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        vm.SetSelectedFlatMemberSilent(leafVm);

        memberChangedFires.Should().Be(0,
            "silent setter suppresses MemberChanged — host's RefreshTree / dispatcher " +
            "re-sync paths already handled the side effects");
        vm.SelectedFlatMember.Should().BeSameAs(leafVm);
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.SelectedFlatMember));
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.HasSelection));
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.SelectedMemberDisplay));
    }

    [Fact]
    public void SelectedScope_Setter_RaisesPropertyChangedAndScopeChangedEvent()
    {
        var (vm, _, _) = Build();
        var scope = new ScopeLevel("root", "Root", 1, Array.Empty<MemberNode>());
        vm.AvailableScopes.Add(scope);

        var scopeChangedFires = 0;
        var changedProps = new List<string?>();
        vm.ScopeChanged += () => scopeChangedFires++;
        vm.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        vm.SelectedScope = scope;

        scopeChangedFires.Should().Be(1);
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.SelectedScope));
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.HasScope));
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.CanEdit));
    }

    [Fact]
    public void SetSelectedScopeSilent_RaisesPropertyChangedButNotScopeChanged()
    {
        var (vm, _, _) = Build();
        var scope = new ScopeLevel("root", "Root", 1, Array.Empty<MemberNode>());

        var scopeChangedFires = 0;
        var changedProps = new List<string?>();
        vm.ScopeChanged += () => scopeChangedFires++;
        vm.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        vm.SetSelectedScopeSilent(scope);

        scopeChangedFires.Should().Be(0,
            "silent setter suppresses ScopeChanged so callers that re-fire " +
            "UpdateHighlighting themselves don't double-run it");
        vm.SelectedScope.Should().BeSameAs(scope);
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.SelectedScope));
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.HasScope));
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.CanEdit));
    }

    /// <summary>
    /// SetSelectedScopeSilent uses SetProperty&lt;T&gt; which short-circuits when
    /// the value is unchanged. Pre-PR the host raised PropertyChanged for
    /// SelectedScope / HasScope / CanEdit unconditionally — this test locks
    /// the new contract: a null→null silent set must raise NOTHING. If you
    /// ever decide to revert to the unconditional pattern, flip this test's
    /// expectations rather than letting it silently drift.
    /// </summary>
    [Fact]
    public void SetSelectedScopeSilent_NullOnNull_IsCompletelyQuiet()
    {
        var (vm, _, _) = Build();
        vm.SelectedScope.Should().BeNull("setup: starts null");

        var scopeChangedFires = 0;
        var changedProps = new List<string?>();
        vm.ScopeChanged += () => scopeChangedFires++;
        vm.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        vm.SetSelectedScopeSilent(null);

        scopeChangedFires.Should().Be(0);
        changedProps.Should().BeEmpty(
            "null→null is a no-op — SetProperty short-circuits on equality. " +
            "If we ever decide bindings need an explicit nudge here, the " +
            "fix is in the slice, not in callers paying twice for nothing.");
    }

    /// <summary>
    /// <see cref="SelectedFlatMember"/> short-circuits while
    /// <see cref="MemberTreeViewModel.IsRefreshing"/> is true — verified by
    /// <see cref="SelectedFlatMember_SetterSuppressed_WhenTreeIsRefreshing"/>.
    /// <see cref="SelectedScope"/> does NOT have the same guard. That's
    /// intentional: <c>RefreshTree</c> assigns <c>SelectedScope</c> while
    /// inside the flat-list rebuild scope (to restore the user's prior
    /// scope after Apply), and gating it would silently drop the restore.
    /// Negative test so an "unhelpful symmetry" fix doesn't ship.
    /// </summary>
    [Fact]
    public void SelectedScope_Setter_NotGated_OnTreeIsRefreshing()
    {
        var (vm, tree, _) = BuildWithSingleDb();
        var scope = new ScopeLevel("root", "Root", 1, Array.Empty<MemberNode>());
        vm.AvailableScopes.Add(scope);

        ScopeLevel? scopeInsideRefresh = null;
        tree.RebuildFlatList(insideRefreshScope: () =>
        {
            vm.SelectedScope = scope;
            scopeInsideRefresh = vm.SelectedScope;
        });

        scopeInsideRefresh.Should().BeSameAs(scope,
            "assigning SelectedScope while Tree.IsRefreshing is true must take effect — " +
            "RefreshTree depends on this for selection-restore");
    }

    [Fact]
    public void HasScope_FalseWhenInManualMode_EvenIfSelectedScopeNonNull()
    {
        var (vm, tree, _) = BuildWithSingleDb();
        var leaves = tree.RootMembers.Where(r => r.IsLeaf).Take(2).ToList();
        var scope = new ScopeLevel("root", "Root", 1, Array.Empty<MemberNode>());
        vm.AvailableScopes.Add(scope);
        vm.SetSelectedScopeSilent(scope);

        vm.HasScope.Should().BeTrue("scope is set and we're not in manual mode yet");

        // Enter manual mode (2 leaves).
        vm.AddManualPath(leaves[0]);
        vm.AddManualPath(leaves[1]);
        vm.RaiseManualSelectionChanged();

        vm.IsManualMode.Should().BeTrue();
        vm.HasScope.Should().BeFalse(
            "manual mode supersedes scope — HasScope must reflect that even " +
            "though SelectedScope still holds a value");
        vm.CanEdit.Should().BeTrue("manual mode is editable too");
    }

    [Fact]
    public void IsManualMode_True_OnlyWhenTwoOrMorePathsSelected()
    {
        var (vm, tree, _) = BuildWithSingleDb();
        var leaves = tree.RootMembers.Where(r => r.IsLeaf).Take(2).ToList();

        vm.AddManualPath(leaves[0]);
        vm.IsManualMode.Should().BeFalse("1 leaf → not manual mode");

        vm.AddManualPath(leaves[1]);
        vm.IsManualMode.Should().BeTrue("2 leaves → manual mode");

        vm.RemoveManualPath(leaves[0]);
        vm.IsManualMode.Should().BeFalse("dropping back to 1 leaf exits manual mode");
    }

    [Fact]
    public void AddManualPath_ReturnsFalseOnDuplicate()
    {
        var (vm, tree, _) = BuildWithSingleDb();
        var leaf = tree.RootMembers.First(r => r.IsLeaf);

        vm.AddManualPath(leaf).Should().BeTrue("first add changes the set");
        vm.AddManualPath(leaf).Should().BeFalse("re-adding the same VM is a no-op");
    }

    [Fact]
    public void RemoveManualPath_ReturnsFalseWhenAbsent()
    {
        var (vm, tree, _) = BuildWithSingleDb();
        var leaf = tree.RootMembers.First(r => r.IsLeaf);

        vm.RemoveManualPath(leaf).Should().BeFalse("never added → remove is a no-op");

        vm.AddManualPath(leaf);
        vm.RemoveManualPath(leaf).Should().BeTrue("present → remove returns true");
    }

    [Fact]
    public void RaiseManualSelectionChanged_NotifiesAllDerivedProperties()
    {
        var (vm, tree, _) = BuildWithSingleDb();
        var leaves = tree.RootMembers.Where(r => r.IsLeaf).Take(2).ToList();
        foreach (var l in leaves) vm.AddManualPath(l);

        var changedProps = new List<string?>();
        var manualChangedFires = 0;
        vm.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);
        vm.ManualSelectionChanged += () => manualChangedFires++;

        vm.RaiseManualSelectionChanged();

        manualChangedFires.Should().Be(1);
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.IsManualMode));
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.ManualSelectionCount));
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.ManualSelectionSummary));
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.IsSelectionTypeHomogeneous));
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.HasScope));
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.CanEdit));
        changedProps.Should().Contain(nameof(SelectionScopeViewModel.SelectedMemberDisplay));
    }

    [Fact]
    public void ClearManualPaths_EmptiesSetSilently()
    {
        var (vm, tree, _) = BuildWithSingleDb();
        var leaves = tree.RootMembers.Where(r => r.IsLeaf).Take(2).ToList();
        foreach (var l in leaves) vm.AddManualPath(l);

        var manualChangedFires = 0;
        vm.ManualSelectionChanged += () => manualChangedFires++;

        vm.ClearManualPaths();

        vm.ManualSelectedPaths.Should().BeEmpty();
        vm.IsManualMode.Should().BeFalse();
        manualChangedFires.Should().Be(0,
            "ClearManualPaths is silent so callers bundle the manual-changed " +
            "notification with their wider clear-cascade side effects");
    }

    [Fact]
    public void IsSelectionTypeHomogeneous_FalseWhenMixedDatatypes()
    {
        var (vm, tree, _) = BuildWithSingleDb();
        // MakeFlatDb has Speed (Int) and Enable (Bool) — distinct datatypes.
        var intLeaf = tree.RootMembers.First(r => r.Name == "Speed");
        var boolLeaf = tree.RootMembers.First(r => r.Name == "Enable");
        vm.AddManualPath(intLeaf);
        vm.AddManualPath(boolLeaf);

        vm.IsManualMode.Should().BeTrue();
        vm.IsSelectionTypeHomogeneous.Should().BeFalse(
            "Int + Bool → mixed datatypes — manual-mode validation blocks Apply " +
            "when types differ");
        vm.GetSelectedDatatypes().Should().HaveCount(2);
    }

    [Fact]
    public void SelectedMemberDisplay_FallsBackToManualSummary_InManualMode()
    {
        var (vm, tree, _) = BuildWithSingleDb();
        var leaves = tree.RootMembers.Where(r => r.IsLeaf).Take(2).ToList();
        foreach (var l in leaves) vm.AddManualPath(l);

        // Even though SelectedFlatMember is null, manual-mode wins.
        vm.SelectedMemberDisplay.Should().Be(vm.ManualSelectionSummary);
        vm.ManualSelectionSummary.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void OnNodeSelected_DeselectsAllOtherNodes_GlobalSingleFocus()
    {
        // Two-DB tree so leaves in different DBs would otherwise be selected
        // simultaneously without the cascade.
        var (vm, tree, _) = BuildWithMultiDb();
        var dbA = tree.RootMembers[0];
        var dbB = tree.RootMembers[1];
        var leafA = dbA.AllDescendants().First(n => n.IsLeaf);
        var leafB = dbB.AllDescendants().First(n => n.IsLeaf);

        leafA.IsSelected = true;
        leafB.IsSelected = true;

        // OnNodeSelected is wired by the host as MemberNodeViewModel.SelectedChanged
        // handler; calling it directly here simulates the event firing for leafB.
        vm.OnNodeSelected(leafB);

        leafA.IsSelected.Should().BeFalse(
            "global single-focus invariant (#95): selecting leafB must deselect " +
            "leafA in the other active DB");
        leafB.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void OnNodeSelected_ReEntry_IsSilentlyIgnored()
    {
        // Re-entrancy guard: while the cascade is walking the tree and
        // deselecting peers, those nodes' PropertyChanged event fires
        // (true→false transitions on IsSelected raise PropertyChanged via
        // SetProperty, even though SelectedChanged stays gated to true→true).
        // If a host wiring routes from PropertyChanged back into
        // OnNodeSelected, the _inSelectionCascade flag must short-circuit
        // the inner call.
        var (vm, tree, _) = BuildWithMultiDb();
        var leafA = tree.RootMembers[0].AllDescendants().First(n => n.IsLeaf);
        var leafB = tree.RootMembers[1].AllDescendants().First(n => n.IsLeaf);

        leafA.IsSelected = true;
        leafB.IsSelected = true;

        // Simulate a re-entry route: when leafA's IsSelected flips during
        // the cascade, a listener calls OnNodeSelected again. The inner
        // call must hit the guard and return immediately. The outer cascade
        // must still complete and leafB must remain selected.
        int reentrantCalls = 0;
        bool reentrantSawCascadeFlag = false;
        leafA.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MemberNodeViewModel.IsSelected)) return;
            // Capture observable state *before* re-entering — the inner call
            // must short-circuit on the cascade flag.
            reentrantCalls++;
            int leafBStateBefore = leafB.IsSelected ? 1 : 0;
            vm.OnNodeSelected(leafA);
            // If the guard didn't fire, the inner cascade would have walked
            // the tree and deselected leafB. Capture the result.
            reentrantSawCascadeFlag = leafB.IsSelected == (leafBStateBefore == 1);
        };

        vm.OnNodeSelected(leafB);

        leafA.IsSelected.Should().BeFalse("outer cascade deselected leafA");
        leafB.IsSelected.Should().BeTrue(
            "leafB is the outer call's justSelected — the guard prevented the " +
            "inner re-entrant call from clearing it as a side effect");
        reentrantCalls.Should().BeGreaterThan(0,
            "PropertyChanged fires on true→false IsSelected transitions, so the " +
            "re-entry path was actually exercised");
        reentrantSawCascadeFlag.Should().BeTrue(
            "the inner call must short-circuit on _inSelectionCascade==true " +
            "(otherwise it would have walked the tree and deselected leafB)");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fixtures
    // ─────────────────────────────────────────────────────────────────────

    private static (SelectionScopeViewModel vm, MemberTreeViewModel tree, List<ActiveDb> dbs) Build()
    {
        var dbs = new List<ActiveDb>();
        var tree = new MemberTreeViewModel(
            getActiveDbs: () => dbs,
            getCurrentPlcName: () => "",
            commentLanguagePolicy: new CommentLanguagePolicy(null, null, new[] { "en-GB" }),
            subscribeToVm: _ => { });
        var vm = new SelectionScopeViewModel(tree);
        return (vm, tree, dbs);
    }

    private static (SelectionScopeViewModel vm, MemberTreeViewModel tree, ActiveDb db) BuildWithSingleDb()
    {
        var (vm, tree, dbs) = Build();
        var speed = new MemberNode("Speed", "Int", "0", "Speed", null, Array.Empty<MemberNode>());
        var enableFlag = new MemberNode("Enable", "Bool", "false", "Enable", null, Array.Empty<MemberNode>());
        var info = new DataBlockInfo("DB_Test", 1, "Optimized", "GlobalDB", new[] { speed, enableFlag });
        var db = new ActiveDb(info, "<Block />");
        dbs.Add(db);
        tree.BuildRootMembersFromActiveDbs();
        return (vm, tree, db);
    }

    private static (SelectionScopeViewModel vm, MemberTreeViewModel tree, IReadOnlyList<ActiveDb> dbs) BuildWithMultiDb()
    {
        var (vm, tree, dbs) = Build();
        var speedA = new MemberNode("SpeedA", "Int", "0", "SpeedA", null, Array.Empty<MemberNode>());
        var speedB = new MemberNode("SpeedB", "Int", "0", "SpeedB", null, Array.Empty<MemberNode>());
        dbs.Add(new ActiveDb(new DataBlockInfo("DB_A", 1, "Optimized", "GlobalDB", new[] { speedA }), "<Block />"));
        dbs.Add(new ActiveDb(new DataBlockInfo("DB_B", 2, "Optimized", "GlobalDB", new[] { speedB }), "<Block />"));
        tree.BuildRootMembersFromActiveDbs();
        return (vm, tree, dbs);
    }
}
