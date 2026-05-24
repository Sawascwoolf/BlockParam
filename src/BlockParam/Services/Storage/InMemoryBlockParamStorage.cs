using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BlockParam.Services.Storage;

/// <summary>
/// In-memory fake of <see cref="IBlockParamStorage"/> for unit tests. Stores
/// files as <c>byte[]</c> keyed by their absolute path, with an explicit set
/// of "known" directories so <see cref="DirectoryExists"/> can return false
/// for paths that have never been created.
///
/// Path comparison is case-insensitive to match Windows (the production
/// target). The fake does not enforce parent-directory existence on writes —
/// it mirrors <see cref="FileSystemBlockParamStorage"/>, which auto-creates.
/// </summary>
public sealed class InMemoryBlockParamStorage : IBlockParamStorage
{
    private readonly Dictionary<string, byte[]> _files =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastWriteTimes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories =
        new(StringComparer.OrdinalIgnoreCase);

    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    public bool FileExists(StoragePath path) => _files.ContainsKey(path.FullPath);

    public bool DirectoryExists(StoragePath path) =>
        _directories.Contains(NormaliseDir(path.FullPath));

    public string ReadAllText(StoragePath path) =>
        Encoding.UTF8.GetString(ReadAllBytes(path));

    public byte[] ReadAllBytes(StoragePath path)
    {
        if (!_files.TryGetValue(path.FullPath, out var bytes))
            throw new FileNotFoundException($"In-memory file not found: {path.FullPath}", path.FullPath);
        return (byte[])bytes.Clone();
    }

    public Stream OpenRead(StoragePath path) => new MemoryStream(ReadAllBytes(path), writable: false);

    public void WriteAllText(StoragePath path, string contents) =>
        WriteAllBytes(path, Encoding.UTF8.GetBytes(contents));

    public void WriteAllBytes(StoragePath path, byte[] contents)
    {
        if (contents is null) throw new ArgumentNullException(nameof(contents));
        EnsureParent(path);
        _files[path.FullPath] = (byte[])contents.Clone();
        _lastWriteTimes[path.FullPath] = Clock();
    }

    public void AppendAllText(StoragePath path, string contents)
    {
        var addition = Encoding.UTF8.GetBytes(contents);
        if (_files.TryGetValue(path.FullPath, out var existing))
        {
            var combined = new byte[existing.Length + addition.Length];
            Buffer.BlockCopy(existing, 0, combined, 0, existing.Length);
            Buffer.BlockCopy(addition, 0, combined, existing.Length, addition.Length);
            WriteAllBytes(path, combined);
        }
        else
        {
            WriteAllBytes(path, addition);
        }
    }

    public void EnsureDirectory(StoragePath path)
    {
        if (string.IsNullOrEmpty(path.FullPath)) return;
        var current = NormaliseDir(path.FullPath);
        // Materialise the full chain of parents so DirectoryExists is consistent.
        while (!string.IsNullOrEmpty(current))
        {
            if (!_directories.Add(current)) break;
            current = NormaliseDir(Path.GetDirectoryName(current) ?? string.Empty);
        }
    }

    public void DeleteFile(StoragePath path)
    {
        _files.Remove(path.FullPath);
        _lastWriteTimes.Remove(path.FullPath);
    }

    public void DeleteDirectory(StoragePath path)
    {
        var dir = NormaliseDir(path.FullPath);
        if (!_directories.Contains(dir)) return;
        if (_files.Keys.Any(f => IsUnder(f, dir)) ||
            _directories.Any(d => !string.Equals(d, dir, StringComparison.OrdinalIgnoreCase) && IsUnder(d, dir)))
        {
            throw new IOException($"Directory is not empty: {dir}");
        }
        _directories.Remove(dir);
    }

    public DateTime GetLastWriteTime(StoragePath path)
    {
        // Mirrors File.GetLastWriteTime which returns 1601-01-01 (the FILETIME
        // epoch) for missing files rather than throwing — keeps the FS and
        // in-memory contracts aligned for TempCacheCleanup-style sweepers
        // that race against external deletes.
        return _lastWriteTimes.TryGetValue(path.FullPath, out var t)
            ? t
            : new DateTime(1601, 1, 1);
    }

    public IEnumerable<StoragePath> EnumerateFiles(
        StoragePath directory, string pattern = "*", bool recursive = false)
    {
        var dir = NormaliseDir(directory.FullPath);
        if (!_directories.Contains(dir)) return Array.Empty<StoragePath>();

        var regex = WildcardToRegex(pattern);
        return _files.Keys
            .Where(f => IsUnder(f, dir, recursive))
            .Where(f => regex.IsMatch(Path.GetFileName(f)))
            .Select(f => new StoragePath(f))
            .ToList();
    }

    public IEnumerable<StoragePath> EnumerateDirectories(
        StoragePath directory, string pattern = "*", bool recursive = false)
    {
        var dir = NormaliseDir(directory.FullPath);
        if (!_directories.Contains(dir)) return Array.Empty<StoragePath>();

        var regex = WildcardToRegex(pattern);
        return _directories
            .Where(d => !string.Equals(d, dir, StringComparison.OrdinalIgnoreCase))
            .Where(d => IsUnder(d, dir, recursive))
            .Where(d => regex.IsMatch(Path.GetFileName(d)))
            .Select(d => new StoragePath(d))
            .ToList();
    }

    public bool HasAnyEntries(StoragePath directory)
    {
        var dir = NormaliseDir(directory.FullPath);
        if (!_directories.Contains(dir)) return false;
        return _files.Keys.Any(f => IsUnder(f, dir))
            || _directories.Any(d => !string.Equals(d, dir, StringComparison.OrdinalIgnoreCase) && IsUnder(d, dir));
    }

    public void Replace(StoragePath source, StoragePath destination)
    {
        if (!_files.TryGetValue(source.FullPath, out var bytes))
            throw new FileNotFoundException($"In-memory file not found: {source.FullPath}", source.FullPath);
        EnsureParent(destination);
        _files[destination.FullPath] = bytes;
        _lastWriteTimes[destination.FullPath] = Clock();
        _files.Remove(source.FullPath);
        _lastWriteTimes.Remove(source.FullPath);
    }

    /// <summary>
    /// Test-only helper: stamps an explicit last-write time on a file so
    /// tests can simulate "aged" files without juggling <see cref="Clock"/>.
    /// </summary>
    public void SetLastWriteTime(StoragePath path, DateTime when)
    {
        if (!_files.ContainsKey(path.FullPath))
            throw new FileNotFoundException($"In-memory file not found: {path.FullPath}", path.FullPath);
        _lastWriteTimes[path.FullPath] = when;
    }

    private void EnsureParent(StoragePath path)
    {
        var parent = Path.GetDirectoryName(path.FullPath);
        if (!string.IsNullOrEmpty(parent)) EnsureDirectory(new StoragePath(parent));
    }

    private static string NormaliseDir(string p) =>
        string.IsNullOrEmpty(p) ? string.Empty : p.TrimEnd('\\', '/');

    private static bool IsUnder(string candidate, string dir, bool recursive = true)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        var parent = NormaliseDir(Path.GetDirectoryName(candidate) ?? string.Empty);
        if (recursive)
            return parent.Equals(dir, StringComparison.OrdinalIgnoreCase)
                || parent.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || parent.StartsWith(dir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return parent.Equals(dir, StringComparison.OrdinalIgnoreCase);
    }

    private static Regex WildcardToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase);
    }
}
