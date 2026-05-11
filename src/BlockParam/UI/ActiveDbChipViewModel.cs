using System;
using System.Windows.Input;

namespace BlockParam.UI;

/// <summary>
/// Tag-style chip for one entry in the dialog's active-DB set. Replaces the
/// single big "current DB" button + comma-joined summary with a row of
/// equal-status chips, each carrying its own × close button. The peer-DB
/// model treats every chip the same (no anchor privilege beyond storage
/// order), so visual styling is uniform; the only per-chip difference is
/// that the close × is disabled when the active set is down to one entry,
/// since the dialog must always have at least one DB to operate on.
/// </summary>
public class ActiveDbChipViewModel : ViewModelBase
{
    public ActiveDbChipViewModel(
        string displayName,
        string plcPrefix,
        bool canClose,
        Action onClose,
        Action onSolo,
        int? number = null)
    {
        DisplayName = displayName;
        PlcPrefix = plcPrefix;
        Number = number;
        NumberLabel = number is int n ? $"DB{n}" : "";
        HasNumber = number.HasValue;
        _canClose = canClose;
        CloseCommand = new RelayCommand(_ => onClose(), _ => _canClose);
        // Solo (chip-body click): replace the active set with just this DB,
        // OR — when this is the only chip and there's nothing to solo away —
        // open the picker so the user has a one-click path to "switch DB".
        // The VM resolves which behavior to run; the chip just always fires
        // SoloCommand on click, so CanExecute stays true.
        SoloCommand = new RelayCommand(_ => onSolo());
    }

    public string DisplayName { get; }

    /// <summary>Numeric block ID, or null when the parser/Openness didn't surface one.</summary>
    public int? Number { get; }

    /// <summary>Pre-formatted "DB{n}" string; empty when <see cref="Number"/> is null.</summary>
    public string NumberLabel { get; }

    public bool HasNumber { get; }

    /// <summary>
    /// Owning PLC name shown as a dim prefix on the chip. Empty in
    /// single-PLC projects so the chip stays clean; populated in multi-PLC
    /// projects so users can disambiguate same-named DBs across PLCs.
    /// </summary>
    public string PlcPrefix { get; }

    public bool HasPlcPrefix => !string.IsNullOrEmpty(PlcPrefix);

    private bool _canClose;
    /// <summary>
    /// False only when this chip is the last remaining active DB — the dialog
    /// must always have at least one. Bound to the × button's IsEnabled.
    /// </summary>
    public bool CanClose
    {
        get => _canClose;
        set
        {
            if (SetProperty(ref _canClose, value))
                (CloseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public ICommand CloseCommand { get; }
    public ICommand SoloCommand { get; }
}
