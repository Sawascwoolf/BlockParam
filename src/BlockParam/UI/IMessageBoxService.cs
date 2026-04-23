namespace BlockParam.UI;

/// <summary>
/// Abstracts message box interactions for testability.
/// The ViewModel uses this instead of calling MessageBox.Show directly.
/// </summary>
public interface IMessageBoxService
{
    bool AskYesNo(string message, string title);
    void ShowError(string message, string title);
}

/// <summary>
/// Default implementation using WPF MessageBox.
/// </summary>
public class WpfMessageBoxService : IMessageBoxService
{
    public bool AskYesNo(string message, string title)
    {
        var result = System.Windows.MessageBox.Show(
            message, title,
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        return result == System.Windows.MessageBoxResult.Yes;
    }

    public void ShowError(string message, string title)
    {
        System.Windows.MessageBox.Show(
            message, title,
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
    }
}
