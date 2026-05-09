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
public class PillMultiSelectViewModel : ViewModelBase
{
    private string _searchText = string.Empty;
    private bool _isOpen;
    private string _label = string.Empty;
    private string? _searchPlaceholder;
    private Geometry? _icon;
    private readonly ICollectionView _filteredView;

    public PillMultiSelectViewModel()
    {
        Items = new ObservableCollection<PillMultiSelectItemViewModel>();
        Items.CollectionChanged += OnItemsCollectionChanged;

        _filteredView = CollectionViewSource.GetDefaultView(Items);
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

    public ObservableCollection<PillMultiSelectItemViewModel> Items { get; }

    public ICollectionView FilteredItems => _filteredView;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                _filteredView.Refresh();
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public int SelectedCount => Items.Count(i => i.IsSelected);
    public int TotalCount => Items.Count;
    public bool HasSelection => SelectedCount > 0;

    /// <summary>
    /// Comma-joined abbreviations of the selected items, in selection order.
    /// Drives the trigger pill's middle-text region — matches the reference
    /// screenshot's "AKO, BSC" / "AKO, EKR, GWE" rendering.
    /// </summary>
    public string SelectedAbbreviationsText =>
        string.Join(", ", Items.Where(i => i.IsSelected).Select(i => i.Abbreviation));

    public ICommand ToggleOpenCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand ClearCommand { get; }

    public void AddItem(PillMultiSelectItemViewModel item)
    {
        Items.Add(item);
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
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
