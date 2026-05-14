using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using BlockParam.Diagnostics;
using BlockParam.Localization;
using BlockParam.Models;
using BlockParam.Services;

namespace BlockParam.UI;

/// <summary>
/// State container + mutators for the dialog's active-DB set
/// (#80 slices 8a + 8b). Owns the <see cref="ActiveSetState"/> snapshot,
/// the bound <see cref="StashedDbs"/> collection, the DB-switcher
/// dropdown state, and the PLC-pill row.
///
/// <para>
/// <see cref="SetState"/> raises <see cref="StateChanged"/> with the
/// (old, new) pair so the host's cross-slice cascade (tree rebuild,
/// selection clear, filter pass, pill rebuild, anchor-display refresh)
/// runs exactly once per snapshot change. <see cref="StashedDbs"/> is
/// re-mirrored from the new snapshot internally — the host doesn't
/// need a second copy.
/// </para>
///
/// <para>
/// Mutators (Add / Solo / Remove / Reactivate) compose the next
/// snapshot in locals and assign once via <see cref="SetState"/>:
/// exactly one cascade per user gesture regardless of how many DBs
/// were swapped in or out. Operations with host-side side effects
/// (Apply-in-place on remove, replay stashed edits onto the live tree)
/// run through caller-supplied callbacks (<see cref="_tryApplyActiveDbInPlace"/>,
/// <see cref="_restoreStashOntoLive"/>) so the slice never reaches
/// into the host's <c>_writer</c> / <c>Tree</c> / <c>Subscription</c>.
/// </para>
/// </summary>
public sealed class ActiveSetViewModel : ViewModelBase
{
    private ActiveSetState _state;
    private string _title = "";

    // --- Optional dependencies (8b). All-null defaults keep the legacy
    // ActiveSetViewModel(initial) ctor (used by slice-8a tests) working
    // with reduced functionality: mutators that need a missing dep throw
    // a clear InvalidOperationException, never NRE silently.
    private readonly IMessageBoxService? _messageBox;
    private readonly PendingEditStore? _pendingEditStore;
    private readonly Func<IReadOnlyDictionary<MemberNode, ActiveDb>>? _getModelToDb;
    private readonly Func<MemberNode, string?>? _getStartValueForNode;
    private readonly Func<DataBlockSummary, ActiveDb?>? _buildActiveDbForSummary;
    private readonly Func<IReadOnlyList<DataBlockSummary>>? _enumerateDataBlocks;
    private readonly Func<DataBlockSummary, string>? _switchToDataBlock;
    private readonly Func<ActiveDb, bool>? _tryApplyActiveDbInPlace;
    private readonly Func<StashedDbState, ActiveDb?, (int restored, int dropped)>? _restoreStashOntoLive;
    private readonly Action<string>? _setStatus;
    private readonly Func<int>? _getPendingCount;

    // --- DB-switcher dropdown state (#59) ---
    private IReadOnlyList<DataBlockSummary>? _availableDataBlocks;
    private IReadOnlyList<DataBlockSummary> _filteredDataBlocks = Array.Empty<DataBlockSummary>();
    private IReadOnlyList<DataBlockListItem> _filteredDataBlockItems = Array.Empty<DataBlockListItem>();
    private string _dataBlockSearchText = "";
    private bool _isDataBlocksDropdownOpen;
    private bool _isLoadingDataBlocks;

    // --- Pill-row state ---
    private readonly HashSet<string> _extraPillPlcs = new(StringComparer.Ordinal);
    // Re-entrancy guard: when the cascade rewrites SelectedDbs on each pill,
    // the pill fires SelectionChanged → OnPillSelectionChanged. Without the
    // guard we'd enter AddActiveDbToSet / RemoveActiveDb for every item in
    // the selection sync, spiraling into multiple cascades.
    private bool _syncingPillSelection;
    private bool _isAddDbPopupOpen;

    /// <summary>
    /// Legacy state-only constructor preserved for the slice-8a tests. The
    /// slice operates as a pure snapshot container with no mutator wiring;
    /// any call to a mutator method that needs an unwired dep throws
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    public ActiveSetViewModel(ActiveSetState initial)
        : this(initial,
            messageBox: null,
            pendingEditStore: null,
            getModelToDb: null,
            getStartValueForNode: null,
            buildActiveDbForSummary: null,
            enumerateDataBlocks: null,
            switchToDataBlock: null,
            tryApplyActiveDbInPlace: null,
            restoreStashOntoLive: null,
            setStatus: null,
            getPendingCount: null,
            dispatcher: null)
    {
    }

