namespace BlockParam.UI.Controls.PillMultiSelect;

public class PillMultiSelectItemViewModel : ViewModelBase
{
    private bool _isSelected;

    public PillMultiSelectItemViewModel(string display, string abbreviation, object? payload = null)
    {
        Display = display;
        Abbreviation = abbreviation;
        Payload = payload;
    }

    public string Display { get; }
    public string Abbreviation { get; }
    public object? Payload { get; }

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
    private bool _wasSelectedAtSort;
}
