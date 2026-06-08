using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using BlockParam.Diagnostics;
using BlockParam.Models;
using BlockParam.Services;

namespace BlockParam.UI;

/// <summary>
/// Owns the pill ↔ active-set selection cascade (#169). Sits between
/// <see cref="PlcPillViewModel.SelectionChanged"/> and the active-set
/// mutators on the host. The re-entrancy guard (<c>_syncing</c>) lives
/// here instead of on <see cref="ActiveSetViewModel"/>.
/// </summary>
public sealed class PillSelectionCoordinator : ViewModelBase
{
    private readonly HashSet<string> _extraPillPlcs = new(StringComparer.Ordinal);
    private bool _syncing;
    private bool _isAddDbPopupOpen;

    // Host callbacks — keep the coordinator from reaching into the host's internals.
    private readonly Func<ActiveSetState> _getState;
    private readonly Func<DataBlockSummary, (bool isActive, bool isAnchor)> _getActiveStatusFor;
    private readonly Action<DataBlockSummary> _addActiveDbFromSummary;
    private readonly Func<DataBlockSummary, ActiveDb?> _findActiveDb;
    private readonly Action<ActiveDb> _removeActiveDb;
    private readonly Func<bool> _hasDataBlockSwitcher;
    private readonly Action<bool> _loadAvailableDataBlocks;
    private readonly Func<IReadOnlyList<DataBlockSummary>?> _getAvailableDataBlocks;
    private readonly bool _hasEnumerateDataBlocks;
    private readonly Action<DataBlockListItem>? _onDataBlockListItemToggled;

    public PillSelectionCoordinator(
        Func<ActiveSetState> getState,
        Func<DataBlockSummary, (bool isActive, bool isAnchor)> getActiveStatusFor,
        Action<DataBlockSummary> addActiveDbFromSummary,
        Func<DataBlockSummary, ActiveDb?> findActiveDb,
        Action<ActiveDb> removeActiveDb,
        Func<bool> hasDataBlockSwitcher,
        Action<bool> loadAvailableDataBlocks,
        Func<IReadOnlyList<DataBlockSummary>?> getAvailableDataBlocks,
        bool hasEnumerateDataBlocks,
        Action<DataBlockListItem>? onDataBlockListItemToggled)
    {
        _getState = getState ?? throw new ArgumentNullException(nameof(getState));
        _getActiveStatusFor = getActiveStatusFor ?? throw new ArgumentNullException(nameof(getActiveStatusFor));
        _addActiveDbFromSummary = addActiveDbFromSummary ?? throw new ArgumentNullException(nameof(addActiveDbFromSummary));
        _findActiveDb = findActiveDb ?? throw new ArgumentNullException(nameof(findActiveDb));
        _removeActiveDb = removeActiveDb ?? throw new ArgumentNullException(nameof(removeActiveDb));
        _hasDataBlockSwitcher = hasDataBlockSwitcher ?? throw new ArgumentNullException(nameof(hasDataBlockSwitcher));
        _loadAvailableDataBlocks = loadAvailableDataBlocks ?? throw new ArgumentNullException(nameof(loadAvailableDataBlocks));
        _getAvailableDataBlocks = getAvailableDataBlocks ?? throw new ArgumentNullException(nameof(getAvailableDataBlocks));
        _hasEnumerateDataBlocks = hasEnumerateDataBlocks;
        _onDataBlockListItemToggled = onDataBlockListItemToggled;

        PlcPills = new ObservableCollection<PlcPillViewModel>();
    }

    /// <summary>
    /// One pill per PLC that has at least one active DB. Rebuilt by
    /// <see cref="RebuildPlcPills"/> whenever the active set changes.
    /// </summary>
    public ObservableCollection<PlcPillViewModel> PlcPills { get; }

    /// <summary>
    /// PLC names present in the project but not yet represented in the
    /// pill row. Drives the "+ PLC" popup's item list.
    /// </summary>
    public IReadOnlyList<string> InactiveProjectPlcs
    {
        get
        {
            if (!_hasDataBlockSwitcher()) return Array.Empty<string>();
            _loadAvailableDataBlocks(false);
            var projectDbs = _getAvailableDataBlocks();
            if (projectDbs == null || projectDbs.Count == 0)
                return Array.Empty<string>();

            var rowPlcs = new HashSet<string>(
                PlcPills.Select(p => p.PlcName ?? ""), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var db in projectDbs)
            {
                var plc = db.PlcName ?? "";
                if (rowPlcs.Contains(plc)) continue;
                if (!seen.Add(plc)) continue;
                result.Add(plc);
            }
            return result;
        }
    }

    /// <summary>
    /// True when at least one project PLC is not yet in the row. Bound
    /// to the "+ PLC" button's Visibility.
    /// </summary>
    public bool CanAddPlc => InactiveProjectPlcs.Count > 0;

