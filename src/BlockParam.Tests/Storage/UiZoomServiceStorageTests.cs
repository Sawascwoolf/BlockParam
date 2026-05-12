using BlockParam.Services;
using BlockParam.Services.Storage;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests.Storage;

/// <summary>
/// Exercises <see cref="UiZoomService"/>'s new storage-injecting constructor.
/// Disk-based behavior stays in <see cref="UiZoomServiceTests"/>; this suite
/// uses an in-memory fake so persistence assertions are deterministic.
/// </summary>
public class UiZoomServiceStorageTests
{
    private static StoragePath SettingsPath =>
        StoragePath.FromAbsolute(@"C:\appdata") / "BlockParam" / "ui-settings.json";

    [Fact]
    public void Loads_default_when_settings_file_missing()
    {
        var fs = new InMemoryBlockParamStorage();
        var svc = new UiZoomService(fs, SettingsPath);

        svc.ZoomFactor.Should().Be(UiZoomService.DefaultZoom);
    }

    [Fact]
    public void Persists_via_injected_storage_only()
    {
        var fs = new InMemoryBlockParamStorage();
        var svc = new UiZoomService(fs, SettingsPath);

        svc.SetZoom(1.5);
        svc.FlushPendingSave();

        fs.FileExists(SettingsPath).Should().BeTrue();
        fs.ReadAllText(SettingsPath).Should().Contain("1.5");
    }

    [Fact]
    public void Reads_previously_written_value_on_second_instance()
    {
        var fs = new InMemoryBlockParamStorage();

        var writer = new UiZoomService(fs, SettingsPath);
        writer.SetZoom(1.5);
        writer.FlushPendingSave();

        var reader = new UiZoomService(fs, SettingsPath);
        reader.ZoomFactor.Should().Be(1.5);
    }

    [Fact]
    public void Corrupt_settings_falls_back_to_default()
    {
        var fs = new InMemoryBlockParamStorage();
        fs.WriteAllText(SettingsPath, "{not valid json");

        var svc = new UiZoomService(fs, SettingsPath);

        svc.ZoomFactor.Should().Be(UiZoomService.DefaultZoom);
    }

    [Fact]
    public void Ephemeral_storage_path_disables_persistence()
    {
        var fs = new InMemoryBlockParamStorage();

        // default(StoragePath) signals "no persistence" — same contract as
        // CreateEphemeral() in the legacy string-based constructor.
        var svc = new UiZoomService(fs, default);

        svc.SetZoom(1.5);
        svc.FlushPendingSave();

        fs.FileExists(SettingsPath).Should().BeFalse();
    }
}
