using System;
using System.IO;
using System.Linq;
using BlockParam.Services.Storage;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests.Storage;

/// <summary>
/// Happy-path coverage of <see cref="FileSystemBlockParamStorage"/> against
/// a real temp directory. The in-memory fake gets the bulk of the unit test
/// budget; this suite exists so a parameter-order or auto-create regression
/// in the FS adapter can't slip past tests that never hit a disk.
/// </summary>
public class FileSystemBlockParamStorageTests : IDisposable
{
    private readonly string _rootDir;
    private readonly StoragePath _root;
    private readonly FileSystemBlockParamStorage _fs;

    public FileSystemBlockParamStorageTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "BlockParamTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
        _root = StoragePath.FromAbsolute(_rootDir);
        _fs = new FileSystemBlockParamStorage();
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void WriteAllText_then_ReadAllText_roundtrips_through_disk()
    {
        var path = _root / "sample.json";

        _fs.WriteAllText(path, "{\"k\":1}");

        _fs.FileExists(path).Should().BeTrue();
        _fs.ReadAllText(path).Should().Be("{\"k\":1}");
        File.ReadAllText(path.FullPath).Should().Be("{\"k\":1}"); // really on disk
    }

    [Fact]
    public void WriteAllText_auto_creates_parent_directory_chain()
    {
        var nested = _root / "a" / "b" / "c.txt";

        _fs.WriteAllText(nested, "x");

        Directory.Exists(Path.Combine(_rootDir, "a", "b")).Should().BeTrue();
        _fs.FileExists(nested).Should().BeTrue();
    }

    [Fact]
    public void EnumerateFiles_returns_empty_for_missing_directory()
    {
        _fs.EnumerateFiles(_root / "never-created").Should().BeEmpty();
    }

    [Fact]
    public void EnumerateFiles_recursive_matches_pattern()
    {
        _fs.WriteAllText(_root / "a.json", "");
        _fs.WriteAllText(_root / "sub" / "b.json", "");
        _fs.WriteAllText(_root / "sub" / "c.txt", "");

        var jsons = _fs.EnumerateFiles(_root, "*.json", recursive: true)
            .Select(p => p.FileName)
            .OrderBy(n => n)
            .ToList();

        jsons.Should().BeEquivalentTo("a.json", "b.json");
    }

    [Fact]
    public void GetLastWriteTime_on_missing_file_returns_FILETIME_epoch()
    {
        // Documented contract — both FS and in-memory return 1601-01-01 for
        // missing files rather than throwing.
        _fs.GetLastWriteTime(_root / "missing").Should().Be(new DateTime(1601, 1, 1));
    }

    [Fact]
    public void DeleteDirectory_throws_when_non_empty()
    {
        _fs.WriteAllText(_root / "child.txt", "");

        Action act = () => _fs.DeleteDirectory(_root);
        act.Should().Throw<IOException>();
        _fs.DirectoryExists(_root).Should().BeTrue();
    }

    [Fact]
    public void HasAnyEntries_distinguishes_empty_missing_and_populated()
    {
        var emptyDir = _root / "empty";
        _fs.EnsureDirectory(emptyDir);
        _fs.HasAnyEntries(emptyDir).Should().BeFalse();
        _fs.HasAnyEntries(_root / "never-created").Should().BeFalse();

        _fs.WriteAllText(emptyDir / "now-has-a-file.txt", "");
        _fs.HasAnyEntries(emptyDir).Should().BeTrue();
    }

    [Fact]
    public void Replace_swaps_destination_atomically_on_same_volume()
    {
        var dest = _root / "live.dat";
        var src = _root / "live.dat.tmp";
        _fs.WriteAllText(dest, "old");
        _fs.WriteAllText(src, "new");

        _fs.Replace(src, dest);

        _fs.FileExists(src).Should().BeFalse();
        _fs.ReadAllText(dest).Should().Be("new");
        File.Exists(src.FullPath).Should().BeFalse();
        File.ReadAllText(dest.FullPath).Should().Be("new");
    }

    [Fact]
    public void Replace_renames_when_destination_does_not_exist()
    {
        var dest = _root / "live.dat";
        var src = _root / "live.dat.tmp";
        _fs.WriteAllText(src, "first");

        _fs.Replace(src, dest);

        _fs.FileExists(src).Should().BeFalse();
        _fs.ReadAllText(dest).Should().Be("first");
    }
}
