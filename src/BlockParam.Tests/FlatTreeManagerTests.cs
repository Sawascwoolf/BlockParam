using FluentAssertions;
using BlockParam.Models;
using BlockParam.UI;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Tests for FlatTreeManager: flat list building, smart-expand filtering,
/// and CycleExpandState transitions.
///
/// Tree structure used in most tests:
///   Root (Struct)
///     ├── Child1 (Struct)
///     │     ├── Leaf1A (Int, StartValue=1)   ← affected
///     │     └── Leaf1B (Bool, StartValue=true)
///     └── Child2 (Struct)
///           ├── Leaf2A (Int, StartValue=2)   ← affected
///           └── Leaf2B (Bool, StartValue=false)
/// </summary>
public class FlatTreeManagerTests
{
    private static (MemberNodeViewModel root, MemberNodeViewModel child1, MemberNodeViewModel child2,
        MemberNodeViewModel leaf1A, MemberNodeViewModel leaf1B,
        MemberNodeViewModel leaf2A, MemberNodeViewModel leaf2B) BuildTestTree()
    {
        var leaf1A = new MemberNode("Leaf1A", "Int", "1", "Root.Child1.Leaf1A", null, Array.Empty<MemberNode>());
        var leaf1B = new MemberNode("Leaf1B", "Bool", "true", "Root.Child1.Leaf1B", null, Array.Empty<MemberNode>());
        var child1 = new MemberNode("Child1", "Struct", null, "Root.Child1", null, new[] { leaf1A, leaf1B });
        var leaf2A = new MemberNode("Leaf2A", "Int", "2", "Root.Child2.Leaf2A", null, Array.Empty<MemberNode>());
        var leaf2B = new MemberNode("Leaf2B", "Bool", "false", "Root.Child2.Leaf2B", null, Array.Empty<MemberNode>());
        var child2 = new MemberNode("Child2", "Struct", null, "Root.Child2", null, new[] { leaf2A, leaf2B });
        var rootModel = new MemberNode("Root", "Struct", null, "Root", null, new[] { child1, child2 });

        var rootVm = new MemberNodeViewModel(rootModel, null);
        var child1Vm = rootVm.Children[0];
        var child2Vm = rootVm.Children[1];
        var leaf1AVm = child1Vm.Children[0];
        var leaf1BVm = child1Vm.Children[1];
        var leaf2AVm = child2Vm.Children[0];
        var leaf2BVm = child2Vm.Children[1];

        return (rootVm, child1Vm, child2Vm, leaf1AVm, leaf1BVm, leaf2AVm, leaf2BVm);
    }

    private static List<string> GetFlatNames(FlatTreeManager mgr)
    {
        return mgr.FlatList.Select(n => n.Name).ToList();
    }

    // ===== Flat list building =====

    [Fact]
    public void FlatList_AllCollapsed_OnlyRootVisible()
    {
        var (root, _, _, _, _, _, _) = BuildTestTree();
        var mgr = new FlatTreeManager();

        mgr.Refresh(new[] { root });

        GetFlatNames(mgr).Should().Equal("Root");
    }

    [Fact]
    public void FlatList_RootExpanded_ShowsDirectChildren()
    {
        var (root, _, _, _, _, _, _) = BuildTestTree();
        root.IsExpanded = true;
        var mgr = new FlatTreeManager();

        mgr.Refresh(new[] { root });

        GetFlatNames(mgr).Should().Equal("Root", "Child1", "Child2");
    }

    [Fact]
    public void FlatList_FullyExpanded_ShowsAllNodes()
    {
        var (root, child1, child2, _, _, _, _) = BuildTestTree();
        root.IsExpanded = true;
        child1.IsExpanded = true;
        child2.IsExpanded = true;
        var mgr = new FlatTreeManager();

        mgr.Refresh(new[] { root });

        GetFlatNames(mgr).Should().Equal(
            "Root", "Child1", "Leaf1A", "Leaf1B", "Child2", "Leaf2A", "Leaf2B");
    }

    // ===== Smart-expand filtering =====

