namespace BlockParam.UI;

/// <summary>
/// Abstracts message box interactions for testability.
/// The ViewModel uses this instead of calling MessageBox.Show directly.
/// </summary>
public interface IMessageBoxService
{
    bool AskYesNo(string message, string title);
    void ShowError(string message, string title);
    void ShowInfo(string message, string title);

    /// <summary>
    /// 3-way prompt used when an action would lose pending work and the user
    /// has three meaningful choices: commit (Yes), keep-but-don't-commit (No),
    /// or back out entirely (Cancel). Used by the DB-switcher (#59) to ask
    /// "apply staged edits to TIA before switching?" without forcing a destructive
    /// 2-way collapse of "Apply" and "Keep stashed".
    /// </summary>
    YesNoCancelResult AskYesNoCancel(string message, string title);
}

public enum YesNoCancelResult
{
    Yes,
    No,
    Cancel,
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

    public void ShowInfo(string message, string title)
    {
        System.Windows.MessageBox.Show(
            message, title,
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    public YesNoCancelResult AskYesNoCancel(string message, string title)
    {
        var result = System.Windows.MessageBox.Show(
            message, title,
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Warning);
        return result switch
        {
            System.Windows.MessageBoxResult.Yes => YesNoCancelResult.Yes,
            System.Windows.MessageBoxResult.No => YesNoCancelResult.No,
            _ => YesNoCancelResult.Cancel,
        };
    }
}
