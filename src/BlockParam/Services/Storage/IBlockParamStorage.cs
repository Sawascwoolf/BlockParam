using System;
using System.Collections.Generic;
using System.IO;

namespace BlockParam.Services.Storage;

/// <summary>
/// File-system abstraction for all of BlockParam's persistent state — config,
/// licensing, cache, logs, UI settings. Centralises the
/// "create-dir-if-missing + swallow IOException" boilerplate that was
/// duplicated across 10+ files (#85) and gives tests a seam to substitute
/// an in-memory implementation without touching the real disk.
///
/// Operations are intentionally narrow:
/// - <b>Reads</b> on a missing file throw <see cref="FileNotFoundException"/>;
///   callers that want a "exists or default" pattern should call
///   <see cref="FileExists"/> first.
/// - <b>Writes</b> auto-create the parent directory (since 100% of the
///   migrated call sites paired <c>Directory.CreateDirectory</c> with
///   <c>File.WriteAllText</c> anyway).
/// - <b>Best-effort cleanup</b> calls (<see cref="DeleteFile"/>,
///   <see cref="DeleteDirectory"/>) swallow not-found but propagate
///   permission/lock errors so the caller can log them.
/// </summary>
public interface IBlockParamStorage
{
    bool FileExists(StoragePath path);
    bool DirectoryExists(StoragePath path);

    string ReadAllText(StoragePath path);
    byte[] ReadAllBytes(StoragePath path);
    Stream OpenRead(StoragePath path);

    void WriteAllText(StoragePath path, string contents);
    void WriteAllBytes(StoragePath path, byte[] contents);
    void AppendAllText(StoragePath path, string contents);

    void EnsureDirectory(StoragePath path);

    void DeleteFile(StoragePath path);
    void DeleteDirectory(StoragePath path);

    DateTime GetLastWriteTime(StoragePath path);

    /// <summary>
    /// Enumerates files under <paramref name="directory"/> that match
    /// <paramref name="pattern"/>. Returns an empty sequence if the directory
    /// does not exist — that's the desired behavior at every migrated caller,
    /// none of which want to distinguish "missing" from "empty".
    /// </summary>
    IEnumerable<StoragePath> EnumerateFiles(
        StoragePath directory,
        string pattern = "*",
        bool recursive = false);

    /// <summary>
    /// Enumerates subdirectories. Same missing-is-empty contract as
    /// <see cref="EnumerateFiles"/>.
    /// </summary>
    IEnumerable<StoragePath> EnumerateDirectories(
        StoragePath directory,
        string pattern = "*",
        bool recursive = false);

    /// <summary>
    /// True iff the directory contains at least one file or subdirectory.
    /// Used by cleanup code that wants to know "is this directory safe to
    /// remove" without enumerating the entire contents.
    /// </summary>
    bool HasAnyEntries(StoragePath directory);
}
