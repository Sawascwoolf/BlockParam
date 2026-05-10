using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Keeps three selection edges consistent for one <see cref="PillMultiSelect"/>
/// instance:
/// <list type="bullet">
///   <item><b>Edge A</b> — row <see cref="PillRowViewModel.IsSelected"/> ↔
///     <see cref="PillMultiSelect.SelectedItems"/> collection. Toggling a
///     checkbox adds/removes the source from the host's collection; adding/
///     removing from the collection flips the row's checkbox.</item>
///   <item><b>Edge B</b> — row <see cref="PillRowViewModel.IsSelected"/> ↔
///     source item's <c>IsSelectedMemberPath</c> bool property.  Toggling
///     writes through; an external <see cref="INotifyPropertyChanged"/> raise
///     on the bound property flips the row back.</item>
///   <item><b>Edge C</b> — when <see cref="PillItemSource"/> adds or removes
///     rows (ItemsSource collection mutation), new rows are reconciled against
///     both <see cref="PillMultiSelect.SelectedItems"/> membership and the
///     IsSelectedMemberPath bool. Removed rows are unsubscribed.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Re-entrancy guard</b>: a single <c>_syncing</c> flag wraps every
/// propagation cycle. Without it, a toggle on Edge A would update Edge B which
/// would raise <see cref="INotifyPropertyChanged"/> which would re-enter Edge B
/// and loop. The guard means "one propagation cycle owns the update; all others
/// observe but don't re-propagate."
/// </para>
/// <para>
/// <b>Last-writer-wins at startup</b>: if both <c>SelectedItems</c> and
/// <c>IsSelectedMemberPath</c> are set simultaneously and disagree for the same
/// row, the second DP changed-callback to fire wins. This is acceptable because
/// both DPs in conflict is a host-configuration error; the control makes a
/// deterministic choice rather than throwing. Document it in the host's code review.
/// </para>
/// <para>
/// <b>Source items without INPC</b>: when <c>IsSelectedMemberPath</c> is set
/// but the source does not implement <see cref="INotifyPropertyChanged"/>, the
/// initial read still syncs IsSelected. External mutations to that property will
/// NOT be reflected in the checkbox — the same constraint WPF data binding has.
/// </para>
/// </remarks>
internal sealed class PillSelectionSync
{
    private readonly PillMultiSelectInternalState _state;
    private readonly MemberPathResolver _resolver;

    // The host's SelectedItems collection (may change via DP swap).
    private IList? _selectedItems;
    private string? _isSelectedMemberPath;

    // Re-entrancy guard. Every public entry point that propagates must check
    // this before mutating anything, then set it for the duration of its work.
    private bool _syncing;

    // Pending SelectionChanged raise: set inside a syncing cycle so that we
    // fire once at the end of the cycle rather than once per row mutation.
    private bool _pendingSelectionChanged;

    // Raised by this class when the selection set changes. The UserControl
    // converts this into a routed RoutedEvent.
    internal event EventHandler? SelectionChanged;

    internal PillSelectionSync(
        PillMultiSelectInternalState state,
        PillItemSource itemSource,
        MemberPathResolver resolver)
    {
        _state = state;
        _resolver = resolver;

        // Subscribe to row lifecycle events from PillItemSource so we can
        // reconcile new rows and unsubscribe from removed ones (Edge C).
        itemSource.RowAdded += OnRowAdded;
        itemSource.RowRemoved += OnRowRemoved;

        // Subscribe to existing rows' IsSelected so Edge A/B propagation
        // works for rows that were created before this sync object exists.
        // (In practice this fires after the ctor is wired in the UserControl,
        // but defensive initialisation prevents edge cases in tests.)
        foreach (var row in state.Items)
            row.PropertyChanged += OnRowPropertyChanged;
    }

    // ── Public setters (called from UserControl DP callbacks) ────────────────

    /// <summary>
    /// Called when the <c>SelectedItems</c> DP changes on the UserControl.
    /// Unsubscribes from the old collection, subscribes to the new one, and
    /// reconciles row <see cref="PillRowViewModel.IsSelected"/> flags.
    /// </summary>
    internal void SetSelectedItems(IList? newValue)
    {
        // Unsubscribe from the previous collection.
        if (_selectedItems is INotifyCollectionChanged oldNcc)
            oldNcc.CollectionChanged -= OnSelectedItemsCollectionChanged;

        _selectedItems = newValue;

        if (_selectedItems is INotifyCollectionChanged newNcc)
            newNcc.CollectionChanged += OnSelectedItemsCollectionChanged;

        // Reconcile rows against the new collection (Edge A initial sync).
        ReconcileRowsFromSelectedItems();
    }

