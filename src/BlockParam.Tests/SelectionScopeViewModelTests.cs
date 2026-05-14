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
        // Re-entrancy guard: when OnNodeSelected sets a peer node's
        // IsSelected = false, that node's SelectedChanged event fires
        // and the host's wiring (`SelectedChanged += Selection.OnNodeSelected`)
        // would re-route back into OnNodeSelected. The _inSelectionCascade
        // flag prevents the recursive body from running on the inner call.
        var (vm, tree, _) = BuildWithMultiDb();
        var leafA = tree.RootMembers[0].AllDescendants().First(n => n.IsLeaf);
        var leafB = tree.RootMembers[1].AllDescendants().First(n => n.IsLeaf);

        leafA.IsSelected = true;
        leafB.IsSelected = true;

        int innerCallsBlockedByGuard = 0;
        // When leafA's IsSelected flips to false during the outer cascade,
        // its SelectedChanged event fires. Simulate the host's wiring by
        // routing back into OnNodeSelected from the handler.
        leafA.SelectedChanged += node =>
        {
            // The guard inside vm.OnNodeSelected should short-circuit on
            // re-entry. Count how often it was reached at all.
            innerCallsBlockedByGuard++;
            vm.OnNodeSelected(node);
        };

        // Outer call deselects leafA, which fires the recursive handler.
        // Without the guard this would either stack-overflow or wipe leafB.
        vm.OnNodeSelected(leafB);

        leafA.IsSelected.Should().BeFalse("outer cascade must still complete");
        leafB.IsSelected.Should().BeTrue(
            "leafB is the justSelected node — must not be touched by the cascade " +
            "(neither outer nor any recursive re-entry that the guard short-circuited)");
        innerCallsBlockedByGuard.Should().Be(1,
            "the handler fired exactly once when leafA was deselected; the guard " +
            "stopped that re-entry from looping further");
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
