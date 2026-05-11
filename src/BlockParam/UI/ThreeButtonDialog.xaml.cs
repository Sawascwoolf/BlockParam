using System.Linq;
using System.Windows;

namespace BlockParam.UI;

/// <summary>
/// A small modal dialog with three named buttons (primary / secondary / cancel).
/// Used by <see cref="WpfMessageBoxService"/> wherever the conventional Yes/No/Cancel
/// labels would be misleading because each choice maps to a distinct, non-obvious action.
/// </summary>
public partial class ThreeButtonDialog : Window
{
    public enum ButtonChoice { Primary, Secondary, Cancel }

    public ButtonChoice Choice { get; private set; } = ButtonChoice.Cancel;

    public ThreeButtonDialog(
        string message,
        string title,
        string primaryLabel,
        string secondaryLabel,
        string cancelLabel)
    {
        InitializeComponent();
        WindowIconHelper.SetIcon(this);
        ZoomHost.Attach(this);

        Title = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryLabel;
        SecondaryButton.Content = secondaryLabel;
        CancelButton.Content = cancelLabel;

        Owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive);
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        Choice = ButtonChoice.Primary;
        DialogResult = true;
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        Choice = ButtonChoice.Secondary;
        DialogResult = false;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Choice = ButtonChoice.Cancel;
        DialogResult = null;
    }
}
