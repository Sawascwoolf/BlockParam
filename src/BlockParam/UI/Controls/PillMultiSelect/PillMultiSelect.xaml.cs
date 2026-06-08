using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Bindable multi-select pill control. Hosts wire it up like a
/// <c>ListBox</c> or <c>ComboBox</c>:
/// <code>
/// &lt;PillMultiSelect ItemsSource="{Binding Dbs}"
///                   DisplayMemberPath="Name"
///                   AbbreviationMemberPath="Number"
///                   Label="Data blocks"
///                   TooltipMode="FullNames" /&gt;
/// </code>
/// </summary>
public partial class PillMultiSelect : UserControl
{
    private readonly MultiSelectInternalState _internalState;
    private readonly MemberPathResolver _memberPathResolver;
    private readonly MultiSelectItemSource _itemSource;
    private readonly MultiSelectSelectionSync _selectionSync;
    private readonly MultiSelectFormatter _formatter;

    // Guards the IsOpen DP ↔ _internalState.IsOpen two-way propagation
    // from cycling indefinitely.
    private bool _syncingIsOpen;

    public PillMultiSelect()
    {
        InitializeComponent();

        _internalState = new MultiSelectInternalState();
        _memberPathResolver = new MemberPathResolver();
        _itemSource = new MultiSelectItemSource(_internalState, _memberPathResolver);
        _selectionSync = new MultiSelectSelectionSync(_internalState, _itemSource, _memberPathResolver);
        _formatter = new MultiSelectFormatter(_internalState, _itemSource);

        _selectionSync.SelectionChanged += OnSyncSelectionChanged;

        // Subscribe to _internalState.IsOpen so user-driven popup opens/closes
        // propagate back to the DP (two-way binding support).
        _internalState.PropertyChanged += OnInternalStatePropertyChanged;

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

        MultiSelectLog.Information("PillMultiSelect: control instantiated");
    }

