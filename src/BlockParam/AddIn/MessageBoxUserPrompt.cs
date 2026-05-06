using BlockParam.Services;

namespace BlockParam.AddIn;

/// <summary>
/// WPF-backed <see cref="IUserPrompt"/>. Uses a hidden topmost owner window so
/// the dialog appears in the foreground above TIA Portal — without that owner,
/// the dialog can pop behind the main TIA window depending on z-order at the
/// moment the prompt fires.
/// </summary>
public sealed class MessageBoxUserPrompt : IUserPrompt
{
    public bool AskYesNo(string title, string message)
    {
        return Show(message, title,
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question)
            == System.Windows.MessageBoxResult.Yes;
    }

    public void ShowError(string title, string message)
    {
        Show(message, title,
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
    }

    private static System.Windows.MessageBoxResult Show(
        string message, string title,
        System.Windows.MessageBoxButton buttons,
        System.Windows.MessageBoxImage icon)
    {
        var owner = new System.Windows.Window
        {
            Width = 0, Height = 0,
            WindowStyle = System.Windows.WindowStyle.None,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = true,
        };
        owner.Show();
        try
        {
            return System.Windows.MessageBox.Show(owner, message, title, buttons, icon);
        }
        finally
        {
            owner.Close();
        }
    }
}