    public bool IsAddDbPopupOpen
    {
        get => _isAddDbPopupOpen;
        set => SetProperty(ref _isAddDbPopupOpen, value);
    }

    /// <summary>
    /// Adds <paramref name="plcName"/> to the row as an empty pill.
    /// No-op if the PLC isn't a current candidate.
    /// </summary>
    public void AddPlcToRow(string plcName)
    {
        if (string.IsNullOrEmpty(plcName)) return;
        if (!InactiveProjectPlcs.Contains(plcName, StringComparer.Ordinal)) return;
        _extraPillPlcs.Add(plcName);
        RebuildPlcPills();
        OnPropertyChanged(nameof(InactiveProjectPlcs));
    }

    /// <summary>
    /// Rebuilds <see cref="PlcPills"/> from the current snapshot. Called
    /// on every active-set change by the host's cascade subscriber.
    /// </summary>
    public void RebuildPlcPills()
    {
        foreach (var pill in PlcPills)
            pill.SelectionChanged -= OnPillSelectionChanged;
        PlcPills.Clear();

        var availableDataBlocks = _getAvailableDataBlocks();
        if (_extraPillPlcs.Count > 0 && availableDataBlocks != null)
        {
            var projectPlcs = new HashSet<string>(
                availableDataBlocks.Select(d => d.PlcName ?? ""),
                StringComparer.Ordinal);
            _extraPillPlcs.RemoveWhere(p => !projectPlcs.Contains(p));
        }

        var state = _getState();
        var newPills = PlcPillGroupsService.Build(
            state.Dbs,
            state.AnchorPlcName,
            loadDbsForPlc: LoadDbsForPlcAsync,
            extraPlcs: _extraPillPlcs);

        foreach (var pill in newPills)
        {
            pill.SelectionChanged += OnPillSelectionChanged;
            PlcPills.Add(pill);
        }

        OnPropertyChanged(nameof(InactiveProjectPlcs));
        OnPropertyChanged(nameof(CanAddPlc));
    }

    /// <summary>
    /// Lazy-loads the available DB list for a specific PLC. Called by each
    /// <see cref="PlcPillViewModel"/> on first popup open.
    /// </summary>
    internal Task<IReadOnlyList<DataBlockListItem>> LoadDbsForPlcAsync(string plcName)
    {
        if (!_hasEnumerateDataBlocks)
            return Task.FromResult<IReadOnlyList<DataBlockListItem>>(Array.Empty<DataBlockListItem>());

        _loadAvailableDataBlocks(false);

        var source = _getAvailableDataBlocks() ?? Array.Empty<DataBlockSummary>();
        var filtered = string.IsNullOrEmpty(plcName)
            ? source
            : source.Where(s => string.Equals(s.PlcName, plcName, StringComparison.Ordinal)).ToList();

        var items = filtered
            .Select(s =>
            {
                var (isActive, isAnchor) = _getActiveStatusFor(s);
                var item = new DataBlockListItem(s, isActive, isAnchor);
                if (_onDataBlockListItemToggled != null)
                    item.ToggleRequested += _onDataBlockListItemToggled;
                return item;
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<DataBlockListItem>>(items);
    }

    private void OnPillSelectionChanged(object? sender, PillSelectionChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            var state = _getState();
            foreach (var summary in e.Added)
            {
                if (!_getActiveStatusFor(summary).isActive)
                    _addActiveDbFromSummary(summary);
            }
            foreach (var summary in e.Removed)
            {
                if (state.Dbs.Count <= 1)
                {
                    Log.Information(
                        "Refusing pill remove on {Name} — at least one DB must stay active",
                        summary.Name);
                    if (sender is PlcPillViewModel pill)
                    {
                        var activeItems = GetActiveItemsForPlc(pill);
                        pill.SyncSelectedDbs(activeItems);
                    }
                    return;
                }
                var match = _findActiveDb(summary);
                if (match != null) _removeActiveDb(match);
                // Re-read state after mutation — the host may have changed
                // the snapshot via SetState inside RemoveActiveDb.
                state = _getState();
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private IReadOnlyList<DataBlockListItem> GetActiveItemsForPlc(PlcPillViewModel pill)
    {
        var state = _getState();
        var result = new List<DataBlockListItem>();
        for (int i = 0; i < state.Dbs.Count; i++)
        {
            var db = state.Dbs[i];
            var plc = db.PlcName ?? "";
            if (!string.Equals(plc, pill.PlcName, StringComparison.Ordinal)) continue;
            result.Add(new DataBlockListItem(
                new DataBlockSummary(db.Info.Name, "", plcName: plc, number: db.Info.Number),
                isActive: true,
                isAnchor: i == 0));
        }
        return result;
    }
}
