using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;

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

        // Replace any existing entry for this file (template recycling).
        _fileSections.RemoveAll(fs => ReferenceEquals(fs.Root, root));
        _fileSections.Add(new FileSectionVisuals(file, root, header));

        // Trigger one sticky pass so the just-added section participates immediately.
        Dispatcher.BeginInvoke(new Action(UpdateRollingSticky),
            System.Windows.Threading.DispatcherPriority.Loaded);
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

        // Reset all transforms first.
        foreach (var fs in _fileSections)
        {
            if (fs.Header.RenderTransform is TranslateTransform existing)
                existing.Y = 0;
        }

        var viewportTop = 0.0; // relative to the ScrollViewer's content area
        FileSectionVisuals? activeSticky = null;
        FileSectionVisuals? nextSection = null;

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
            else if (rootTopInScroll > viewportTop && nextSection == null)
            {
                nextSection = fs;
            }
        }

        if (activeSticky != null)
        {
            var rootTop = TopWithinScrollViewer(activeSticky.Root);
            // Pin: translate header down to viewport top.
            var translate = -rootTop;

            // Push it back out as the next section approaches, so the swap is smooth.
            if (nextSection != null)
            {
                var nextTop = TopWithinScrollViewer(nextSection.Root);
                var headerH = activeSticky.Header.ActualHeight;
                var distance = nextTop - viewportTop;
                if (distance < headerH)
                    translate -= (headerH - distance);
            }

            if (activeSticky.Header.RenderTransform is TranslateTransform t)
                t.Y = translate;
        }
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
