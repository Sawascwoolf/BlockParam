using FluentAssertions;
using BlockParam.Config;
using BlockParam.Models;
using Xunit;

namespace BlockParam.Tests;

public class ProfileManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public ProfileManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BlockParamProfileTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "profiles.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void SaveProfile_CreatesEntry()
    {
        var mgr = new ProfileManager(_filePath);
        mgr.Save(new ChangeProfile { Name = "Test", PathPattern = @".*\\\.ModuleId", NewValue = "42" });

        mgr.GetAll().Should().HaveCount(1);
        mgr.GetAll()[0].Name.Should().Be("Test");
    }

    [Fact]
    public void LoadProfile_RestoresValues()
    {
        var mgr1 = new ProfileManager(_filePath);
        mgr1.Save(new ChangeProfile
        {
            Name = "SetModuleId",
            PathPattern = @".*\\\.ModuleId",
            MemberDatatype = "Int",
            NewValue = "42",
            ScopePreference = "broadest"
        });

        // New instance reads from disk
        var mgr2 = new ProfileManager(_filePath);
        var profile = mgr2.FindByName("SetModuleId");

        profile.Should().NotBeNull();
        profile!.PathPattern.Should().Be(@".*\\\.ModuleId");
        profile.NewValue.Should().Be("42");
        profile.ScopePreference.Should().Be("broadest");
    }

    [Fact]
    public void DeleteProfile_RemovesEntry()
    {
        var mgr = new ProfileManager(_filePath);
        mgr.Save(new ChangeProfile { Name = "A" });
        mgr.Save(new ChangeProfile { Name = "B" });

        mgr.Delete("A");

        mgr.GetAll().Should().HaveCount(1);
        mgr.GetAll()[0].Name.Should().Be("B");
    }

    [Fact]
    public void RoundTrip_SaveLoad_Identical()
    {
        var original = new ChangeProfile
        {
            Name = "Full Test",
            PathPattern = @".*\\\.Speed",
            MemberDatatype = "Int",
            NewValue = "1500",
            ScopePreference = "narrowest",
            Description = "Set speed to 1500"
        };

        var mgr1 = new ProfileManager(_filePath);
        mgr1.Save(original);

        var mgr2 = new ProfileManager(_filePath);
        var loaded = mgr2.FindByName("Full Test")!;

        loaded.PathPattern.Should().Be(original.PathPattern);
        loaded.NewValue.Should().Be(original.NewValue);
        loaded.ScopePreference.Should().Be(original.ScopePreference);
        loaded.Description.Should().Be(original.Description);
    }

    [Fact]
    public void MissingFile_ReturnsEmptyList()
    {
        var mgr = new ProfileManager(Path.Combine(_tempDir, "nonexistent.json"));

        mgr.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void SaveExistingName_Updates()
    {
        var mgr = new ProfileManager(_filePath);
        mgr.Save(new ChangeProfile { Name = "Test", NewValue = "1" });
        mgr.Save(new ChangeProfile { Name = "Test", NewValue = "2" });

        mgr.GetAll().Should().HaveCount(1);
        mgr.GetAll()[0].NewValue.Should().Be("2");
    }
}
