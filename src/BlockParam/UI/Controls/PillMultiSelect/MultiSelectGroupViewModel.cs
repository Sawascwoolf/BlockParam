using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Header view model for one group inside <see cref="PillMultiSelect"/> when
/// <see cref="PillMultiSelect.GroupKeyMemberPath"/> (or the
/// <c>GroupKeySelector</c> escape hatch) is configured. Owns the aggregate
/// tri-state checkbox (<see cref="IsSelected"/>), the expand/collapse state
/// (<see cref="IsExpanded"/>), and the header label.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tri-state semantics</b>: <see cref="IsSelected"/> is <c>true</c> when
/// every child row is selected, <c>false</c> when none are, and <c>null</c>
/// when the children are mixed. The header checkbox in XAML binds two-way
/// against this property with <c>IsThreeState=false</c> so user clicks only
/// produce <c>true</c>/<c>false</c> — indeterminate appears only via the
/// aggregate-recompute path triggered by child changes.
/// </para>
/// <para>
/// <b>Expand/collapse</b>: <see cref="IsExpanded"/> defaults to <c>true</c>.
/// When the popup search box is non-empty, the internal state temporarily
/// forces <see cref="IsExpanded"/> on every group that has at least one
/// row passing the filter and restores the prior value when search clears.
/// </para>
/// </remarks>
// `public`, not `internal`: see MultiSelectViewModelBase comment / #141. The group
// header template binds `{Binding Header}`, `{Binding IsSelected}`,
// `{Binding IsExpanded}`, `{Binding SelectedCount}`, `{Binding TotalCount}`
// against instances of this type — same partial-trust reflection failure
// otherwise.
public sealed class MultiSelectGroupViewModel : MultiSelectViewModelBase
{
    private readonly List<MultiSelectRowViewModel> _children = new();
    private bool _isExpanded = true;
    private bool? _isSelected = false;
    private string _header;

    // While propagating IsSelected to children (header → leaves), suppress the
    // aggregate-recompute callback from each child so we don't re-derive the
    // header state mid-loop. Same shape as MultiSelectSelectionSync's _syncing guard.
    private bool _propagatingToChildren;

    // While the internal state forces IsExpanded for an active search,
    // remember the user's prior collapsed state so it can be restored when
    // the search clears. Null = no override active.
    private bool? _userIsExpandedBeforeSearch;

    // Guards the IsExpanded setter from rewriting _userIsExpandedBeforeSearch
    // when ForceExpandedForSearch is the caller. Without this guard the
    // force-expand would clobber whatever pre-search value we just captured.
    private bool _settingExpandedForSearch;

    // Sticks once the user (or any external caller) touches IsExpanded during
    // a search session. ForceExpandedForSearch then leaves the group alone on
    // subsequent keystrokes so a mid-search collapse persists. Cleared by
    // RestoreUserExpandedAfterSearch when the search ends.
    private bool _userOverrodeExpandedDuringSearch;

    internal MultiSelectGroupViewModel(object key, string header)
    {
        Key = key;
        _header = header;

        ToggleExpandedCommand = new MultiSelectRelayCommand(() => IsExpanded = !IsExpanded);
    }

    /// <summary>
    /// The raw group-key value (e.g. the property value pulled from each
    /// source item via <c>GroupKeyMemberPath</c>). Used by
    /// <see cref="MultiSelectItemSource"/> for the
    /// <c>Dictionary&lt;key, MultiSelectGroupViewModel&gt;</c> lookup and by
    /// <see cref="MultiSelectInternalState"/> to expose this VM as the
    /// <see cref="System.Windows.Data.CollectionViewGroup.Name"/> of the
    /// matching <see cref="System.Windows.Data.CollectionViewGroup"/>.
    /// </summary>
    public object Key { get; }

    /// <summary>
    /// Header text shown at the top of the group inside the popup list.
    /// Defaults to <c>Key.ToString()</c>; hosts that want richer labels
    /// can override via the <c>GroupHeaderTemplate</c> DP and bind against
    /// <see cref="Key"/> directly.
    /// </summary>
    public string Header
    {
        get => _header;
        set => SetProperty(ref _header, value);
    }

    /// <summary>
    /// Aggregate tri-state derived from the child rows. Setting this from
    /// XAML (user clicks header checkbox) propagates the non-null value to
    /// every child; setting it from <see cref="RecomputeFromChildren"/>
    /// only updates the displayed state without touching children.
    /// </summary>
    public bool? IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;

