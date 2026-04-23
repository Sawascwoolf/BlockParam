using FluentAssertions;
using BlockParam.Config;
using BlockParam.Models;
using Xunit;

namespace BlockParam.Tests;

public class RulesDirectoryLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RulesDirectoryLoader _loader = new();

    public RulesDirectoryLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BlockParamRulesTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void WriteSharedRuleFile(string filename, string json)
    {
        File.WriteAllText(Path.Combine(_tempDir, filename), json);
    }

    [Fact]
    public void Load_DirectoryWithMultipleFiles_MergesRules()
    {
        WriteSharedRuleFile("a-moduleId.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": ""moduleId"", ""datatype"": ""Int"" }]
        }");
        WriteSharedRuleFile("b-elementId.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": ""elementId"", ""datatype"": ""Int"" }]
        }");

        var result = _loader.LoadFromDirectory(_tempDir);

        result.Rules.Should().HaveCount(2);
        result.Rules[0].PathPattern.Should().Be("moduleId");
        result.Rules[1].PathPattern.Should().Be("elementId");
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Load_DirectoryNotExists_ReturnsEmptyWithWarning()
    {
        var result = _loader.LoadFromDirectory(@"C:\nonexistent\path\12345");

        result.Rules.Should().BeEmpty();
        result.Warnings.Should().ContainSingle().Which.Should().Contain("not found");
    }

    [Fact]
    public void Load_DirectoryEmpty_ReturnsEmpty()
    {
        var result = _loader.LoadFromDirectory(_tempDir);

        result.Rules.Should().BeEmpty();
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Load_InvalidJsonFile_SkippedWithWarning()
    {
        WriteSharedRuleFile("broken.json", "{ not valid json!!!");

        var result = _loader.LoadFromDirectory(_tempDir);

        result.Rules.Should().BeEmpty();
        result.Warnings.Should().ContainSingle().Which.Should().Contain("Invalid JSON");
    }

    [Fact]
    public void Load_MixedValidInvalid_ValidLoaded()
    {
        WriteSharedRuleFile("a-valid.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": ""speed"" }]
        }");
        WriteSharedRuleFile("b-broken.json", "not json");

        var result = _loader.LoadFromDirectory(_tempDir);

        result.Rules.Should().HaveCount(1);
        result.Rules[0].PathPattern.Should().Be("speed");
        result.Warnings.Should().HaveCount(1);
    }

    [Fact]
    public void Load_RuleWithCommentTemplate_Loaded()
    {
        WriteSharedRuleFile("comment-rule.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{
                ""pathPattern"": "".*{udt:messageConfig_UDT}$"",
                ""commentTemplate"": ""{db}.{parent}"",
                ""commentLanguage"": ""en-GB""
            }]
        }");

        var result = _loader.LoadFromDirectory(_tempDir);

        result.Rules.Should().HaveCount(1);
        result.Rules[0].CommentTemplate.Should().Be("{db}.{parent}");
    }

    [Fact]
    public void Load_RuleWithExcludeFromSetpoints()
    {
        WriteSharedRuleFile("exclude-actual.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{
                ""pathPattern"": "".*\\.actualValue$"",
                ""excludeFromSetpoints"": true
            }]
        }");

        var result = _loader.LoadFromDirectory(_tempDir);

        result.Rules.Should().HaveCount(1);
        result.Rules[0].ExcludeFromSetpoints.Should().BeTrue();
    }

    [Fact]
    public void Load_NonRuleJsonFile_Skipped()
    {
        WriteSharedRuleFile("package.json", @"{ ""name"": ""some-package"", ""version"": ""2.0.0"" }");
        WriteSharedRuleFile("rules.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": ""speed"" }]
        }");

        var result = _loader.LoadFromDirectory(_tempDir);

        // package.json has version "2.0.0" but no empty version — it will pass sentinel
        // but has no rules, so no harm. The sentinel check is for files without version field.
        result.Rules.Should().HaveCount(1);
    }

    [Fact]
    public void Load_InvalidRegexPattern_SkippedWithWarning()
    {
        WriteSharedRuleFile("bad-regex.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": ""[invalid"" }]
        }");

        var result = _loader.LoadFromDirectory(_tempDir);

        result.Rules.Should().BeEmpty();
        result.Warnings.Should().Contain(w => w.Contains("invalid pattern"));
    }

    [Fact]
    public void Load_AlphabeticalOrder_Deterministic()
    {
        WriteSharedRuleFile("c-third.json", @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": ""c"" }] }");
        WriteSharedRuleFile("a-first.json", @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": ""a"" }] }");
        WriteSharedRuleFile("b-second.json", @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": ""b"" }] }");

        var result = _loader.LoadFromDirectory(_tempDir);

        result.Rules.Should().HaveCount(3);
        result.Rules[0].PathPattern.Should().Be("a");
        result.Rules[1].PathPattern.Should().Be("b");
        result.Rules[2].PathPattern.Should().Be("c");
    }

    [Fact]
    public void Load_DuplicatePathPattern_BothLoaded()
    {
        WriteSharedRuleFile("a-file.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": "".*\\.moduleId$"", ""datatype"": ""Int"" }]
        }");
        WriteSharedRuleFile("b-file.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": "".*\\.moduleId$"", ""datatype"": ""Real"" }]
        }");

        var result = _loader.LoadFromDirectory(_tempDir);

        result.Rules.Should().HaveCount(2); // Both loaded, first-match-wins at query time
    }

    [Fact]
    public void Load_SkipFileNames_SkipsMatchingFiles()
    {
        WriteSharedRuleFile("moduleId.json", @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": ""moduleId"" }] }");
        WriteSharedRuleFile("elementId.json", @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": ""elementId"" }] }");

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "moduleId.json" };
        var result = _loader.LoadFromDirectory(_tempDir, skipFileNames: skip);

        result.Rules.Should().HaveCount(1);
        result.Rules[0].PathPattern.Should().Be("elementId");
    }
}

