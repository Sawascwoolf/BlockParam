using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Owns the lifecycle of "host's <see cref="IEnumerable"/> source items →
/// wrapper <see cref="PillRowViewModel"/> rows" for one
/// <see cref="PillMultiSelect"/> instance. Subscribes to
/// <see cref="INotifyCollectionChanged"/> when the source supports it and
/// handles incremental Add/Remove/Replace/Reset. Resolves Display and
/// Abbreviation strings via <see cref="MemberPathResolver"/>.
/// </summary>
/// <remarks>
/// This class has no knowledge of WPF DependencyProperties; it is pure data
/// plumbing between the host's collection and the internal state's row list.
/// The UserControl's DP callbacks call the setters here, which manage
/// subscriptions and trigger row rebuilds or string refreshes as needed.
/// </remarks>
internal sealed class PillItemSource
{
    // Identity comparer: source items might not implement equality correctly
    // (e.g. two distinct objects with the same property values). Identity is
    // always unambiguous and matches WPF's own ItemsControl behaviour.
    private static readonly IEqualityComparer<object> IdentityComparer =
        new ReferenceIdentityComparer();

    private readonly PillMultiSelectInternalState _state;
    private readonly MemberPathResolver _resolver;
    private readonly Dictionary<object, PillRowViewModel> _rowBySource;

    private IEnumerable? _itemsSource;
    private string? _displayMemberPath;
    private string? _abbreviationMemberPath;
    private Func<object, string>? _displayOverride;
    private Func<object, string>? _abbreviationOverride;

    /// <summary>
    /// Raised after a row is added to the internal state. Allows
    /// <see cref="PillSelectionSync"/> to reconcile selection and subscribe
    /// to <see cref="System.ComponentModel.INotifyPropertyChanged"/> on the
    /// new source item without needing access to internal dictionaries.
    /// </summary>
    internal event Action<PillRowViewModel>? RowAdded;

    /// <summary>
    /// Raised after a row is removed from the internal state (including during
    /// a Reset). Allows <see cref="PillSelectionSync"/> to unsubscribe from
    /// the source item's <see cref="System.ComponentModel.INotifyPropertyChanged"/>.
    /// </summary>
    internal event Action<PillRowViewModel>? RowRemoved;

    internal PillItemSource(PillMultiSelectInternalState state, MemberPathResolver resolver)
    {
        _state = state;
        _resolver = resolver;
        _rowBySource = new Dictionary<object, PillRowViewModel>(IdentityComparer);
    }

    /// <summary>
    /// The host's source collection. Setting this unsubscribes from the
    /// previous collection (if it was <see cref="INotifyCollectionChanged"/>),
    /// clears the row list, and rebuilds it from the new source.
    /// </summary>
    public IEnumerable? ItemsSource
    {
        get => _itemsSource;
        set
        {
            if (ReferenceEquals(_itemsSource, value)) return;

            // Unsubscribe from the old source.
            if (_itemsSource is INotifyCollectionChanged oldNcc)
                oldNcc.CollectionChanged -= OnSourceCollectionChanged;

            _itemsSource = value;

            // Subscribe to the new source.
            if (_itemsSource is INotifyCollectionChanged newNcc)
                newNcc.CollectionChanged += OnSourceCollectionChanged;

            RebuildRows();
        }
    }

    /// <summary>
    /// Path to the display-name property on each source item. Setting this
    /// re-resolves <see cref="PillRowViewModel.Display"/> on existing rows
    /// without rebuilding them.
    /// </summary>
    public string? DisplayMemberPath
    {
        get => _displayMemberPath;
        set
        {
            if (_displayMemberPath == value) return;
            _displayMemberPath = value;
            RefreshDisplayStrings();
        }
    }

