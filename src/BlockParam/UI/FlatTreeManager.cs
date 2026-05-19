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
    private readonly BulkObservableCollection<MemberNodeViewModel> _flatList = new();

    public ObservableCollection<MemberNodeViewModel> FlatList => _flatList;

    /// <summary>
    /// Rebuilds the flat list from root members.
    /// Respects current expand/collapse state and visibility filter.
    /// </summary>
    public void BuildFlatList(IEnumerable<MemberNodeViewModel> rootMembers)
    {
        var roots = rootMembers as IReadOnlyList<MemberNodeViewModel>
                    ?? rootMembers.ToList();

        // #154 H4: refresh every node's HasHighlightedDescendantCache in one
        // O(n) post-order pass instead of letting AddNodeToFlatList recurse
        // the whole subtree per visible node (O(n²) on a flat array). Done
        // here, not in ApplyFilter, so the cache is correct on EVERY rebuild
        // trigger — search, filter, expand, AND pending-edit/inline-error
        // mutations that change IsHighlighted without re-running ApplyFilter.
        foreach (var root in roots)
            RefreshHighlightCache(root);

        // #154 H3: assemble into a plain List then push it in one shot so the
        // bound collection raises a single Reset instead of Clear()+N×Add
        // (N+1 CollectionChanged events through WPF on every rebuild).
        var flat = new List<MemberNodeViewModel>();
        foreach (var root in roots)
            AddNodeToFlatList(root, flat);
        _flatList.ReplaceAll(flat);
    }

    /// <summary>
    /// Post-order pass: sets
    /// <see cref="MemberNodeViewModel.HasHighlightedDescendantCache"/> (true
    /// iff any descendant is highlighted) and returns whether this node OR
    /// any descendant is highlighted, so a parent folds its subtree in O(1).
    /// One walk over the forest — O(n) total.
    /// </summary>
    private static bool RefreshHighlightCache(MemberNodeViewModel node)
    {
        bool descendantHighlighted = false;
        foreach (var child in node.Children)
        {
            // No short-circuit: every node's cache must be set.
            if (RefreshHighlightCache(child))
                descendantHighlighted = true;
        }
        node.HasHighlightedDescendantCache = descendantHighlighted;
        return IsHighlighted(node) || descendantHighlighted;
    }

    /// <summary>
    /// Rebuilds only — call after filter or expand/collapse changes.
    /// </summary>
    public void Refresh(IEnumerable<MemberNodeViewModel> rootMembers)
    {
        BuildFlatList(rootMembers);
    }

    private void AddNodeToFlatList(
        MemberNodeViewModel node,
        List<MemberNodeViewModel> flat,
        bool parentIsSmartExpanded = false)
    {
        if (!node.IsVisible) return;

        // Inside a smart-expanded parent, only show highlighted nodes
        // (affected or already-matching) or nodes that have such descendants.
        // #154 H4: HasHighlightedDescendantCache was filled by the single
        // RefreshHighlightCache pass — O(1) here instead of a fresh recursion.
        if (parentIsSmartExpanded && !IsHighlighted(node) && !node.HasHighlightedDescendantCache)
            return;

        flat.Add(node);

        if (node.IsExpanded)
        {
            // Only propagate smart-expand filter to children if THIS node is smart-expanded.
            // A manually expanded node (IsSmartExpanded=false) breaks the filter chain,
            // even if its parent was smart-expanded.
            foreach (var child in node.Children)
            {
                AddNodeToFlatList(child, flat, node.IsSmartExpanded);
            }
        }
    }

    private static bool IsHighlighted(MemberNodeViewModel node)
        => node.IsAffected || node.IsAlreadyMatching || node.IsSearchMatch || node.IsPendingInlineEdit || node.HasInlineError;

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
