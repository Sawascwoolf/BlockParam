using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using BlockParam.Diagnostics;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Presentation state for the <see cref="PillMultiSelect"/> UserControl.
/// Owns the row collection, the open/closed state of the popup, the search
/// text, the derived trigger summary (joined abbreviations + count), and the
/// optional explicit-grouping <see cref="PillGroupViewModel"/> map.
/// Rows track their own selection state; this class aggregates by subscribing
/// to <see cref="PillRowViewModel.IsSelected"/> changes.
/// </summary>
/// <remarks>
/// This class is internal infrastructure — hosts use the UserControl's
/// DependencyProperties and never instantiate this directly. The UserControl
/// creates one instance in its constructor and sets the content's
/// <c>DataContext</c> to it so the existing XAML bindings (
/// <c>{Binding IsOpen}</c>, <c>{Binding FilteredItems}</c>, etc.) resolve
/// here unchanged.
///
/// Known v1 scope limit: no keyboard navigation inside the popup — users
/// toggle by click and dismiss by clicking outside. There is no Tab-into-
/// list, arrow-walk, Space/Enter-toggle, or Escape-to-close. Wire those up
/// in <see cref="PillMultiSelect"/>'s code-behind via <c>PreviewKeyDown</c>
/// on the search box and ListBox when a host app needs them.
///
/// Localization: every user-facing string the control renders is exposed
/// as an overridable property (<see cref="SearchPlaceholder"/>,
/// <see cref="ClearTooltip"/>, <see cref="SelectAllText"/>,
/// <see cref="ResetText"/>) plus <see cref="PillOverflowOptions.PlusMoreFormat"/>
/// for the "+N more" suffix. Defaults are English literals; host apps that
/// want localization bind the corresponding DPs on <see cref="PillMultiSelect"/>
/// to their own resource lookup.
/// </remarks>
// `public`, not `internal`: see PillViewModelBase comment / #141. WPF's
// binding engine resolves `{Binding IsOpen}`, `{Binding HasSelection}`, etc.
// against an instance of this type via reflection from PresentationFramework
// (a foreign assembly). Under TIA's partial-trust SandboxDomain those
// reflections silently fail for non-public types, producing the empty-pill
// regression. The members are still control infrastructure — hosts use the
// PillMultiSelect UserControl's DependencyProperties.
public sealed class PillMultiSelectInternalState : PillViewModelBase
{
    private readonly ObservableCollection<PillRowViewModel> _items;
    private readonly ListCollectionView _filteredView;
    private readonly Dictionary<object, PillGroupViewModel> _groups;
    private string _searchText = string.Empty;
    private bool _isOpen;
    private string _label = string.Empty;
    private string? _searchPlaceholder;
    private string? _clearTooltip;
    private string? _selectAllText;
    private string? _resetText;
    private Geometry? _icon;
    private DataTemplate? _groupHeaderTemplate;
    private bool _sortSelectedFirst = true;
    private bool _isExplicitGroupingActive;
    private double _popupWidth = 280;
    private double _popupMaxListHeight = 280;
    private bool _showSearchBox = true;
    private bool _showFooterActions = true;
    private Func<IReadOnlyList<PillRowViewModel>, string>? _displayFormatter;
    private Func<IReadOnlyList<PillRowViewModel>, string?>? _tooltipFormatter;
    private Func<PillRowViewModel, string, bool>? _customFilter;

