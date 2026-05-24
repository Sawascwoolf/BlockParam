using System.IO;
using System.Security;
using BlockParam.Diagnostics;
using BlockParam.Services.Storage;

namespace BlockParam.Services;

/// <summary>
/// File-system gateway for the rule files the ConfigEditor surfaces. Owns the
/// "list every <c>*.json</c> under this directory", "exists?" and
/// "best-effort delete" patterns that used to be duplicated in
/// <c>ConfigEditorViewModel</c> and <c>RuleFileViewModel</c>.
///
/// All I/O is routed through <see cref="IBlockParamStorage"/>, satisfying the
/// "no new <c>File.*</c> / <c>Directory.*</c> outside the storage layer"
/// guardrail (#85) and letting tests swap in
/// <see cref="InMemoryBlockParamStorage"/> without touching disk.
/// </summary>
public class RuleFileRepository
{
    private readonly IBlockParamStorage _storage;

    public static RuleFileRepository Default { get; } = new(FileSystemBlockParamStorage.Instance);

    public RuleFileRepository(IBlockParamStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public bool DirectoryExists(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return false;
        return _storage.DirectoryExists(StoragePath.FromAbsolute(directoryPath));
    }

    public bool FileExists(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        return _storage.FileExists(StoragePath.FromAbsolute(filePath));
    }

    public string ReadAllText(string filePath) =>
        _storage.ReadAllText(StoragePath.FromAbsolute(filePath));

    /// <summary>
    /// Returns the absolute paths of every <c>*.json</c> file directly under
    /// <paramref name="directoryPath"/>, sorted case-insensitively. Returns an
    /// empty array (never throws) when the directory is missing or unreadable,
    /// mirroring the legacy ConfigEditorViewModel.LoadFilesFromDirectory
    /// behaviour the user already relies on.
    /// </summary>
    public string[] ListJsonFiles(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return Array.Empty<string>();
        var dir = StoragePath.FromAbsolute(directoryPath);
        if (!_storage.DirectoryExists(dir)) return Array.Empty<string>();

        try
        {
            var files = _storage.EnumerateFiles(dir, "*.json")
                .Select(p => p.FullPath)
                .ToArray();
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return files;
        }
        // SecurityException is intentional alongside IO/UnauthorizedAccess:
        // under TIA's partial-trust Add-In Loader sandbox, EnumerateFiles can
        // throw SecurityException on paths the host process denies — the old
        // ConfigEditorViewModel.ClaimsFor used a bare catch precisely so the
        // save flow could fall through with an empty claim seed and let
        // SaveRuleFile surface a clearer error later. PEVerify + the
        // PartialTrustSandboxTests defend against the IL pattern; this catch
        // defends against the runtime exception path that pattern produces.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Log.Warning(ex, "Cannot access rules directory: {Path}", directoryPath);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Returns just the file names (not absolute paths) of every <c>*.json</c>
    /// directly under <paramref name="directoryPath"/>. Missing/unreadable
    /// directories return empty — used by the ConfigEditor to seed
    /// per-target-dir filename-claim sets.
    /// </summary>
    public IEnumerable<string> ListJsonFileNames(string directoryPath)
    {
        foreach (var p in ListJsonFiles(directoryPath))
            yield return Path.GetFileName(p);
    }

    /// <summary>
    /// Deletes a file. Throws on permission/lock errors so the caller can
    /// surface a meaningful error (the ConfigEditor uses this to populate
    /// <c>ValidationMessage</c>); silently returns when the path is empty.
    /// </summary>
    public void DeleteFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        _storage.DeleteFile(StoragePath.FromAbsolute(filePath));
    }
}
