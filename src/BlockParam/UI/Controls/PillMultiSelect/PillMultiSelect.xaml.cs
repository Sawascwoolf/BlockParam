using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Bindable multi-select pill control. Hosts wire it up like a
/// <c>ListBox</c> or <c>ComboBox</c>:
/// <code>
/// &lt;PillMultiSelect ItemsSource="{Binding Dbs}"
///                   DisplayMemberPath="Name"
///                   AbbreviationMemberPath="Number"
///                   Label="Data blocks" /&gt;
/// </code>
/// Selection is managed through the popup checkboxes in Phase 1;
/// <c>SelectedItems</c> / <c>IsSelectedMemberPath</c> arrive in Phase 2.
/// </summary>
public partial class PillMultiSelect : UserControl
{
    private readonly PillMultiSelectInternalState _internalState;
    private readonly MemberPathResolver _memberPathResolver;
    private readonly PillItemSource _itemSource;
    private readonly PillSelectionSync _selectionSync;

    public PillMultiSelect()
    {
        InitializeComponent();

        _internalState = new PillMultiSelectInternalState();
        _memberPathResolver = new MemberPathResolver();
        _itemSource = new PillItemSource(_internalState, _memberPathResolver);
        _selectionSync = new PillSelectionSync(_internalState, _itemSource, _memberPathResolver);
        _selectionSync.SelectionChanged += OnSyncSelectionChanged;

        // Set DataContext on the *content* so the existing XAML bindings
        // ({Binding IsOpen}, {Binding FilteredItems}, etc.) resolve against
        // _internalState. The UserControl's own DataContext is left to the
        // host so bindings on the control element itself resolve against the
        // host's data context as expected.
        ((FrameworkElement)Content).DataContext = _internalState;

        // Give every instance its own default SelectedItems collection.
        // SetCurrentValue preserves any two-way binding the host applies later;
        // SetValue would kill it. DP metadata default is null to avoid the
        // "shared default across instances" trap for collection DPs.
        SetCurrentValue(SelectedItemsProperty, new ObservableCollection<object>());
    }

