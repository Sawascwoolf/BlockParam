using System.Collections.ObjectModel;
using System.Windows.Input;
using BlockParam.Localization;
using BlockParam.Models;

namespace BlockParam.UI;

/// <summary>
/// ViewModel for the Diff Preview dialog.
/// Shows all members that would be affected, with old and new values.
/// </summary>
public class DiffPreviewViewModel : ViewModelBase
{
    public DiffPreviewViewModel(
        string dbName,
        string memberName,
        string newValue,
        IReadOnlyList<DiffEntry> entries)
    {
        Title = $"Preview: Set {memberName} to {newValue}";
        Entries = new ObservableCollection<DiffEntry>(entries);
        ChangedCount = entries.Count(e => e.IsChanged);
        TotalCount = entries.Count;
        Summary = $"{ChangedCount} of {TotalCount} values will change";
    }

    public string Title { get; }
    public ObservableCollection<DiffEntry> Entries { get; }
    public int ChangedCount { get; }
    public int TotalCount { get; }
    public string Summary { get; }

    /// <summary>Set to true when user clicks "Apply Changes".</summary>
    public bool Confirmed { get; private set; }

    public event Action? RequestClose;

    private RelayCommand? _applyCommand;
    public ICommand ApplyCommand => _applyCommand ??= new RelayCommand(() =>
    {
        Confirmed = true;
        RequestClose?.Invoke();
    }, () => ChangedCount > 0);

    private RelayCommand? _cancelCommand;
    public ICommand CancelCommand => _cancelCommand ??= new RelayCommand(() =>
    {
        Confirmed = false;
        RequestClose?.Invoke();
    });
}
