using System;
using System.IO;

namespace BlockParam.Services.Storage;

/// <summary>
/// Strongly-typed file-system path rooted at one of the BlockParam well-known
/// directories (<see cref="AppData"/>, <see cref="ProgramData"/>,
/// <see cref="Temp"/>, <see cref="Logs"/>). Used as the parameter type for
/// <see cref="IBlockParamStorage"/>.
///
/// Compose paths with the <c>/</c> operator, mirroring the
/// <see cref="Path.Combine(string, string)"/> shape (#85):
/// <code>
/// StoragePath p = StoragePath.AppData / "config.json";
/// StoragePath t = StoragePath.Temp / "TagTables" / fileName;
/// </code>
///
/// The struct is just a wrapper around the absolute path string — it neither
/// touches the disk nor caches anything. Implementations of
/// <see cref="IBlockParamStorage"/> read <see cref="FullPath"/> and call into
/// the BCL (or a test fake) directly.
/// </summary>
public readonly struct StoragePath : IEquatable<StoragePath>
{
    /// <summary>Absolute path. Never null; may be empty (default struct value).</summary>
    public string FullPath { get; }

    public StoragePath(string fullPath)
    {
        FullPath = fullPath ?? throw new ArgumentNullException(nameof(fullPath));
    }

    /// <summary>Per-user app data root (<c>%APPDATA%\BlockParam</c>).</summary>
    public static StoragePath AppData => new(AppDirectories.AppData);

    /// <summary>Machine-wide app data root (<c>%PROGRAMDATA%\BlockParam</c>).</summary>
    public static StoragePath ProgramData => new(AppDirectories.ProgramData);

    /// <summary>Per-machine TEMP root (<c>%TEMP%\BlockParam</c>).</summary>
    public static StoragePath Temp => new(AppDirectories.Temp);

    /// <summary>Rolling log directory under <see cref="AppData"/>.</summary>
    public static StoragePath Logs => new(AppDirectories.LogsDir);

    /// <summary>
    /// Escape hatch for callers that already hold an absolute path — e.g. a
    /// user-chosen file from a dialog, or a path read from configuration.
    /// New code under one of the BlockParam roots should compose from the
    /// static roots instead so the path convention stays in one place (#86).
    /// </summary>
    public static StoragePath FromAbsolute(string absolutePath) => new(absolutePath);

    public static StoragePath operator /(StoragePath left, string segment)
    {
        if (segment is null) throw new ArgumentNullException(nameof(segment));
        return new StoragePath(Path.Combine(left.FullPath, segment));
    }

    public string FileName => Path.GetFileName(FullPath);

    public StoragePath Parent
    {
        get
        {
            var dir = Path.GetDirectoryName(FullPath);
            return new StoragePath(dir ?? string.Empty);
        }
    }

    public bool IsEmpty => string.IsNullOrEmpty(FullPath);

    public override string ToString() => FullPath;

    public bool Equals(StoragePath other) =>
        string.Equals(FullPath, other.FullPath, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is StoragePath sp && Equals(sp);

    public override int GetHashCode() =>
        FullPath is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(FullPath);

    public static bool operator ==(StoragePath a, StoragePath b) => a.Equals(b);
    public static bool operator !=(StoragePath a, StoragePath b) => !a.Equals(b);
}