    [Fact]
    public void FlatList_SmartExpanded_OnlyShowsAffectedChildren()
    {
        var (root, child1, child2, leaf1A, _, leaf2A, _) = BuildTestTree();
        // Simulate highlighting: Root and children smart-expanded, only LeafA nodes affected
        root.IsExpanded = true;
        root.IsSmartExpanded = true;
        child1.IsExpanded = true;
        child1.IsSmartExpanded = true;
        child2.IsExpanded = true;
        child2.IsSmartExpanded = true;
        leaf1A.IsAffected = true;
        leaf2A.IsAffected = true;

        var mgr = new FlatTreeManager();
        mgr.Refresh(new[] { root });

        // Only affected leaves should be visible, not Leaf1B/Leaf2B
        GetFlatNames(mgr).Should().Equal(
            "Root", "Child1", "Leaf1A", "Child2", "Leaf2A");
    }

    [Fact]
    public void FlatList_ManualExpandBreaksSmartFilter()
    {
        // Root is smart-expanded, but Child1 was manually expanded (user clicked full-expand)
        var (root, child1, child2, leaf1A, _, leaf2A, _) = BuildTestTree();
        root.IsExpanded = true;
        root.IsSmartExpanded = true;
        child1.IsExpanded = true;
        child1.IsSmartExpanded = false; // manually expanded → show ALL children
        child2.IsExpanded = true;
        child2.IsSmartExpanded = true; // still smart → filter
        leaf1A.IsAffected = true;
        leaf2A.IsAffected = true;

        var mgr = new FlatTreeManager();
        mgr.Refresh(new[] { root });

        // Child1 fully expanded: both leaves visible
        // Child2 smart-expanded: only Leaf2A visible
        GetFlatNames(mgr).Should().Equal(
            "Root", "Child1", "Leaf1A", "Leaf1B", "Child2", "Leaf2A");
    }

    [Fact]
    public void FlatList_SmartExpanded_HidesContainerWithoutAffectedDescendants()
    {
        var (root, child1, child2, leaf1A, _, _, _) = BuildTestTree();
        root.IsExpanded = true;
        root.IsSmartExpanded = true;
        child1.IsExpanded = true;
        child1.IsSmartExpanded = true;
        // Only leaf1A is affected, nothing in child2
        leaf1A.IsAffected = true;

        var mgr = new FlatTreeManager();
        mgr.Refresh(new[] { root });

        // Child2 has no affected descendants → hidden entirely
        GetFlatNames(mgr).Should().Equal("Root", "Child1", "Leaf1A");
    }

    // ===== CycleExpandState transitions =====

    [Fact]
    public void CycleExpand_Collapsed_BecomesExpanded()
    {
        var (_, child1, _, _, _, _, _) = BuildTestTree();
        child1.IsExpanded.Should().BeFalse();
        child1.IsSmartExpanded.Should().BeFalse();

        FlatTreeManager.CycleExpandState(child1);

        child1.IsExpanded.Should().BeTrue();
        child1.IsSmartExpanded.Should().BeFalse();
    }

    [Fact]
    public void CycleExpand_Expanded_NoAffected_BecomesCollapsed()
    {
        var (_, child1, _, _, _, _, _) = BuildTestTree();
        child1.IsExpanded = true;

        FlatTreeManager.CycleExpandState(child1);

        child1.IsExpanded.Should().BeFalse();
        child1.IsSmartExpanded.Should().BeFalse();
    }

    [Fact]
    public void CycleExpand_Expanded_WithAffected_BecomesSmartExpanded()
    {
        var (_, child1, _, leaf1A, _, _, _) = BuildTestTree();
        child1.IsExpanded = true;
        leaf1A.IsAffected = true;

        FlatTreeManager.CycleExpandState(child1);

        child1.IsExpanded.Should().BeTrue("should stay open");
        child1.IsSmartExpanded.Should().BeTrue("should switch to filtered view");
    }

    [Fact]
    public void CycleExpand_SmartExpanded_BecomesFullyExpanded()
    {
        var (_, child1, _, leaf1A, _, _, _) = BuildTestTree();
        child1.IsExpanded = true;
        child1.IsSmartExpanded = true;
        leaf1A.IsAffected = true;

        FlatTreeManager.CycleExpandState(child1);

        child1.IsExpanded.Should().BeTrue("should stay open");
        child1.IsSmartExpanded.Should().BeFalse("should show all children now");
    }

