using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlockParam.Diagnostics;
using BlockParam.Localization;
using BlockParam.Services;

namespace BlockParam.UI;

public partial class BulkChangeDialog : Window
{
    public BulkChangeDialog()
    {
        InitializeComponent();
        WindowIconHelper.SetIcon(this);
        ZoomHost.Attach(this);
    }

    private bool _suppressSelectionEvents;

    // Inspector collapse: remember the expanded width so we can restore it.
    // 340 matches the XAML default; overwritten once the user resizes.
    private double _lastExpandedSidebarWidth = 340;
    private const double CollapsedSidebarWidth = 28;

    public BulkChangeDialog(BulkChangeViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.RequestClose += () => Close();
        viewModel.FlatListRefreshed += RehydrateManualSelection;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => viewModel.Dispose();

        // Briefly set Topmost to appear above TIA Portal, then release
        // so other windows (non-TIA) can go in front.
        Loaded += (_, _) =>
        {
            Topmost = true;
            Activate();
            Topmost = false;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BulkChangeViewModel.IsInspectorCollapsed)) return;
        if (DataContext is not BulkChangeViewModel vm) return;

        if (vm.IsInspectorCollapsed)
        {
            // Save the user's last expanded width before collapsing.
            if (SidebarColumn.Width.IsAbsolute && SidebarColumn.Width.Value > CollapsedSidebarWidth)
                _lastExpandedSidebarWidth = SidebarColumn.Width.Value;
            SidebarColumn.Width = new GridLength(CollapsedSidebarWidth);
            SplitterColumn.Width = new GridLength(0);
        }
        else
        {
            SidebarColumn.Width = new GridLength(_lastExpandedSidebarWidth);
            SplitterColumn.Width = new GridLength(4);
        }
    }

    /// <summary>
    /// Click on a Bulk-preview row → jump to that member in the tree and scroll it into view.
    /// </summary>
    private void OnBulkPreviewRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not BulkPreviewEntry entry) return;
        JumpToMember(entry.Node);
    }

    /// <summary>
    /// Click on a Pending-edits row → jump to that member (ignored when click came
    /// from the per-row Undo button).
    /// </summary>
    private void OnPendingRowClick(object sender, MouseButtonEventArgs e)
    {
        // If the click bubbled up from the Undo button, skip the jump.
        if (e.OriginalSource is DependencyObject dep && FindAncestor<Button>(dep) != null) return;
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not PendingEditEntry entry) return;
        JumpToMember(entry.Node);
    }

    /// <summary>
    /// Click on an Issues-list row → jump to that member in the tree (#26).
    /// Issues are read-only findings; the row has no buttons of its own,
    /// so any click anywhere on the row should jump.
    /// </summary>
    private void OnIssueRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not ExistingIssueEntry entry) return;
        JumpToMember(entry.Node);
    }

    /// <summary>Per-row ↶ button in the Pending-edits list.</summary>
    private void OnUndoPendingClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not PendingEditEntry entry) return;
        if (DataContext is not BulkChangeViewModel vm) return;
        vm.UndoPendingEdit(entry);
        e.Handled = true;
    }

    private void JumpToMember(MemberNodeViewModel target)
    {
        if (DataContext is not BulkChangeViewModel vm) return;
        target.EnsureVisible();
        vm.SelectedFlatMember = target;
        MemberListView.ScrollIntoView(target);
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void OnListViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var vmCheck = DataContext as BulkChangeViewModel;
        var addedList = e.AddedItems.OfType<MemberNodeViewModel>().ToList();
        var removedList = e.RemovedItems.OfType<MemberNodeViewModel>().ToList();
        bool refreshing = vmCheck?.IsRefreshing == true;

        // Ignore selection changes that are actually caused by the VM mutating
        // the FlatMembers ObservableCollection during a refresh — they are ghost
        // removals/additions, not user input.
        if (_suppressSelectionEvents || refreshing) return;
        if (DataContext is not BulkChangeViewModel vm) return;

        // Deselect any non-leaf items that got included in the multi-select.
        // Per UX decision: only leaf members are selectable for manual bulk edit.
        var parentsToDrop = addedList.Where(m => !m.IsLeaf).ToList();
        if (parentsToDrop.Count > 0)
        {
            _suppressSelectionEvents = true;
            try
            {
                foreach (var p in parentsToDrop)
                    MemberListView.SelectedItems.Remove(p);
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }

        // WPF fires SelectionChanged with RemovedItems=[X] when the singular
        // SelectedItem property changes, even if X is still in SelectedItems
        // (multi-select). Filter those ghost removals — a real deselect means
        // the item is no longer in SelectedItems.
        var stillSelected = MemberListView.SelectedItems.OfType<MemberNodeViewModel>().ToHashSet();
        var realRemoved = removedList.Where(m => !stillSelected.Contains(m)).ToList();

        var added = addedList.Where(m => m.IsLeaf);
        vm.UpdateManualSelection(added, realRemoved, isFilterRehydration: false);
    }

    /// <summary>
    /// Re-applies the VM's persisted manual selection to the ListView after the
    /// flat list was rebuilt (e.g. after a filter change). Items whose paths
    /// are not currently visible stay in the VM's set but are not shown as selected.
    /// </summary>
    private void RehydrateManualSelection()
    {
        if (DataContext is not BulkChangeViewModel vm) return;

        if (vm.ManualSelectedPaths.Count == 0)
        {
            if (MemberListView.SelectedItems.Count > 1)
            {
                _suppressSelectionEvents = true;
                try { MemberListView.SelectedItems.Clear(); }
                finally { _suppressSelectionEvents = false; }
            }
            return;
        }

        _suppressSelectionEvents = true;
        try
        {
            MemberListView.SelectedItems.Clear();
            foreach (var m in MemberListView.Items.OfType<MemberNodeViewModel>())
            {
                if (vm.ManualSelectedPaths.Contains(m.Path))
                {
                    MemberListView.SelectedItems.Add(m);
                }
            }
        }
        finally
        {
            _suppressSelectionEvents = false;
        }
    }

    private void OnExpandToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is MemberNodeViewModel memberVm
            && DataContext is BulkChangeViewModel vm)
        {
            FlatTreeManager.CycleExpandState(memberVm);
            vm.RefreshFlatList();
        }
    }

    /// <summary>
    /// #9: GridView columns have no native star sizing, so we compute it here.
    /// Fixed columns keep their widths; the Comment column soaks up whatever
    /// space is left so long comments stop truncating when the dialog is wide.
    /// </summary>
    private void OnMemberListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CommentColumn == null || NameColumn == null
            || DataTypeColumn == null || StartValueColumn == null) return;

        // Chrome + vertical scrollbar + row padding. Slightly conservative so
        // we never force a horizontal scrollbar when space is tight.
        const double Chrome = 32;
        var available = MemberListView.ActualWidth
                        - NameColumn.Width
                        - DataTypeColumn.Width
                        - StartValueColumn.Width
                        - Chrome;

        const double MinCommentWidth = 160;
        CommentColumn.Width = System.Math.Max(MinCommentWidth, available);
    }

    private void OnListViewDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MemberListView.SelectedItem is MemberNodeViewModel memberVm && !memberVm.IsLeaf)
        {
            if (DataContext is BulkChangeViewModel vm)
            {
                FlatTreeManager.CycleExpandState(memberVm);
                vm.RefreshFlatList();
            }
        }
    }

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        // Columns are not sortable — ignore header clicks
    }

    private void OnSuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is AutocompleteSuggestion suggestion
            && DataContext is BulkChangeViewModel vm)
        {
            vm.AcceptSuggestion(suggestion.DisplayName);
            lb.SelectedItem = null;
        }
    }

    private void OnBulkValueMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox tb && DataContext is BulkChangeViewModel vm)
        {
            if (!tb.IsFocused)
            {
                tb.Focus();
                tb.SelectAll();
                e.Handled = true; // Prevent caret positioning
                vm.ShowAllSuggestions();
            }
            // When already focused, let normal caret positioning happen
        }
    }

    private void OnDropdownArrowClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is BulkChangeViewModel vm)
        {
            vm.ToggleAllSuggestions();
            // Select all text in the NewValue TextBox
            if (sender is Button btn && btn.Parent is Grid grid)
            {
                var tb = grid.Children.OfType<System.Windows.Controls.TextBox>().FirstOrDefault();
                tb?.SelectAll();
                tb?.Focus();
            }
        }
    }

    private void OnInlineDropdownClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Parent is Grid grid
            && DataContext is BulkChangeViewModel vm)
        {
            var tb = grid.Children.OfType<TextBox>().FirstOrDefault();
            if (tb?.Tag is MemberNodeViewModel memberVm)
            {
                var suggestions = vm.GetSuggestionsForMember(memberVm, tb.Text);
                ShowInlineOverlay(tb, memberVm, suggestions);
                tb.SelectAll();
                tb.Focus();
            }
        }
    }

    private void OnInlineTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.IsFocused
            && DataContext is BulkChangeViewModel vm && tb.Tag is MemberNodeViewModel memberVm)
        {
            var suggestions = vm.GetSuggestionsForMember(memberVm, tb.Text);
            ShowInlineOverlay(tb, memberVm, suggestions);
            // HasInlineError can flip on each keystroke — keep the hint overlay
            // in sync so a cell that starts valid but becomes invalid (or vice
            // versa) surfaces the right feedback without needing a refocus.
            // Do NOT reposition here: if the user dragged the overlay out of
            // the way, re-anchoring on every keystroke would snap it back.
            UpdateInlineHintVisibility(memberVm);
        }
    }

    /// <summary>Places the dialog-root <c>InlineOverlay</c> below the given TextBox.</summary>
    private void ShowInlineOverlay(TextBox tb, MemberNodeViewModel memberVm,
        System.Collections.Generic.IReadOnlyList<AutocompleteSuggestion> suggestions)
    {
        if (suggestions.Count == 0)
        {
            HideInlineOverlay();
            return;
        }

        InlineOverlayList.ItemsSource = suggestions;
        InlineOverlayList.SelectedItem = null;
        InlineOverlayList.Tag = memberVm;

        // Position relative to the Content root — the overlay is a sibling
        // of `tb`'s ancestor inside the same LayoutTransform, so using `this`
        // (outside the transform) would double-scale the margin when zoomed.
        var bottomLeft = tb.TranslatePoint(new System.Windows.Point(0, tb.ActualHeight), (UIElement)Content);
        InlineOverlay.Width = tb.ActualWidth;
        InlineOverlay.Margin = new Thickness(bottomLeft.X, bottomLeft.Y, 0, 0);
        InlineOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>Hides the inline overlay. Safe to call when already hidden.</summary>
    private void HideInlineOverlay()
    {
        InlineOverlay.Visibility = Visibility.Collapsed;
        InlineOverlayList.ItemsSource = null;
        InlineOverlayList.Tag = null;
    }

    /// <summary>
    /// Scripted-only entry point (DevLauncher capture mode): surfaces the
    /// inline-autocomplete overlay AND the hint/error popup for the given
    /// member path with a typed prefix, without going through the interactive
    /// focus / TextChanged chain. Finds the cell's TextBox via the ListView's
    /// container generator and positions the dialog-root overlays over it.
    /// </summary>
    internal void ShowInlineOverlayForScripted(MemberNodeViewModel memberVm, string typed)
    {
        if (DataContext is not BulkChangeViewModel vm) return;

        // Materialize the row if it's outside the viewport so the TextBox exists.
        MemberListView.ScrollIntoView(memberVm);
        UpdateLayout();

        var item = MemberListView.ItemContainerGenerator.ContainerFromItem(memberVm) as ListViewItem;
        if (item == null)
        {
            Log.Warning("ShowInlineOverlayForScripted: no container for {Path}", memberVm.Path);
            return;
        }

        var tb = FindDescendant<TextBox>(item, "InlineStartValue");
        if (tb == null)
        {
            Log.Warning("ShowInlineOverlayForScripted: no TextBox for {Path}", memberVm.Path);
            return;
        }

        var suggestions = vm.GetSuggestionsForMember(memberVm, typed);
        ShowInlineOverlay(tb, memberVm, suggestions);
        // Also surface the rule-hint / validation-error popup so scripted
        // frames reflect the same UI a user sees when focusing the cell.
        ShowInlineHintOverlay(tb, memberVm);
    }

    /// <summary>Scripted-only: tears down the inline overlay.</summary>
    internal void HideInlineOverlayScripted() => HideInlineOverlay();

    /// <summary>Scripted-only: tears down the hint/error overlay.</summary>
    internal void HideInlineHintOverlayScripted() => HideInlineHintOverlay();

    /// <summary>
    /// Scripted-only: opens a fake ComboBox dropdown below the real scope
    /// ComboBox, populated with the VM's AvailableScopes. The real Popup
    /// lives in a separate HWND and cannot be captured by RenderTargetBitmap,
    /// so we draw our own overlay inside the main visual tree.
    /// If <paramref name="hoverPath"/> matches a scope's AncestorPath that
    /// row is pre-selected so it reads as "mouse is about to click this".
    /// </summary>
    internal void ShowScopeDropdownForScripted(string? hoverPath = null)
    {
        if (DataContext is not BulkChangeViewModel vm) return;
        if (vm.AvailableScopes.Count == 0) return;

        // Force a layout pass so the ComboBox has a real ActualWidth/Height.
        UpdateLayout();

        ScopeOverlayList.ItemsSource = vm.AvailableScopes;
        ScopeOverlayList.SelectedItem = null;
        if (hoverPath != null)
        {
            foreach (var s in vm.AvailableScopes)
            {
                if (s.AncestorPath == hoverPath)
                {
                    ScopeOverlayList.SelectedItem = s;
                    break;
                }
            }
        }

        var bottomLeft = ScopeCombo.TranslatePoint(
            new System.Windows.Point(0, ScopeCombo.ActualHeight), (UIElement)Content);
        ScopeOverlay.Width = ScopeCombo.ActualWidth;
        ScopeOverlay.Margin = new Thickness(bottomLeft.X, bottomLeft.Y, 0, 0);
        ScopeOverlay.Visibility = Visibility.Visible;

        // Materialize row containers so cursor-target lookups can resolve them.
        UpdateLayout();
        foreach (var item in ScopeOverlayList.Items)
            ScopeOverlayList.ItemContainerGenerator.ContainerFromItem(item);
    }

    /// <summary>Scripted-only: tears down the scope dropdown overlay.</summary>
    internal void HideScopeDropdownScripted()
    {
        ScopeOverlay.Visibility = Visibility.Collapsed;
        ScopeOverlayList.ItemsSource = null;
        ScopeOverlayList.SelectedItem = null;
    }

    /// <summary>
    /// Finds the per-row undo (↶) Button for the pending-edit entry whose
    /// node has the given path. Walks the PendingEditsList container
    /// generator + the row's visual tree. Returns null if the entry isn't
    /// in the pending queue or hasn't been materialized yet.
    /// </summary>
    private FrameworkElement? FindRevertButton(string nodePath)
    {
        if (DataContext is not BulkChangeViewModel vm) return null;
        UpdateLayout();
        foreach (var item in PendingEditsList.Items)
        {
            if (item is PendingEditEntry entry && entry.Node?.Path == nodePath)
            {
                var container = PendingEditsList.ItemContainerGenerator
                    .ContainerFromItem(item) as FrameworkElement;
                if (container == null) return null;
                // The row template has exactly one Button (the ↶ undo button),
                // so the first Button descendant is the one we want.
                foreach (var btn in FindAllDescendants<System.Windows.Controls.Button>(container))
                    return btn;
            }
        }
        return null;
    }

    /// <summary>
    /// Finds the ListBoxItem in the scripted scope overlay whose bound
    /// ScopeLevel has the given AncestorPath. Returns null if the overlay
    /// is hidden or the path doesn't match any scope.
    /// </summary>
    private ListBoxItem? FindScopeOverlayRow(string ancestorPath)
    {
        if (ScopeOverlay.Visibility != Visibility.Visible) return null;
        if (ScopeOverlayList.ItemsSource == null) return null;
        foreach (var item in ScopeOverlayList.Items)
        {
            if (item is Services.ScopeLevel s && s.AncestorPath == ancestorPath)
                return ScopeOverlayList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
        }
        return null;
    }

    /// <summary>
    /// Scripted-only: paints the cursor overlay at window-relative (x, y).
    /// Coordinates are in DIPs (same space as the viewport config). Hotspot
    /// is the cursor tip at (0, 0) after the translation.
    /// </summary>
    internal void ShowCursorAt(double x, double y)
    {
        Canvas.SetLeft(CursorShape, x);
        Canvas.SetTop(CursorShape, y);
        CursorOverlay.Visibility = Visibility.Visible;
        _lastCursorX = x;
        _lastCursorY = y;
        _hasCursorPoint = true;
    }

    /// <summary>Scripted-only: hides the cursor overlay (and the click ring).</summary>
    internal void HideCursor()
    {
        CursorOverlay.Visibility = Visibility.Collapsed;
        HideClickRing();
    }

    /// <summary>
    /// Scripted-only: paints a click ring centered on the cursor's last known
    /// position. Two phases produce a natural "press → release" feel:
    ///   "press"   — inner tight ring (28 DIP, high opacity). Place on a
    ///               frame where the click target is still visible (dropdown
    ///               open, row highlighted) — this is the "finger goes down"
    ///               beat, BEFORE the action commits.
    ///   "release" — outer expanded ring (56 DIP, lower opacity). Place on
    ///               the action frame itself — this is the "finger lifts,
    ///               UI reacts" beat.
    /// No-op if no cursor has been shown yet in the current session.
    /// </summary>
    internal void ShowClickRingAtLastCursor(string phase)
    {
        if (!_hasCursorPoint) return;
        double size = phase == "press" ? 28 : 56;
        double opacity = phase == "press" ? 0.9 : 0.55;
        ClickRing.Width = size;
        ClickRing.Height = size;
        ClickRing.Opacity = opacity;
        Canvas.SetLeft(ClickRing, _lastCursorX - size * 0.5);
        Canvas.SetTop(ClickRing, _lastCursorY - size * 0.5);
        ClickRing.Visibility = Visibility.Visible;
        // Also reveal the overlay so the ring renders even if the arrow
        // isn't painted on this frame.
        CursorOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>Scripted-only: hides the click ring (arrow stays as-is).</summary>
    internal void HideClickRing() => ClickRing.Visibility = Visibility.Collapsed;

    private double _lastCursorX;
    private double _lastCursorY;
    private bool _hasCursorPoint;

    /// <summary>
    /// Scripted-only: resolves a cursor target name to window-relative
    /// DIPs. Returns null if the target element isn't in the visual tree
    /// yet (e.g. overlay not open). The cursor shape's hotspot is its
    /// upper-left tip, so the returned point is the tip position — not
    /// the element center. ListBoxItem containers extend beyond the
    /// visible text column (scrollbar track, trailing Comment slot),
    /// so we anchor the tip with a fixed DIP inset from the element's
    /// left edge rather than a width ratio.
    /// </summary>
    internal System.Windows.Point? ResolveCursorTarget(string target)
    {
        FrameworkElement? element;
        double xInset = 0;  // DIPs from element's left edge
        if (target == "search")
        {
            element = SearchBox;
            xInset = SearchBox.ActualWidth * 0.5;   // search box: center
        }
        else if (target == "apply")
        {
            element = ApplyButton;
            xInset = ApplyButton.ActualWidth * 0.5; // button: center
        }
        else if (target == "set")
        {
            element = SetButton;
            xInset = SetButton.ActualWidth * 0.5;
        }
        else if (target == "newValue")
        {
            element = BulkNewValueBox;
            xInset = BulkNewValueBox.ActualWidth * 0.5;
        }
        else if (target == "scope")
        {
            element = ScopeCombo;
            // Point at the dropdown chevron on the right of the ComboBox,
            // so the "about to click to open" read is unambiguous.
            xInset = ScopeCombo.ActualWidth - 12;
        }
        else if (target.StartsWith("scopeItem:"))
        {
            element = FindScopeOverlayRow(target.Substring("scopeItem:".Length));
            xInset = 20;
        }
        else if (target.StartsWith("revertButton:"))
        {
            element = FindRevertButton(target.Substring("revertButton:".Length));
            // The ↶ button is small and right-aligned — aim for its center.
            xInset = element != null ? element.ActualWidth * 0.5 : 0;
        }
        else if (target.StartsWith("suggestion:"))
        {
            element = FindSuggestionRow(target.Substring("suggestion:".Length));
            xInset = 20; // over the DisplayName text, regardless of item width
        }
        else if (target.StartsWith("cell:"))
        {
            element = FindInlineCell(target.Substring("cell:".Length));
            xInset = 20; // over the cell text, not its right-edge dropdown arrow
        }
        else element = null;

        if (element == null) return null;
        // Translate to Content (inside any LayoutTransform) so the returned
        // point is in the same space Canvas.SetLeft/Top on CursorShape expects.
        var tip = element.TranslatePoint(
            new System.Windows.Point(xInset, element.ActualHeight * 0.5),
            (UIElement)Content);
        // Cursor shape data starts at (1,1); subtract so the tip pixel
        // lands exactly on the computed point.
        return new System.Windows.Point(tip.X - 1, tip.Y - 1);
    }

    /// <summary>
    /// Finds the inline-edit TextBox for the given member path. Used to
    /// point the cursor at a cell before any typing has happened.
    /// </summary>
    private FrameworkElement? FindInlineCell(string path)
    {
        if (DataContext is not BulkChangeViewModel vm) return null;
        var node = FindFlatByPath(vm, path);
        if (node == null) return null;
        MemberListView.ScrollIntoView(node);
        UpdateLayout();
        var item = MemberListView.ItemContainerGenerator.ContainerFromItem(node)
                   as ListViewItem;
        if (item == null) return null;
        return FindDescendant<TextBox>(item, "InlineStartValue");
    }

    private static MemberNodeViewModel? FindFlatByPath(BulkChangeViewModel vm, string path)
    {
        foreach (var m in vm.FlatMembers)
            if (m.Path == path) return m;
        return null;
    }

    /// <summary>
    /// Finds the visible ListBoxItem whose bound AutocompleteSuggestion
    /// has the given DisplayName. Checks both the inline overlay and the
    /// sidebar suggestion popup.
    /// </summary>
    private ListBoxItem? FindSuggestionRow(string displayName)
    {
        var hit = FindSuggestionRowIn(InlineOverlayList, displayName);
        if (hit != null) return hit;
        // Sidebar popup uses the FilteredSuggestions ListBox; no x:Name,
        // so walk from the dialog root.
        foreach (var lb in FindAllDescendants<ListBox>(this))
        {
            if (lb == InlineOverlayList) continue;
            var cand = FindSuggestionRowIn(lb, displayName);
            if (cand != null) return cand;
        }
        return null;
    }

    private static ListBoxItem? FindSuggestionRowIn(ListBox lb, string displayName)
    {
        if (lb.ItemsSource == null) return null;
        foreach (var item in lb.Items)
        {
            if (item is Services.AutocompleteSuggestion s && s.DisplayName == displayName)
                return lb.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
        }
        return null;
    }

    private static System.Collections.Generic.IEnumerable<T> FindAllDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var sub in FindAllDescendants<T>(child)) yield return sub;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T fe && fe.Name == name) return fe;
            var hit = FindDescendant<T>(child, name);
            if (hit != null) return hit;
        }
        return null;
    }

    private void OnInlineOverlaySuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is AutocompleteSuggestion suggestion
            && lb.Tag is MemberNodeViewModel memberVm)
        {
            memberVm.EditableStartValue = suggestion.DisplayName;
            HideInlineOverlay();
        }
    }

    private void OnStartValueGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
        {
            tb.Background = System.Windows.Media.Brushes.White;
            tb.BorderThickness = new Thickness(1);

            // Auto-select this member in the right panel for autocomplete
            if (tb.DataContext is MemberNodeViewModel memberVm
                && memberVm.IsLeaf
                && DataContext is BulkChangeViewModel vm)
            {
                vm.SelectedFlatMember = memberVm;
                ShowInlineHintOverlay(tb, memberVm);
            }
        }
    }

    private void OnStartValueLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
        {
            tb.Background = System.Windows.Media.Brushes.Transparent;
            tb.BorderThickness = new Thickness(0);

            // Focus moving into the overlay (user clicking a suggestion) must
            // not tear down Tag/ItemsSource — SelectionChanged would then fire
            // with Tag == null and the click would no-op. Let the selection
            // handler hide the overlay itself.
            if (!InlineOverlay.IsKeyboardFocusWithin && !InlineOverlay.IsMouseOver)
                HideInlineOverlay();
            // Same pattern for the hint overlay: if the user is interacting
            // with it (clicking to drag), the TextBox losing focus must not
            // snatch it away mid-gesture.
            if (!InlineHintOverlay.IsMouseOver && !_draggingHint)
                HideInlineHintOverlay();
        }
    }

    /// <summary>
    /// Positions the dialog-root <c>InlineHintOverlay</c> to the right of the
    /// given TextBox so the rule hint / validation error stays visible while
    /// the cell is focused. Hidden when the member has neither a hint nor an
    /// error — an empty popup would just be visual noise.
    /// </summary>
    private void ShowInlineHintOverlay(TextBox tb, MemberNodeViewModel memberVm)
    {
        _inlineHintAnchor = tb;
        InlineHintOverlay.DataContext = memberVm;
        UpdateInlineHintVisibility(memberVm);
        // Focus can fire before the selected row has had its final layout pass
        // (e.g. when clicking an unrealized virtualized row), so a direct
        // TranslatePoint would land on stale coords. Defer until WPF finishes
        // laying out — Render priority runs after Arrange.
        Dispatcher.BeginInvoke(new Action(() => PositionInlineHintOverlay(tb)),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void UpdateInlineHintVisibility(MemberNodeViewModel memberVm)
    {
        bool hasContent = memberVm.HasInlineError || memberVm.HasRuleHint;
        InlineHintOverlay.Visibility = hasContent ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PositionInlineHintOverlay(TextBox tb)
    {
        if (InlineHintOverlay.Visibility != Visibility.Visible) return;
        if (!tb.IsDescendantOf(this))
        {
            // Anchor was virtualized / detached while the overlay was open —
            // drop references rather than computing coords against a ghost.
            _inlineHintAnchor = null;
            return;
        }
        // Right of the TextBox so the autocomplete overlay below the cell stays clear.
        // Translate to Content (inside any LayoutTransform) so the margin isn't double-scaled.
        var topRight = tb.TranslatePoint(new System.Windows.Point(tb.ActualWidth, 0), (UIElement)Content);
        InlineHintOverlay.Margin = new Thickness(topRight.X + 8, topRight.Y, 0, 0);
    }

    private void HideInlineHintOverlay()
    {
        InlineHintOverlay.Visibility = Visibility.Collapsed;
        InlineHintOverlay.DataContext = null;
        _inlineHintAnchor = null;
        // Cancel any in-flight drag whose mouse-up we'd otherwise miss.
        if (_draggingHint)
        {
            _draggingHint = false;
            InlineHintOverlay.ReleaseMouseCapture();
        }
    }

    private TextBox? _inlineHintAnchor;

    private bool _draggingHint;
    private System.Windows.Point _hintDragStart;
    private System.Windows.Point _hintDragOrigin;

    /// <summary>
    /// Starts a drag-to-reposition gesture on the hint overlay. The user can
    /// pull it out of the way when it covers content they want to see. The
    /// next focus change re-anchors it next to the newly focused cell, so
    /// the moved position is intentionally session-scoped.
    /// </summary>
    private void OnInlineHintMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        // Measure the drag in Content coords so pixel deltas stay consistent
        // with Margin at any zoom level.
        _hintDragStart = e.GetPosition((UIElement)Content);
        _hintDragOrigin = new System.Windows.Point(
            InlineHintOverlay.Margin.Left, InlineHintOverlay.Margin.Top);
        _draggingHint = true;
        fe.CaptureMouse();
        e.Handled = true;
    }

    private void OnInlineHintMouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingHint) return;
        var pos = e.GetPosition((UIElement)Content);
        InlineHintOverlay.Margin = new Thickness(
            _hintDragOrigin.X + (pos.X - _hintDragStart.X),
            _hintDragOrigin.Y + (pos.Y - _hintDragStart.Y),
            0, 0);
    }

    private void OnInlineHintMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingHint) return;
        _draggingHint = false;
        if (sender is FrameworkElement fe) fe.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnListViewContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListView lv || DataContext is not BulkChangeViewModel vm)
        {
            e.Handled = true;
            return;
        }

        var memberVm = lv.SelectedItem as MemberNodeViewModel;
        if (memberVm == null || !memberVm.HasChildren)
        {
            e.Handled = true;
            return;
        }

        var menu = lv.ContextMenu;
        menu.Items.Clear();

        var expandItem = new MenuItem { Header = Res.Get("Context_ExpandAllChildren") };
        expandItem.Click += (_, _) => vm.ExpandAllChildren(memberVm);
        menu.Items.Add(expandItem);

        var collapseItem = new MenuItem { Header = Res.Get("Context_CollapseAllChildren") };
        collapseItem.Click += (_, _) => vm.CollapseAllChildren(memberVm);
        menu.Items.Add(collapseItem);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is not BulkChangeViewModel vm) return;

        // Unsaved pending edits (yellow) — ask user before closing
        if (vm.PendingInlineEditCount > 0)
        {
            var result = MessageBox.Show(
                Res.Format("Dialog_UnsavedChanges_Prompt", vm.PendingInlineEditCount),
                Res.Get("Dialog_UnsavedChanges_Title"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            switch (result)
            {
                case MessageBoxResult.Yes:
                    vm.ApplyCommand.Execute(null);
                    // Apply may have bailed out (e.g. user declined the compile prompt on an
                    // inconsistent block). Pending edits are preserved in that case — keep
                    // the dialog open so the user can retry or explicitly Discard.
                    if (vm.PendingInlineEditCount > 0)
                    {
                        e.Cancel = true;
                        return;
                    }
                    break;
                case MessageBoxResult.No:
                    // Discard pending edits. Use the silent variant — the user
                    // already confirmed via the Yes/No/Cancel dialog above, a
                    // second "are you sure?" is noise.
                    vm.DiscardPendingSilent();
                    break;
                case MessageBoxResult.Cancel:
                    e.Cancel = true;
                    return;
            }
        }

        // Commit applied changes to TIA Portal. If the user cancels the compile prompt,
        // abort the close so the staged XML isn't silently thrown away.
        if (vm.HasPendingChanges && !vm.CommitChanges())
        {
            e.Cancel = true;
            return;
        }
    }

    // --- DB-switcher dropdown (#59) ---

    /// <summary>
    /// ToggleButton click on the combo. Opens the popup via the VM command
    /// (which lazy-loads + caches the DB list on first open).
    /// </summary>
    private void OnDbSwitcherButtonClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BulkChangeViewModel vm) return;
        if (vm.IsDataBlocksDropdownOpen)
        {
            vm.IsDataBlocksDropdownOpen = false;
            return;
        }
        if (vm.OpenDataBlocksDropdownCommand.CanExecute(null))
            vm.OpenDataBlocksDropdownCommand.Execute(null);
    }

    /// <summary>Focus the search box and clear it whenever the popup opens.</summary>
    private void OnDbSwitcherPopupOpened(object? sender, EventArgs e)
    {
        if (DataContext is BulkChangeViewModel vm)
            vm.DataBlockSearchText = "";
        DbSwitcherSearchBox.Focus();
        Keyboard.Focus(DbSwitcherSearchBox);
    }

    /// <summary>
    /// Search-box keys: ↓ jumps to the list, Enter accepts the first match,
    /// Esc closes the popup.
    /// </summary>
    private void OnDbSwitcherSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not BulkChangeViewModel vm) return;

        switch (e.Key)
        {
            case Key.Escape:
                vm.IsDataBlocksDropdownOpen = false;
                e.Handled = true;
                break;
            case Key.Down:
                if (DbSwitcherList.Items.Count > 0)
                {
                    DbSwitcherList.SelectedIndex = 0;
                    var first = (ListBoxItem?)DbSwitcherList.ItemContainerGenerator.ContainerFromIndex(0);
                    first?.Focus();
                    e.Handled = true;
                }
                break;
            case Key.Enter:
                if (DbSwitcherList.Items.Count > 0
                    && DbSwitcherList.Items[0] is Models.DataBlockSummary first)
                {
                    vm.SwitchToDataBlock(first);
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>List keys: Esc closes; Enter switches to the highlighted DB.</summary>
    private void OnDbSwitcherListKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not BulkChangeViewModel vm) return;

        if (e.Key == Key.Escape)
        {
            vm.IsDataBlocksDropdownOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && DbSwitcherList.SelectedItem is Models.DataBlockSummary picked)
        {
            vm.SwitchToDataBlock(picked);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Mouse selection on the list switches to the picked DB. Resets the
    /// SelectedItem afterwards so the same row can be re-clicked later.
    /// </summary>
    private void OnDbSwitcherSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not BulkChangeViewModel vm) return;
        if (sender is not ListBox lb) return;
        if (lb.SelectedItem is not Models.DataBlockSummary picked) return;

        // Defer so the popup can absorb the StaysOpen=False close gracefully.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            vm.SwitchToDataBlock(picked);
            lb.SelectedItem = null;
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
}