public class UnifiedRulesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _localRulesDir;
    private readonly string _sharedRulesDir;

    public UnifiedRulesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BlockParamUnified_{Guid.NewGuid():N}");
        _localRulesDir = Path.Combine(_tempDir, "rules");
        _sharedRulesDir = Path.Combine(_tempDir, "shared");
        Directory.CreateDirectory(_localRulesDir);
        Directory.CreateDirectory(_sharedRulesDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteConfig(string? rulesDirectory = null)
    {
        var path = Path.Combine(_tempDir, "config.json");
        var rd = rulesDirectory != null
            ? $@"""rulesDirectory"": ""{rulesDirectory.Replace("\\", "\\\\")}"""
            : "";
        File.WriteAllText(path, $@"{{ ""version"": ""1.0""{(rd.Length > 0 ? ", " + rd : "")} }}");
        return path;
    }

    [Fact]
    public void Load_LocalRulesDir_Scanned()
    {
        File.WriteAllText(Path.Combine(_localRulesDir, "speed.json"),
            @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": ""speed"" }] }");
        var configPath = WriteConfig();

        var loader = new ConfigLoader(configPath);
        var config = loader.GetConfig();

        config!.Rules.Should().HaveCount(1);
        config.Rules[0].PathPattern.Should().Be("speed");
    }

    [Fact]
    public void Load_LocalRulesDir_AutoCreated()
    {
        var freshDir = Path.Combine(_tempDir, "fresh");
        Directory.CreateDirectory(freshDir);
        var configPath = Path.Combine(freshDir, "config.json");
        File.WriteAllText(configPath, @"{ ""version"": ""1.0"" }");

        var loader = new ConfigLoader(configPath);
        loader.GetConfig();

        Directory.Exists(Path.Combine(freshDir, "rules")).Should().BeTrue();
    }

    [Fact]
    public void Load_SharedAndLocal_Merged()
    {
        File.WriteAllText(Path.Combine(_localRulesDir, "local.json"),
            @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": ""localVar"" }] }");
        File.WriteAllText(Path.Combine(_sharedRulesDir, "shared.json"),
            @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": ""sharedVar"" }] }");
        var configPath = WriteConfig(_sharedRulesDir);

        var loader = new ConfigLoader(configPath);
        var config = loader.GetConfig();

        config!.Rules.Should().HaveCount(2);
        config.Rules[0].PathPattern.Should().Be("localVar"); // Local first
        config.Rules[1].PathPattern.Should().Be("sharedVar");
    }

    [Fact]
    public void Load_SameFilename_LocalWins_SharedSkipped()
    {
        File.WriteAllText(Path.Combine(_localRulesDir, "moduleId.json"),
            @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": ""moduleId"", ""constraints"": { ""max"": 9999 } }] }");
        File.WriteAllText(Path.Combine(_sharedRulesDir, "moduleId.json"),
            @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": ""moduleId"", ""constraints"": { ""max"": 100 } }] }");
        var configPath = WriteConfig(_sharedRulesDir);

        var loader = new ConfigLoader(configPath);
        var config = loader.GetConfig();

        // Only 1 rule — shared was skipped
        config!.Rules.Should().HaveCount(1);
        config.Rules[0].Constraints!.Max.Should().Be(9999);
    }

    [Fact]
    public void Load_LegacyConfigWithRules_Ignored()
    {
        File.WriteAllText(Path.Combine(_localRulesDir, "local.json"),
            @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": ""fromFile"" }] }");
        var configPath = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(configPath, @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": ""fromLegacy"" }]
        }");

        var loader = new ConfigLoader(configPath);
        var config = loader.GetConfig();

        // Legacy rules in config.json are no longer loaded — only rule files count
        config!.Rules.Should().HaveCount(1);
        config.Rules[0].PathPattern.Should().Be("fromFile");
    }

    [Fact]
    public void SaveRuleFile_WritesValidJson()
    {
        var filePath = Path.Combine(_localRulesDir, "test.json");
        var content = new BulkChangeConfig
        {
            Version = "1.0",
            Rules = new List<MemberRule> { new() { PathPattern = @".*\\.test" } }
        };

        var loader = new ConfigLoader(null);
        loader.SaveRuleFile(filePath, content);

        File.Exists(filePath).Should().BeTrue();
        var reloaded = ConfigLoader.Deserialize(File.ReadAllText(filePath));
        reloaded!.Rules.Should().HaveCount(1);
    }

    [Fact]
    public void SaveRuleFile_CreatesDirectory()
    {
        var newDir = Path.Combine(_tempDir, "newdir");
        var filePath = Path.Combine(newDir, "test.json");

        var loader = new ConfigLoader(null);
        loader.SaveRuleFile(filePath, new BulkChangeConfig { Version = "1.0" });

        Directory.Exists(newDir).Should().BeTrue();
    }

    [Fact]
    public void CopiedFromMetadata_Serialized()
    {
        var content = new BulkChangeConfig
        {
            Version = "1.0",
            CopiedFrom = new CopiedFromMetadata
            {
                Source = @"\\server\share\rules\moduleId.json",
                CopiedAt = new DateTime(2026, 4, 11, 14, 30, 0, DateTimeKind.Utc),
                SourceModifiedAt = new DateTime(2026, 4, 10, 9, 0, 0, DateTimeKind.Utc)
            }
        };

        var filePath = Path.Combine(_localRulesDir, "moduleId.json");
        var loader = new ConfigLoader(null);
        loader.SaveRuleFile(filePath, content);

        var json = File.ReadAllText(filePath);
        json.Should().Contain("_copiedFrom");
        json.Should().Contain("server");

        var reloaded = ConfigLoader.Deserialize(json);
        reloaded!.CopiedFrom.Should().NotBeNull();
        reloaded.CopiedFrom!.Source.Should().Contain("server");
    }
}