    /// <summary>
    /// Full constructor. Host VM wires every callback; tests pass only
    /// the dependencies their scenario needs and rely on the no-op /
    /// throw defaults for the rest.
    /// </summary>
    public ActiveSetViewModel(
        ActiveSetState initial,
        IMessageBoxService? messageBox,
        PendingEditStore? pendingEditStore,
        Func<IReadOnlyDictionary<MemberNode, ActiveDb>>? getModelToDb,
        Func<MemberNode, string?>? getStartValueForNode,
        Func<DataBlockSummary, ActiveDb?>? buildActiveDbForSummary,
        Func<IReadOnlyList<DataBlockSummary>>? enumerateDataBlocks,
        Func<DataBlockSummary, string>? switchToDataBlock,
        Func<ActiveDb, bool>? tryApplyActiveDbInPlace,
        Func<StashedDbState, ActiveDb?, (int restored, int dropped)>? restoreStashOntoLive,
        Action<string>? setStatus,
        Func<int>? getPendingCount,
        Dispatcher? dispatcher)
    {
        _state = initial ?? throw new ArgumentNullException(nameof(initial));
        StashedDbs = new ObservableCollection<StashedDbState>();
        SyncStashedDbsCollection();
        // Seed the dialog window title from the initial snapshot so the
        // host's XAML binding has a non-empty value before the first
        // active-set change. SetState recomputes on every snapshot swap.
        _title = ComputeTitleFromState();

        _messageBox = messageBox;
        _pendingEditStore = pendingEditStore;
        _getModelToDb = getModelToDb;
        _getStartValueForNode = getStartValueForNode;
        _buildActiveDbForSummary = buildActiveDbForSummary;
        _enumerateDataBlocks = enumerateDataBlocks;
        _switchToDataBlock = switchToDataBlock;
        _tryApplyActiveDbInPlace = tryApplyActiveDbInPlace;
        _restoreStashOntoLive = restoreStashOntoLive;
        _setStatus = setStatus;
        _getPendingCount = getPendingCount;
        _ = dispatcher; // reserved for LoadDbsForPlcAsync continuation if it ever goes off-thread

        PlcPills = new ObservableCollection<PlcPillViewModel>();

        OpenDataBlocksDropdownCommand = new RelayCommand(ExecuteOpenDataBlocksDropdown,
            () => _enumerateDataBlocks != null && _switchToDataBlock != null);
        CloseDataBlocksDropdownCommand = new RelayCommand(() =>
        {
            IsDataBlocksDropdownOpen = false;
        });
        RefreshDataBlocksCommand = new RelayCommand(ExecuteRefreshDataBlocks,
            () => _enumerateDataBlocks != null && !_isLoadingDataBlocks);
        SwitchToStashedDbCommand = new RelayCommand(parameter =>
        {
            // Peer model: clicking a stashed-DB header re-activates that DB
            // by adding it back to the active set, then restoring its stash
            // edits. The previous anchor stays — the user can remove it
            // themselves if desired. Overwriting the anchor in place would
            // destroy the user's other active DBs, which the legacy
            // SwitchToDataBlock path did under the old single-DB model.
            if (parameter is StashedDbState stash) ReactivateStashedDb(stash);
        });
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

    /// <summary>True when more than one DB is active in this session (#58).</summary>
    public bool HasMultipleActiveDbs => _state.Dbs.Count > 1;

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

        bool dbsChanged = !ReferenceEquals(old.Dbs, next.Dbs);
        bool anchorPlcChanged = !string.Equals(
            old.AnchorPlcName, next.AnchorPlcName, StringComparison.Ordinal);

        if (dbsChanged)
            OnPropertyChanged(nameof(HasMultipleActiveDbs));

        // Anchor display: Title + CurrentDataBlockName + CurrentPlcName all
        // derive from State.Dbs[0] / State.AnchorPlcName. Recompute the
        // title once per snapshot install (#91) — every gesture that swaps
        // the active set funnels through SetState, so the chip-only/single-DB
        // title shape stays in sync without each mutator owning its own
        // refresh call.
        if (dbsChanged || anchorPlcChanged)
        {
            Title = ComputeTitleFromState();
            OnPropertyChanged(nameof(CurrentDataBlockName));
        }

        if (anchorPlcChanged)
        {
            OnPropertyChanged(nameof(CurrentPlcName));
            OnPropertyChanged(nameof(HasCurrentPlcName));
        }

        StateChanged?.Invoke(old, next);
    }

    /// <summary>
    /// Dialog window title. Single-DB sessions render
    /// <c>"BlockParam v{version}: {PLC} / {DB}"</c>; multi-DB sessions
    /// drop the DB/PLC suffix entirely (#91). XAML binds via
    /// <c>{Binding ActiveSet.Title}</c>.
    /// </summary>
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>Header label — the DB name part of the title combo.</summary>
    public string CurrentDataBlockName =>
        _state.Dbs.Count > 0 ? _state.Dbs[0].Info.Name : "";

    /// <summary>
    /// Anchor PLC display, derived from <see cref="ActiveSetState.AnchorPlcName"/>.
    /// Multi-PLC sessions render the chip prefix from this; single-PLC
    /// hosts (DevLauncher) leave it empty and the prefix is omitted.
    /// </summary>
    public string CurrentPlcName => _state.AnchorPlcName;

    /// <summary>True when <see cref="CurrentPlcName"/> is non-empty.</summary>
    public bool HasCurrentPlcName => !string.IsNullOrEmpty(_state.AnchorPlcName);

    private string ComputeTitleFromState()
    {
        var version = typeof(ActiveSetViewModel).Assembly.GetName().Version;
        var anchorName = _state.Dbs.Count > 0 ? _state.Dbs[0].Info.Name : "";
        return BuildTitle(version, _state.AnchorPlcName, anchorName, _state.Dbs.Count);
    }

