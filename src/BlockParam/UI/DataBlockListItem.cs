using BlockParam.Models;

namespace BlockParam.UI;

/// <summary>
/// Dropdown row for the multi-select DB picker (#58). Wraps a
/// <see cref="DataBlockSummary"/> with a transient <see cref="IsActive"/>
/// flag so each row can render a checkbox bound to whether that DB is
/// currently part of the dialog's active set. Toggling the checkbox raises
/// <see cref="ToggleRequested"/>; the VM handles the actual add/remove
/// (with the stash-on-remove prompt when pending edits exist).
///
/// Wrapper-by-row instead of a property on <see cref="DataBlockSummary"/>
/// because the summary is an immutable model shared across the cache —
/// transient UI selection state belongs on the VM-side wrapper.
/// </summary>
public class DataBlockListItem : ViewModelBase
{
    private bool _isActive;
    // Suppresses the side-effect when the VM rewrites IsActive in bulk
    // (e.g. recomputing checked state after the active set changes). Without
    // this guard the recomputation re-fires ToggleRequested in a loop.
    private bool _suppressToggle;

    public DataBlockListItem(DataBlockSummary summary, bool isActive, bool isAnchor)
    {
        Summary = summary;
        _isActive = isActive;
        IsAnchor = isAnchor;
    }

    public DataBlockSummary Summary { get; }

    public string Name => Summary.Name;
    public string FolderPath => Summary.FolderPath;
    public bool IsInstanceDb => Summary.IsInstanceDb;
    public string PlcName => Summary.PlcName;
    public int? Number => Summary.Number;
    public string NumberLabel => Summary.Number is int n ? $"DB{n}" : "";
    public bool HasNumber => Summary.Number.HasValue;

    /// <summary>
    /// True when this DB is part of the dialog's current active set.
    /// Two-way bound to the row's checkbox.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged();
            if (!_suppressToggle)
                ToggleRequested?.Invoke(this);
        }
    }

    /// <summary>
    /// True when this row corresponds to the active set's anchor (index 0,
    /// the DB the dialog uses for default title / scope display). Carries
    /// no privilege over removability — peer DBs can still be unchecked
    /// individually; the next remaining DB just becomes the new anchor.
    /// </summary>
    public bool IsAnchor
    {
        get => _isAnchor;
        set => SetProperty(ref _isAnchor, value);
    }
    private bool _isAnchor;

    /// <summary>
    /// Raised when the user toggles the row's checkbox. The VM resolves the
    /// new state (add to active set / remove with stash prompt; the next
    /// remaining DB becomes the new anchor when the current anchor is
    /// removed).
    /// </summary>
    public event System.Action<DataBlockListItem>? ToggleRequested;

    /// <summary>
    /// Mirrors authoritative state from the VM into this row without
    /// re-firing <see cref="ToggleRequested"/>. Called when the active set
    /// changes through any path other than this row's own checkbox.
    /// </summary>
    public void SyncFrom(bool isActive, bool isAnchor)
    {
        _suppressToggle = true;
        try
        {
            IsActive = isActive;
            IsAnchor = isAnchor;
        }
        finally
        {
            _suppressToggle = false;
        }
    }
}
