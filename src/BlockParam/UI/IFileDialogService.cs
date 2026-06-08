using Microsoft.Win32;

namespace BlockParam.UI;

/// <summary>
/// Thin seam over the Win32 open/save file dialogs so the ConfigEditor's
/// import/export commands (#36) can be unit-tested without a real shell dialog.
/// Production uses <see cref="Win32FileDialogService"/>; tests inject a fake.
/// </summary>
public interface IFileDialogService
{
    /// <summary>
    /// Shows an open-file dialog. Returns the selected absolute paths, or an
    /// empty array when the user cancels.
    /// </summary>
    string[] OpenFiles(string title, string filter, bool multiselect);

    /// <summary>
    /// Shows a save-file dialog seeded with <paramref name="suggestedFileName"/>.
    /// Returns the chosen absolute path, or null when the user cancels.
    /// </summary>
    string? SaveFile(string title, string filter, string suggestedFileName);
}

/// <summary>
/// Default <see cref="IFileDialogService"/> backed by the WPF
/// <see cref="Microsoft.Win32.OpenFileDialog"/> / <see cref="SaveFileDialog"/>.
/// </summary>
public sealed class Win32FileDialogService : IFileDialogService
{
    public static readonly Win32FileDialogService Instance = new();

    public string[] OpenFiles(string title, string filter, bool multiselect)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = multiselect,
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : Array.Empty<string>();
    }

    public string? SaveFile(string title, string filter, string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = suggestedFileName,
            AddExtension = true,
            DefaultExt = ".json",
            OverwritePrompt = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
