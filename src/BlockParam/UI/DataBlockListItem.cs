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

    public DataBlockListItem(DataBlockSummary summary, bool isActive, bool isFocused)
    {
        Summary = summary;
        _isActive = isActive;
        IsFocused = isFocused;
    }

    public DataBlockSummary Summary { get; }

    public string Name => Summary.Name;
    public string FolderPath => Summary.FolderPath;
    public bool IsInstanceDb => Summary.IsInstanceDb;
    public string PlcName => Summary.PlcName;

    /// <summary>
    /// True when this DB is part of the dialog's current active set (focused
    /// or companion). Two-way bound to the row's checkbox.
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
    /// True when this is the focused DB (index 0 of the active set). Used by
    /// the row template to render a small "★" / different chrome. Stays in
    /// sync with the VM via <see cref="SyncFrom"/>.
    /// </summary>
    public bool IsFocused
    {
        get => _isFocused;
        set { if (SetProperty(ref _isFocused, value)) /* no side-effect */ ; }
    }
    private bool _isFocused;

    /// <summary>
    /// Raised when the user toggles the row's checkbox. The VM resolves the
    /// new state (add to companions / remove with stash prompt / promote to
    /// focused DB if it was the last unchecked).
    /// </summary>
    public event System.Action<DataBlockListItem>? ToggleRequested;

    /// <summary>
    /// Mirrors authoritative state from the VM into this row without
    /// re-firing <see cref="ToggleRequested"/>. Called when the active set
    /// changes through any path other than this row's own checkbox.
    /// </summary>
    public void SyncFrom(bool isActive, bool isFocused)
    {
        _suppressToggle = true;
        try
        {
            IsActive = isActive;
            IsFocused = isFocused;
        }
        finally
        {
            _suppressToggle = false;
        }
    }
}