public class ConfigLoaderMergeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sharedDir;

    public ConfigLoaderMergeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BlockParamMergeTest_{Guid.NewGuid():N}");
        _sharedDir = Path.Combine(_tempDir, "shared");
        Directory.CreateDirectory(_sharedDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteLocalConfig(string json)
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private void WriteSharedRuleFile(string filename, string json)
    {
        File.WriteAllText(Path.Combine(_sharedDir, filename), json);
    }

    [Fact]
    public void GetConfig_WithRulesDirectory_MergesRules()
    {
        WriteSharedRuleFile("shared-rules.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": ""sharedVar"" }]
        }");
        // Write local rule as a rule file in the rules/ subdirectory
        var localRulesDir = Path.Combine(_tempDir, "rules");
        Directory.CreateDirectory(localRulesDir);
        File.WriteAllText(Path.Combine(localRulesDir, "local-rules.json"), @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": ""localVar"" }]
        }");
        var configPath = WriteLocalConfig($@"{{
            ""version"": ""1.0"",
            ""rulesDirectory"": ""{_sharedDir.Replace("\\", "\\\\")}""
        }}");

        var loader = new ConfigLoader(configPath);
        var config = loader.GetConfig();

        config.Should().NotBeNull();
        config!.Rules.Should().Contain(r => r.PathPattern == "localVar");
        config.Rules.Should().Contain(r => r.PathPattern == "sharedVar");
    }

    [Fact]
    public void GetConfig_LocalRuleOverridesDirectory()
    {
        // Shared directory has "speed" with max=100
        WriteSharedRuleFile("speed.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": ""speed"", ""constraints"": { ""max"": 100 } }]
        }");
        // Local rules\ dir (next to config.json) has "speed" with max=9999
        var localRulesDir = Path.Combine(_tempDir, "rules");
        Directory.CreateDirectory(localRulesDir);
        File.WriteAllText(Path.Combine(localRulesDir, "speed.json"), @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": ""speed"", ""constraints"": { ""max"": 9999 } }]
        }");
        var configPath = WriteLocalConfig($@"{{
            ""version"": ""1.0"",
            ""rulesDirectory"": ""{_sharedDir.Replace("\\", "\\\\")}""
        }}");

        var loader = new ConfigLoader(configPath);
        var config = loader.GetConfig();

        // Same filename → shared skipped, local wins
        var speedMember = new MemberNode("speed", "", null, "speed", null, new List<MemberNode>(), false);
        var rule = config!.GetRule(speedMember);
        rule.Should().NotBeNull();
        rule!.Constraints!.Max.Should().Be(9999);
    }

    [Fact]
    public void GetConfig_NoRulesDirectory_LocalOnly()
    {
        var configPath = WriteLocalConfig(@"{ ""version"": ""1.0"" }");

        // Write rule as a rule file in the rules/ subdirectory
        var localRulesDir = Path.Combine(_tempDir, "rules");
        Directory.CreateDirectory(localRulesDir);
        File.WriteAllText(Path.Combine(localRulesDir, "local-rules.json"), @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": ""localOnly"" }]
        }");

        var loader = new ConfigLoader(configPath);
        var config = loader.GetConfig();

        config!.Rules.Should().HaveCount(1);
    }

    [Fact]
    public void GetConfig_UnreachableDirectory_FallbackToLocal()
    {
        var configPath = WriteLocalConfig(@"{
            ""version"": ""1.0"",
            ""rulesDirectory"": ""\\\\nonexistent\\share\\rules""
        }");

        // Write rule as a rule file in the rules/ subdirectory
        var localRulesDir = Path.Combine(_tempDir, "rules");
        Directory.CreateDirectory(localRulesDir);
        File.WriteAllText(Path.Combine(localRulesDir, "local-rules.json"), @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": ""local"" }]
        }");

        var loader = new ConfigLoader(configPath);
        var config = loader.GetConfig();

        config!.Rules.Should().HaveCount(1);
        config.Rules[0].PathPattern.Should().Be("local");
    }

    [Fact]
    public void GetConfig_MergesExcludeRulesFromShared()
    {
        WriteSharedRuleFile("exclude-bmk.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{
                ""pathPattern"": "".*\\.bmkId$"",
                ""excludeFromSetpoints"": true
            }]
        }");
        var configPath = WriteLocalConfig($@"{{
            ""version"": ""1.0"",
            ""rulesDirectory"": ""{_sharedDir.Replace("\\", "\\\\")}""
        }}");

        var loader = new ConfigLoader(configPath);
        var config = loader.GetConfig();

        config!.Rules.Should().Contain(r => r.ExcludeFromSetpoints && r.PathPattern!.Contains("bmkId"));
    }

    [Fact]
    public void GetConfig_RulesDirectoryReloadedAfterInvalidate()
    {
        var configPath = WriteLocalConfig($@"{{
            ""version"": ""1.0"",
            ""rulesDirectory"": ""{_sharedDir.Replace("\\", "\\\\")}""
        }}");

        var loader = new ConfigLoader(configPath);

        // First call: no rules in directory
        var config1 = loader.GetConfig();
        config1!.Rules.Should().BeEmpty();

        // Add a rule file
        WriteSharedRuleFile("new-rule.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": ""newVar"" }]
        }");

        // Cached: still empty
        var config2 = loader.GetConfig();
        config2!.Rules.Should().BeEmpty();

        // After invalidate: picks up new rule
        loader.Invalidate();
        var config3 = loader.GetConfig();
        config3!.Rules.Should().HaveCount(1);
    }
}
