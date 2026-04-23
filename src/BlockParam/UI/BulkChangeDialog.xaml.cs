using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlockParam.Localization;
using BlockParam.Services;

namespace BlockParam.UI;

public partial class BulkChangeDialog : Window
{
    public BulkChangeDialog()
    {
        InitializeComponent();
        WindowIconHelper.SetIcon(this);
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
        Serilog.Log.Debug(
            "LV.SelectionChanged: suppressed={Sup} refreshing={Ref} Added=[{Add}] Removed=[{Rem}] SelectedItems.Count={Count}",
            _suppressSelectionEvents, refreshing,
            string.Join(",", addedList.Select(m => m.Name)),
            string.Join(",", removedList.Select(m => m.Name)),
            MemberListView.SelectedItems.Count);

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
            Serilog.Log.Debug("LV.SelectionChanged: dropping {N} non-leaf items", parentsToDrop.Count);
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
        if (realRemoved.Count != removedList.Count)
        {
            Serilog.Log.Debug("LV.SelectionChanged: dropped {N} ghost removals still in SelectedItems",
                removedList.Count - realRemoved.Count);
        }

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
        Serilog.Log.Debug(
            "Rehydrate ENTER: paths=[{Paths}] currentSelected=[{Sel}] items.Count={Items}",
            string.Join(",", vm.ManualSelectedPaths),
            string.Join(",", MemberListView.SelectedItems.OfType<MemberNodeViewModel>().Select(m => m.Name)),
            MemberListView.Items.Count);

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
            int restored = 0;
            foreach (var m in MemberListView.Items.OfType<MemberNodeViewModel>())
            {
                if (vm.ManualSelectedPaths.Contains(m.Path))
                {
                    MemberListView.SelectedItems.Add(m);
                    restored++;
                }
            }
            Serilog.Log.Debug("Rehydrate: restored {R}/{Total} selections",
                restored, vm.ManualSelectedPaths.Count);
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

        // Compute TextBox bottom-left in dialog coords and position the overlay.
        var bottomLeft = tb.TranslatePoint(new System.Windows.Point(0, tb.ActualHeight), this);
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
    /// inline-autocomplete overlay for the given member path with a typed
    /// prefix, without going through the interactive focus / TextChanged
    /// chain. Finds the cell's TextBox via the ListView's container generator
    /// and positions the dialog-root overlay over it.
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
            Serilog.Log.Logger.Warning("ShowInlineOverlayForScripted: no container for {Path}", memberVm.Path);
            return;
        }

        var tb = FindDescendant<TextBox>(item, "InlineStartValue");
        if (tb == null)
        {
            Serilog.Log.Logger.Warning("ShowInlineOverlayForScripted: no TextBox for {Path}", memberVm.Path);
            return;
        }

        var suggestions = vm.GetSuggestionsForMember(memberVm, typed);
        ShowInlineOverlay(tb, memberVm, suggestions);
    }

    /// <summary>Scripted-only: tears down the inline overlay.</summary>
    internal void HideInlineOverlayScripted() => HideInlineOverlay();

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
    }

    /// <summary>Scripted-only: hides the cursor overlay.</summary>
    internal void HideCursor() => CursorOverlay.Visibility = Visibility.Collapsed;

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
        var tip = element.TranslatePoint(
            new System.Windows.Point(xInset, element.ActualHeight * 0.5),
            this);
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
        }
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
                $"Do you want to save {vm.PendingInlineEditCount} pending change(s) before closing?",
                "Unsaved Changes",
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
}
