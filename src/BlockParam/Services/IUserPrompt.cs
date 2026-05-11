namespace BlockParam.Services;

/// <summary>
/// Abstracts the WPF message-box UX so addin-side prompts (compile inconsistent
/// block? compile inconsistent UDTs?) can be unit-tested without spinning up WPF.
/// Implementations live alongside the host (<c>MessageBoxUserPrompt</c> for the
/// real addin); tests use NSubstitute stubs.
/// </summary>
public interface IUserPrompt
{
    /// <summary>
    /// Shows a Yes/No question to the user. Returns true on Yes.
    /// </summary>
    bool AskYesNo(string title, string message);

    /// <summary>
    /// Shows an error dialog with an OK button.
    /// </summary>
    void ShowError(string title, string message);
}
