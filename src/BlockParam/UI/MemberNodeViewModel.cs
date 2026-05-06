using System.Collections.ObjectModel;
using System.Linq;
using BlockParam.Models;

namespace BlockParam.UI;

/// <summary>
/// ViewModel wrapper for MemberNode, providing UI state
/// (expansion, selection, highlighting, filtering).
/// </summary>
public class MemberNodeViewModel : ViewModelBase
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isAffected;
    private bool _isAlreadyMatching;
    private bool _isVisible = true;
    private bool _isSmartExpanded;
    private bool _isSearchMatch;
    private bool _hasInlineError;
    private string? _inlineErrorMessage;
    private bool _hasExistingViolation;
    private string? _existingViolationMessage;
    private string? _editableStartValue;
    private string? _ruleHint;

    public MemberNodeViewModel(
        MemberNode model,
        MemberNodeViewModel? parent,
        CommentLanguagePolicy? commentLanguagePolicy = null)
    {
        Model = model;
        Parent = parent;
        _commentLanguagePolicy = commentLanguagePolicy;
        Children = new ObservableCollection<MemberNodeViewModel>();

        foreach (var child in model.Children)
        {
            Children.Add(new MemberNodeViewModel(child, this, commentLanguagePolicy));
        }
    }

    public MemberNode Model { get; }
    public MemberNodeViewModel? Parent { get; }
    public ObservableCollection<MemberNodeViewModel> Children { get; }

    /// <summary>
    /// Policy for picking the comment variant shown in the UI. When null, the
    /// model's pre-picked <see cref="MemberNode.Comment"/> is used (first
    /// non-empty variant the parser happened to see first).
    /// </summary>
    private readonly CommentLanguagePolicy? _commentLanguagePolicy;

    // Pass-through properties from model
    public string Name => Model.Name;
    public string Datatype => Model.Datatype;
    public string? StartValue => Model.StartValue;
    public string Path => Model.Path;
    /// <summary>
    /// Visual nesting depth (0 = root, 1 = child of a root, …). Counts VM
    /// ancestors instead of model ancestors so the synthetic per-DB group
    /// root in multi-DB sessions adds a real indent level for its members.
    /// In single-DB sessions this matches the underlying Model.Depth.
    /// </summary>
    public int Depth
    {
        get
        {
            int depth = 0;
            for (var p = Parent; p != null; p = p.Parent) depth++;
            return depth;
        }
    }
    public bool IsLeaf => Model.IsLeaf;
    public bool IsUdtInstance => Model.IsUdtInstance;
    public bool IsStruct => Model.IsStruct;
    public bool IsArray => Model.IsArray;
    public bool IsArrayElement => Model.IsArrayElement;
    public string? UnresolvedBound => Model.UnresolvedBound;
    public bool HasUnresolvedBound => Model.UnresolvedBound != null;
    public string? Comment => _commentLanguagePolicy?.Pick(Model.Comments) ?? Model.Comment;

    private string? _previewComment;

    /// <summary>Generated comment preview (null = no change planned).</summary>
    public string? PreviewComment
    {
        get => _previewComment;
        set
        {
            if (SetProperty(ref _previewComment, value))
            {
                OnPropertyChanged(nameof(DisplayComment));
                OnPropertyChanged(nameof(CommentState));
                OnPropertyChanged(nameof(CommentTooltip));
            }
        }
    }

    /// <summary>What to show in the comment column.</summary>
    public string? DisplayComment => _previewComment ?? Comment;

    /// <summary>
    /// Comment state for color coding:
    /// "unchanged" = black, "updated" = blue, "new" = grey/italic
    /// </summary>
    public string CommentState
    {
        get
        {
            if (_previewComment == null) return "unchanged";
            if (string.IsNullOrEmpty(Comment)) return "new";
            return string.Equals(Comment, _previewComment, StringComparison.Ordinal)
                ? "unchanged" : "updated";
        }
    }

    /// <summary>Tooltip showing current vs new vs template.</summary>
    public string? CommentTooltip
    {
        get
        {
            if (_previewComment == null && string.IsNullOrEmpty(Comment)) return null;
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Comment))
                parts.Add($"Current:  {Comment}");
            if (_previewComment != null && _previewComment != Comment)
                parts.Add($"New:      {_previewComment}");
            return parts.Count > 0 ? string.Join("\n", parts) : null;
        }
    }

    /// <summary>
    /// One entry per ancestor level, used to render tree guide lines in the Name
    /// column (one vertical stroke per level of indentation).
    /// </summary>
    public IEnumerable<int> GuideLevels => Enumerable.Range(0, Depth);

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>True if this member is affected by the current bulk scope selection.</summary>
    public bool IsAffected
    {
        get => _isAffected;
        set => SetProperty(ref _isAffected, value);
    }

    /// <summary>True if this member is in scope but already has the target value.</summary>
    public bool IsAlreadyMatching
    {
        get => _isAlreadyMatching;
        set => SetProperty(ref _isAlreadyMatching, value);
    }

    /// <summary>Controls visibility (for setpoints-only filter).</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>True if this node was auto-expanded by scope highlighting (not by user).</summary>
    public bool IsSmartExpanded
    {
        get => _isSmartExpanded;
        set => SetProperty(ref _isSmartExpanded, value);
    }

    /// <summary>True if this member matches the current search query (yellow highlight).</summary>
    public bool IsSearchMatch
    {
        get => _isSearchMatch;
        set => SetProperty(ref _isSearchMatch, value);
    }

    public bool HasInlineError
    {
        get => _hasInlineError;
        set => SetProperty(ref _hasInlineError, value);
    }

    public string? InlineErrorMessage
    {
        get => _inlineErrorMessage;
        set => SetProperty(ref _inlineErrorMessage, value);
    }

    /// <summary>
    /// True when this member's *current* StartValue (as it sits in the DB,
    /// independent of any pending edit) violates a configured rule. Surfaced
    /// in the inspector "Issues" section as a read-only finding (#26).
    /// Distinct from <see cref="HasInlineError"/>: pre-existing violations
    /// must NOT block Apply — Apply only cares about pending edits.
    /// </summary>
    public bool HasExistingViolation
    {
        get => _hasExistingViolation;
        set => SetProperty(ref _hasExistingViolation, value);
    }

    /// <summary>
    /// Human-readable description of why this member's existing value fails its
    /// rule (e.g. "Range: 0 – 100"). Null when <see cref="HasExistingViolation"/> is false.
    /// </summary>
    public string? ExistingViolationMessage
    {
        get => _existingViolationMessage;
        set => SetProperty(ref _existingViolationMessage, value);
    }

    /// <summary>
    /// Human-readable description of the rule constraining this member
    /// (e.g. "Range: 0 – 100"). Populated by the owning ViewModel at build
    /// time from <see cref="Services.RuleHintFormatter"/>. Null when the
    /// member has no rule or the rule exposes no constraints.
    /// </summary>
    public string? RuleHint
    {
        get => _ruleHint;
        set
        {
            if (SetProperty(ref _ruleHint, value))
                OnPropertyChanged(nameof(HasRuleHint));
        }
    }

    /// <summary>True when <see cref="RuleHint"/> is non-empty.</summary>
    public bool HasRuleHint => !string.IsNullOrEmpty(_ruleHint);

    /// <summary>Has a start value that can be bulk-edited.</summary>
    public bool HasStartValue => !string.IsNullOrEmpty(StartValue);

    /// <summary>True if the TIA SetPoint attribute is set on this member or inherited from a parent.</summary>
    public bool IsSetPoint => Model.IsSetPoint;

    /// <summary>True if this node has children (can be expanded/collapsed).</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>
    /// Pending inline-edited value (not yet applied to XML).
    /// Null means no pending edit — the original StartValue is shown.
    /// </summary>
    public string? PendingValue
    {
        get => _pendingValue;
        set
        {
            if (SetProperty(ref _pendingValue, value))
            {
                OnPropertyChanged(nameof(IsPendingInlineEdit));
                OnPropertyChanged(nameof(EditableStartValue));
            }
        }
    }
    private string? _pendingValue;

    /// <summary>True when this node has an unsaved inline edit (yellow highlight).</summary>
    public bool IsPendingInlineEdit => _pendingValue != null;

    /// <summary>
    /// Editable start value for inline editing in the table.
    /// Shows PendingValue if set, otherwise the original StartValue.
    /// Setting this stores the value as pending (does NOT apply to XML).
    /// </summary>
    public string? EditableStartValue
    {
        get => _pendingValue ?? _editableStartValue ?? StartValue;
        set
        {
            if (value != _editableStartValue)
            {
                _editableStartValue = value;
                // Same as original → clear pending instead of creating one
                if (value == StartValue)
                    ClearPending();
                else
                    PendingValue = value;
                OnPropertyChanged();
                StartValueEdited?.Invoke(this, value ?? "");
            }
        }
    }

    /// <summary>Clears the pending inline edit, reverting to the original StartValue.</summary>
    public void ClearPending()
    {
        _pendingValue = null;
        _editableStartValue = null;
        HasInlineError = false;
        InlineErrorMessage = null;
        OnPropertyChanged(nameof(PendingValue));
        OnPropertyChanged(nameof(IsPendingInlineEdit));
        OnPropertyChanged(nameof(EditableStartValue));
    }

    /// <summary>Fired when user edits a start value directly in the table.</summary>
    public event Action<MemberNodeViewModel, string>? StartValueEdited;

    /// <summary>Count of affected children (for badge display on collapsed parents).</summary>
    public int AffectedChildCount => CountAffectedDescendants();

    /// <summary>Display text for the affected count badge.</summary>
    public string? AffectedBadge
    {
        get
        {
            var count = AffectedChildCount;
            return count > 0 && !IsExpanded ? $"({count} affected)" : null;
        }
    }

    private bool HasPendingDescendant()
    {
        foreach (var child in Children)
        {
            if (child.IsPendingInlineEdit || child.HasInlineError || child.HasPendingDescendant())
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns a flat list of all descendants (depth-first).
    /// </summary>
    public IEnumerable<MemberNodeViewModel> AllDescendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var descendant in child.AllDescendants())
                yield return descendant;
        }
    }

    /// <summary>
    /// Applies the combined filters.
    /// - Rule filter hides members whose path matches a rule with excludeFromSetpoints=true.
    /// - Setpoints-only filter hides leaves unless the leaf and every ancestor up to the
    ///   DB root are marked as SetPoint (UDT-type-level flag for nested UDT leaves).
    /// Filters are AND-combined. Pending inline edits and errors always stay visible.
    /// Container members are visible when they have visible descendants.
    /// </summary>
    public void ApplyFilter(
        bool ruleFilterActive,
        HashSet<string>? searchMatchPaths = null,
        HashSet<string>? excludedByRules = null,
        bool showSetpointsOnly = false)
    {
        IsSearchMatch = searchMatchPaths?.Contains(Path) == true;
        bool isExcluded = ruleFilterActive && excludedByRules?.Contains(Path) == true;

        if (IsLeaf)
        {
            // Pending inline edits and errors are always visible regardless of filters
            if (IsPendingInlineEdit || HasInlineError)
            {
                IsVisible = true;
            }
            else
            {
                bool passesRuleFilter = !isExcluded;
                bool passesSearch = searchMatchPaths == null || IsSearchMatch;
                bool passesSetPoint = !showSetpointsOnly || EffectiveSetPoint;
                IsVisible = passesRuleFilter && passesSearch && passesSetPoint;
            }
        }
        else
        {
            foreach (var child in Children)
                child.ApplyFilter(ruleFilterActive, searchMatchPaths, excludedByRules, showSetpointsOnly);

            bool hasVisibleChildren = Children.Any(c => c.IsVisible);
            bool hasPendingChildren = HasPendingDescendant();
            IsVisible = hasPendingChildren || hasVisibleChildren;
            if (searchMatchPaths != null && !hasPendingChildren)
                IsVisible = IsVisible && (hasVisibleChildren || IsSearchMatch);
        }
    }

    /// <summary>
    /// True if this node and every non-Struct ancestor up to the DB root have <c>IsSetPoint=true</c>.
    /// Models the TIA Portal rule "a leaf is a visible setpoint only if the UDT instance
    /// is enabled AND the member is marked as SetPoint in the UDT type".
    /// Structs are transparent — TIA shows no SetPoint checkbox for them, so they
    /// neither grant nor block SetPoint visibility for their children.
    /// </summary>
    public bool EffectiveSetPoint
    {
        get
        {
            var current = this;
            while (current != null)
            {
                if (!current.IsStruct && !current.IsSetPoint) return false;
                current = current.Parent;
            }
            return true;
        }
    }

    /// <summary>
    /// Clears the affected state for this node and all descendants.
    /// </summary>
    public void ClearAffected()
    {
        IsAffected = false;
        IsAlreadyMatching = false;
        if (IsSmartExpanded)
        {
            // Collapse nodes that were only auto-expanded by highlighting.
            // Pending inline edits no longer force expansion (#10) — they are
            // surfaced in the sidebar, so the tree's shape stays user-driven.
            IsExpanded = false;
            IsSmartExpanded = false;
        }
        foreach (var child in Children)
            child.ClearAffected();
        OnPropertyChanged(nameof(AffectedBadge));
        OnPropertyChanged(nameof(AffectedChildCount));
    }

    /// <summary>
    /// Smart-expand: ensures this node's ancestors are expanded so it becomes visible.
    /// Only auto-expands parents that were not manually expanded.
    /// </summary>
    public void EnsureVisible()
    {
        var parent = Parent;
        while (parent != null)
        {
            if (!parent.IsExpanded)
            {
                parent.IsExpanded = true;
                parent.IsSmartExpanded = true;
            }
            parent = parent.Parent;
        }
    }

    /// <summary>
    /// Raises PropertyChanged for the given property name. Allows external callers to trigger UI updates.
    /// </summary>
    public void RaisePropertyChanged(string propertyName) => OnPropertyChanged(propertyName);

    private int CountAffectedDescendants()
    {
        int count = 0;
        foreach (var child in Children)
        {
            if (child.IsAffected || child.IsAlreadyMatching) count++;
            count += child.CountAffectedDescendants();
        }
        return count;
    }
}
