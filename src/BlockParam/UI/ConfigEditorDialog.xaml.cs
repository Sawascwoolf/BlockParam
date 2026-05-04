using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;

namespace BlockParam.UI;

public partial class ConfigEditorDialog : Window
{
    /// <summary>
    /// Tracks each visible file section's chrome — used by the rolling-sticky
    /// scroll handler to translate the header straddling the viewport top.
    /// </summary>
    private readonly List<FileSectionVisuals> _fileSections = new();

    public ConfigEditorDialog()
    {
        InitializeComponent();
        WindowIconHelper.SetIcon(this);
        ZoomHost.Attach(this);
    }

    public ConfigEditorDialog(ConfigEditorViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.RequestClose += () => Close();

        Loaded += (_, _) =>
        {
            Topmost = true;
            Activate();
            Topmost = false;
        };
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnBrowseSharedDirectory(object sender, RoutedEventArgs e)
    {
        var vm = DataContext as ConfigEditorViewModel;
        if (vm == null) return;

        using var dialog = new FolderBrowserDialog
        {
            Description = "Select shared rules directory",
            ShowNewFolderButton = true
        };

        if (!string.IsNullOrWhiteSpace(vm.SharedRulesDirectory))
            dialog.SelectedPath = vm.SharedRulesDirectory;

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            vm.SharedRulesDirectory = dialog.SelectedPath;
        }
    }

