using System.Windows;
using System.Windows.Forms;

namespace BlockParam.UI;

public partial class ConfigEditorDialog : Window
{
    public ConfigEditorDialog()
    {
        InitializeComponent();
        WindowIconHelper.SetIcon(this);
    }

    public ConfigEditorDialog(ConfigEditorViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.RequestClose += () => Close();

        // Briefly set Topmost to appear above TIA Portal, then release
        // so other windows (non-TIA) can go in front.
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
}
