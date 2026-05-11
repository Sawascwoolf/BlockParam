using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading.Tasks;
using System.Windows.Input;
using BlockParam.Models;

namespace BlockParam.UI;

/// <summary>
/// One pill per PLC in the DB-picker pill row (#pill-refactor).
/// Each pill is lazily loaded: the first time the user opens the popup,
/// <see cref="LoadCommand"/> fetches the DB list for this PLC from the
/// host and populates <see cref="AvailableDbs"/>. Subsequent opens skip
/// the fetch (IsLoaded guard).
///
/// <para>
/// Selection changes fire <see cref="SelectionChanged"/> with the added
/// and removed <see cref="DataBlockSummary"/> lists. The VM guards against
/// re-entrancy: when the cascade rewrites <see cref="SelectedDbs"/> from
/// the outside, the guard <c>_syncingSelection</c> suppresses the event
/// so the cascade can't trigger itself.
/// </para>
/// </summary>
public class PlcPillViewModel : ViewModelBase
{
    private readonly Func<string, Task<IReadOnlyList<DataBlockListItem>>> _loadDbs;
    private readonly IReadOnlyList<DataBlockListItem> _initialActiveItems;

    private bool _isOpen;
    private bool _isLoaded;
    private string _plcName = "";
    private string _label = "";
    private bool _isAnchor;

    // Re-entrancy guard: when the cascade rewrites SelectedDbs, we must
    // not echo those changes back as new SelectionChanged events.
    private bool _syncingSelection;

    public PlcPillViewModel(
        string plcName,
        bool isAnchor,
        IReadOnlyList<DataBlockListItem> initialActiveItems,
        Func<string, Task<IReadOnlyList<DataBlockListItem>>> loadDbs)
    {
        _plcName = plcName;
        _isAnchor = isAnchor;
        _initialActiveItems = initialActiveItems;
        _loadDbs = loadDbs;

        // AvailableDbs starts empty; LoadCommand fills it on first open.
        AvailableDbs = new ObservableCollection<DataBlockListItem>();

        // SelectedDbs starts with the currently active DBs for this PLC.
        // The host passed them in so the pill shows the right initial state
        // without waiting for LoadCommand to run.
        SelectedDbs = new ObservableCollection<object>(initialActiveItems);
        SelectedDbs.CollectionChanged += OnSelectedDbsChanged;

        LoadCommand = new RelayCommand(_ => OnIsOpenFlippedToTrue(), _ => !_isLoaded);
    }

    // ── Props ─────────────────────────────────────────────────────────────────

    public string PlcName
    {
        get => _plcName;
        private set => SetProperty(ref _plcName, value);
    }

    /// <summary>Label shown on the pill trigger button.</summary>
    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    /// <summary>True when this pill's PLC is the anchor (index 0).</summary>
    public bool IsAnchor
    {
        get => _isAnchor;
        set => SetProperty(ref _isAnchor, value);
    }

    /// <summary>Whether the pill popup is open. Bound two-way to PillMultiSelect.IsOpen.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!SetProperty(ref _isOpen, value)) return;
            if (value && !_isLoaded)
                OnIsOpenFlippedToTrue();
        }
    }

    /// <summary>True once AvailableDbs has been populated from the host fetch.</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        private set => SetProperty(ref _isLoaded, value);
    }

    /// <summary>All DBs for this PLC (lazy-populated on first open).</summary>
    public ObservableCollection<DataBlockListItem> AvailableDbs { get; }

    /// <summary>
    /// Currently selected DBs for this PLC. Two-way bound to PillMultiSelect.SelectedItems.
    /// Changes fire <see cref="SelectionChanged"/> unless guarded by <c>_syncingSelection</c>.
    /// </summary>
    public ObservableCollection<object> SelectedDbs { get; }

    /// <summary>
    /// Invoked when IsOpen flips to true and IsLoaded is false.
    /// Public so the host can pre-warm the list if desired.
    /// </summary>
    public ICommand LoadCommand { get; }

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired when the user adds or removes a DB from this pill's selection.
    /// Not fired when the cascade rewrites SelectedDbs via
    /// <see cref="SyncSelectedDbs"/> — the re-entrancy guard suppresses it.
    /// </summary>
    public event EventHandler<PillSelectionChangedEventArgs>? SelectionChanged;

    // ── Internal API for cascade rewrites ────────────────────────────────────

    /// <summary>
    /// Called by the VM cascade to push the authoritative active set back into
    /// this pill without firing <see cref="SelectionChanged"/>. The guard
    /// prevents an echo-back loop.
    /// </summary>
    public void SyncSelectedDbs(IReadOnlyList<DataBlockListItem> activeItems)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        try
        {
            SelectedDbs.Clear();
            foreach (var item in activeItems)
                SelectedDbs.Add(item);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    /// <summary>
    /// Called by the VM cascade when a DB is added to AvailableDbs and should
    /// also appear selected (e.g. when the same PLC's pill is rebuilt with
    /// a freshly-loaded item that was already in the active set).
    /// </summary>
    public void AddToAvailableAndSelected(DataBlockListItem item)
    {
        if (!AvailableDbs.Contains(item))
            AvailableDbs.Add(item);

        if (!SelectedDbs.Contains(item))
        {
            _syncingSelection = true;
            try { SelectedDbs.Add(item); }
            finally { _syncingSelection = false; }
        }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private async void OnIsOpenFlippedToTrue()
    {
        if (_isLoaded) return;

        try
        {
            IReadOnlyList<DataBlockListItem> items = await _loadDbs(_plcName);

            // Populate AvailableDbs on the dispatcher thread.
            AvailableDbs.Clear();
            foreach (var item in items)
                AvailableDbs.Add(item);

            // Sync selection: items that are in the initial active set stay
            // selected. Use (Name, PlcName) identity, same as FindActiveDb.
            _syncingSelection = true;
            try
            {
                SelectedDbs.Clear();
                foreach (var item in AvailableDbs)
                {
                    foreach (var active in _initialActiveItems)
                    {
                        if (string.Equals(item.Name, active.Name, StringComparison.Ordinal)
                            && string.Equals(item.PlcName, active.PlcName, StringComparison.Ordinal))
                        {
                            SelectedDbs.Add(item);
                            break;
                        }
                    }
                }
            }
            finally
            {
                _syncingSelection = false;
            }

            IsLoaded = true;
            (LoadCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            // Silently swallow load failures — the pill just stays empty.
            // The VM should have already logged.
            _ = ex;
        }
    }

    private void OnSelectedDbsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_syncingSelection) return;

        var added = new List<DataBlockSummary>();
        var removed = new List<DataBlockSummary>();

        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is DataBlockListItem dbi)
                    added.Add(dbi.Summary);
            }
        }

        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is DataBlockListItem dbi)
                    removed.Add(dbi.Summary);
            }
        }

        if (added.Count > 0 || removed.Count > 0)
            SelectionChanged?.Invoke(this, new PillSelectionChangedEventArgs(added, removed));
    }
}

/// <summary>
/// Carried by <see cref="PlcPillViewModel.SelectionChanged"/>. Contains the
/// lists of summaries added to and removed from the pill's selection.
/// </summary>
public sealed class PillSelectionChangedEventArgs : EventArgs
{
    public PillSelectionChangedEventArgs(
        IReadOnlyList<DataBlockSummary> added,
        IReadOnlyList<DataBlockSummary> removed)
    {
        Added = added;
        Removed = removed;
    }

    public IReadOnlyList<DataBlockSummary> Added { get; }
    public IReadOnlyList<DataBlockSummary> Removed { get; }
}
