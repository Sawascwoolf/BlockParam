using BlockParam.Config;
using BlockParam.Services.Storage;
using BlockParam.Updates;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests.Storage;

/// <summary>
/// Regression tests pinning <see cref="ConfigLoader"/> to the
/// <see cref="IBlockParamStorage"/> abstraction (#85 follow-up). Disk-based
/// behavior lives in <see cref="ConfigLoaderTests"/>; this suite exercises
/// the in-memory path so any future code that drops a direct
/// <c>File.*</c> / <c>Directory.*</c> call back in fails a deterministic test.
/// </summary>
public class ConfigLoaderStorageTests
{
    private static StoragePath ConfigPath =>
        StoragePath.FromAbsolute(@"C:\bp\appdata") / "config.json";
    private static StoragePath LocalRulesDir =>
        StoragePath.FromAbsolute(@"C:\bp\appdata") / "rules";

    [Fact]
    public void GetConfig_seeds_local_rules_directory_on_first_read()
    {
        var fs = new InMemoryBlockParamStorage();
        var loader = new ConfigLoader(fs, ConfigPath.FullPath);

        var config = loader.GetConfig();

        config.Should().NotBeNull();
        config!.Rules.Should().BeEmpty();
        // GetConfig() must create the local rules dir even when no rules
        // exist yet — otherwise the editor's "+ File" command lands in a
        // missing directory and SaveRuleFile blows up.
        fs.DirectoryExists(LocalRulesDir).Should().BeTrue();
    }

    [Fact]
    public void Local_json_files_under_config_dir_are_merged_into_rules()
    {
        var fs = new InMemoryBlockParamStorage();
        fs.WriteAllText(LocalRulesDir / "speed.json",
            "{ \"version\": \"1.0\", \"rules\": [ { \"pathPattern\": \"Speed\", \"datatype\": \"Int\" } ] }");
        fs.WriteAllText(LocalRulesDir / "temp.json",
            "{ \"version\": \"1.0\", \"rules\": [ { \"pathPattern\": \"Temp\", \"datatype\": \"Real\" } ] }");

        var loader = new ConfigLoader(fs, ConfigPath.FullPath);
        var config = loader.GetConfig();

        config!.Rules.Should().HaveCount(2);
        config.Rules.Select(r => r.PathPattern)
            .Should().BeEquivalentTo("Speed", "Temp");
    }

    [Fact]
    public void SaveSharedRulesDirectory_writes_through_storage_and_invalidates()
    {
        var fs = new InMemoryBlockParamStorage();
        var loader = new ConfigLoader(fs, ConfigPath.FullPath);

        loader.SaveSharedRulesDirectory(@"C:\shared\rules");

        fs.FileExists(ConfigPath).Should().BeTrue();
        fs.ReadAllText(ConfigPath).Should().Contain(@"C:\\shared\\rules");

        // Re-read picks up what was just written — cache must have been invalidated.
        loader.GetConfig()!.RulesDirectory.Should().Be(@"C:\shared\rules");
    }

    [Fact]
    public void SaveUpdateCheckSettings_preserves_other_config_keys()
    {
        var fs = new InMemoryBlockParamStorage();
        fs.WriteAllText(ConfigPath,
            "{ \"version\": \"1.0\", \"rulesDirectory\": \"C:\\\\keep\", \"language\": \"de-DE\" }");

        var loader = new ConfigLoader(fs, ConfigPath.FullPath);
        loader.SaveUpdateCheckSettings(new UpdateCheckSettings
        {
            Enabled = false,
            IncludePrereleases = true
        });

        var raw = fs.ReadAllText(ConfigPath);
        raw.Should().Contain(@"C:\\keep");
        raw.Should().Contain("de-DE");
        raw.Should().Contain("\"enabled\"");
        raw.Should().Contain("false");
    }

    [Fact]
    public void SaveRuleFile_routes_through_storage_and_auto_creates_parent()
    {
        var fs = new InMemoryBlockParamStorage();
        var loader = new ConfigLoader(fs, ConfigPath.FullPath);

        var target = LocalRulesDir / "subdir" / "speed.json";
        loader.SaveRuleFile(target.FullPath, new BulkChangeConfig
        {
            Version = "1.0",
            Rules = { new MemberRule { PathPattern = "Speed", Datatype = "Int" } }
        });

        fs.FileExists(target).Should().BeTrue();
        fs.DirectoryExists(target.Parent).Should().BeTrue();
        fs.ReadAllText(target).Should().Contain("\"Speed\"");
    }

    [Fact]
    public void Missing_config_file_returns_empty_config_without_throwing()
    {
        var fs = new InMemoryBlockParamStorage();
        var loader = new ConfigLoader(fs, ConfigPath.FullPath);

        var config = loader.GetConfig();

        config.Should().NotBeNull();
        config!.Rules.Should().BeEmpty();
        config.RulesDirectory.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Local_rules_skip_files_already_present_in_local_when_loading_shared()
    {
        // Local overrides shared when both directories have a file of the
        // same name — verifies the dedupe path through the storage abstraction.
        var fs = new InMemoryBlockParamStorage();
        var sharedDir = StoragePath.FromAbsolute(@"C:\shared\rules");
        fs.WriteAllText(LocalRulesDir / "common.json",
            "{ \"version\": \"1.0\", \"rules\": [ { \"pathPattern\": \"LocalWins\", \"datatype\": \"Int\" } ] }");
        fs.WriteAllText(sharedDir / "common.json",
            "{ \"version\": \"1.0\", \"rules\": [ { \"pathPattern\": \"SharedLoses\", \"datatype\": \"Int\" } ] }");
        fs.WriteAllText(ConfigPath,
            $"{{ \"version\": \"1.0\", \"rulesDirectory\": \"{sharedDir.FullPath.Replace("\\", "\\\\")}\" }}");

        var loader = new ConfigLoader(fs, ConfigPath.FullPath);
        var config = loader.GetConfig();

        config!.Rules.Select(r => r.PathPattern).Should().ContainSingle().Which.Should().Be("LocalWins");
    }
}
