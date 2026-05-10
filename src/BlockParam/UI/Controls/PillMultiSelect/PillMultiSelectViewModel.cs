using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using BlockParam.Localization;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// View model for <see cref="PillMultiSelect"/>. Owns the item collection,
/// the open/closed state of the popup, the search text, and the derived
/// trigger summary (joined abbreviations + count). Items track their own
/// selection — the VM aggregates by listening for <see cref="PillMultiSelectItemViewModel.IsSelected"/>
/// changes — so callers can drive selection from outside without going
/// through the VM (e.g. by setting <c>IsSelected</c> on a known item).
/// </summary>
/// <remarks>
/// Known v1 scope limit: no keyboard navigation inside the popup — users
/// toggle by click and dismiss by clicking outside. There is no Tab-into-
/// list, arrow-walk, Space/Enter-toggle, or Escape-to-close. Wire those up
/// in <see cref="PillMultiSelect"/>'s code-behind via <c>PreviewKeyDown</c>
/// on the search box and ListBox when a host app needs them.
///
/// Localization: every user-facing string the control renders is exposed
/// as an overridable VM property (<see cref="SearchPlaceholder"/>,
/// <see cref="ClearTooltip"/>, <see cref="SelectAllText"/>,
/// <see cref="ResetText"/>) plus <see cref="PillOverflowOptions.PlusMoreFormat"/>
/// for the "+N more" suffix. Defaults pull from BlockParam's resx so the
/// in-tree app stays unchanged; host apps that don't ship that resx can
/// set the properties directly without dragging in <c>BlockParam.Localization</c>.
/// </remarks>
public class PillMultiSelectViewModel : ViewModelBase
{
    private readonly ObservableCollection<PillMultiSelectItemViewModel> _items;
    private readonly ListCollectionView _filteredView;
    private string _searchText = string.Empty;
    private bool _isOpen;
    private string _label = string.Empty;
    private string? _searchPlaceholder;
    private string? _clearTooltip;
    private string? _selectAllText;
    private string? _resetText;
    private Geometry? _icon;
    private bool _sortSelectedFirst = true;
    private double _popupWidth = 280;
    private double _popupMaxListHeight = 280;
    private bool _showSearchBox = true;
    private bool _showFooterActions = true;
    private Func<IReadOnlyList<PillMultiSelectItemViewModel>, string>? _displayFormatter;
    private Func<IReadOnlyList<PillMultiSelectItemViewModel>, string?>? _tooltipFormatter;

    public PillMultiSelectViewModel()
    {
        _items = new ObservableCollection<PillMultiSelectItemViewModel>();
        _items.CollectionChanged += OnItemsCollectionChanged;

        _filteredView = (ListCollectionView)CollectionViewSource.GetDefaultView(_items);
        _filteredView.Filter = FilterPredicate;

        ToggleOpenCommand = new RelayCommand(() => IsOpen = !IsOpen);
        SelectAllCommand = new RelayCommand(SelectAllVisible);
        ResetCommand = new RelayCommand(ResetSelection);
        ClearCommand = new RelayCommand(ResetSelection);
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
    /// Placeholder text shown inside the popup's search box when empty.
    /// Defaults to the localized "Search..." string; callers can override
    /// for context-specific phrasing (e.g. "Search employees...").
    /// </summary>
    public string SearchPlaceholder
    {
        get => _searchPlaceholder ?? Res.Get("PillMultiSelect_SearchPlaceholder");
        set => SetProperty(ref _searchPlaceholder, value);
    }

    /// <summary>
    /// Tooltip on the trigger pill's clear (X) button. Defaults to the
    /// localized "Clear"; override per-host to match surrounding terminology.
    /// </summary>
    public string ClearTooltip
    {
        get => _clearTooltip ?? Res.Get("PillMultiSelect_Clear");
        set => SetProperty(ref _clearTooltip, value);
    }

    /// <summary>
    /// Caption of the popup's "Select all" footer button. Defaults to the
    /// localized "Select all".
    /// </summary>
    public string SelectAllText
    {
        get => _selectAllText ?? Res.Get("PillMultiSelect_SelectAll");
        set => SetProperty(ref _selectAllText, value);
    }

    /// <summary>
    /// Caption of the popup's "Reset" footer button. Defaults to the
    /// localized "Reset".
    /// </summary>
    public string ResetText
    {
        get => _resetText ?? Res.Get("PillMultiSelect_Reset");
        set => SetProperty(ref _resetText, value);
    }

    /// <summary>
    /// All items the control knows about, regardless of search/sort. Read-only
    /// — callers add and remove through <see cref="AddItem"/>,
    /// <see cref="RemoveItem"/>, and <see cref="ClearItems"/> so the VM can
    /// keep its internal <c>PropertyChanged</c> subscriptions in sync.
    /// </summary>
    public IReadOnlyList<PillMultiSelectItemViewModel> Items => _items;

    public ICollectionView FilteredItems => _filteredView;

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
    /// shown. Set false when bulk actions and counts aren't useful to the
    /// host application.
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
            if (SetProperty(ref _searchText, value))
            {
                // Search overrides the "selected first" grouping — when the
                // user is hunting for a name, ranking by selection isn't
                // useful. Re-apply grouping when the search box is cleared.
                ApplyOrderingToView();
                _filteredView.Refresh();
            }
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (SetProperty(ref _isOpen, value) && value)
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
    /// The grouping is *frozen* while the popup is open so toggling a
    /// checkbox doesn't make the row jump out from under the cursor.
    /// Disable to keep strict source order.
    /// </summary>
    public bool SortSelectedFirst
    {
        get => _sortSelectedFirst;
        set
        {
            if (SetProperty(ref _sortSelectedFirst, value))
            {
                SnapshotOrdering();
            }
        }
    }

    public int SelectedCount => Items.Count(i => i.IsSelected);
    public int TotalCount => Items.Count;
    public bool HasSelection => SelectedCount > 0;

    /// <summary>
    /// Custom display strategy for the trigger pill's middle-text region.
    /// Default (when null) is "comma-joined abbreviations of selected items".
    /// Callers with overflow concerns (long DB names, many entries) set this
    /// to <see cref="PillOverflowFormatter.Format"/> bound to a configured
    /// <see cref="PillOverflowOptions"/>.
    /// </summary>
    public Func<IReadOnlyList<PillMultiSelectItemViewModel>, string>? DisplayFormatter
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
    /// falls back to the simple comma-join used by the reference design.
    /// </summary>
    public string SelectedAbbreviationsText
    {
        get
        {
            var selected = Items.Where(i => i.IsSelected).ToList();
            return _displayFormatter != null
                ? _displayFormatter(selected)
                : string.Join(", ", selected.Select(i => i.Abbreviation));
        }
    }

    /// <summary>
    /// Optional tooltip strategy for the trigger pill. When null (default),
    /// the pill has no tooltip. When set, the delegate is invoked on each
    /// selection change to produce the tooltip text — common use is showing
    /// full <see cref="PillMultiSelectItemViewModel.Display"/> names of all
    /// selected items, one per line, so users can recover from the
    /// abbreviation/collapse overflow without opening the popup.
    /// Returning null from the formatter also suppresses the tooltip
    /// (e.g. when nothing is selected).
    /// </summary>
    public Func<IReadOnlyList<PillMultiSelectItemViewModel>, string?>? TooltipFormatter
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
    /// is unset or the formatter returns null, which WPF interprets as
    /// "no tooltip" (no popup, no hover delay penalty).
    /// </summary>
    public string? SelectionTooltip
    {
        get
        {
            if (_tooltipFormatter == null) return null;
            var selected = Items.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0) return null;
            return _tooltipFormatter(selected);
        }
    }

