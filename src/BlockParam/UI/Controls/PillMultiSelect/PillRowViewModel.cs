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
/// <see cref="IsSelected"/> is <see cref="bool"/>? so the row can faithfully
/// represent an indeterminate state pushed in by an external bool? source
/// property (Edge B). User clicks on the row's checkbox only ever produce
/// <c>true</c>/<c>false</c> — the leaf rows are not three-state from the UI
/// perspective. Indeterminate header tri-state lives on
/// <see cref="PillGroupViewModel"/>, not here.
///
/// <see cref="GroupKey"/> is populated by <see cref="PillItemSource"/> when
/// <see cref="PillMultiSelect.GroupKeyMemberPath"/> / <c>GroupKeySelector</c>
/// is set; the matching <see cref="PillGroupViewModel"/> is hung off
/// <see cref="OwningGroup"/> so the row can notify its group on selection
/// changes without a tree walk.
/// </remarks>
// `public`, not `internal`: see PillViewModelBase comment / #141. The row
// DataTemplate binds `{Binding Display}`, `{Binding Abbreviation}`,
// `{Binding IsSelected}` against instances of this type, which WPF resolves
// by reflection — non-public under TIA's partial-trust SandboxDomain means
// rows render blank.
public sealed class PillRowViewModel : PillViewModelBase
{
    private string _display;
    private string _abbreviation;
    private bool? _isSelected;
    private bool _wasSelectedAtSort;

    internal PillRowViewModel(object source, string display, string abbreviation)
    {
        Source = source;
        _display = display;
        _abbreviation = abbreviation;
        _isSelected = false;
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

    /// <summary>
    /// Tri-state selection. <c>true</c> = checked, <c>false</c> = unchecked,
    /// <c>null</c> = indeterminate (only ever set when an external bool?
    /// source property feeds null through Edge B). User clicks on the
    /// checkbox produce only <c>true</c>/<c>false</c>.
    /// </summary>
    public bool? IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Convenience for callers that only care about "fully checked" — true
    /// only when <see cref="IsSelected"/> is exactly <c>true</c>. Used by
    /// <see cref="PillMultiSelectInternalState"/> aggregates and by
    /// <see cref="PillSelectionSync"/> when projecting into
    /// <see cref="PillMultiSelect.SelectedItems"/> (which never contains
    /// indeterminate entries).
    /// </summary>
    public bool IsCheckedTrue => _isSelected == true;

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
    /// Group key value resolved from the source item via
    /// <see cref="PillMultiSelect.GroupKeyMemberPath"/> or
    /// <c>GroupKeySelector</c>. Null when no grouping is configured —
    /// in which case the ListBox renders rows flat (no group headers).
    /// </summary>
    public object? GroupKey { get; set; }

    /// <summary>
    /// Back-reference to the <see cref="PillGroupViewModel"/> that owns
    /// this row when grouping is active. Null when no grouping is configured.
    /// Set by <see cref="PillItemSource"/> as it places rows into groups.
    /// </summary>
    internal PillGroupViewModel? OwningGroup { get; set; }
}