    /// <summary>
    /// Called when the <c>IsSelectedMemberPath</c> DP changes on the UserControl.
    /// Unsubscribes the old path listener, subscribes the new one, and re-reads
    /// each source item's bool to sync row IsSelected (Edge B initial sync).
    /// </summary>
    internal void SetIsSelectedMemberPath(string? newValue)
    {
        if (_isSelectedMemberPath == newValue) return;

        // If there was a previous path, unsubscribe source INPC listeners —
        // they filtered by the old property name and are now stale.
        if (_isSelectedMemberPath != null)
            UnsubscribeAllSourceInpc();

        _isSelectedMemberPath = newValue;

        if (_isSelectedMemberPath != null)
        {
            // Re-read each row's bool from the new path and subscribe INPC.
            ReconcileRowsFromMemberPath();
        }
    }

    // ── Edge C — row lifecycle ───────────────────────────────────────────────

    private void OnRowAdded(PillRowViewModel row)
    {
        // Subscribe to the new row's IsSelected so Edge A/B propagation fires.
        row.PropertyChanged += OnRowPropertyChanged;

        // Reconcile the new row's IsSelected from SelectedItems membership (Edge A)
        // and from the IsSelectedMemberPath bool (Edge B). Last-writer-wins: we
        // apply SelectedItems first, then overwrite with the member-path value if
        // both DPs are set — the member-path value is considered more "live".
        bool newSelected = false;

        if (_selectedItems != null)
            newSelected = ContainsRef(_selectedItems, row.Source);

        if (_isSelectedMemberPath != null)
            newSelected = ReadBoolFromSource(row.Source, _isSelectedMemberPath);

        // Guard against triggering re-entrancy: we set the row directly here
        // during the "seed" phase, then subscribe INPC for future changes.
        if (row.IsSelected != newSelected)
        {
            row.PropertyChanged -= OnRowPropertyChanged;  // silence during seed
            row.IsSelected = newSelected;
            row.PropertyChanged += OnRowPropertyChanged;
        }

        // Mirror the seed selection into SelectedItems (OnRowPropertyChanged
        // was silenced above, so we must do this explicitly).
        if (newSelected && _selectedItems != null && !ContainsRef(_selectedItems, row.Source))
            _selectedItems.Add(row.Source);

        if (_isSelectedMemberPath != null)
            SubscribeSourceInpc(row.Source);
    }

    private void OnRowRemoved(PillRowViewModel row)
    {
        row.PropertyChanged -= OnRowPropertyChanged;

        if (_isSelectedMemberPath != null)
            UnsubscribeSourceInpc(row.Source);

        // Intentionally do NOT remove row.Source from _selectedItems when the
        // row is removed from the ItemsSource. The host owns that collection;
        // a source item could temporarily leave the visible ItemsSource while
        // still being conceptually "selected" in the host's model. Removing it
        // silently would surprise the host. Document this contract in tests.
    }

