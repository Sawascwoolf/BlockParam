using System.Security;
using BlockParam.Services;
using BlockParam.Services.Storage;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests.Storage;

/// <summary>
/// Pins <see cref="RuleFileRepository"/> to the
/// <see cref="IBlockParamStorage"/> abstraction so the
/// "no new <c>File.*</c> / <c>Directory.*</c> outside the storage layer"
/// guardrail (#85) is exercised on every test run.
/// </summary>
public class RuleFileRepositoryTests
{
    private static StoragePath RulesDir =>
        StoragePath.FromAbsolute(@"C:\bp\rules");

    [Fact]
    public void ListJsonFiles_returns_empty_for_missing_directory()
    {
        var fs = new InMemoryBlockParamStorage();
        var repo = new RuleFileRepository(fs);

        repo.ListJsonFiles(RulesDir.FullPath).Should().BeEmpty();
    }

    [Fact]
    public void ListJsonFiles_filters_to_json_and_sorts_case_insensitive()
    {
        var fs = new InMemoryBlockParamStorage();
        fs.WriteAllText(RulesDir / "Zebra.json", "{}");
        fs.WriteAllText(RulesDir / "apple.json", "{}");
        fs.WriteAllText(RulesDir / "readme.txt", "ignore me");

        var repo = new RuleFileRepository(fs);
        var files = repo.ListJsonFiles(RulesDir.FullPath);

        files.Should().HaveCount(2);
        // OrdinalIgnoreCase puts apple.json before Zebra.json.
        Path.GetFileName(files[0]).Should().Be("apple.json");
        Path.GetFileName(files[1]).Should().Be("Zebra.json");
    }

    [Fact]
    public void ListJsonFileNames_returns_bare_names()
    {
        var fs = new InMemoryBlockParamStorage();
        fs.WriteAllText(RulesDir / "a.json", "{}");
        fs.WriteAllText(RulesDir / "b.json", "{}");

        var repo = new RuleFileRepository(fs);
        var names = repo.ListJsonFileNames(RulesDir.FullPath).ToList();

        names.Should().BeEquivalentTo("a.json", "b.json");
    }

    [Fact]
    public void FileExists_and_DirectoryExists_route_through_storage()
    {
        var fs = new InMemoryBlockParamStorage();
        var file = RulesDir / "x.json";
        fs.WriteAllText(file, "{}");

        var repo = new RuleFileRepository(fs);

        repo.DirectoryExists(RulesDir.FullPath).Should().BeTrue();
        repo.DirectoryExists(@"C:\nope").Should().BeFalse();
        repo.FileExists(file.FullPath).Should().BeTrue();
        repo.FileExists((RulesDir / "missing.json").FullPath).Should().BeFalse();
    }

    [Fact]
    public void DeleteFile_removes_existing_file()
    {
        var fs = new InMemoryBlockParamStorage();
        var file = RulesDir / "x.json";
        fs.WriteAllText(file, "{}");

        var repo = new RuleFileRepository(fs);
        repo.DeleteFile(file.FullPath);

        fs.FileExists(file).Should().BeFalse();
    }

    [Fact]
    public void DeleteFile_is_noop_for_empty_path()
    {
        var fs = new InMemoryBlockParamStorage();
        var repo = new RuleFileRepository(fs);

        Action act = () => repo.DeleteFile("");
        act.Should().NotThrow();
    }

    [Fact]
    public void ReadAllText_roundtrips_a_written_rule_file()
    {
        var fs = new InMemoryBlockParamStorage();
        var file = RulesDir / "rule.json";
        fs.WriteAllText(file, "{\"version\":\"1.0\"}");

        var repo = new RuleFileRepository(fs);
        repo.ReadAllText(file.FullPath).Should().Be("{\"version\":\"1.0\"}");
    }

    [Fact]
    public void ListJsonFiles_swallows_SecurityException_and_returns_empty()
    {
        // Pins the partial-trust behavior the old ConfigEditorViewModel.ClaimsFor
        // relied on (bare catch). Under TIA's Add-In Loader sandbox,
        // EnumerateFiles can throw SecurityException; ListJsonFiles must seed
        // an empty result so SaveAll falls through to its own clearer error
        // path instead of crashing the dispatcher.
        var fs = new ThrowingStorage(new SecurityException("denied by sandbox"));
        var repo = new RuleFileRepository(fs);

        repo.ListJsonFiles(RulesDir.FullPath).Should().BeEmpty();
    }

    /// <summary>
    /// Minimal IBlockParamStorage stub that pretends a directory exists but
    /// throws on enumeration. The full InMemoryBlockParamStorage doesn't
    /// surface SecurityException so we wrap it instead.
    /// </summary>
    private sealed class ThrowingStorage : IBlockParamStorage
    {
        private readonly Exception _toThrow;
        public ThrowingStorage(Exception toThrow) { _toThrow = toThrow; }

        public bool DirectoryExists(StoragePath path) => true;
        public IEnumerable<StoragePath> EnumerateFiles(StoragePath directory, string pattern = "*", bool recursive = false)
            => throw _toThrow;

        // Unused — defaulted to throw so any accidental call surfaces loudly.
        public bool FileExists(StoragePath path) => throw new NotImplementedException();
        public string ReadAllText(StoragePath path) => throw new NotImplementedException();
        public byte[] ReadAllBytes(StoragePath path) => throw new NotImplementedException();
        public System.IO.Stream OpenRead(StoragePath path) => throw new NotImplementedException();
        public void WriteAllText(StoragePath path, string contents) => throw new NotImplementedException();
        public void WriteAllBytes(StoragePath path, byte[] contents) => throw new NotImplementedException();
        public void AppendAllText(StoragePath path, string contents) => throw new NotImplementedException();
        public void EnsureDirectory(StoragePath path) => throw new NotImplementedException();
        public void DeleteFile(StoragePath path) => throw new NotImplementedException();
        public void DeleteDirectory(StoragePath path) => throw new NotImplementedException();
        public DateTime GetLastWriteTime(StoragePath path) => throw new NotImplementedException();
        public IEnumerable<StoragePath> EnumerateDirectories(StoragePath directory, string pattern = "*", bool recursive = false) => throw new NotImplementedException();
        public bool HasAnyEntries(StoragePath directory) => throw new NotImplementedException();
        public void Replace(StoragePath source, StoragePath destination) => throw new NotImplementedException();
    }
}