    // ── DependencyProperties ─────────────────────────────────────────────────

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(PillMultiSelect),
            new PropertyMetadata(null, (d, e) => ((PillMultiSelect)d)._itemSource.ItemsSource = (IEnumerable?)e.NewValue));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty DisplayMemberPathProperty =
        DependencyProperty.Register(nameof(DisplayMemberPath), typeof(string), typeof(PillMultiSelect),
            new PropertyMetadata(null, (d, e) => ((PillMultiSelect)d)._itemSource.DisplayMemberPath = (string?)e.NewValue));

    public string? DisplayMemberPath
    {
        get => (string?)GetValue(DisplayMemberPathProperty);
        set => SetValue(DisplayMemberPathProperty, value);
    }

    public static readonly DependencyProperty AbbreviationMemberPathProperty =
        DependencyProperty.Register(nameof(AbbreviationMemberPath), typeof(string), typeof(PillMultiSelect),
            new PropertyMetadata(null, (d, e) => ((PillMultiSelect)d)._itemSource.AbbreviationMemberPath = (string?)e.NewValue));

    public string? AbbreviationMemberPath
    {
        get => (string?)GetValue(AbbreviationMemberPathProperty);
        set => SetValue(AbbreviationMemberPathProperty, value);
    }

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(PillMultiSelect),
            new PropertyMetadata(string.Empty, (d, e) => ((PillMultiSelect)d)._internalState.Label = (string)e.NewValue));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(Geometry), typeof(PillMultiSelect),
            new PropertyMetadata(null, (d, e) => ((PillMultiSelect)d)._internalState.Icon = (Geometry?)e.NewValue));

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public static readonly DependencyProperty OverflowOptionsProperty =
        DependencyProperty.Register(nameof(OverflowOptions), typeof(PillOverflowOptions), typeof(PillMultiSelect),
            new PropertyMetadata(null, (d, e) => ((PillMultiSelect)d).OnOverflowOptionsChanged((PillOverflowOptions?)e.NewValue)));

    public PillOverflowOptions? OverflowOptions
    {
        get => (PillOverflowOptions?)GetValue(OverflowOptionsProperty);
        set => SetValue(OverflowOptionsProperty, value);
    }

    private void OnOverflowOptionsChanged(PillOverflowOptions? options)
    {
        // Wire the formatter so the trigger summary uses the overflow rules.
        // When options is null, revert to the default comma-join.
        _internalState.DisplayFormatter = options != null
            ? selected => PillOverflowFormatter.Format(selected, options)
            : (Func<IReadOnlyList<PillRowViewModel>, string>?)null;
    }

    public static readonly DependencyProperty PopupWidthProperty =
        DependencyProperty.Register(nameof(PopupWidth), typeof(double), typeof(PillMultiSelect),
            new PropertyMetadata(280.0, (d, e) => ((PillMultiSelect)d)._internalState.PopupWidth = (double)e.NewValue));

    public double PopupWidth
    {
        get => (double)GetValue(PopupWidthProperty);
        set => SetValue(PopupWidthProperty, value);
    }

    public static readonly DependencyProperty PopupMaxListHeightProperty =
        DependencyProperty.Register(nameof(PopupMaxListHeight), typeof(double), typeof(PillMultiSelect),
            new PropertyMetadata(280.0, (d, e) => ((PillMultiSelect)d)._internalState.PopupMaxListHeight = (double)e.NewValue));

    public double PopupMaxListHeight
    {
        get => (double)GetValue(PopupMaxListHeightProperty);
        set => SetValue(PopupMaxListHeightProperty, value);
    }

    public static readonly DependencyProperty ShowSearchBoxProperty =
        DependencyProperty.Register(nameof(ShowSearchBox), typeof(bool), typeof(PillMultiSelect),
            new PropertyMetadata(true, (d, e) => ((PillMultiSelect)d)._internalState.ShowSearchBox = (bool)e.NewValue));

    public bool ShowSearchBox
    {
        get => (bool)GetValue(ShowSearchBoxProperty);
        set => SetValue(ShowSearchBoxProperty, value);
    }

    public static readonly DependencyProperty ShowFooterActionsProperty =
        DependencyProperty.Register(nameof(ShowFooterActions), typeof(bool), typeof(PillMultiSelect),
            new PropertyMetadata(true, (d, e) => ((PillMultiSelect)d)._internalState.ShowFooterActions = (bool)e.NewValue));

    public bool ShowFooterActions
    {
        get => (bool)GetValue(ShowFooterActionsProperty);
        set => SetValue(ShowFooterActionsProperty, value);
    }

    // String DPs default to null at the DP level; the callbacks forward only
    // non-null values so the internal state keeps its resx fallback intact
    // when the host doesn't set the DP.

    public static readonly DependencyProperty ClearTooltipProperty =
        DependencyProperty.Register(nameof(ClearTooltip), typeof(string), typeof(PillMultiSelect),
            new PropertyMetadata(null, (d, e) => { if (e.NewValue is string s) ((PillMultiSelect)d)._internalState.ClearTooltip = s; }));

    public string? ClearTooltip
    {
        get => (string?)GetValue(ClearTooltipProperty);
        set => SetValue(ClearTooltipProperty, value);
    }

    public static readonly DependencyProperty SelectAllTextProperty =
        DependencyProperty.Register(nameof(SelectAllText), typeof(string), typeof(PillMultiSelect),
            new PropertyMetadata(null, (d, e) => { if (e.NewValue is string s) ((PillMultiSelect)d)._internalState.SelectAllText = s; }));

    public string? SelectAllText
    {
        get => (string?)GetValue(SelectAllTextProperty);
        set => SetValue(SelectAllTextProperty, value);
    }

    public static readonly DependencyProperty ResetTextProperty =
        DependencyProperty.Register(nameof(ResetText), typeof(string), typeof(PillMultiSelect),
            new PropertyMetadata(null, (d, e) => { if (e.NewValue is string s) ((PillMultiSelect)d)._internalState.ResetText = s; }));

    public string? ResetText
    {
        get => (string?)GetValue(ResetTextProperty);
        set => SetValue(ResetTextProperty, value);
    }

    public static readonly DependencyProperty SearchPlaceholderProperty =
        DependencyProperty.Register(nameof(SearchPlaceholder), typeof(string), typeof(PillMultiSelect),
            new PropertyMetadata(null, (d, e) => { if (e.NewValue is string s) ((PillMultiSelect)d)._internalState.SearchPlaceholder = s; }));

    public string? SearchPlaceholder
    {
        get => (string?)GetValue(SearchPlaceholderProperty);
        set => SetValue(SearchPlaceholderProperty, value);
    }

    // ── Selection DPs + routed event ─────────────────────────────────────────
    // Logic lives in PillSelectionSync; the code-behind just owns the DP
    // declarations, forwards callbacks, and raises the routed event.

    /// <summary>Two-way bindable selection. Per-instance default set in ctor via
    /// SetCurrentValue — avoids the shared-reference trap of DP metadata defaults.</summary>
    public static readonly DependencyProperty SelectedItemsProperty =
        DependencyProperty.Register(nameof(SelectedItems), typeof(IList), typeof(PillMultiSelect),
            new PropertyMetadata(null,
                (d, e) => ((PillMultiSelect)d)._selectionSync.SetSelectedItems((IList?)e.NewValue)));

    public IList? SelectedItems
    {
        get => (IList?)GetValue(SelectedItemsProperty);
        set => SetValue(SelectedItemsProperty, value);
    }

    /// <summary>Path to a bool property on source items. Reads/writes it directly;
    /// subscribes INotifyPropertyChanged for live updates (Edge B).</summary>
    public static readonly DependencyProperty IsSelectedMemberPathProperty =
        DependencyProperty.Register(nameof(IsSelectedMemberPath), typeof(string), typeof(PillMultiSelect),
            new PropertyMetadata(null,
                (d, e) => ((PillMultiSelect)d)._selectionSync.SetIsSelectedMemberPath((string?)e.NewValue)));

    public string? IsSelectedMemberPath
    {
        get => (string?)GetValue(IsSelectedMemberPathProperty);
        set => SetValue(IsSelectedMemberPathProperty, value);
    }

    /// <summary>Fires once per selection-set change (deduplicated across a full
    /// reconciliation cycle). See <see cref="PillSelectionSync"/> for details.</summary>
    public static readonly RoutedEvent SelectionChangedEvent =
        EventManager.RegisterRoutedEvent(nameof(SelectionChanged),
            RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PillMultiSelect));

    public event RoutedEventHandler SelectionChanged
    {
        add => AddHandler(SelectionChangedEvent, value);
        remove => RemoveHandler(SelectionChangedEvent, value);
    }

    private void OnSyncSelectionChanged(object? sender, EventArgs e) =>
        RaiseEvent(new RoutedEventArgs(SelectionChangedEvent, this));

    // ── CLR escape hatches ────────────────────────────────────────────────────
    // Code-only hosts can set these for fully custom formatting without subclassing.
    // They take precedence over OverflowOptions when both are set.

    /// <summary>
    /// Custom trigger-summary formatter. Overrides <see cref="OverflowOptions"/>
    /// when both are set. Return value is the full text rendered between the
    /// label and the count badge. Internal because the parameter type
    /// <see cref="PillRowViewModel"/> is internal infrastructure; Phase 3
    /// will genericize this or expose a <c>TooltipMode</c> DP for XAML hosts.
    /// </summary>
    internal Func<IReadOnlyList<PillRowViewModel>, string>? DisplayFormatter
    {
        get => _internalState.DisplayFormatter;
        set => _internalState.DisplayFormatter = value;
    }

    /// <summary>
    /// Custom tooltip formatter. Return null to suppress the tooltip
    /// (e.g. when nothing is selected). Internal for the same reason as
    /// <see cref="DisplayFormatter"/>; Phase 3 adds a <c>TooltipMode</c> DP.
    /// </summary>
    internal Func<IReadOnlyList<PillRowViewModel>, string?>? TooltipFormatter
    {
        get => _internalState.TooltipFormatter;
        set => _internalState.TooltipFormatter = value;
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnPopupOpened(object sender, EventArgs e)
    {
        // Push focus into the search box so the user can start typing
        // immediately on click — same affordance as Linear / Notion / cmdk.
        SearchBox.Focus();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        // Stop the click from bubbling to the parent ToggleButton — clicking
        // the X is "wipe selection", not "open the popup". Without this the
        // user gets both behaviors per click which is jarring.
        e.Handled = true;
    }
}