    /// <summary>
    /// Path to the abbreviation property on each source item. Setting this
    /// re-resolves <see cref="PillRowViewModel.Abbreviation"/> on existing
    /// rows without rebuilding them.
    /// </summary>
    public string? AbbreviationMemberPath
    {
        get => _abbreviationMemberPath;
        set
        {
            if (_abbreviationMemberPath == value) return;
            _abbreviationMemberPath = value;
            RefreshAbbreviationStrings();
        }
    }

    /// <summary>
    /// Optional delegate that overrides <see cref="DisplayMemberPath"/> for
    /// display-string resolution. When set, called instead of the member-path
    /// reflection path. Set via the <c>DisplaySelector</c> CLR escape hatch.
    /// </summary>
    internal Func<object, string>? DisplayOverride
    {
        get => _displayOverride;
        set
        {
            _displayOverride = value;
            RefreshDisplayStrings();
        }
    }

    /// <summary>
    /// Optional delegate that overrides <see cref="AbbreviationMemberPath"/>
    /// for abbreviation-string resolution. When set, called instead of the
    /// member-path reflection path. Set via the <c>AbbreviationSelector</c>
    /// CLR escape hatch.
    /// </summary>
    internal Func<object, string>? AbbreviationOverride
    {
        get => _abbreviationOverride;
        set
        {
            _abbreviationOverride = value;
            RefreshAbbreviationStrings();
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private void RebuildRows()
    {
        // Fire RowRemoved for every existing row before the state clears them,
        // so PillSelectionSync can unsubscribe from source INotifyPropertyChanged.
        foreach (var row in _rowBySource.Values)
            RowRemoved?.Invoke(row);

        _state.ClearItems();
        _rowBySource.Clear();

        if (_itemsSource == null) return;

        foreach (var item in _itemsSource)
            AddRow(item);
    }

    private void AddRow(object source)
    {
        var display = ResolveDisplay(source);
        var abbrev = ResolveAbbreviation(source, display);
        var row = new PillRowViewModel(source, display, abbrev);
        _rowBySource[source] = row;
        _state.AddItem(row);
        RowAdded?.Invoke(row);
    }

    private void RemoveRow(object source)
    {
        if (!_rowBySource.TryGetValue(source, out var row)) return;
        _rowBySource.Remove(source);
        _state.RemoveItem(row);
        RowRemoved?.Invoke(row);
    }

    private void RefreshDisplayStrings()
    {
        foreach (var kvp in _rowBySource)
            kvp.Value.Display = ResolveDisplay(kvp.Key);
    }

    private void RefreshAbbreviationStrings()
    {
        foreach (var kvp in _rowBySource)
            kvp.Value.Abbreviation = ResolveAbbreviation(kvp.Key, kvp.Value.Display);
    }

    private string ResolveDisplay(object source)
    {
        if (_displayOverride != null) return _displayOverride(source);
        return _resolver.Resolve(source, _displayMemberPath, s => s.ToString() ?? string.Empty);
    }

    private string ResolveAbbreviation(object source, string displayFallback)
    {
        if (_abbreviationOverride != null) return _abbreviationOverride(source);
        return _resolver.Resolve(source, _abbreviationMemberPath, _ => displayFallback);
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems != null)
                    foreach (var item in e.NewItems)
                        AddRow(item);
                break;

            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems != null)
                    foreach (var item in e.OldItems)
                        RemoveRow(item);
                break;

            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems != null)
                    foreach (var item in e.OldItems)
                        RemoveRow(item);
                if (e.NewItems != null)
                    foreach (var item in e.NewItems)
                        AddRow(item);
                break;

            case NotifyCollectionChangedAction.Reset:
                RebuildRows();
                break;

            case NotifyCollectionChangedAction.Move:
                // Source order is reflected through member-path values, not
                // index positions. Move notifications don't require row changes
                // in Phase 1 (sorting is by selection snapshot, not source index).
                break;
        }
    }

    // ── Identity comparer (net48 has no ReferenceEqualityComparer<T>) ────────

    private sealed class ReferenceIdentityComparer : IEqualityComparer<object>
    {
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
