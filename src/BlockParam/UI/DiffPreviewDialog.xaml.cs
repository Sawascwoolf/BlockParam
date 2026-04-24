using System.Windows;

namespace BlockParam.UI;

public partial class DiffPreviewDialog : Window
{
    public DiffPreviewDialog()
    {
        InitializeComponent();
        WindowIconHelper.SetIcon(this);
        ZoomHost.Attach(this);
    }

    public DiffPreviewDialog(DiffPreviewViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.RequestClose += () => Close();
    }
}
