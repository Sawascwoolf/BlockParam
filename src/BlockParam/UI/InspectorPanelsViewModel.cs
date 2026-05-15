using System.Windows.Input;

namespace BlockParam.UI;

/// <summary>
/// Inspector-panel expand/collapse state (#80 slice 1).
///
/// <para>
/// Owns the four inspector section expanders (Bulk Edit, Bulk Preview,
/// Pending, Issues) plus the overall inspector sidebar collapse flag.
/// Pure UI state — no business logic, no cross-slice dependencies — so
/// this is the pattern-setter slice for the wider #80 split: the host
/// VM exposes one child VM property (<c>Inspector</c>), XAML rebinds
/// from <c>{Binding IsBulkEditExpanded}</c> to
/// <c>{Binding Inspector.IsBulkEditExpanded}</c>, and the slice gets
/// its own focused test class.
/// </para>
/// </summary>
public class InspectorPanelsViewModel : ViewModelBase
{
    private bool _isInspectorCollapsed;
    private bool _isBulkEditExpanded = true;
    private bool _isBulkPreviewExpanded = true;
    private bool _isPendingExpanded = true;
    private bool _isIssuesExpanded = true;

    public InspectorPanelsViewModel()
    {
        ToggleInspectorCommand = new RelayCommand(() => IsInspectorCollapsed = !IsInspectorCollapsed);
        ToggleBulkEditCommand = new RelayCommand(() => IsBulkEditExpanded = !IsBulkEditExpanded);
        ToggleBulkPreviewCommand = new RelayCommand(() => IsBulkPreviewExpanded = !IsBulkPreviewExpanded);
        TogglePendingCommand = new RelayCommand(() => IsPendingExpanded = !IsPendingExpanded);
        ToggleIssuesCommand = new RelayCommand(() => IsIssuesExpanded = !IsIssuesExpanded);
    }

    public bool IsInspectorCollapsed
    {
        get => _isInspectorCollapsed;
        set
        {
            if (SetProperty(ref _isInspectorCollapsed, value))
                OnPropertyChanged(nameof(IsInspectorExpanded));
        }
    }

    public bool IsInspectorExpanded => !_isInspectorCollapsed;

    public bool IsBulkEditExpanded
    {
        get => _isBulkEditExpanded;
        set => SetProperty(ref _isBulkEditExpanded, value);
    }

    public bool IsBulkPreviewExpanded
    {
        get => _isBulkPreviewExpanded;
        set => SetProperty(ref _isBulkPreviewExpanded, value);
    }

    public bool IsPendingExpanded
    {
        get => _isPendingExpanded;
        set => SetProperty(ref _isPendingExpanded, value);
    }

    public bool IsIssuesExpanded
    {
        get => _isIssuesExpanded;
        set => SetProperty(ref _isIssuesExpanded, value);
    }

    public ICommand ToggleInspectorCommand { get; }
    public ICommand ToggleBulkEditCommand { get; }
    public ICommand ToggleBulkPreviewCommand { get; }
    public ICommand TogglePendingCommand { get; }
    public ICommand ToggleIssuesCommand { get; }
}
