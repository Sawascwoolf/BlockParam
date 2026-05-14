using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace BlockParam.UI;

/// <summary>
/// State container for the dialog's active-DB set (#80 slice 8a).
/// Owns the <see cref="ActiveSetState"/> snapshot and the bound
/// <see cref="StashedDbs"/> collection. Mutators (Add / Solo / Remove /
/// Reactivate) still live on the host in 8a — they call
/// <see cref="SetState"/> to swap snapshots. Slice 8b will move the
/// mutators + DB-switcher + PlcPills in.
///
/// <para>
/// <see cref="SetState"/> raises <see cref="StateChanged"/> with the
/// (old, new) pair so the host's cross-slice cascade (tree rebuild,
/// selection clear, filter pass, pill rebuild, anchor-display refresh)
/// runs exactly once per snapshot change. <see cref="StashedDbs"/> is
/// re-mirrored from the new snapshot internally — the host doesn't
/// need a second copy.
/// </para>
/// </summary>
public sealed class ActiveSetViewModel : ViewModelBase
{
    private ActiveSetState _state;

    public ActiveSetViewModel(ActiveSetState initial)
    {
        _state = initial ?? throw new ArgumentNullException(nameof(initial));
        StashedDbs = new ObservableCollection<StashedDbState>();
        SyncStashedDbsCollection();
    }

    /// <summary>
    /// Current snapshot. Read-only from outside; install a new one via
    /// <see cref="SetState"/>.
    /// </summary>
    public ActiveSetState State => _state;

    /// <summary>
    /// Stash entries displayed in the inspector. Mirrors
    /// <c>State.Stashes</c>, sorted by (FolderPath, DbName) for a
    /// stable display order across snapshot swaps.
    /// </summary>
    public ObservableCollection<StashedDbState> StashedDbs { get; }

    public bool HasStashedDbs => StashedDbs.Count > 0;

    /// <summary>
    /// Raised after a new snapshot is installed. (old, new) lets
    /// subscribers diff with reference equality on <c>Dbs</c> /
    /// <c>Stashes</c> to decide which cascade slices to run.
    /// </summary>
    public event Action<ActiveSetState, ActiveSetState>? StateChanged;

    /// <summary>
    /// Install a new snapshot. No-op when reference-equal to the current
    /// one. Raises <see cref="StateChanged"/> only on actual change.
    /// </summary>
    public void SetState(ActiveSetState next)
    {
        if (next == null) throw new ArgumentNullException(nameof(next));
        if (ReferenceEquals(_state, next)) return;

        var old = _state;
        _state = next;

        if (!ReferenceEquals(old.Stashes, next.Stashes))
            SyncStashedDbsCollection();

        StateChanged?.Invoke(old, next);
    }

    private void SyncStashedDbsCollection()
    {
        StashedDbs.Clear();
        foreach (var s in _state.Stashes.Values
            .OrderBy(s => s.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.DbName, StringComparer.OrdinalIgnoreCase))
        {
            StashedDbs.Add(s);
        }
        OnPropertyChanged(nameof(HasStashedDbs));
    }
}