            // User-driven write (header checkbox clicked) — push to children.
            // A null write from the user shouldn't happen (XAML uses
            // IsThreeState=false) but we treat it as a no-op for safety.
            if (!_propagatingToChildren && value.HasValue)
            {
                _propagatingToChildren = true;
                try
                {
                    foreach (var child in _children)
                        child.IsSelected = value.Value;
                }
                finally
                {
                    _propagatingToChildren = false;
                }
            }
        }
    }

    /// <summary>
    /// Whether the group's children are visible. <c>true</c> by default;
    /// flipped by the user via the expand chevron in the group header
    /// (or programmatically via <see cref="ToggleExpandedCommand"/>).
    /// When a search is active, an external write here is treated as the
    /// user's new "true" preference and overwrites the remembered pre-search
    /// value so a mid-search collapse persists across the search clearing.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;

            // External (non-search) writes during a search session reflect a
            // fresh user intent. Update the remembered pre-search value so
            // RestoreUserExpandedAfterSearch honours it when the search clears,
            // and latch the override flag so ForceExpandedForSearch stops
            // re-forcing the expand on subsequent keystrokes.
            if (!_settingExpandedForSearch && _userIsExpandedBeforeSearch.HasValue)
            {
                _userIsExpandedBeforeSearch = value;
                _userOverrodeExpandedDuringSearch = true;
            }
        }
    }

    /// <summary>
    /// Number of children currently selected (counted as fully-checked only —
    /// indeterminate children don't count). Bindable via INPC for the
    /// header's "n / N" badge.
    /// </summary>
    public int SelectedCount => _children.Count(c => c.IsCheckedTrue);

    /// <summary>Total number of children in this group. Bindable via INPC.</summary>
    public int TotalCount => _children.Count;

    public ICommand ToggleExpandedCommand { get; }

    /// <summary>
    /// Child rows owned by this group. The list reference is owned by this
    /// VM — <see cref="MultiSelectItemSource"/> mutates it via <see cref="AddChild"/>
    /// / <see cref="RemoveChild"/> so PropertyChanged subscriptions stay
    /// consistent.
    /// </summary>
    public IReadOnlyList<MultiSelectRowViewModel> Children => _children;

    internal void AddChild(MultiSelectRowViewModel child)
    {
        _children.Add(child);
        child.OwningGroup = this;
        child.PropertyChanged += OnChildPropertyChanged;
        RecomputeFromChildren();
    }

    internal bool RemoveChild(MultiSelectRowViewModel child)
    {
        if (!_children.Remove(child)) return false;
        child.PropertyChanged -= OnChildPropertyChanged;
        if (ReferenceEquals(child.OwningGroup, this))
            child.OwningGroup = null;
        RecomputeFromChildren();
        return true;
    }

    private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_propagatingToChildren) return;
        if (e.PropertyName != nameof(MultiSelectRowViewModel.IsSelected)) return;
        RecomputeFromChildren();
    }

    /// <summary>
    /// Re-derives <see cref="IsSelected"/> from the children's current
    /// <see cref="MultiSelectRowViewModel.IsSelected"/> values:
    /// <list type="bullet">
    ///   <item>All children <c>true</c> → header <c>true</c>.</item>
    ///   <item>All children <c>false</c> → header <c>false</c>.</item>
    ///   <item>Any other mix (true+false, anything with null) → header <c>null</c>.</item>
    /// </list>
    /// </summary>
    internal void RecomputeFromChildren()
    {
        bool? aggregate;
        if (_children.Count == 0)
        {
            aggregate = false;
        }
        else
        {
            var anyTrue = false;
            var anyFalse = false;
            var anyNull = false;
            foreach (var c in _children)
            {
                if (c.IsSelected == true) anyTrue = true;
                else if (c.IsSelected == false) anyFalse = true;
                else anyNull = true;
            }

            if (anyNull || (anyTrue && anyFalse)) aggregate = null;
            else if (anyTrue) aggregate = true;
            else aggregate = false;
        }

        if (_isSelected != aggregate)
        {
            // Bypass the setter so we don't push the recomputed value back to
            // children — the setter's propagation is for user-initiated writes.
            _isSelected = aggregate;
            OnPropertyChanged(nameof(IsSelected));
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(TotalCount));
    }

    /// <summary>
    /// Programmatic expand triggered by an active search match. Remembers
    /// the user's prior <see cref="IsExpanded"/> value the first time this
    /// is called so <see cref="RestoreUserExpandedAfterSearch"/> can put it
    /// back when search clears. Subsequent calls during the same search
    /// session don't overwrite the remembered value. If the user explicitly
    /// collapsed the group mid-search, the setter has already updated the
    /// remembered value, and we don't re-force the expand here so the
    /// collapse persists across keystrokes.
    /// </summary>
    internal void ForceExpandedForSearch()
    {
        if (_userIsExpandedBeforeSearch == null)
            _userIsExpandedBeforeSearch = _isExpanded;

        // User already expressed a preference during this search session —
        // either left the group expanded or collapsed it explicitly. Honour
        // their last write; don't override on subsequent keystrokes.
        if (_userOverrodeExpandedDuringSearch) return;

        if (_isExpanded) return;

        _settingExpandedForSearch = true;
        try { IsExpanded = true; }
        finally { _settingExpandedForSearch = false; }
    }

    /// <summary>
    /// Restores the user's pre-search <see cref="IsExpanded"/> value (or the
    /// most recent mid-search write, whichever is later) and clears the
    /// remembered flags. Safe to call when no search was active.
    /// </summary>
    internal void RestoreUserExpandedAfterSearch()
    {
        if (_userIsExpandedBeforeSearch is bool prior)
        {
            _userIsExpandedBeforeSearch = null;
            _userOverrodeExpandedDuringSearch = false;
            IsExpanded = prior;
        }
    }
}

