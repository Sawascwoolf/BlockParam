using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BlockParam.Diagnostics;

namespace BlockParam.Services.Storage;

/// <summary>
/// Default production implementation of <see cref="IBlockParamStorage"/> that
/// delegates to <see cref="File"/> and <see cref="Directory"/>. Adds two
/// things on top of the BCL calls: auto-creating parent directories for
/// writes, and treating missing directories as empty in the enumeration APIs.
///
/// Exception policy is BCL-default: I/O failures bubble up so callers can
/// decide whether to log-and-swallow (UI persistence, schedule files) or
/// surface to the user (license, config). The old per-caller try/catch
/// blocks lived at the call site for that reason — see e.g.
/// <c>UiZoomService.Save</c> — and stay there after migration.
/// </summary>
public sealed class FileSystemBlockParamStorage : IBlockParamStorage
{
    public static readonly FileSystemBlockParamStorage Instance = new();

    public bool FileExists(StoragePath path) => File.Exists(path.FullPath);
    public bool DirectoryExists(StoragePath path) => Directory.Exists(path.FullPath);

    public string ReadAllText(StoragePath path) => File.ReadAllText(path.FullPath);
    public byte[] ReadAllBytes(StoragePath path) => File.ReadAllBytes(path.FullPath);
    public Stream OpenRead(StoragePath path) => File.OpenRead(path.FullPath);

    public void WriteAllText(StoragePath path, string contents)
    {
        EnsureParent(path);
        File.WriteAllText(path.FullPath, contents);
    }

    public void WriteAllBytes(StoragePath path, byte[] contents)
    {
        EnsureParent(path);
        File.WriteAllBytes(path.FullPath, contents);
    }

    public void AppendAllText(StoragePath path, string contents)
    {
        EnsureParent(path);
        File.AppendAllText(path.FullPath, contents);
    }

    public void EnsureDirectory(StoragePath path)
    {
        if (!string.IsNullOrEmpty(path.FullPath))
            Directory.CreateDirectory(path.FullPath);
    }

    public void DeleteFile(StoragePath path)
    {
        // File.Delete already no-ops on missing files — no Exists check needed.
        File.Delete(path.FullPath);
    }

    public void DeleteDirectory(StoragePath path)
    {
        if (Directory.Exists(path.FullPath))
            Directory.Delete(path.FullPath);
    }

    public DateTime GetLastWriteTime(StoragePath path) =>
        File.Exists(path.FullPath)
            ? File.GetLastWriteTime(path.FullPath)
            : new DateTime(1601, 1, 1); // FILETIME epoch; matches in-memory contract

    public IEnumerable<StoragePath> EnumerateFiles(
        StoragePath directory, string pattern = "*", bool recursive = false)
    {
        if (!Directory.Exists(directory.FullPath))
            return Array.Empty<StoragePath>();

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(directory.FullPath, pattern, option)
            .Select(p => new StoragePath(p));
    }

    public IEnumerable<StoragePath> EnumerateDirectories(
        StoragePath directory, string pattern = "*", bool recursive = false)
    {
        if (!Directory.Exists(directory.FullPath))
            return Array.Empty<StoragePath>();

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateDirectories(directory.FullPath, pattern, option)
            .Select(p => new StoragePath(p));
    }

    public bool HasAnyEntries(StoragePath directory)
    {
        if (!Directory.Exists(directory.FullPath)) return false;
        // GetFileSystemEntries materialises everything; EnumerateFileSystemEntries
        // returns lazily so we can early-exit on the first hit.
        using var e = Directory.EnumerateFileSystemEntries(directory.FullPath).GetEnumerator();
        return e.MoveNext();
    }

    public void Replace(StoragePath source, StoragePath destination)
    {
        EnsureParent(destination);
        try
        {
            if (File.Exists(destination.FullPath))
            {
                // File.Replace is atomic on NTFS via Win32 ReplaceFile — no gap
                // between deleting the destination and renaming the temp file
                // where another writer could create the destination and make a
                // naive Move throw. .NET 5+ has File.Move(overwrite); we target
                // net48 and don't want P/Invoke MoveFileEx just for this.
                File.Replace(source.FullPath, destination.FullPath,
                    destinationBackupFileName: null);
            }
            else
            {
                File.Move(source.FullPath, destination.FullPath);
            }
        }
        catch (IOException ex)
        {
            // ReplaceFile fails across volumes and on non-NTFS filesystems.
            // Fall back to overwrite-copy — non-atomic but FS-agnostic. Log
            // the fallback once per write: the pre-#85-followup LocalUsageTracker
            // emitted this from its own catch so support could grep the per-day
            // runtime log for "torn write resets counter" tickets and confirm
            // a non-NTFS / network-redirected %APPDATA% is the cause. Logging
            // belongs here now that the catch lives in the storage layer.
            Log.Warning(ex,
                "FileSystemBlockParamStorage.Replace fell back to overwrite-copy " +
                "(cross-volume or non-NTFS): {Source} → {Destination}",
                source.FullPath, destination.FullPath);
            File.Copy(source.FullPath, destination.FullPath, overwrite: true);
            // UnauthorizedAccessException alongside IOException: AV holding an
            // open handle on the temp file can fail the delete while the copy
            // already succeeded. Leaking a .tmp is the price; surfacing the
            // exception to a caller that has no recovery path would crash the
            // user-visible flow (counter save → Apply pipeline). Logged so the
            // leak is at least visible.
            try
            {
                File.Delete(source.FullPath);
            }
            catch (Exception delEx) when (delEx is IOException or UnauthorizedAccessException)
            {
                Log.Warning(delEx,
                    "FileSystemBlockParamStorage.Replace could not remove source temp {Source}; orphaned",
                    source.FullPath);
            }
        }
    }

    private static void EnsureParent(StoragePath path)
    {
        var parent = Path.GetDirectoryName(path.FullPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
    }
}