    /// <summary>
    /// Builds the dialog window title. Single-DB sessions render
    /// <c>"BlockParam v{version}: {PLC} / {DB}"</c>. Multi-DB sessions
    /// drop the DB/PLC suffix entirely (#91): the chip strip is the
    /// single source of truth for which DBs are in scope, so surfacing
    /// one specific DB's name in the title contradicts the peer-DB
    /// model. Single-PLC hosts (DevLauncher) pass an empty PLC name and
    /// the prefix is dropped.
    /// </summary>
    private static string BuildTitle(System.Version? version, string plcName, string dbName, int activeDbCount)
    {
        if (activeDbCount > 1) return $"BlockParam v{version}";
        var location = string.IsNullOrEmpty(plcName) ? dbName : $"{plcName} / {dbName}";
        return $"BlockParam v{version}: {location}";
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

    // ===== DB-switcher dropdown (#59) =========================================

    public ICommand OpenDataBlocksDropdownCommand { get; }
    public ICommand CloseDataBlocksDropdownCommand { get; }
    public ICommand RefreshDataBlocksCommand { get; }
    public ICommand SwitchToStashedDbCommand { get; }

    /// <summary>
    /// True when the host wired up DB enumeration + switching callbacks.
    /// The dropdown chevron / popup are hidden in tests + DevLauncher runs
    /// where no project is available.
    /// </summary>
    public bool HasDataBlockSwitcher =>
        _enumerateDataBlocks != null && _switchToDataBlock != null;

    public bool IsDataBlocksDropdownOpen
    {
        get => _isDataBlocksDropdownOpen;
        set => SetProperty(ref _isDataBlocksDropdownOpen, value);
    }

    public bool IsLoadingDataBlocks
    {
        get => _isLoadingDataBlocks;
        private set
        {
            if (SetProperty(ref _isLoadingDataBlocks, value))
                OnPropertyChanged(nameof(ShowEmptyDataBlocksMessage));
        }
    }

    /// <summary>Filtered, alphabetised list shown inside the dropdown.</summary>
    public IReadOnlyList<DataBlockSummary> FilteredDataBlocks
    {
        get => _filteredDataBlocks;
        private set
        {
            if (SetProperty(ref _filteredDataBlocks, value))
            {
                OnPropertyChanged(nameof(ShowEmptyDataBlocksMessage));
                RebuildFilteredDataBlockItems();
            }
        }
    }

    /// <summary>
    /// Multi-select dropdown rows (#58). Same membership and order as
    /// <see cref="FilteredDataBlocks"/>; each row wraps the summary with a
    /// transient IsActive flag that two-way binds to its checkbox.
    /// </summary>
    public IReadOnlyList<DataBlockListItem> FilteredDataBlockItems
    {
        get => _filteredDataBlockItems;
        private set => SetProperty(ref _filteredDataBlockItems, value);
    }

    /// <summary>Type-to-filter text for the dropdown's search box.</summary>
    public string DataBlockSearchText
    {
        get => _dataBlockSearchText;
        set
        {
            if (SetProperty(ref _dataBlockSearchText, value))
                ApplyDataBlockFilter();
        }
    }

    /// <summary>True when enumeration is settled but the filter matches nothing.</summary>
    public bool ShowEmptyDataBlocksMessage =>
        !_isLoadingDataBlocks
        && _availableDataBlocks != null
        && _filteredDataBlocks.Count == 0;

    private void ExecuteOpenDataBlocksDropdown()
    {
        if (_availableDataBlocks == null)
            LoadAvailableDataBlocks(force: false);

        ApplyDataBlockFilter();
        IsDataBlocksDropdownOpen = true;
    }

    private void ExecuteRefreshDataBlocks()
    {
        LoadAvailableDataBlocks(force: true);
        ApplyDataBlockFilter();
    }

    private void LoadAvailableDataBlocks(bool force)
    {
        if (_enumerateDataBlocks == null) return;
        if (!force && _availableDataBlocks != null) return;

        IsLoadingDataBlocks = true;
        try
        {
            // Enumeration runs on the UI thread today: TIA Openness calls
            // must originate from the same thread that owns the project.
            // The visible spinner + the dialog's scoped scope keep this OK
            // for typical project sizes; if it ever bites we'd move the
            // enumeration onto a background task with a marshalled call.
            _availableDataBlocks = DataBlockListFilter.Sort(_enumerateDataBlocks());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DB enumeration failed");
            _availableDataBlocks = Array.Empty<DataBlockSummary>();
            _setStatus?.Invoke(Res.Format("Status_DbEnumFailed", ex.Message));
        }
        finally
        {
            IsLoadingDataBlocks = false;
        }
    }

    private void ApplyDataBlockFilter()
    {
        var source = _availableDataBlocks ?? (IReadOnlyList<DataBlockSummary>)Array.Empty<DataBlockSummary>();
        FilteredDataBlocks = DataBlockListFilter.Filter(source, _dataBlockSearchText);
    }

    private void RebuildFilteredDataBlockItems()
    {
        var items = new List<DataBlockListItem>(_filteredDataBlocks.Count);
        foreach (var summary in _filteredDataBlocks)
        {
            var (isActive, isAnchor) = GetActiveStatusFor(summary);
            var item = new DataBlockListItem(summary, isActive, isAnchor);
            item.ToggleRequested += OnDataBlockListItemToggled;
            items.Add(item);
        }
        FilteredDataBlockItems = items;
    }

    /// <summary>
    /// True/false pair for a row: (isActive: in the dialog's active set,
    /// isAnchor: index 0 of the active set, used as the UI's display
    /// anchor). isAnchor carries no privilege over removability — it just
    /// tells the row template whether to render the anchor decoration.
    /// </summary>
    private (bool isActive, bool isAnchor) GetActiveStatusFor(DataBlockSummary summary)
    {
        // Match on (Name, PlcName) — multi-PLC projects can host two DBs
        // with the same name on different PLCs (#58 review must-fix #4).
        // For index 0 the PLC name comes from State.AnchorPlcName (display
        // state); for the rest we read each ActiveDb.PlcName directly.
        for (int i = 0; i < _state.Dbs.Count; i++)
        {
            var db = _state.Dbs[i];
            var plc = i == 0 ? _state.AnchorPlcName : db.PlcName;
            if (string.Equals(db.Info.Name, summary.Name, StringComparison.Ordinal)
                && string.Equals(plc, summary.PlcName, StringComparison.Ordinal))
                return (true, i == 0);
        }
        return (false, false);
    }

    /// <summary>
    /// Pushes the current active-set state back into every existing row so
    /// checkboxes stay accurate after the user toggles one (which mutates
    /// the active set but doesn't rebuild the list).
    /// </summary>
    private void RefreshFilteredDataBlockItemsActiveState()
    {
        foreach (var item in _filteredDataBlockItems)
        {
            var (isActive, isAnchor) = GetActiveStatusFor(item.Summary);
            item.SyncFrom(isActive, isAnchor);
        }
    }

    private void OnDataBlockListItemToggled(DataBlockListItem item)
    {
        // The IsActive setter has already flipped to the new value. Peer
        // model: checked → add to active set; unchecked → remove from active
        // set, with the same Apply / Stash / Cancel prompt regardless of
        // whether the row is the current anchor. The active set must keep
        // at least one DB — refuse the last uncheck. Both branches funnel
        // through State, so the cascade fires exactly once per toggle (#78).
        var (wasActive, _) = GetActiveStatusFor(item.Summary);
        bool wantActive = item.IsActive;
        Log.Information(
            "[gesture] Dropdown row {Name} {Direction} (was={WasActive} now={WantActive}) | {State}",
            item.Name,
            wantActive ? "CHECKED" : "UNCHECKED",
            wasActive, wantActive, SnapshotState());

        if (wantActive && !wasActive)
        {
            AddActiveDbToSet(item);
        }
        else if (!wantActive && wasActive)
        {
            if (_state.Dbs.Count <= 1)
            {
                Log.Information(
                    "Refusing to remove {Name} — at least one DB must stay active",
                    item.Name);
                // Snap the row checkbox back into sync with the unchanged
                // active set — the cascade only fires when State actually
                // changes, so we have to nudge the dropdown rows here.
                RefreshFilteredDataBlockItemsActiveState();
            }
            else
            {
                var match = FindActiveDb(item.Summary);
                if (match != null) RemoveActiveDb(match);
                else RefreshFilteredDataBlockItemsActiveState();
            }
        }
    }

    /// <summary>
    /// Pure-add path: append the dropdown-checked DB to the active set.
    /// PendingEditStore (9814a6e) seeds pending values onto fresh VMs
    /// after the rebuild, so the previously-required Apply/Stash/Cancel
    /// rescue prompt on every other active DB is unnecessary — adding
    /// a peer never destroys an in-progress edit (#93).
    /// </summary>
    private void AddActiveDbToSet(DataBlockListItem item)
    {
        var built = BuildActiveDbFromSummary(item.Summary);
        if (built == null)
        {
            RefreshFilteredDataBlockItemsActiveState();
            return;
        }
        SetState(_state.With(dbs: _state.Dbs.Concat(new[] { built }).ToList()));
        Log.Information("DB enabled via dropdown: {Name}", built.Info.Name);
    }

    // ===== Pill row =========================================================

    /// <summary>
    /// One pill per PLC that has at least one active DB. Replaces the old
    /// chip row + DbSwitcherButton / DbSwitcherPopup in the dialog toolbar.
    /// Rebuilt by <see cref="RebuildPlcPills"/> whenever the active set changes.
    /// </summary>
    public ObservableCollection<PlcPillViewModel> PlcPills { get; }

    /// <summary>
    /// PLC names present in the project but not yet represented in the
    /// pill row. Drives the "+ PLC" popup's item list and the
    /// <see cref="CanAddPlc"/> visibility gate.
    /// </summary>
    public IReadOnlyList<string> InactiveProjectPlcs
    {
        get
        {
            if (!HasDataBlockSwitcher) return Array.Empty<string>();
            LoadAvailableDataBlocks(force: false);
            var projectDbs = _availableDataBlocks;
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
    /// Adds <paramref name="plcName"/> to the row as an empty pill. The
    /// new pill loads its DB list lazily on first popup open, same as the
    /// active-set-derived pills. No-op if the PLC isn't a current candidate
    /// (already in row, or not present in the project's DB list at all) —
    /// this keeps <see cref="_extraPillPlcs"/> from accumulating stale
    /// names if the click stream races a project mutation.
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
        // Unsubscribe old pills before clearing.
        foreach (var pill in PlcPills)
            pill.SelectionChanged -= OnPillSelectionChanged;
        PlcPills.Clear();

        // Prune _extraPillPlcs of names that no longer correspond to any
        // project PLC. Without this, a manually-added pill survives even
        // after its PLC disappears from the project (e.g., after a
        // refresh), and the orphan stays in the row indefinitely.
        if (_extraPillPlcs.Count > 0 && _availableDataBlocks != null)
        {
            var projectPlcs = new HashSet<string>(
                _availableDataBlocks.Select(d => d.PlcName ?? ""),
                StringComparer.Ordinal);
            _extraPillPlcs.RemoveWhere(p => !projectPlcs.Contains(p));
        }

        var newPills = PlcPillGroupsService.Build(
            _state.Dbs,
            _state.AnchorPlcName,
            loadDbsForPlc: LoadDbsForPlcAsync,
            extraPlcs: _extraPillPlcs);

        foreach (var pill in newPills)
        {
            pill.SelectionChanged += OnPillSelectionChanged;
            PlcPills.Add(pill);
        }

        // Pill row just changed — re-evaluate the inactive-PLCs list so
        // the "+ PLC" popup and its button visibility stay in sync.
        OnPropertyChanged(nameof(InactiveProjectPlcs));
        OnPropertyChanged(nameof(CanAddPlc));
    }

    private void OnPillSelectionChanged(object? sender, PillSelectionChangedEventArgs e)
    {
        if (_syncingPillSelection) return;
        _syncingPillSelection = true;
        try
        {
            foreach (var summary in e.Added)
            {
                if (!GetActiveStatusFor(summary).isActive)
                    AddActiveDbFromSummary(summary);
            }
            foreach (var summary in e.Removed)
            {
                if (_state.Dbs.Count <= 1)
                {
                    Log.Information(
                        "Refusing pill remove on {Name} — at least one DB must stay active",
                        summary.Name);
                    // Snap pill selection back so the UI doesn't show a deselected last DB.
                    if (sender is PlcPillViewModel pill)
                    {
                        var activeItems = GetActiveItemsForPlc(pill);
                        pill.SyncSelectedDbs(activeItems);
                    }
                    return;
                }
                var match = FindActiveDb(summary);
                if (match != null) RemoveActiveDb(match);
            }
        }
        finally
        {
            _syncingPillSelection = false;
        }
    }

    private IReadOnlyList<DataBlockListItem> GetActiveItemsForPlc(PlcPillViewModel pill)
    {
        var result = new List<DataBlockListItem>();
        for (int i = 0; i < _state.Dbs.Count; i++)
        {
            var db = _state.Dbs[i];
            var plc = (i == 0 ? _state.AnchorPlcName : db.PlcName) ?? "";
            if (!string.Equals(plc, pill.PlcName, StringComparison.Ordinal)) continue;
            result.Add(new DataBlockListItem(
                new DataBlockSummary(db.Info.Name, "", plcName: plc, number: db.Info.Number),
                isActive: true,
                isAnchor: i == 0));
        }
        return result;
    }

    /// <summary>
    /// Lazy-loads the available DB list for a specific PLC. Called by each
    /// <see cref="PlcPillViewModel"/> on first popup open.
    ///
    /// Reuses the same <see cref="_availableDataBlocks"/> cache that
    /// <see cref="RefreshDataBlocksCommand"/> populates, then filters to the
    /// requested PLC so each pill only shows its own DBs.
    ///
    /// Returns on the calling (UI) thread since <see cref="LoadAvailableDataBlocks"/>
    /// already runs synchronously on the UI thread.
    /// </summary>
    internal Task<IReadOnlyList<DataBlockListItem>> LoadDbsForPlcAsync(string plcName)
    {
        if (_enumerateDataBlocks == null)
            return Task.FromResult<IReadOnlyList<DataBlockListItem>>(Array.Empty<DataBlockListItem>());

        LoadAvailableDataBlocks(force: false);

        var source = _availableDataBlocks ?? Array.Empty<DataBlockSummary>();
        var filtered = string.IsNullOrEmpty(plcName)
            ? source
            : source.Where(s => string.Equals(s.PlcName, plcName, StringComparison.Ordinal)).ToList();

        var items = filtered
            .Select(s =>
            {
                var (isActive, isAnchor) = GetActiveStatusFor(s);
                var item = new DataBlockListItem(s, isActive, isAnchor);
                item.ToggleRequested += OnDataBlockListItemToggled;
                return item;
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<DataBlockListItem>>(items);
    }

    // ===== Mutators ==========================================================

    /// <summary>
    /// Refuses the last-DB removal (matches the pill last-uncheck rule)
    /// and routes everything else through the existing
    /// <see cref="RemoveActiveDb"/> path so the stash / Apply prompt fires
    /// for DBs with pending edits. Internal so tests can drive gestures
    /// without going through chip / pill UI objects.
    /// </summary>
    public void RequestRemoveActiveDb(ActiveDb db)
    {
        Log.Information(
            "[gesture] PillRemove on {Name} | {State}", db.Info.Name, SnapshotState());
        if (_state.Dbs.Count <= 1)
        {
            Log.Information(
                "Refusing pill removal on {Name} — at least one DB must stay active",
                db.Info.Name);
            return;
        }
        RemoveActiveDb(db);
    }

    /// <summary>
    /// One-line dump of the gesture-relevant VM state for log breadcrumbs.
    /// Reads the bound collections so the log matches what the user sees.
    /// </summary>
    private string SnapshotState() =>
        $"active=[{string.Join(",", _state.Dbs.Select(d => d.Info.Name))}] " +
        $"pending={_getPendingCount?.Invoke() ?? 0} stashed={StashedDbs.Count} " +
        $"treeShape={(_state.Dbs.Count == 1 ? "single" : "multi")}";

    /// <summary>
    /// Solo gesture (#58 peer-mode): replace the active set with just the
    /// target DB. Each dropped DB runs through the same Apply / Stash /
    /// Cancel prompt RemoveActiveDb uses.
    ///
    /// Cancel semantics:
    ///   • Target was already in the active set → partial-set behaviour
    ///     (current matches user choices: silently-dropped DBs stay dropped,
    ///     cancelled-on DBs stay, target stays).
    ///   • Target was newly built (soloed from the dropdown without first
    ///     checking it) → any cancel reverts the entire gesture so the
    ///     freshly-built target is NOT silently added alongside the DB the
    ///     user explicitly refused to drop. Without this, Solo+Cancel from a
    ///     single-DB session quietly mutates the active set into multi-DB
    ///     shape, triggering a tree rebuild that orphans pending edits on
    ///     the original DB.
    ///
    /// Compound op (#78): builds the next snapshot in a local and commits
    /// once at the end → exactly one cascade per gesture.
    /// </summary>
    public void SoloActiveDb(DataBlockSummary target)
    {
        var original = _state;
        var current = original;
        var targetDb = FindActiveDb(target);
        bool targetWasNewlyBuilt = false;
        if (targetDb == null)
        {
            // The user soloed a row that wasn't already checked — append it
            // to the snapshot before pruning so the "leave only this one"
            // invariant always holds.
            var built = BuildActiveDbFromSummary(target);
            if (built == null)
            {
                IsDataBlocksDropdownOpen = false;
                return;
            }
            current = current.With(dbs: current.Dbs.Concat(new[] { built }).ToList());
            targetDb = built;
            targetWasNewlyBuilt = true;
        }

        var next = ComposeRemoveOthers(current, targetDb);

        // If we just appended a fresh target and the prune loop bailed
        // (next still carries DBs other than target), drop the whole
        // composition: the user's Cancel applies to the entire gesture.
        if (targetWasNewlyBuilt && next.Dbs.Count > 1)
        {
            Log.Information(
                "Solo+Cancel reverted: target {Name} was newly built and a " +
                "prune cancelled — restoring original active set",
                target.Name);
            IsDataBlocksDropdownOpen = false;
            return;
        }

        SetState(next);
        IsDataBlocksDropdownOpen = false;
    }

    /// <summary>
    /// Reference-based variant of <see cref="SoloActiveDb"/>. Originally
    /// called from the chip body click; surviving as a public seam used by
    /// tests and (in future) any "solo this DB" pill affordance. Same
    /// prompts on every dropped DB with pending edits; if the user cancels
    /// mid-solo, the remaining drops are skipped and the active set is
    /// whatever survived.
    /// </summary>
    public void SoloActiveDbByReference(ActiveDb target)
    {
        Log.Information(
            "[gesture] SoloByReference → {Name} | {State}",
            target.Info.Name, SnapshotState());
        if (_state.Dbs.Count <= 1)
        {
            // Already the only active DB — there's nothing to solo away.
            return;
        }

        SetState(ComposeRemoveOthers(_state, target));
    }

    /// <summary>
    /// Composes the snapshot that results from removing every DB in
    /// <paramref name="current"/> except <paramref name="target"/>. Walks
    /// the others in storage order, calling <see cref="TryComputeRemove"/>
    /// for each (which prompts on pending edits). If the user cancels mid-
    /// loop, returns the partial composition so the active set ends up as
    /// whatever survived. Caller assigns the result to <see cref="State"/>.
    /// </summary>
    private ActiveSetState ComposeRemoveOthers(ActiveSetState current, ActiveDb target)
    {
        var next = current;
        foreach (var db in current.Dbs.Where(d => !ReferenceEquals(d, target)).ToList())
        {
            var step = TryComputeRemove(next, db);
            if (step == null)
            {
                Log.Information(
                    "Solo cancelled at {Name} — leaving partial set", db.Info.Name);
                break;
            }
            next = step;
        }
        return next;
    }

    /// <summary>
    /// Inspector path: re-add a previously-stashed DB back into the active
    /// set, drop the others (with the same Apply / Stash / Cancel prompt
    /// every remove uses), pop the stash entry, and replay its edits onto
    /// the live tree. One snapshot is composed across all of these and
    /// committed once → exactly one cascade. The pending-value replay
    /// runs after the State assignment so it lands on the freshly-built
    /// tree, not on VMs the cascade is about to discard.
    /// </summary>
    public void ReactivateStashedDb(StashedDbState stash)
    {
        Log.Information(
            "[gesture] Stash header click → reactivate {Name} | {State}",
            stash.DbName, SnapshotState());
        var summary = stash.Summary;
        var current = _state;
        var target = FindActiveDb(summary);

        // #92 — When the user has ≥2 active DBs and clicks a stash header,
        // ask whether they want to *add* the stashed DB to the current
        // session (additive) or *replace* the active set with it (the
        // legacy single-DB behaviour). Single-DB sessions skip the prompt:
        // there's nothing to "replace away" so the gesture is unambiguous
        // and goes through the additive branch implicitly via the existing
        // append-then-prune flow below (which becomes a no-op prune when
        // current has only one DB == target).
        if (current.Dbs.Count >= 2)
        {
            if (_messageBox == null)
                throw new InvalidOperationException(
                    "ReactivateStashedDb requires a messageBox dependency");
            var decision = _messageBox.AskAddOrReplace(
                Res.Format("Reactivate_AdditiveOrReplace_Text", summary.Name),
                Res.Get("Reactivate_AdditiveOrReplace_Title"));
            if (decision == AddOrReplaceResult.Cancel)
            {
                Log.Information("Reactivate cancelled by user for {Name}", summary.Name);
                return;
            }
            if (decision == AddOrReplaceResult.Add)
            {
                ReactivateStashedDbAdditive(stash);
                return;
            }
            // Replace falls through to the Replace path below.
        }

        if (target == null)
        {
            var built = BuildActiveDbFromSummary(summary);
            if (built == null) return;
            current = current.With(dbs: current.Dbs.Concat(new[] { built }).ToList());
            target = built;
        }

        var next = ComposeRemoveOthers(current, target);

        // Pop the stash entry inside the same snapshot so the cascade
        // sees stashes-changed once. Other DBs that got stashed during
        // ComposeRemoveOthers (peer with pending edits, user picked Stash)
        // stay in next.Stashes — only the reactivated DB's entry is popped.
        var stashKey = StashKey(summary);
        if (next.Stashes.ContainsKey(stashKey))
        {
            var stashesNew = next.Stashes.ToDictionary(kv => kv.Key, kv => kv.Value);
            stashesNew.Remove(stashKey);
            next = next.With(stashes: stashesNew);
        }

        SetState(next);

        // Replay the stash edits onto the now-final live tree. Runs after
        // State assignment so the cascade has already rebuilt RootMembers
        // — otherwise the new PendingValue assignments would land on VMs
        // the rebuild is about to discard.
        if (_restoreStashOntoLive != null)
        {
            var (restored, dropped) = _restoreStashOntoLive(stash, null);
            if (restored > 0 || dropped > 0)
            {
                _setStatus?.Invoke(dropped == 0
                    ? Res.Format("Status_DbSwitched_StashRestored", summary.Name, restored)
                    : Res.Format("Status_DbSwitched_StashPartial",
                        summary.Name, restored, dropped));
            }
        }
    }

    /// <summary>
    /// Additive branch of <see cref="ReactivateStashedDb"/>: append the
    /// stashed DB to the active set without dropping any other active DBs,
    /// pop its stash entry, replay its edits. One snapshot, one cascade.
    /// </summary>
    private void ReactivateStashedDbAdditive(StashedDbState stash)
    {
        var summary = stash.Summary;
        var built = BuildActiveDbFromSummary(summary);
        if (built == null) return;

        var stashesNew = _state.Stashes.ToDictionary(kv => kv.Key, kv => kv.Value);
        stashesNew.Remove(StashKey(summary));
        SetState(_state.With(
            dbs: _state.Dbs.Concat(new[] { built }).ToList(),
            stashes: stashesNew));

        // Multi-DB shape after additive add — scope the path lookup to the
        // just-added DB's subtree so peer DBs with colliding member paths
        // don't accidentally absorb the stashed edits (#82 path-identity).
        if (_restoreStashOntoLive != null)
        {
            var (restored, dropped) = _restoreStashOntoLive(stash, built);
            if (restored > 0 || dropped > 0)
            {
                _setStatus?.Invoke(dropped == 0
                    ? Res.Format("Status_DbSwitched_StashRestored", summary.Name, restored)
                    : Res.Format("Status_DbSwitched_StashPartial",
                        summary.Name, restored, dropped));
            }
        }
    }

    /// <summary>
    /// Pure builder (#78): produces a fully-wired <see cref="ActiveDb"/>
    /// for a dropdown-picked DB without touching <see cref="State"/>.
    /// Compound mutations (Solo, Reactivate) call this directly and
    /// fold the result into their composed snapshot; the simple
    /// "checkbox checked" path goes through
    /// <see cref="AddActiveDbFromSummary"/>, which builds + assigns.
    /// </summary>
    private ActiveDb? BuildActiveDbFromSummary(DataBlockSummary summary)
    {
        try
        {
            if (_buildActiveDbForSummary == null)
            {
                Log.Information(
                    "DB enable ignored: buildActiveDbForSummary callback not wired");
                return null;
            }
            var built = _buildActiveDbForSummary(summary);
            if (built == null)
                Log.Information("ActiveDb build returned null for {Name}", summary.Name);
            return built;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to build ActiveDb {Name}", summary.Name);
            _setStatus?.Invoke(Res.Format("Status_DbSwitchFailed", summary.Name, ex.Message));
            return null;
        }
    }

    /// <summary>
    /// Single-step add (dropdown checkbox-on). Builds the ActiveDb and
    /// commits to <see cref="State"/> in one shot — the cascade fires
    /// once on the resulting snapshot. Compound paths build directly via
    /// <see cref="BuildActiveDbFromSummary"/> and skip this wrapper.
    /// </summary>
    public void AddActiveDbFromSummary(DataBlockSummary summary)
    {
        var built = BuildActiveDbFromSummary(summary);
        if (built == null) return;
        SetState(_state.With(dbs: _state.Dbs.Concat(new[] { built }).ToList()));
        Log.Information("DB enabled via dropdown: {Name}", built.Info.Name);
    }

    /// <summary>
    /// Looks up an active DB by (Name, PlcName) so dropdown rows resolve to
    /// the right ActiveDb instance even in multi-PLC projects where two
    /// PLCs share a DB name (#58 review must-fix #4).
    /// </summary>
    private ActiveDb? FindActiveDb(DataBlockSummary summary)
    {
        for (int i = 0; i < _state.Dbs.Count; i++)
        {
            var db = _state.Dbs[i];
            // Index 0 reads its display PLC from State.AnchorPlcName; the
            // rest read it from each ActiveDb directly.
            var plc = i == 0 ? _state.AnchorPlcName : db.PlcName;
            if (string.Equals(db.Info.Name, summary.Name, StringComparison.Ordinal)
                && string.Equals(plc, summary.PlcName, StringComparison.Ordinal))
                return db;
        }
        return null;
    }

    /// <summary>
    /// User's choice in response to the "this DB has pending edits" prompt
    /// raised when removing a DB from the active set (#78). Centralised so
    /// every remove path maps the <see cref="ApplyStashCancelResult"/>
    /// the same way: ApplyAndSwitch→Apply, StashAndSwitch→Stash, Cancel→Cancel.
    /// <c>NoEdits</c> is the no-prompt fast path.
    /// </summary>
    private enum PendingDecision { NoEdits, Apply, Stash, Cancel }

    /// <summary>
    /// Single source of truth for the 3-way prompt that fires whenever a
    /// DB with pending inline edits is about to be dropped from the active
    /// set (#78). Returns <see cref="PendingDecision.NoEdits"/> immediately
    /// if there's nothing pending — caller proceeds with the removal
    /// without showing a dialog.
    /// </summary>
    private PendingDecision PromptForPendingEditsOnRemove(ActiveDb db)
    {
        var pendingCount = CountPendingEditsForDb(db);
        if (pendingCount == 0)
        {
            Log.Information(
                "[prompt] no-prompt fast path on {Name} (CountPendingEditsForDb=0) | {State}",
                db.Info.Name, SnapshotState());
            return PendingDecision.NoEdits;
        }
        if (_messageBox == null)
        {
            // Without a wired message-box service we can't ask the user —
            // treat it as a Cancel so the remove aborts cleanly rather than
            // dropping pending edits silently.
            Log.Information(
                "[prompt] no message-box wired; treating remove of {Name} as Cancel",
                db.Info.Name);
            return PendingDecision.Cancel;
        }
        Log.Information(
            "[prompt] Showing 3-way for {Name} (pending={Pending}) | {State}",
            db.Info.Name, pendingCount, SnapshotState());
        var result = _messageBox.AskApplyStashCancel(
            Res.Format("Dialog_SwitchDb_KeepConfirm_Text",
                pendingCount, db.Info.Name),
            Res.Get("Dialog_SwitchDb_KeepConfirm_Title"));
        Log.Information(
            "[prompt] User picked {Result} for {Name}", result, db.Info.Name);
        return result switch
        {
            ApplyStashCancelResult.ApplyAndSwitch => PendingDecision.Apply,
            ApplyStashCancelResult.StashAndSwitch => PendingDecision.Stash,
            _ => PendingDecision.Cancel,
        };
    }

    /// <summary>
    /// Snapshots pending inline edits for <paramref name="db"/> into an
    /// inert <see cref="StashedDbState"/> ready to be added to a new
    /// <see cref="ActiveSetState"/>. Returns null when the DB has no
    /// pending edits to capture. Reads from the store — unambiguous in
    /// multi-DB sessions where the same Path can exist in multiple DBs,
    /// and correct even if the tree hasn't rebuilt yet. Pure read —
    /// does NOT mutate <c>State.Stashes</c> (the snapshot setter does).
    /// </summary>
    private StashedDbState? CaptureStashForDb(ActiveDb db)
    {
        if (_pendingEditStore == null || _getModelToDb == null) return null;
        var modelToDb = _getModelToDb();
        var entries = new List<StashedEditEntry>();
        foreach (var (node, pendingValue) in _pendingEditStore.GetForDb(db, modelToDb))
        {
            // Resolve to the live tree to read StartValue (which is model-
            // derived and stable). If the lookup can't find it, fall back to
            // the model's stored StartValue.
            var startValue = _getStartValueForNode?.Invoke(node) ?? node.StartValue ?? "";
            entries.Add(new StashedEditEntry(node.Path, startValue, pendingValue));
        }
        if (entries.Count == 0) return null;
        var summary = new DataBlockSummary(
            db.Info.Name,
            "",
            blockType: db.Info.BlockType,
            isInstanceDb: string.Equals(db.Info.BlockType, "InstanceDB", StringComparison.Ordinal),
            plcName: _state.AnchorPlcName);
        return new StashedDbState(summary, entries);
    }

    /// <summary>
    /// Computes the next snapshot after removing <paramref name="db"/>
    /// from <paramref name="current"/>. Side-effects (the user-visible
    /// prompt + Apply branch's per-DB OnApply + usage-counter charge)
    /// run here because they MUST occur against the live (pre-cascade)
    /// tree. Returns null if the user cancelled or the Apply branch
    /// failed (cap hit / read-only OnApply); caller treats null as
    /// "abort this remove."
    /// </summary>
    private ActiveSetState? TryComputeRemove(ActiveSetState current, ActiveDb db)
    {
        var decision = PromptForPendingEditsOnRemove(db);
        if (decision == PendingDecision.Cancel)
        {
            Log.Information("DB remove cancelled by user: {Name}", db.Info.Name);
            return null;
        }
        if (decision == PendingDecision.Apply)
        {
            if (_tryApplyActiveDbInPlace == null || !_tryApplyActiveDbInPlace(db))
            {
                Log.Information(
                    "DB remove aborted: pending Apply did not succeed for {Name}",
                    db.Info.Name);
                return null;
            }
        }

        IReadOnlyDictionary<string, StashedDbState> nextStashes = current.Stashes;
        if (decision == PendingDecision.Stash)
        {
            var captured = CaptureStashForDb(db);
            if (captured != null)
            {
                var dict = current.Stashes.ToDictionary(kv => kv.Key, kv => kv.Value);
                dict[StashKey(captured.Summary)] = captured;
                nextStashes = dict;
                // The DB's edits are now snapshotted into the inert stash —
                // evict the store entries so the live tree doesn't keep
                // showing them as pending on whatever DB inherits the path.
                if (_pendingEditStore != null && _getModelToDb != null)
                    _pendingEditStore.ClearForDb(db, _getModelToDb());
            }
        }

        var nextDbs = current.Dbs.Where(d => !ReferenceEquals(d, db)).ToList();
        // Anchor handoff: only when the removed DB was the anchor; the
        // new anchor is whatever moved into nextDbs[0]. Same rule as the
        // legacy RemoveActiveDb.
        var nextAnchor = current.AnchorPlcName;
        if (current.Dbs.Count > 0 && ReferenceEquals(db, current.Dbs[0]))
        {
            nextAnchor = nextDbs.Count > 0 ? (nextDbs[0].PlcName ?? "") : "";
        }

        Log.Information(
            ReferenceEquals(db, current.Dbs[0])
                ? "Anchor removed: now {NewName} (was: {OldName})"
                : "DB disabled: {OldName}",
            nextDbs.Count > 0 ? nextDbs[0].Info.Name : "<empty>",
            db.Info.Name);

        return new ActiveSetState(nextDbs, nextStashes, nextAnchor);
    }

    /// <summary>
    /// Single-step remove (chip × / dropdown uncheck). Compound paths
    /// (solo, reactivate) call <see cref="TryComputeRemove"/> directly
    /// to compose multiple removes into one snapshot before assigning.
    /// </summary>
    /// <returns>true if removed; false if the user cancelled.</returns>
    private bool RemoveActiveDb(ActiveDb db)
    {
        var next = TryComputeRemove(_state, db);
        if (next == null) return false;
        SetState(next);
        return true;
    }

    /// <summary>
    /// Counts pending inline edits inside a DB's subtree. Used by the
    /// remove-with-stash-prompt flow (#58, #78). Reads from the store
    /// rather than walking the VM tree — O(store.Count) instead of
    /// O(subtree size), and correct even when the tree hasn't rebuilt yet.
    /// </summary>
    private int CountPendingEditsForDb(ActiveDb db)
    {
        if (_pendingEditStore == null || _getModelToDb == null) return 0;
        return _pendingEditStore.CountForDb(db, _getModelToDb());
    }

    private static string StashKey(DataBlockSummary summary) =>
        $"{summary.PlcName}{summary.FolderPath}{summary.Name}";
}
