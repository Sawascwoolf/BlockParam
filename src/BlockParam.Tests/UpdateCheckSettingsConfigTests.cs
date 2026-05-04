using System.IO;
using FluentAssertions;
using BlockParam.Config;
using BlockParam.Updates;
using Xunit;

namespace BlockParam.Tests;

public class UpdateCheckSettingsConfigTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;
    private readonly string _managedPath;

    public UpdateCheckSettingsConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BlockParamUpdateCfg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.json");
        // Point the managed-config probe at a non-existent file so a real
        // %PROGRAMDATA%\BlockParam\config.json on the host machine cannot
        // taint the test outcome.
        _managedPath = Path.Combine(_tempDir, "managed-config.json");
    }

    private ConfigLoader Build()
    {
        var loader = new ConfigLoader(_configPath);
        loader.ManagedConfigPathOverride = _managedPath;
        return loader;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void ReadUpdateCheckSettings_DefaultsWhenFileMissing()
    {
        var loader = Build();
        var settings = loader.ReadUpdateCheckSettings();

        settings.Enabled.Should().BeTrue();
        settings.IncludePrereleases.Should().BeFalse();
    }

    [Fact]
    public void ReadUpdateCheckSettings_FromUserConfig()
    {
        File.WriteAllText(_configPath, @"{
            ""version"": ""1.0"",
            ""updateCheck"": {
                ""enabled"": false,
                ""includePrereleases"": true
            }
        }");

        var loader = Build();
        var settings = loader.ReadUpdateCheckSettings();

        settings.Enabled.Should().BeFalse();
        settings.IncludePrereleases.Should().BeTrue();
    }

    [Fact]
    public void ReadUpdateCheckSettings_ManagedOverride_DisablesEnabledFlag()
    {
        // User locally has updates ON.
        File.WriteAllText(_configPath, @"{
            ""updateCheck"": { ""enabled"": true }
        }");
        // IT pushes a managed config that disables the check.
        File.WriteAllText(_managedPath, @"{
            ""updateCheck"": { ""enabled"": false }
        }");

        var loader = Build();
        var settings = loader.ReadUpdateCheckSettings();

        // Managed flag wins.
        settings.Enabled.Should().BeFalse();
    }

    [Fact]
    public void SaveUpdateCheckSettings_PreservesOtherKeys()
    {
        File.WriteAllText(_configPath, @"{
            ""version"": ""1.0"",
            ""rulesDirectory"": ""C:\\rules"",
            ""language"": ""de""
        }");

        var loader = Build();
        loader.SaveUpdateCheckSettings(new UpdateCheckSettings
        {
            Enabled = false,
            IncludePrereleases = true,
        });

        var json = File.ReadAllText(_configPath);
        json.Should().Contain("\"rulesDirectory\"");
        json.Should().Contain("\"language\"");
        json.Should().Contain("\"updateCheck\"");

        // Round-trip through the loader
        loader.Invalidate();
        var roundtrip = loader.ReadUpdateCheckSettings();
        roundtrip.Enabled.Should().BeFalse();
        roundtrip.IncludePrereleases.Should().BeTrue();
    }
}