    public ICommand ToggleOpenCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand ClearCommand { get; }

    public void AddItem(PillMultiSelectItemViewModel item)
    {
        _items.Add(item);
    }

    public bool RemoveItem(PillMultiSelectItemViewModel item)
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

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Reset is handled by ClearItems before this fires (it can't be
        // handled here — the collection is already empty and OldItems is
        // null). Add/Remove paths still need to (un)subscribe.
        if (e.NewItems != null)
            foreach (PillMultiSelectItemViewModel item in e.NewItems)
                item.PropertyChanged += OnItemPropertyChanged;

        if (e.OldItems != null)
            foreach (PillMultiSelectItemViewModel item in e.OldItems)
                item.PropertyChanged -= OnItemPropertyChanged;

        RaiseAggregatesChanged();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PillMultiSelectItemViewModel.IsSelected))
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
    /// <see cref="PillMultiSelectItemViewModel.WasSelectedAtSort"/> field
    /// and re-applies the sort/group descriptions on the filtered view.
    /// Called when the popup opens or when <see cref="SortSelectedFirst"/>
    /// changes.
    /// </summary>
    private void SnapshotOrdering()
    {
        var enabled = _sortSelectedFirst;
        foreach (var item in Items)
            item.WasSelectedAtSort = enabled && item.IsSelected;
        ApplyOrderingToView();
    }

    private void ApplyOrderingToView()
    {
        using (_filteredView.DeferRefresh())
        {
            _filteredView.SortDescriptions.Clear();
            _filteredView.GroupDescriptions.Clear();

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
                new SortDescription(nameof(PillMultiSelectItemViewModel.WasSelectedAtSort),
                                    ListSortDirection.Descending));
            _filteredView.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(PillMultiSelectItemViewModel.WasSelectedAtSort)));
        }
    }

    private bool FilterPredicate(object obj)
    {
        if (string.IsNullOrEmpty(_searchText)) return true;
        if (obj is not PillMultiSelectItemViewModel item) return false;
        return item.Display.IndexOf(_searchText, System.StringComparison.OrdinalIgnoreCase) >= 0
            || item.Abbreviation.IndexOf(_searchText, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Selects every item currently passing the filter. Items hidden by the
    /// active search aren't touched — matches the convention of "Select all"
    /// in filterable list UIs (Linear, Notion, Figma) where it means
    /// "all *visible*", not "all in dataset".
    /// </summary>
    private void SelectAllVisible()
    {
        foreach (var item in _filteredView.Cast<PillMultiSelectItemViewModel>())
            item.IsSelected = true;
    }

    private void ResetSelection()
    {
        foreach (var item in Items)
            item.IsSelected = false;
    }
}
