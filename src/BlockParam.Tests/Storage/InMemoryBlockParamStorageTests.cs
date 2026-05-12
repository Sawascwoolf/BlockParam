using System;
using System.IO;
using System.Linq;
using System.Text;
using BlockParam.Services.Storage;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests.Storage;

public class InMemoryBlockParamStorageTests
{
    private static StoragePath Root => StoragePath.FromAbsolute(@"C:\bp-tests");

    [Fact]
    public void WriteAllText_then_ReadAllText_roundtrips()
    {
        var fs = new InMemoryBlockParamStorage();
        var path = Root / "config.json";

        fs.WriteAllText(path, "hello");

        fs.FileExists(path).Should().BeTrue();
        fs.ReadAllText(path).Should().Be("hello");
    }

    [Fact]
    public void WriteAllBytes_returns_a_defensive_copy()
    {
        var fs = new InMemoryBlockParamStorage();
        var path = Root / "blob.bin";
        var bytes = new byte[] { 1, 2, 3 };

        fs.WriteAllBytes(path, bytes);
        bytes[0] = 99;

        fs.ReadAllBytes(path).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void ReadAllText_on_missing_file_throws()
    {
        var fs = new InMemoryBlockParamStorage();
        Action act = () => fs.ReadAllText(Root / "nope.txt");
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Write_auto_creates_parent_directory()
    {
        var fs = new InMemoryBlockParamStorage();
        var nested = Root / "a" / "b" / "c.txt";

        fs.WriteAllText(nested, "x");

        fs.DirectoryExists(Root / "a" / "b").Should().BeTrue();
        fs.DirectoryExists(Root / "a").Should().BeTrue();
        fs.DirectoryExists(Root).Should().BeTrue();
    }

    [Fact]
    public void AppendAllText_concatenates_existing_content()
    {
        var fs = new InMemoryBlockParamStorage();
        var path = Root / "log.txt";

        fs.AppendAllText(path, "one\n");
        fs.AppendAllText(path, "two\n");

        fs.ReadAllText(path).Should().Be("one\ntwo\n");
    }

    [Fact]
    public void DeleteFile_removes_entry_silently_if_missing()
    {
        var fs = new InMemoryBlockParamStorage();
        var path = Root / "gone.txt";

        Action act = () => fs.DeleteFile(path); // missing
        act.Should().NotThrow();

        fs.WriteAllText(path, "");
        fs.DeleteFile(path);
        fs.FileExists(path).Should().BeFalse();
    }

    [Fact]
    public void EnumerateFiles_returns_empty_when_directory_missing()
    {
        var fs = new InMemoryBlockParamStorage();
        fs.EnumerateFiles(Root / "missing").Should().BeEmpty();
    }

    [Fact]
    public void EnumerateFiles_recursive_walks_subtree()
    {
        var fs = new InMemoryBlockParamStorage();
        fs.WriteAllText(Root / "a.txt", "");
        fs.WriteAllText(Root / "sub" / "b.txt", "");
        fs.WriteAllText(Root / "sub" / "deep" / "c.txt", "");

        fs.EnumerateFiles(Root, "*", recursive: false)
            .Select(p => p.FileName)
            .Should().BeEquivalentTo("a.txt");

        fs.EnumerateFiles(Root, "*", recursive: true)
            .Select(p => p.FileName)
            .Should().BeEquivalentTo("a.txt", "b.txt", "c.txt");
    }

    [Fact]
    public void EnumerateFiles_applies_wildcard_pattern()
    {
        var fs = new InMemoryBlockParamStorage();
        fs.WriteAllText(Root / "a.json", "");
        fs.WriteAllText(Root / "b.txt", "");
        fs.WriteAllText(Root / "c.json", "");

        fs.EnumerateFiles(Root, "*.json")
            .Select(p => p.FileName)
            .Should().BeEquivalentTo("a.json", "c.json");
    }

    [Fact]
    public void HasAnyEntries_true_for_files_or_subdirs_and_false_for_empty()
    {
        var fs = new InMemoryBlockParamStorage();
        fs.EnsureDirectory(Root);
        fs.HasAnyEntries(Root).Should().BeFalse();

        fs.EnsureDirectory(Root / "sub");
        fs.HasAnyEntries(Root).Should().BeTrue();

        fs.HasAnyEntries(Root / "sub").Should().BeFalse();
        fs.WriteAllText(Root / "sub" / "x", "");
        fs.HasAnyEntries(Root / "sub").Should().BeTrue();
    }

    [Fact]
    public void HasAnyEntries_false_when_directory_missing()
    {
        // TempCacheCleanup relies on this: a never-created dir must be
        // indistinguishable from an empty one so the bottom-up sweeper
        // doesn't try to delete something that isn't there.
        var fs = new InMemoryBlockParamStorage();
        fs.HasAnyEntries(Root / "never-created").Should().BeFalse();
    }

    [Fact]
    public void DeleteDirectory_throws_when_non_empty()
    {
        // Mirrors Directory.Delete(path) without recursive=true. The catch
        // in TempCacheCleanup.DeleteEmptyDirectories swallows this — so the
        // throw is the load-bearing signal that "this dir isn't safe to
        // remove yet"; silently succeeding would cascade orphans.
        var fs = new InMemoryBlockParamStorage();
        fs.WriteAllText(Root / "child.txt", "");

        Action act = () => fs.DeleteDirectory(Root);
        act.Should().Throw<IOException>();
        fs.DirectoryExists(Root).Should().BeTrue();
    }

    [Fact]
    public void OpenRead_returns_non_writable_stream()
    {
        var fs = new InMemoryBlockParamStorage();
        fs.WriteAllText(Root / "f.txt", "x");

        using var s = fs.OpenRead(Root / "f.txt");
        s.CanRead.Should().BeTrue();
        s.CanWrite.Should().BeFalse();
    }

    [Fact]
    public void GetLastWriteTime_on_missing_file_returns_FILETIME_epoch()
    {
        // Aligns with File.GetLastWriteTime — callers that race against
        // external deletes don't need a separate existence check.
        var fs = new InMemoryBlockParamStorage();
        fs.GetLastWriteTime(Root / "missing").Should().Be(new DateTime(1601, 1, 1));
    }

    [Fact]
    public void GetLastWriteTime_uses_clock_at_write_unless_overridden()
    {
        var fs = new InMemoryBlockParamStorage();
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        fs.Clock = () => t;

        var p = Root / "f.txt";
        fs.WriteAllText(p, "");
        fs.GetLastWriteTime(p).Should().Be(t);

        fs.SetLastWriteTime(p, new DateTime(2020, 1, 1));
        fs.GetLastWriteTime(p).Should().Be(new DateTime(2020, 1, 1));
    }

    [Fact]
    public void OpenRead_returns_readable_stream_of_contents()
    {
        var fs = new InMemoryBlockParamStorage();
        fs.WriteAllText(Root / "x.txt", "hello");

        using var s = fs.OpenRead(Root / "x.txt");
        using var r = new StreamReader(s, Encoding.UTF8);
        r.ReadToEnd().Should().Be("hello");
    }
}
