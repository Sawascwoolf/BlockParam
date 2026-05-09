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
}