    internal PillMultiSelectInternalState()
    {
        _items = new ObservableCollection<PillRowViewModel>();
        _items.CollectionChanged += OnItemsCollectionChanged;

        _filteredView = (ListCollectionView)CollectionViewSource.GetDefaultView(_items);
        _filteredView.Filter = FilterPredicate;

        _groups = new Dictionary<object, PillGroupViewModel>();

        ToggleOpenCommand = new PillRelayCommand(() => IsOpen = !IsOpen);
        SelectAllCommand = new PillRelayCommand(SelectAllVisible);
        ResetCommand = new PillRelayCommand(ResetSelection);
        ClearCommand = new PillRelayCommand(ResetSelection);
    }

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    /// <summary>
    /// Optional leading icon (Geometry) shown inside the trigger pill.
    /// Null hides the icon and lets the label sit flush left.
    /// </summary>
    public Geometry? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value))
                OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => _icon != null;

    /// <summary>
    /// Optional override for the group header rendering. When null, the
    /// control's built-in default template is used. Set by the
    /// <see cref="PillMultiSelect.GroupHeaderTemplate"/> DP.
    /// </summary>
    public DataTemplate? GroupHeaderTemplate
    {
        get => _groupHeaderTemplate;
        set => SetProperty(ref _groupHeaderTemplate, value);
    }

    /// <summary>
    /// Placeholder text shown inside the popup's search box when empty.
    /// Default English literal; bind to a host resource lookup for i18n.
    /// </summary>
    public string SearchPlaceholder
    {
        get => _searchPlaceholder ?? "Search...";
        set => SetProperty(ref _searchPlaceholder, value);
    }

    /// <summary>
    /// Tooltip on the trigger pill's clear (X) button. Default English literal;
    /// override to match surrounding terminology or for i18n.
    /// </summary>
    public string ClearTooltip
    {
        get => _clearTooltip ?? "Clear";
        set => SetProperty(ref _clearTooltip, value);
    }

    /// <summary>
    /// Caption of the popup's "Select all" footer button. Default English literal.
    /// </summary>
    public string SelectAllText
    {
        get => _selectAllText ?? "Select all";
        set => SetProperty(ref _selectAllText, value);
    }

    /// <summary>
    /// Caption of the popup's "Reset" footer button. Default English literal.
    /// </summary>
    public string ResetText
    {
        get => _resetText ?? "Reset";
        set => SetProperty(ref _resetText, value);
    }

    /// <summary>
    /// All rows the control knows about, regardless of search/sort. Read-only
    /// — mutate through <see cref="AddItem"/>, <see cref="RemoveItem"/>, and
    /// <see cref="ClearItems"/> so the internal <c>PropertyChanged</c>
    /// subscriptions stay consistent.
    /// </summary>
    public IReadOnlyList<PillRowViewModel> Items => _items;

    public ICollectionView FilteredItems => _filteredView;

    /// <summary>
    /// Group view models keyed by raw group-key value. Owned and mutated by
    /// <see cref="PillItemSource"/> via <see cref="EnsureGroupForKey"/> /
    /// <see cref="RemoveGroup"/> / <see cref="ClearGroups"/>. Read-only access
    /// for tests and downstream consumers.
    /// </summary>
    internal IReadOnlyDictionary<object, PillGroupViewModel> Groups => _groups;

    /// <summary>
    /// True when <see cref="PillItemSource"/> reports that grouping inputs
    /// (member path or selector) are set. Drives the choice between
    /// "selected-first" grouping and "explicit key" grouping in
    /// <see cref="ApplyOrderingToView"/>. Set by
    /// <see cref="OnGroupingActiveChanged"/>.
    /// </summary>
    public bool IsExplicitGroupingActive => _isExplicitGroupingActive;

    /// <summary>
    /// Width of the popup chrome (in DIPs). Default 280 matches the reference
    /// design; tune up for wider Display strings or down for compact contexts.
    /// </summary>
    public double PopupWidth
    {
        get => _popupWidth;
        set => SetProperty(ref _popupWidth, value);
    }

    /// <summary>
    /// Maximum height (in DIPs) the scrollable item list will grow to before
    /// engaging the vertical scrollbar. Default 280 fits ~10 rows.
    /// </summary>
    public double PopupMaxListHeight
    {
        get => _popupMaxListHeight;
        set => SetProperty(ref _popupMaxListHeight, value);
    }

    /// <summary>
    /// Whether the search box is visible at the top of the popup. Set false
    /// for short, fixed lists where filtering is overkill.
    /// </summary>
    public bool ShowSearchBox
    {
        get => _showSearchBox;
        set => SetProperty(ref _showSearchBox, value);
    }

    /// <summary>
    /// Whether the popup footer (Select all / Reset links + n/N counter) is
    /// shown. Set false when bulk actions and counts aren't useful.
    /// </summary>
    public bool ShowFooterActions
    {
        get => _showFooterActions;
        set => SetProperty(ref _showFooterActions, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            var oldValue = _searchText;
            if (!SetProperty(ref _searchText, value)) return;

            // Search overrides the "selected first" grouping — when the
            // user is hunting for a name, ranking by selection isn't
            // useful. Re-apply grouping when the search box is cleared.
            ApplyOrderingToView();
            _filteredView.Refresh();

            // Expand any explicit groups that now contain matches; restore
            // user expansion state when the search box clears. This is what
            // makes a search-hit inside a collapsed group discoverable.
            ApplySearchExpansionPolicy(hadSearch: !string.IsNullOrEmpty(oldValue),
                                       hasSearch: !string.IsNullOrEmpty(value));
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!SetProperty(ref _isOpen, value)) return;

            // Control-side open/close. If the user clicks a blank/zero-width
            // trigger this never fires — pair with the PlcPill/PlcPillGroups
            // lines to tell "trigger not clickable" from "opened but empty".
            // Locals only (bool/int/string) — no readonly-struct member access
            // here, per the partial-trust IL rule for this file (CLAUDE.md).
            Log.Information(
                "PillMultiSelect[label='{Label}']: IsOpen -> {Value}, rows={Rows}",
                _label, value, _items.Count);

            if (value)
            {
                // Popup just opened — snapshot which items are currently
                // selected so the "selected first" ordering stays stable
                // while the user toggles checkboxes inside the popup.
                SnapshotOrdering();
            }
        }
    }

    /// <summary>
    /// When true (default), items selected at the moment the popup opens
    /// are shown at the top, separated from the rest by a 1px divider.
    /// The grouping is frozen while the popup is open so toggling a
    /// checkbox doesn't make the row jump out from under the cursor.
    /// Disable to keep strict source order. Has no effect when
    /// <see cref="IsExplicitGroupingActive"/> is true — explicit grouping
    /// supersedes selected-first.
    /// </summary>
    public bool SortSelectedFirst
    {
        get => _sortSelectedFirst;
        set
        {
            if (SetProperty(ref _sortSelectedFirst, value))
                SnapshotOrdering();
        }
    }

    public int SelectedCount => Items.Count(i => i.IsCheckedTrue);
    public int TotalCount => Items.Count;
    public bool HasSelection => SelectedCount > 0;

    /// <summary>
    /// Custom display strategy for the trigger pill's middle-text region.
    /// Default (when null) is "comma-joined abbreviations of selected items".
    /// Set to <see cref="PillOverflowFormatter.Format"/> bound to a configured
    /// <see cref="PillOverflowOptions"/> when overflow handling is needed.
    /// </summary>
    public Func<IReadOnlyList<PillRowViewModel>, string>? DisplayFormatter
    {
        get => _displayFormatter;
        set
        {
            if (SetProperty(ref _displayFormatter, value))
                OnPropertyChanged(nameof(SelectedAbbreviationsText));
        }
    }

    /// <summary>
    /// What the trigger pill renders for its selection summary. Routes
    /// through <see cref="DisplayFormatter"/> when one is set; otherwise
    /// falls back to a comma-join over the bundled trigger tokens
    /// (fully-checked groups collapse to one "Engineering"-style token,
    /// partial groups and ungrouped rows emit their abbreviations).
    /// </summary>
    public string SelectedAbbreviationsText
    {
        get
        {
            var selected = Items.Where(i => i.IsCheckedTrue).ToList();
            if (_displayFormatter != null)
                return _displayFormatter(selected);

            var tokens = PillTriggerTokenBuilder.Build(selected);
            // #141: a numberless instance DB (e.g. Gen_Main_IDB — _IDB
            // suffix, no DB number) has an empty Abbreviation. Joining the
            // bare Abbreviation here rendered a BLANK closed-trigger summary
            // when no OverflowOptions/_displayFormatter is wired. Fall back
            // to the full Display name — the same rule PillOverflowFormatter
            // already applies on the _displayFormatter path (kept in parity).
            return string.Join(", ", tokens.Select(
                t => string.IsNullOrEmpty(t.Abbreviation) ? t.Display : t.Abbreviation));
        }
    }

    /// <summary>
    /// Optional tooltip strategy for the trigger pill. When null (default),
    /// the pill has no tooltip. When set, the delegate is invoked on each
    /// selection change to produce the tooltip text — common use is showing
    /// full <see cref="PillRowViewModel.Display"/> names of all selected items,
    /// one per line, so users can recover from abbreviation/collapse overflow
    /// without opening the popup. Returning null from the formatter also
    /// suppresses the tooltip.
    /// </summary>
    public Func<IReadOnlyList<PillRowViewModel>, string?>? TooltipFormatter
    {
        get => _tooltipFormatter;
        set
        {
            if (SetProperty(ref _tooltipFormatter, value))
                OnPropertyChanged(nameof(SelectionTooltip));
        }
    }

    /// <summary>
    /// Bound to the trigger pill's <c>ToolTip</c>. Null when the formatter
    /// is unset or returns null, which WPF interprets as "no tooltip".
    /// </summary>
    public string? SelectionTooltip
    {
        get
        {
            if (_tooltipFormatter == null) return null;
            var selected = Items.Where(i => i.IsCheckedTrue).ToList();
            if (selected.Count == 0) return null;
            return _tooltipFormatter(selected);
        }
    }

    public ICommand ToggleOpenCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand ClearCommand { get; }

    public void AddItem(PillRowViewModel item)
    {
        _items.Add(item);
    }

    public bool RemoveItem(PillRowViewModel item)
    {
        return _items.Remove(item);
    }

    /// <summary>
    /// Empty the list. Unsubscribes <c>PropertyChanged</c> handlers BEFORE
    /// the underlying <see cref="ObservableCollection{T}.Clear"/> fires its
    /// Reset notification — Reset doesn't carry <c>OldItems</c>, so handler
    /// cleanup must happen here, not inside <see cref="OnItemsCollectionChanged"/>.
    /// </summary>
    public void ClearItems()
    {
        foreach (var item in _items)
            item.PropertyChanged -= OnItemPropertyChanged;
        _items.Clear();
    }

    // ── Explicit-grouping management (called by PillItemSource) ──────────────

    /// <summary>
    /// Returns the <see cref="PillGroupViewModel"/> for the given key, creating
    /// it on first request. Header text defaults to <c>key.ToString()</c> for
    /// the default header template; hosts wanting richer headers bind against
    /// <see cref="PillGroupViewModel.Key"/> in their own GroupHeaderTemplate.
    /// </summary>
    internal PillGroupViewModel EnsureGroupForKey(object key)
    {
        if (_groups.TryGetValue(key, out var existing))
            return existing;

        var header = key is PillItemSource.NullGroupSentinel
            ? string.Empty
            : key.ToString() ?? string.Empty;
        var vm = new PillGroupViewModel(key, header);
        _groups[key] = vm;
        return vm;
    }

    /// <summary>
    /// Removes a group VM from the map. Called by <see cref="PillItemSource"/>
    /// after the group's last child row leaves.
    /// </summary>
    internal void RemoveGroup(PillGroupViewModel group)
    {
        _groups.Remove(group.Key);
    }

    /// <summary>
    /// Empties the group map. Called during full rebuild / regroup. Detaches
    /// each group's children first so their <c>PropertyChanged</c> handlers
    /// against the (about-to-be-orphaned) group's <c>OnChildPropertyChanged</c>
    /// unwind cleanly. Without this, rows held alive by an outside reference
    /// (e.g. the host's <c>SelectedItems</c>) would keep stale subscriptions
    /// to discarded group VMs.
    /// </summary>
    internal void ClearGroups()
    {
        foreach (var group in _groups.Values)
        {
            // Snapshot to a local because RemoveChild mutates the children list.
            var children = group.Children.ToArray();
            foreach (var child in children)
                group.RemoveChild(child);
        }
        _groups.Clear();
    }

    /// <summary>
    /// Called by <see cref="PillItemSource"/> whenever the
    /// <c>GroupKeyMemberPath</c> / <c>GroupKeyOverride</c> configuration
    /// changes. Updates <see cref="IsExplicitGroupingActive"/> and re-applies
    /// the view's group descriptions.
    /// </summary>
    internal void OnGroupingActiveChanged(bool isActive)
    {
        if (_isExplicitGroupingActive == isActive) return;
        _isExplicitGroupingActive = isActive;
        OnPropertyChanged(nameof(IsExplicitGroupingActive));
        SnapshotOrdering();
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Reset is handled by ClearItems before this fires (it can't be
        // handled here — the collection is already empty and OldItems is
        // null). Add/Remove paths still need to (un)subscribe.
        if (e.NewItems != null)
            foreach (PillRowViewModel item in e.NewItems)
                item.PropertyChanged += OnItemPropertyChanged;

        if (e.OldItems != null)
            foreach (PillRowViewModel item in e.OldItems)
                item.PropertyChanged -= OnItemPropertyChanged;

        RaiseAggregatesChanged();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PillRowViewModel.IsSelected))
            RaiseAggregatesChanged();
    }

    private void RaiseAggregatesChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedAbbreviationsText));
        OnPropertyChanged(nameof(SelectionTooltip));
    }

    /// <summary>
    /// Captures the current selection state into each item's
    /// <see cref="PillRowViewModel.WasSelectedAtSort"/> field and re-applies
    /// the sort/group descriptions on the filtered view. Called when the
    /// popup opens, when <see cref="SortSelectedFirst"/> changes, or when
    /// grouping mode toggles.
    /// </summary>
    private void SnapshotOrdering()
    {
        var enabled = _sortSelectedFirst;
        foreach (var item in Items)
            item.WasSelectedAtSort = enabled && item.IsCheckedTrue;
        ApplyOrderingToView();
    }

    private void ApplyOrderingToView()
    {
        using (_filteredView.DeferRefresh())
        {
            _filteredView.SortDescriptions.Clear();
            _filteredView.GroupDescriptions.Clear();

            if (_isExplicitGroupingActive)
            {
                // Explicit grouping supersedes selected-first; the user has
                // declared a meaningful grouping axis and we honour it.
                _filteredView.GroupDescriptions.Add(new PillGroupDescription());
                return;
            }

            // Skip grouping/sorting when search is active (overflow rules
            // already shape what the user sees) or when grouping would
            // produce a single group (everything-or-nothing selected) —
            // in either case the divider is meaningless and the original
            // source order is what the user expects.
            if (!_sortSelectedFirst) return;
            if (!string.IsNullOrEmpty(_searchText)) return;

            var anyIn = false;
            var anyOut = false;
            foreach (var item in Items)
            {
                if (item.WasSelectedAtSort) anyIn = true;
                else anyOut = true;
                if (anyIn && anyOut) break;
            }
            if (!(anyIn && anyOut)) return;

            _filteredView.SortDescriptions.Add(
                new SortDescription(nameof(PillRowViewModel.WasSelectedAtSort),
                                    ListSortDirection.Descending));
            _filteredView.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(PillRowViewModel.WasSelectedAtSort)));
        }
    }

    /// <summary>
    /// When explicit grouping is active and a search is in progress, force
    /// every group containing at least one matching row to be expanded so
    /// the hit is reachable. When the search clears, restore each group's
    /// pre-search expanded state. No-op when explicit grouping is off.
    /// </summary>
    private void ApplySearchExpansionPolicy(bool hadSearch, bool hasSearch)
    {
        if (!_isExplicitGroupingActive) return;

        if (hasSearch)
        {
            foreach (var group in _groups.Values)
            {
                var hasMatch = false;
                foreach (var child in group.Children)
                {
                    if (FilterPredicate(child))
                    {
                        hasMatch = true;
                        break;
                    }
                }

                if (hasMatch)
                    group.ForceExpandedForSearch();
            }
        }
        else if (hadSearch)
        {
            foreach (var group in _groups.Values)
                group.RestoreUserExpandedAfterSearch();
        }
    }

    /// <summary>
    /// Optional custom filter predicate. When set, replaces the default
    /// Display/Abbreviation contains-check. Receives the row and current
    /// search text; return true to keep the row visible.
    /// <para>Set by the UserControl when the host provides a
    /// <c>FilterPredicate</c> CLR escape hatch that operates on source items.
    /// The UserControl wraps that delegate to project source → row before
    /// forwarding it here.</para>
    /// </summary>
    internal Func<PillRowViewModel, string, bool>? CustomFilter
    {
        get => _customFilter;
        set
        {
            _customFilter = value;
            _filteredView.Refresh();
        }
    }

    private bool FilterPredicate(object obj)
    {
        if (string.IsNullOrEmpty(_searchText)) return true;
        if (obj is not PillRowViewModel item) return false;

        if (_customFilter != null)
            return _customFilter(item, _searchText);

        return item.Display.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0
            || item.Abbreviation.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Selects every item currently passing the filter. Items hidden by the
    /// active search aren't touched — matches the convention of "Select all"
    /// in filterable list UIs (Linear, Notion, Figma) where it means
    /// "all visible", not "all in dataset".
    /// </summary>
    private void SelectAllVisible()
    {
        foreach (var item in _filteredView.Cast<PillRowViewModel>())
            item.IsSelected = true;
    }

    private void ResetSelection()
    {
        foreach (var item in Items)
            item.IsSelected = false;
    }

    /// <summary>
    /// Custom <see cref="GroupDescription"/> that uses each row's already-
    /// assigned <see cref="PillRowViewModel.OwningGroup"/> as the group key.
    /// This way the <see cref="CollectionViewGroup.Name"/> rendered by the
    /// GroupItem template IS the <see cref="PillGroupViewModel"/>, so XAML
    /// can bind <c>{Binding Name.IsSelected}</c>, <c>{Binding Name.IsExpanded}</c>
    /// etc. against the group's tri-state and expansion props directly.
    /// </summary>
    private sealed class PillGroupDescription : GroupDescription
    {
        public override object GroupNameFromItem(object item, int level, CultureInfo culture)
        {
            if (item is PillRowViewModel row && row.OwningGroup is { } group)
                return group;

            // Fall back to a stable sentinel when a row somehow isn't bound
            // to a group VM. Keeps the view from crashing in misconfiguration.
            return PillItemSource.NullGroupSentinel.Instance;
        }

        public override bool NamesMatch(object groupName, object itemName)
            => ReferenceEquals(groupName, itemName);
    }
}