    // ===== ClearAffected =====

    [Fact]
    public void ClearAffected_CollapsesSmartExpandedNodes()
    {
        var (root, child1, child2, leaf1A, _, leaf2A, _) = BuildTestTree();
        root.IsExpanded = true;
        root.IsSmartExpanded = true;
        child1.IsExpanded = true;
        child1.IsSmartExpanded = true;
        child2.IsExpanded = true;
        child2.IsSmartExpanded = true;
        leaf1A.IsAffected = true;
        leaf2A.IsAffected = true;

        root.ClearAffected();

        root.IsExpanded.Should().BeFalse();
        root.IsSmartExpanded.Should().BeFalse();
        child1.IsExpanded.Should().BeFalse();
        child1.IsSmartExpanded.Should().BeFalse();
        leaf1A.IsAffected.Should().BeFalse();
        leaf2A.IsAffected.Should().BeFalse();
    }

    [Fact]
    public void ClearAffected_PreservesManuallyExpandedNodes()
    {
        var (root, child1, child2, leaf1A, _, leaf2A, _) = BuildTestTree();
        // Root manually expanded, children smart-expanded
        root.IsExpanded = true;
        root.IsSmartExpanded = false; // user expanded this manually
        child1.IsExpanded = true;
        child1.IsSmartExpanded = true;
        leaf1A.IsAffected = true;

        root.ClearAffected();

        root.IsExpanded.Should().BeTrue("manually expanded should stay open");
        child1.IsExpanded.Should().BeFalse("smart-expanded should collapse");
    }

    // ===== EnsureVisible =====

    [Fact]
    public void EnsureVisible_CollapsedParent_BecomesSmartExpanded()
    {
        var (root, child1, _, leaf1A, _, _, _) = BuildTestTree();
        root.IsExpanded.Should().BeFalse();
        child1.IsExpanded.Should().BeFalse();

        leaf1A.EnsureVisible();

        root.IsExpanded.Should().BeTrue();
        root.IsSmartExpanded.Should().BeTrue();
        child1.IsExpanded.Should().BeTrue();
        child1.IsSmartExpanded.Should().BeTrue();
    }

    [Fact]
    public void EnsureVisible_AlreadyExpandedParent_NotChangedToSmart()
    {
        var (root, child1, _, leaf1A, _, _, _) = BuildTestTree();
        // User manually expanded root before any highlighting
        root.IsExpanded = true;

        leaf1A.EnsureVisible();

        root.IsExpanded.Should().BeTrue();
        root.IsSmartExpanded.Should().BeFalse("already-expanded node should not become smart");
        child1.IsExpanded.Should().BeTrue();
        child1.IsSmartExpanded.Should().BeTrue("was collapsed, so auto-expanded as smart");
    }

    // ===== Full round-trip: select → highlight → re-select =====

    [Fact]
    public void RoundTrip_SelectThenReselect_SmartExpandCorrect()
    {
        var (root, child1, child2, leaf1A, leaf1B, leaf2A, leaf2B) = BuildTestTree();
        var mgr = new FlatTreeManager();

        // Step 1: Simulate selecting Leaf1A → highlight Leaf1A and Leaf2A
        leaf1A.IsAffected = true;
        leaf2A.IsAffected = true;
        leaf1A.EnsureVisible();
        leaf2A.EnsureVisible();
        mgr.Refresh(new[] { root });

        GetFlatNames(mgr).Should().Equal("Root", "Child1", "Leaf1A", "Child2", "Leaf2A");

        // Step 2: Clear and re-highlight for Leaf1B → highlight Leaf1B and Leaf2B
        root.ClearAffected();
        leaf1B.IsAffected = true;
        leaf2B.IsAffected = true;
        leaf1B.EnsureVisible();
        leaf2B.EnsureVisible();
        mgr.Refresh(new[] { root });

        GetFlatNames(mgr).Should().Equal("Root", "Child1", "Leaf1B", "Child2", "Leaf2B");
    }
}
