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

    /// <summary>
    /// Creates the directory and every missing parent. No-op if it already
    /// exists. Implementations may cascade past the BlockParam product roots
    /// (the in-memory fake materialises drive letters too); callers that
    /// care about "did *I* create this" should track that themselves.
    /// </summary>
    void EnsureDirectory(StoragePath path);

    void DeleteFile(StoragePath path);

    /// <summary>
    /// Removes <paramref name="path"/> if it exists and is empty. Throws
    /// <see cref="IOException"/> if the directory still contains files or
    /// subdirectories — both implementations honour that contract so
    /// best-effort bottom-up sweepers (see <c>TempCacheCleanup</c>) can rely
    /// on the failure to skip non-empty dirs.
    /// </summary>
    void DeleteDirectory(StoragePath path);

    /// <summary>
    /// Returns the last write time (local time), or exactly
    /// <c>new DateTime(1601, 1, 1)</c> (the FILETIME epoch, Kind=Unspecified)
    /// if the file does not exist — so callers don't need a separate existence
    /// check before stat'ing and the sentinel is timezone-independent.
    /// </summary>
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

    /// <summary>
    /// Moves <paramref name="source"/> over <paramref name="destination"/>,
    /// replacing the destination if it exists. Best-effort atomic on NTFS
    /// (Win32 ReplaceFile) when the destination already exists; falls back
    /// to overwrite-copy + source-delete on cross-volume / non-NTFS targets
    /// and uses a plain rename when the destination is absent. Source must
    /// exist — throws otherwise.
    ///
    /// Used by callers that need write-temp-then-rename atomicity to avoid
    /// torn writes on crash (see <c>LocalUsageTracker</c>'s counter file).
    /// </summary>
    void Replace(StoragePath source, StoragePath destination);
}