    // ── DependencyProperties ─────────────────────────────────────────────────

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(PillMultiSelect),
            new PropertyMetadata(null, (d, e) => ((PillMultiSelect)d).OnItemsSourceDpChanged((IEnumerable?)e.NewValue)));

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

    /// <summary>
    /// Optional path to a property on each source item used to group rows in
    /// the popup list. When set, the ListBox renders one expandable section
    /// per distinct group-key value with a tri-state header checkbox that
    /// toggles all children. When null (default), the list is flat.
    /// <para>
    /// <b>Precedence</b>: overridden by <see cref="GroupKeySelector"/> CLR
    /// escape hatch when both are set.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty GroupKeyMemberPathProperty =
        DependencyProperty.Register(nameof(GroupKeyMemberPath), typeof(string), typeof(PillMultiSelect),
            new PropertyMetadata(null, (d, e) => ((PillMultiSelect)d)._itemSource.GroupKeyMemberPath = (string?)e.NewValue));

    public string? GroupKeyMemberPath
    {
        get => (string?)GetValue(GroupKeyMemberPathProperty);
        set => SetValue(GroupKeyMemberPathProperty, value);
    }

    /// <summary>
    /// Optional custom <see cref="DataTemplate"/> for the group header row
    /// (checkbox + label + expand chevron + count). When null, the control's
    /// built-in default template defined in XAML is used. The template's
    /// DataContext is the <see cref="MultiSelectGroupViewModel"/> itself — the
    /// container template unwraps the <see cref="System.Windows.Data.CollectionViewGroup.Name"/>
    /// (which is the group VM) into a <c>ContentControl.Content</c>, so a
    /// host template binds directly against the group VM's members:
    /// <c>{Binding Header}</c>, <c>{Binding IsSelected}</c>,
    /// <c>{Binding IsExpanded}</c>, <c>{Binding SelectedCount}</c>,
    /// <c>{Binding TotalCount}</c>.
    /// </summary>
    public static readonly DependencyProperty GroupHeaderTemplateProperty =
        DependencyProperty.Register(nameof(GroupHeaderTemplate), typeof(DataTemplate), typeof(PillMultiSelect),
            new PropertyMetadata(null, (d, e) => ((PillMultiSelect)d)._internalState.GroupHeaderTemplate = (DataTemplate?)e.NewValue));

    public DataTemplate? GroupHeaderTemplate
    {
        get => (DataTemplate?)GetValue(GroupHeaderTemplateProperty);
        set => SetValue(GroupHeaderTemplateProperty, value);
    }

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(PillMultiSelect),
            new PropertyMetadata(string.Empty, (d, e) => ((PillMultiSelect)d).OnLabelDpChanged((string)e.NewValue)));

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

    /// <summary>
    /// Overflow rules for the trigger pill's summary text. Controls when the
    /// control switches from full display names to abbreviations (entry-count
    /// or char-count threshold) and when it collapses to "+N more".
    /// <para>
    /// <b>Precedence</b>: overridden by the <see cref="DisplayFormatter"/> CLR
    /// escape hatch when both are set — code hosts that supply a custom
    /// formatter take priority over the DP.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty OverflowOptionsProperty =
        DependencyProperty.Register(nameof(OverflowOptions), typeof(PillOverflowOptions), typeof(PillMultiSelect),
            new PropertyMetadata(null, (d, e) =>
                ((PillMultiSelect)d)._formatter.OnOverflowOptionsChanged((PillOverflowOptions?)e.NewValue)));

    public PillOverflowOptions? OverflowOptions
    {
        get => (PillOverflowOptions?)GetValue(OverflowOptionsProperty);
        set => SetValue(OverflowOptionsProperty, value);
    }

    /// <summary>
    /// Built-in tooltip strategy for the trigger pill.
    /// <list type="bullet">
    /// <item><see cref="PillTooltipMode.None"/> — no tooltip (default).</item>
    /// <item><see cref="PillTooltipMode.FullNames"/> — one full display name per line.</item>
    /// <item><see cref="PillTooltipMode.AbbrevAndFullNames"/> — "Abbrev — Display" per line.</item>
    /// </list>
    /// <para>
    /// <b>Precedence</b>: overridden by the <see cref="TooltipFormatter"/> CLR
    /// escape hatch when both are set. Setting <c>TooltipFormatter</c> back to
    /// <c>null</c> re-activates this DP.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty TooltipModeProperty =
        DependencyProperty.Register(nameof(TooltipMode), typeof(PillTooltipMode), typeof(PillMultiSelect),
            new PropertyMetadata(PillTooltipMode.None, (d, e) =>
                ((PillMultiSelect)d)._formatter.OnTooltipModeChanged((PillTooltipMode)e.NewValue)));

    public PillTooltipMode TooltipMode
    {
        get => (PillTooltipMode)GetValue(TooltipModeProperty);
        set => SetValue(TooltipModeProperty, value);
    }

    /// <summary>
    /// Whether the popup is open. Two-way bindable; propagates to/from the
    /// internal state so hosts can drive open/close programmatically without
    /// reflection, and so the DP stays in sync when the user opens/closes the
    /// popup by clicking the trigger.
    /// </summary>
    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(PillMultiSelect),
            new PropertyMetadata(false, (d, e) => ((PillMultiSelect)d).OnIsOpenDpChanged((bool)e.NewValue)));

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    private void OnIsOpenDpChanged(bool value)
    {
        MultiSelectLog.Information("PillMultiSelect DP: IsOpen={Value} (host->control)", value);
        if (_syncingIsOpen) return;
        _syncingIsOpen = true;
        try { _internalState.IsOpen = value; }
        finally { _syncingIsOpen = false; }
    }

    // Diagnostic shims around the host->control DP boundary (#141). The pill
    // renders an empty bubble when the host bindings never deliver Label /
    // ItemsSource / SelectedItems into _internalState; these lines make that
    // boundary observable in the runtime log instead of a silent blank.
    private void OnLabelDpChanged(string value)
    {
        MultiSelectLog.Information("PillMultiSelect DP: Label='{Label}' (host->control)", value ?? "");
        _internalState.Label = value;
    }

    private void OnItemsSourceDpChanged(IEnumerable? value)
    {
        int n = value is ICollection c ? c.Count : -1;
        MultiSelectLog.Information("PillMultiSelect DP: ItemsSource set (items={N}, null={Null})",
            n, value == null);
        _itemSource.ItemsSource = value;
    }

    private void OnSelectedItemsDpChanged(IList? value)
    {
        MultiSelectLog.Information("PillMultiSelect DP: SelectedItems set (count={N}, null={Null})",
            value?.Count ?? -1, value == null);
        _selectionSync.SetSelectedItems(value);
    }

    private void OnInternalStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MultiSelectInternalState.IsOpen)) return;
        if (_syncingIsOpen) return;
        _syncingIsOpen = true;
        try { SetCurrentValue(IsOpenProperty, _internalState.IsOpen); }
        finally { _syncingIsOpen = false; }
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
    // non-null values so the internal state keeps its English-literal default
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
    // Logic lives in MultiSelectSelectionSync; the code-behind just owns the DP
    // declarations, forwards callbacks, and raises the routed event.

    /// <summary>Two-way bindable selection. Per-instance default set in ctor via
    /// SetCurrentValue — avoids the shared-reference trap of DP metadata defaults.</summary>
    public static readonly DependencyProperty SelectedItemsProperty =
        DependencyProperty.Register(nameof(SelectedItems), typeof(IList), typeof(PillMultiSelect),
            new PropertyMetadata(null,
                (d, e) => ((PillMultiSelect)d).OnSelectedItemsDpChanged((IList?)e.NewValue)));

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
    /// reconciliation cycle). See <see cref="MultiSelectSelectionSync"/> for details.</summary>
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
    // Precedence: CLR escape hatches always override the corresponding DP.
    // To restore DP-driven behaviour, set the CLR property back to null.

    /// <summary>
    /// Custom trigger-summary formatter. Receives the source items (not wrapper
    /// rows) of the currently selected entries.
    /// <para>
    /// <b>Precedence</b>: overrides <see cref="OverflowOptions"/> when both are
    /// set. Setting back to <c>null</c> restores any <c>OverflowOptions</c> DP
    /// value.
    /// </para>
    /// </summary>
    public Func<IReadOnlyList<object>, string>? DisplayFormatter
    {
        get => _formatter.DisplayFormatter;
        set => _formatter.DisplayFormatter = value;
    }

    /// <summary>
    /// Custom tooltip formatter. Receives the source items of selected entries;
    /// return <c>null</c> to suppress the tooltip.
    /// <para>
    /// <b>Precedence</b>: overrides <see cref="TooltipMode"/> when both are set.
    /// Setting back to <c>null</c> restores any <c>TooltipMode</c> DP value.
    /// </para>
    /// </summary>
    public Func<IReadOnlyList<object>, string?>? TooltipFormatter
    {
        get => _formatter.TooltipFormatter;
        set => _formatter.TooltipFormatter = value;
    }

    /// <summary>
    /// Per-item display-string selector. Overrides <see cref="DisplayMemberPath"/>
    /// when set; falls back to <c>ToString()</c> when neither is set.
    /// Existing rows are re-resolved immediately.
    /// </summary>
    public Func<object, string>? DisplaySelector
    {
        get => _formatter.DisplaySelector;
        set => _formatter.DisplaySelector = value;
    }

    /// <summary>
    /// Per-item abbreviation-string selector. Overrides <see cref="AbbreviationMemberPath"/>
    /// when set; falls back to Display when neither is set.
    /// Existing rows are re-resolved immediately.
    /// </summary>
    public Func<object, string>? AbbreviationSelector
    {
        get => _formatter.AbbreviationSelector;
        set => _formatter.AbbreviationSelector = value;
    }

    /// <summary>
    /// Per-item group-key selector. Overrides <see cref="GroupKeyMemberPath"/>
    /// when set; existing rows are re-grouped immediately. Setting back to
    /// <c>null</c> restores the DP-driven behaviour.
    /// </summary>
    public Func<object, object?>? GroupKeySelector
    {
        get => _itemSource.GroupKeyOverride;
        set => _itemSource.GroupKeyOverride = value;
    }

    /// <summary>
    /// Custom search-filter predicate. Receives a source item and the current
    /// search text; return <c>true</c> to include the item. Overrides the
    /// default Display/Abbreviation contains-check when set. Setting back to
    /// <c>null</c> restores the default.
    /// </summary>
    public Func<object, string, bool>? FilterPredicate
    {
        get => _formatter.FilterPredicate;
        set => _formatter.FilterPredicate = value;
    }

    // ── Internals exposed for headless screenshot composition ────────────────
    // The Popup lives in its own HWND, so a window-level RenderTargetBitmap
    // misses it — the DevLauncher capture tool composites the trigger and the
    // popup's child element manually. These properties hand it the two named
    // XAML elements without needing reflection.

    internal System.Windows.Controls.Primitives.Popup PopupElement => PillPopup;
    internal FrameworkElement TriggerElement => PillTrigger;

    /// <summary>
    /// Backing view-model used by the popup's bindings. Exposed for headless
    /// capture and tests (both projects have <c>InternalsVisibleTo</c>) so
    /// they can drive non-DP state — search text, group expansion — without
    /// reflection. Not part of the public API surface.
    /// </summary>
    internal MultiSelectInternalState InternalState => _internalState;

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnPopupOpened(object sender, EventArgs e)
    {
        // Push focus into the search box so the user can start typing
        // immediately on click — same affordance as Linear / Notion / cmdk.
        // The SearchBox lives in MultiSelectDropdown now; route through it.
        Dropdown.FocusSearchBox();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        // Stop the click from bubbling to the parent ToggleButton — clicking
        // the X is "wipe selection", not "open the popup". Without this the
        // user gets both behaviors per click which is jarring.
        e.Handled = true;
    }

    /// <summary>
    /// Scripted-only: drive the same lazy-load path the real popup uses
    /// (<see cref="MultiSelectInternalState.IsOpen"/> → host VM's
    /// <c>OnIsOpenFlippedToTrue</c>) without going through the trigger
    /// click. <see cref="InternalState"/> and <see cref="TriggerElement"/>
    /// already cover the read side; this method is the write side capture
    /// mode needs.
    /// </summary>
    internal void SetScriptedOpenState(bool open) => _internalState.IsOpen = open;
}
