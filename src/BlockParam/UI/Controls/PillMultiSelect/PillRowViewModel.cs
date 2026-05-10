namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Internal per-row view model for <see cref="PillMultiSelect"/>.
/// Each row wraps one host-supplied source item and holds the strings
/// the XAML data template needs: <see cref="Display"/>, <see cref="Abbreviation"/>,
/// and the toggle state <see cref="IsSelected"/>.
/// </summary>
/// <remarks>
/// <see cref="Display"/> and <see cref="Abbreviation"/> are settable so
/// <see cref="PillItemSource"/> can re-resolve them when
/// <see cref="PillMultiSelect.DisplayMemberPath"/> or
/// <see cref="PillMultiSelect.AbbreviationMemberPath"/> changes without
/// needing to rebuild the row collection.
///
/// <see cref="GroupKey"/> is a v2 seam for future grouping support — always
/// null in v1. <see cref="PillMultiSelectInternalState"/> leaves
/// <c>ICollectionView.GroupDescriptions</c> hookable precisely so v2 can
/// populate this without rewiring the view pipeline.
/// </remarks>
internal sealed class PillRowViewModel : ViewModelBase
{
    private string _display;
    private string _abbreviation;
    private bool _isSelected;
    private bool _wasSelectedAtSort;

    internal PillRowViewModel(object source, string display, string abbreviation)
    {
        Source = source;
        _display = display;
        _abbreviation = abbreviation;
    }

    /// <summary>
    /// Reference to the host item this row wraps. Used by
    /// <see cref="PillItemSource"/> to map source-collection changes back
    /// to their wrapper rows. Never null.
    /// </summary>
    public object Source { get; }

    public string Display
    {
        get => _display;
        set => SetProperty(ref _display, value);
    }

    public string Abbreviation
    {
        get => _abbreviation;
        set => SetProperty(ref _abbreviation, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Snapshot of <see cref="IsSelected"/> taken when the popup opened.
    /// Drives the "selected items at the top" ordering — held frozen while
    /// the popup is open so toggling a checkbox doesn't reshuffle the list
    /// out from under the user's cursor. Reset on the next popup open.
    /// </summary>
    public bool WasSelectedAtSort
    {
        get => _wasSelectedAtSort;
        set => SetProperty(ref _wasSelectedAtSort, value);
    }

    /// <summary>
    /// v2 grouping seam — always null in v1. When v2 lands
    /// <c>GroupKeyMemberPath</c>, <see cref="PillItemSource"/> populates
    /// this and <see cref="PillMultiSelectInternalState"/> attaches a
    /// <c>PropertyGroupDescription</c> against it.
    /// </summary>
    public object? GroupKey { get; set; }
}
