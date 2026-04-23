using System.Collections.ObjectModel;
using BlockParam.Models;

namespace BlockParam.UI;

/// <summary>
/// Manages a flat list representation of the member tree for use with
/// ListView/GridView (which provides proper column alignment).
/// Handles expand/collapse by adding/removing items from the flat list.
/// </summary>
public class FlatTreeManager
{
    private readonly ObservableCollection<MemberNodeViewModel> _flatList = new();

    public ObservableCollection<MemberNodeViewModel> FlatList => _flatList;

    /// <summary>
    /// Rebuilds the flat list from root members.
    /// Respects current expand/collapse state and visibility filter.
    /// </summary>
    public void BuildFlatList(IEnumerable<MemberNodeViewModel> rootMembers)
    {
        _flatList.Clear();
        foreach (var root in rootMembers)
        {
            AddNodeToFlatList(root);
        }
    }

    /// <summary>
    /// Rebuilds only — call after filter or expand/collapse changes.
    /// </summary>
    public void Refresh(IEnumerable<MemberNodeViewModel> rootMembers)
    {
        BuildFlatList(rootMembers);
    }

    private void AddNodeToFlatList(MemberNodeViewModel node, bool parentIsSmartExpanded = false)
    {
        if (!node.IsVisible) return;

        // Inside a smart-expanded parent, only show highlighted nodes
        // (affected or already-matching) or nodes that have such descendants
        if (parentIsSmartExpanded && !IsHighlighted(node) && !HasHighlightedDescendant(node))
            return;

        _flatList.Add(node);

        if (node.IsExpanded)
        {
            // Only propagate smart-expand filter to children if THIS node is smart-expanded.
            // A manually expanded node (IsSmartExpanded=false) breaks the filter chain,
            // even if its parent was smart-expanded.
            foreach (var child in node.Children)
            {
                AddNodeToFlatList(child, node.IsSmartExpanded);
            }
        }
    }

    private static bool IsHighlighted(MemberNodeViewModel node)
        => node.IsAffected || node.IsAlreadyMatching || node.IsSearchMatch || node.IsPendingInlineEdit || node.HasInlineError;

    private static bool HasHighlightedDescendant(MemberNodeViewModel node)
    {
        foreach (var child in node.Children)
        {
            if (IsHighlighted(child) || HasHighlightedDescendant(child))
                return true;
        }
        return false;
    }

    // Keep for CycleExpandState (determines if smart-expand toggle is offered)
    private static bool HasAffectedDescendant(MemberNodeViewModel node)
    {
        foreach (var child in node.Children)
        {
            if (IsHighlighted(child) || HasAffectedDescendant(child))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Cycles the expand state for a node:
    /// - Collapsed → Expanded (full)
    /// - Smart-Expanded → Expanded (full, showing all children)
    /// - Expanded (with affected children) → Smart-Expanded (filtered)
    /// - Expanded (no affected children) → Collapsed
    /// </summary>
    public static void CycleExpandState(MemberNodeViewModel node)
    {
        if (node.IsSmartExpanded)
        {
            // Smart-expanded (filtered) → fully expanded (all children)
            node.IsSmartExpanded = false;
            // IsExpanded stays true
        }
        else if (node.IsExpanded)
        {
            if (HasAffectedDescendant(node))
            {
                // Fully expanded with affected children → back to smart-expanded
                node.IsSmartExpanded = true;
            }
            else
            {
                // No affected children → collapse
                node.IsExpanded = false;
            }
        }
        else
        {
            // Collapsed → expand
            node.IsExpanded = true;
        }
    }

    /// <summary>
    /// Toggles expand/collapse for a node and refreshes the flat list.
    /// </summary>
    public void ToggleExpand(MemberNodeViewModel node, IEnumerable<MemberNodeViewModel> rootMembers)
    {
        node.IsExpanded = !node.IsExpanded;
        Refresh(rootMembers);
    }

    /// <summary>
    /// Expands all visible nodes in the tree.
    /// </summary>
    public static void ExpandAll(IEnumerable<MemberNodeViewModel> rootMembers)
    {
        foreach (var root in rootMembers)
            ExpandRecursive(root);
    }

    /// <summary>
    /// Collapses all nodes in the tree.
    /// </summary>
    public static void CollapseAll(IEnumerable<MemberNodeViewModel> rootMembers)
    {
        foreach (var root in rootMembers)
            CollapseRecursive(root);
    }

    /// <summary>
    /// Expands a node and all its descendants.
    /// </summary>
    public static void ExpandAllChildren(MemberNodeViewModel node)
    {
        ExpandRecursive(node);
    }

    /// <summary>
    /// Collapses a node and all its descendants.
    /// </summary>
    public static void CollapseAllChildren(MemberNodeViewModel node)
    {
        CollapseRecursive(node);
    }

    private static void ExpandRecursive(MemberNodeViewModel node)
    {
        if (node.HasChildren && node.IsVisible)
        {
            node.IsExpanded = true;
            node.IsSmartExpanded = false;
        }
        foreach (var child in node.Children)
            ExpandRecursive(child);
    }

    private static void CollapseRecursive(MemberNodeViewModel node)
    {
        node.IsExpanded = false;
        node.IsSmartExpanded = false;
        foreach (var child in node.Children)
            CollapseRecursive(child);
    }
}