    /// <summary>
    /// Each file section registers itself here when its container Grid loads.
    /// Once registered we can find the header Border + the section's overall
    /// bounds for the rolling-sticky calculation.
    /// </summary>
    private void OnFileSectionLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid root) return;
        if (root.Tag is not RuleFileViewModel file) return;

        var header = FindDescendantByName(root, "FileHeader") as Border;
        if (header == null) return;

        // The inline <TranslateTransform/> declared in the DataTemplate gets
        // auto-frozen by BAML (no bindings → shareable). A frozen Freezable
        // throws InvalidOperationException on any property set, killing
        // UpdateRollingSticky on every scroll tick. Swap in a per-instance
        // mutable transform so the sticky math can mutate Y.
        header.RenderTransform = new TranslateTransform();

        // Replace any existing entry for this file (template recycling).
        _fileSections.RemoveAll(fs => ReferenceEquals(fs.Root, root));
        _fileSections.Add(new FileSectionVisuals(file, root, header));

        // Trigger one sticky pass so the just-added section participates immediately.
        Dispatcher.BeginInvoke(new Action(UpdateRollingSticky),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Removes the section from the tracking list when its container leaves
    /// the visual tree (filter refresh, save reload, dialog close). Without
    /// this, _fileSections grows unboundedly across edit cycles.
    /// </summary>
    private void OnFileSectionUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid root) return;
        _fileSections.RemoveAll(fs => ReferenceEquals(fs.Root, root));
    }

    private void OnFileHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not Border header) return;
        if (header.DataContext is not RuleFileViewModel file) return;

        // Toggle expand/collapse and select the file (without changing rule selection
        // unless none was set yet).
        file.IsExpanded = !file.IsExpanded;

        if (DataContext is ConfigEditorViewModel vm)
        {
            vm.SelectedFile = file;
            // If no rule is selected or the selected rule belongs to another file,
            // adopt the first rule of this file so the detail panel has something
            // to show. Otherwise leave the existing rule selection alone.
            if (vm.SelectedRule == null || vm.SelectedRule.ParentFile != file)
                vm.SelectedRule = file.Rules.FirstOrDefault();
        }

        Dispatcher.BeginInvoke(new Action(UpdateRollingSticky),
            System.Windows.Threading.DispatcherPriority.Loaded);
        e.Handled = true;
    }

    private void OnRuleRowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not Border row) return;
        if (row.Tag is not RuleViewModel rule) return;

        if (DataContext is ConfigEditorViewModel vm)
            vm.SelectedRule = rule;

        e.Handled = true;
    }

    /// <summary>
    /// The right-side controls in the file header (source pills, overflow)
    /// must not bubble their clicks up to the header — that would toggle
    /// expansion every time the user changed the source.
    /// </summary>
    private void OnHeaderControlsMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void OnHeaderControlsMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void OnFileOverflowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button) return;
        if (button.Tag is not RuleFileViewModel file) return;
        if (DataContext is not ConfigEditorViewModel vm) return;

        var menu = new ContextMenu { PlacementTarget = button };

        var deleteItem = new MenuItem
        {
            Header = BlockParam.Localization.Res.Get("ConfigEditor_DeleteFile")
        };
        deleteItem.Click += (_, _) => vm.DeleteFileCommand.Execute(file);
        menu.Items.Add(deleteItem);

        var exportItem = new MenuItem
        {
            Header = BlockParam.Localization.Res.Get("ConfigEditor_ExportFile")
        };
        exportItem.Click += (_, _) => vm.ExportFileCommand.Execute(file);
        menu.Items.Add(exportItem);

        if (file.HasOverrides)
        {
            menu.Items.Add(new Separator());
            var resetItem = new MenuItem
            {
                Header = BlockParam.Localization.Res.Get("ConfigEditor_ResetToBase")
            };
            resetItem.Click += (_, _) =>
            {
                vm.SelectedFile = file;
                vm.ResetToBaseCommand.Execute(null);
            };
            menu.Items.Add(resetItem);
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// Rolling sticky: only one file header is pinned to the viewport top at
    /// a time — whichever section is currently being scrolled through. As the
    /// user crosses into the next section, that section's header takes over.
    /// </summary>
    private void OnFileListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateRollingSticky();
    }

    private void UpdateRollingSticky()
    {
        if (FileListScroll == null || _fileSections.Count == 0) return;

        // Reset transforms and Z-index on all sections. We re-apply only on
        // the active sticky below.
        foreach (var fs in _fileSections)
        {
            if (fs.Header.RenderTransform is TranslateTransform existing)
                existing.Y = 0;
            SetContainerZIndex(fs.Root, 0);
        }

        var viewportTop = 0.0; // relative to the ScrollViewer's content area
        FileSectionVisuals? activeSticky = null;

        foreach (var fs in _fileSections)
        {
            if (!IsRegistered(fs.Root)) continue;

            var rootTopInScroll = TopWithinScrollViewer(fs.Root);
            var rootHeight = fs.Root.ActualHeight;
            var rootBottom = rootTopInScroll + rootHeight;

            // Active sticky = the section straddling the viewport top.
            // Once the body fully passes above the viewport top, the next
            // section takes over. Single-section sticky (#70) — no stacking.
            if (rootTopInScroll <= viewportTop && rootBottom > viewportTop)
            {
                activeSticky = fs;
            }
        }

        if (activeSticky != null)
        {
            var rootTop = TopWithinScrollViewer(activeSticky.Root);
            var rootBottom = rootTop + activeSticky.Root.ActualHeight;
            var headerH = activeSticky.Header.ActualHeight;

            // Pin: translate the header down to the viewport top.
            var translate = -rootTop;

            // Trailing-edge ride-out: as the section's body bottom approaches
            // the viewport top, push the header upward so it exits smoothly
            // instead of un-pinning abruptly on the next scroll tick. This
            // single metric also handles the incoming-next-section case for
            // adjacent sections (no gap), since rootBottom == nextTop there.
            var distToBottom = rootBottom - viewportTop;
            if (distToBottom < headerH)
                translate -= (headerH - distToBottom);

            if (activeSticky.Header.RenderTransform is TranslateTransform t)
                t.Y = translate;

            // The pinned header lives inside the active section's container.
            // Without bumping the container's Z-index above its siblings,
            // later items in the ItemsControl (drawn in document order) would
            // paint OVER the translated header, clipping it. Setting Panel.ZIndex
            // on the ContentPresenter lifts the whole section to the front.
            SetContainerZIndex(activeSticky.Root, 100);
        }
    }

    /// <summary>
    /// Sets <see cref="Panel.ZIndex"/> on the ItemsControl's container for
    /// <paramref name="root"/> (a ContentPresenter wrapping the data template).
    /// </summary>
    private static void SetContainerZIndex(FrameworkElement root, int z)
    {
        if (VisualTreeHelper.GetParent(root) is FrameworkElement container)
            Panel.SetZIndex(container, z);
    }

    /// <summary>
    /// Computes the top of <paramref name="el"/> relative to the FileListScroll
    /// content area, accounting for the current scroll offset. (Transforming
    /// to the ScrollViewer itself includes the scroll offset; we add it back
    /// out by transforming relative to the ItemsControl.)
    /// </summary>
    private double TopWithinScrollViewer(FrameworkElement el)
    {
        if (FileListScroll?.Content is not FrameworkElement content)
            return 0;
        var p = el.TransformToVisual(content).Transform(new Point(0, 0));
        return p.Y - FileListScroll.VerticalOffset;
    }

    private static bool IsRegistered(FrameworkElement el)
    {
        return PresentationSource.FromVisual(el) != null;
    }

    private static DependencyObject? FindDescendantByName(DependencyObject parent, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && fe.Name == name)
                return child;

            var found = FindDescendantByName(child, name);
            if (found != null) return found;
        }
        return null;
    }

    private record FileSectionVisuals(RuleFileViewModel File, Grid Root, Border Header);
}
