using System.Diagnostics.CodeAnalysis;
using BlockParam.Services.Storage;

namespace BlockParam.Services;

/// <summary>
/// Tiny helper that answers the two questions <see cref="UI.BulkChangeViewModel"/>
/// asks the tag-table cache directory: "does it exist?" and "how fresh is the
/// newest <c>*.xml</c> file in it?".
///
/// Exists so the ViewModel doesn't reach for <c>System.IO.Directory</c> /
/// <c>File.GetLastWriteTime</c> directly, keeping the "no new file I/O
/// outside a storage layer" guardrail (#85) honest while not bloating the
/// already-large ViewModel with another seam.
/// </summary>
public class TagTableDirectoryProbe
{
    private readonly IBlockParamStorage _storage;

    public static TagTableDirectoryProbe Default { get; } =
        new(FileSystemBlockParamStorage.Instance);

    public TagTableDirectoryProbe(IBlockParamStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public bool Exists([NotNullWhen(true)] string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return false;
        return _storage.DirectoryExists(StoragePath.FromAbsolute(directoryPath!));
    }

    /// <summary>
    /// Returns the most recent <c>LastWriteTime</c> across all <c>*.xml</c>
    /// files directly under <paramref name="directoryPath"/>, or null when
    /// the directory is missing / empty. Callers format the age string —
    /// keeping the policy in one place (the ViewModel) instead of having
    /// the probe second-guess what "just now" / "5m ago" should look like.
    /// </summary>
    public DateTime? GetNewestXmlWriteTime(string? directoryPath)
    {
        if (!Exists(directoryPath)) return null;
        var dir = StoragePath.FromAbsolute(directoryPath!);

        DateTime? newest = null;
        foreach (var f in _storage.EnumerateFiles(dir, "*.xml"))
        {
            var t = _storage.GetLastWriteTime(f);
            if (newest == null || t > newest) newest = t;
        }
        return newest;
    }
}
