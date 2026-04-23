using System.IO;
using FluentAssertions;
using BlockParam.Config;
using BlockParam.Models;
using BlockParam.Services;
using Xunit;

namespace BlockParam.Tests;

public class GlobMatcherTests
{
    [Theory]
    [InlineData("FOERDERER_1", "FOERD", true)]       // implicit starts-with
    [InlineData("FOERDERER_1", "*FOERD*", true)]      // explicit contains
    [InlineData("MOD_FOERDERER_DRIVE", "MOD_*DRIVE", true)] // glob
    [InlineData("MOD_MAIN_DRIVE", "MOD_*DRIVE", true)]
    [InlineData("OTHER_THING", "MOD_*DRIVE", false)]
    [InlineData("foerderer", "FOERD", true)]          // case insensitive
    [InlineData("", "test", false)]
    [InlineData("anything", "", true)]
    public void IsMatch_Patterns(string value, string pattern, bool expected)
    {
        GlobMatcher.IsMatch(value, pattern).Should().Be(expected);
    }
}

public class AutocompleteProviderTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private class FakeTagTableReader : ITagTableReader
    {
        public IReadOnlyList<TagTableEntry> ReadTagTable(string tableName)
        {
            if (tableName == "Const_Modules")
                return new List<TagTableEntry>
                {
                    new TagTableEntry("MOD_FOERDERER_1", "42", "Int", "Förderer Halle 1"),
                    new TagTableEntry("MOD_FOERDERER_2", "43", "Int", "Förderer Halle 2"),
                    new TagTableEntry("MOD_MAIN_DRIVE", "100", "Int", "Hauptantrieb"),
                };
            return new List<TagTableEntry>();
        }

        public IReadOnlyList<string> GetTagTableNames() => new List<string> { "Const_Modules" };
    }

    private static TagTableCache CreateCacheWithEntries()
    {
        return new TagTableCache(new FakeTagTableReader());
    }

    private static MemberNode MakeMember(string name, string datatype, MemberNode? parent = null)
    {
        var path = parent != null ? $"{parent.Path}.{name}" : name;
        return new MemberNode(name, datatype, null, path, parent, new List<MemberNode>(), false);
    }

    private ConfigLoader CreateConfigWithTagTable()
    {
        var json = @"{
            ""rules"": [{
                ""pathPattern"": ""ModuleId"",
                ""datatype"": ""Int"",
                ""tagTableReference"": { ""tableName"": ""Const_Modules"" }
            }]
        }";
        return CreateLoader(json);
    }

    private ConfigLoader CreateConfigWithAllowedValues()
    {
        var json = @"{
            ""rules"": [{
                ""pathPattern"": ""ElementId"",
                ""datatype"": ""Int"",
                ""constraints"": { ""allowedValues"": [1, 2, 3, 42, 100] }
            }]
        }";
        return CreateLoader(json);
    }

    private ConfigLoader CreateLoader(string rulesJson)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "autocomplete_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        _tempDirs.Add(tempDir);

        // Write a minimal config.json (no rules — rules come from rule files)
        var configPath = Path.Combine(tempDir, "config.json");
        File.WriteAllText(configPath, @"{ ""version"": ""1.0"" }");

        // Write the rules as a rule file in the rules/ subdirectory
        var rulesDir = Path.Combine(tempDir, "rules");
        Directory.CreateDirectory(rulesDir);
        File.WriteAllText(Path.Combine(rulesDir, "test-rules.json"), rulesJson);

        return new ConfigLoader(configPath);
    }

    [Fact]
    public void Suggestions_WithTagTable_ReturnsEntries()
    {
        var provider = new AutocompleteProvider(CreateConfigWithTagTable(), CreateCacheWithEntries());
        var member = MakeMember("ModuleId", "Int");

        var suggestions = provider.GetSuggestions(member, "");

        suggestions.Should().HaveCount(3);
        suggestions.Should().Contain(s => s.Value == "42" && s.DisplayName == "MOD_FOERDERER_1");
    }

    [Fact]
    public void Suggestions_ImplicitContains()
    {
        var provider = new AutocompleteProvider(CreateConfigWithTagTable(), CreateCacheWithEntries());
        var member = MakeMember("ModuleId", "Int");

        // "FOERD" matches via contains — both MOD_FOERDERER_1 and _2, but
        // not MOD_MAIN_DRIVE.
        var startAnchored = provider.GetSuggestions(member, "FOERD");
        startAnchored.Should().HaveCount(2);
        startAnchored.Should().OnlyContain(s => s.DisplayName.Contains("FOERD"));

        // Infix match: "DERER" appears inside MOD_FOERDERER_1/2. Under the
        // old implicit starts-with this returned 0; contains finds both.
        var infix = provider.GetSuggestions(member, "DERER");
        infix.Should().HaveCount(2);
    }

    [Fact]
    public void Suggestions_WildcardGlob()
    {
        var provider = new AutocompleteProvider(CreateConfigWithTagTable(), CreateCacheWithEntries());
        var member = MakeMember("ModuleId", "Int");

        var suggestions = provider.GetSuggestions(member, "MOD_*DRIVE");

        suggestions.Should().HaveCount(1);
        suggestions[0].DisplayName.Should().Be("MOD_MAIN_DRIVE");
    }

    [Fact]
    public void Suggestions_MatchesValue()
    {
        var provider = new AutocompleteProvider(CreateConfigWithTagTable(), CreateCacheWithEntries());
        var member = MakeMember("ModuleId", "Int");

        var suggestions = provider.GetSuggestions(member, "42");

        suggestions.Should().Contain(s => s.Value == "42");
    }

    [Fact]
    public void Suggestions_NoTagTable_UsesAllowedValues()
    {
        var provider = new AutocompleteProvider(CreateConfigWithAllowedValues());
        var member = MakeMember("ElementId", "Int");

        var suggestions = provider.GetSuggestions(member, "");

        suggestions.Should().HaveCount(5);
        suggestions.Should().Contain(s => s.Value == "42");
    }

    [Fact]
    public void Suggestions_NoConfig_ReturnsEmpty()
    {
        var loader = new ConfigLoader(null);
        var provider = new AutocompleteProvider(loader);
        var member = MakeMember("ModuleId", "Int");

        var suggestions = provider.GetSuggestions(member, "");

        suggestions.Should().BeEmpty();
    }

    [Fact]
    public void Suggestions_CaseInsensitive()
    {
        var provider = new AutocompleteProvider(CreateConfigWithTagTable(), CreateCacheWithEntries());
        var member = MakeMember("ModuleId", "Int");

        var suggestions = provider.GetSuggestions(member, "mod_foerd");

        suggestions.Should().HaveCount(2);
    }
}