    // ── Edge A — row IsSelected → SelectedItems / SelectedItems → row ────────

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PillRowViewModel.IsSelected)) return;
        if (_syncing) return;
        if (sender is not PillRowViewModel row) return;

        _syncing = true;
        _pendingSelectionChanged = true;
        try
        {
            // Edge A: mirror to SelectedItems.
            if (_selectedItems != null)
            {
                if (row.IsSelected)
                {
                    if (!ContainsRef(_selectedItems, row.Source))
                        _selectedItems.Add(row.Source);
                }
                else
                {
                    RemoveRef(_selectedItems, row.Source);
                }
            }

            // Edge B: write through to the source property.
            if (_isSelectedMemberPath != null)
                WriteBoolToSource(row.Source, _isSelectedMemberPath, row.IsSelected);
        }
        finally
        {
            _syncing = false;
            RaiseSelectionChangedIfPending();
        }
    }

    private void OnSelectedItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_syncing) return;

        _syncing = true;
        _pendingSelectionChanged = true;
        try
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    if (e.NewItems != null)
                        foreach (var item in e.NewItems)
                            SetRowSelected(item, true);
                    break;

                case NotifyCollectionChangedAction.Remove:
                    if (e.OldItems != null)
                        foreach (var item in e.OldItems)
                            SetRowSelected(item, false);
                    break;

                case NotifyCollectionChangedAction.Replace:
                    if (e.OldItems != null)
                        foreach (var item in e.OldItems)
                            SetRowSelected(item, false);
                    if (e.NewItems != null)
                        foreach (var item in e.NewItems)
                            SetRowSelected(item, true);
                    break;

                case NotifyCollectionChangedAction.Reset:
                    // The collection was mass-replaced or cleared. Deselect all rows,
                    // then re-select whichever source objects are still in the collection.
                    foreach (var row in _state.Items)
                        row.IsSelected = false;
                    if (_selectedItems != null)
                        foreach (var item in _selectedItems)
                            SetRowSelected(item, true);
                    break;
            }
        }
        finally
        {
            _syncing = false;
            RaiseSelectionChangedIfPending();
        }
    }

    // ── Edge B — source INPC → row IsSelected ───────────────────────────────

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isSelectedMemberPath == null) return;
        if (e.PropertyName != _isSelectedMemberPath) return;
        if (_syncing) return;
        if (sender == null) return;

        _syncing = true;
        _pendingSelectionChanged = true;
        try
        {
            var newBool = ReadBoolFromSource(sender, _isSelectedMemberPath);
            SetRowSelected(sender, newBool);

            // Mirror to SelectedItems as well (Edge A forward from Edge B mutation).
            if (_selectedItems != null)
            {
                if (newBool)
                {
                    if (!ContainsRef(_selectedItems, sender))
                        _selectedItems.Add(sender);
                }
                else
                {
                    RemoveRef(_selectedItems, sender);
                }
            }
        }
        finally
        {
            _syncing = false;
            RaiseSelectionChangedIfPending();
        }
    }

    // ── Reconcile helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Reads each existing row's <see cref="PillRowViewModel.IsSelected"/> from
    /// the current <see cref="_selectedItems"/> membership. Called when
    /// <c>SelectedItems</c> is swapped (Edge A initial sync).
    /// </summary>
    private void ReconcileRowsFromSelectedItems()
    {
        if (_syncing) return;

        _syncing = true;
        _pendingSelectionChanged = true;
        try
        {
            foreach (var row in _state.Items)
            {
                var shouldBeSelected = _selectedItems != null && ContainsRef(_selectedItems, row.Source);
                row.IsSelected = shouldBeSelected;
            }
        }
        finally
        {
            _syncing = false;
            RaiseSelectionChangedIfPending();
        }
    }

    /// <summary>
    /// Re-reads every row's IsSelected from the source property at
    /// <see cref="_isSelectedMemberPath"/> and subscribes INPC. Also mirrors
    /// the updated selection into <see cref="_selectedItems"/> so all three
    /// edges stay consistent after the DP swap (the re-entrancy guard blocks
    /// <see cref="OnRowPropertyChanged"/> from doing this automatically while
    /// we iterate). Called when <c>IsSelectedMemberPath</c> changes (Edge B
    /// initial sync).
    /// </summary>
    private void ReconcileRowsFromMemberPath()
    {
        if (_isSelectedMemberPath == null) return;
        if (_syncing) return;

        _syncing = true;
        _pendingSelectionChanged = true;
        try
        {
            foreach (var row in _state.Items)
            {
                var selected = ReadBoolFromSource(row.Source, _isSelectedMemberPath);
                row.IsSelected = selected;

                // Mirror into SelectedItems so Edge A stays consistent.
                // OnRowPropertyChanged is blocked by _syncing, so we do it here.
                if (_selectedItems != null)
                {
                    if (selected)
                    {
                        if (!ContainsRef(_selectedItems, row.Source))
                            _selectedItems.Add(row.Source);
                    }
                    else
                    {
                        RemoveRef(_selectedItems, row.Source);
                    }
                }

                SubscribeSourceInpc(row.Source);
            }
        }
        finally
        {
            _syncing = false;
            RaiseSelectionChangedIfPending();
        }
    }

    // ── Row lookup helpers ───────────────────────────────────────────────────

    private void SetRowSelected(object source, bool selected)
    {
        foreach (var row in _state.Items)
        {
            if (ReferenceEquals(row.Source, source))
            {
                row.IsSelected = selected;
                return;
            }
        }
    }

    // ── Source INPC subscription management ─────────────────────────────────

    private void SubscribeSourceInpc(object source)
    {
        if (source is INotifyPropertyChanged inpc)
            inpc.PropertyChanged += OnSourcePropertyChanged;
    }

    private void UnsubscribeSourceInpc(object source)
    {
        if (source is INotifyPropertyChanged inpc)
            inpc.PropertyChanged -= OnSourcePropertyChanged;
    }

    private void UnsubscribeAllSourceInpc()
    {
        foreach (var row in _state.Items)
            UnsubscribeSourceInpc(row.Source);
    }

    // ── Source property read/write (via MemberPathResolver) ─────────────────

    private bool ReadBoolFromSource(object source, string path)
    {
        if (!_resolver.TryGetDescriptor(source.GetType(), path, out var descriptor)
            || descriptor == null)
            return false;

        return descriptor.GetValue(source) is true;
    }

    private void WriteBoolToSource(object source, string path, bool value)
    {
        if (!_resolver.TryGetDescriptor(source.GetType(), path, out var descriptor)
            || descriptor == null)
            return;

        if (descriptor.IsReadOnly) return;

        descriptor.SetValue(source, value);
    }

    // ── SelectionChanged deduplication ───────────────────────────────────────

    private void RaiseSelectionChangedIfPending()
    {
        if (!_pendingSelectionChanged) return;
        _pendingSelectionChanged = false;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Reference-equality collection helpers ────────────────────────────────

    private static bool ContainsRef(IList list, object target)
    {
        foreach (var item in list)
            if (ReferenceEquals(item, target)) return true;
        return false;
    }

    private static void RemoveRef(IList list, object target)
    {
        for (int i = list.Count - 1; i >= 0; i--)
            if (ReferenceEquals(list[i], target)) { list.RemoveAt(i); return; }
    }
}
