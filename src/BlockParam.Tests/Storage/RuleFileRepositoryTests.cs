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
}
