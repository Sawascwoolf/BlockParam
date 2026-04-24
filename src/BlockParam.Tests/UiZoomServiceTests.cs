using System.IO;
using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class UiZoomServiceTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"blockparam-zoom-{Path.GetRandomFileName()}.json");

    [Fact]
    public void Default_matches_DefaultZoom_when_no_settings_file()
    {
        var svc = new UiZoomService(TempPath());
        svc.ZoomFactor.Should().Be(UiZoomService.DefaultZoom);
    }

    [Fact]
    public void ZoomIn_increases_by_one_step()
    {
        var svc = new UiZoomService(TempPath());
        var before = svc.ZoomFactor;
        svc.ZoomIn();
        svc.ZoomFactor.Should().BeApproximately(before + UiZoomService.StepZoom, 0.001);
    }

    [Fact]
    public void ZoomOut_decreases_by_one_step()
    {
        var svc = new UiZoomService(TempPath());
        var before = svc.ZoomFactor;
        svc.ZoomOut();
        svc.ZoomFactor.Should().BeApproximately(before - UiZoomService.StepZoom, 0.001);
    }

    [Fact]
    public void ZoomIn_clamps_at_max()
    {
        var svc = new UiZoomService(TempPath());
        for (var i = 0; i < 50; i++) svc.ZoomIn();
        svc.ZoomFactor.Should().Be(UiZoomService.MaxZoom);
    }

    [Fact]
    public void ZoomOut_clamps_at_min()
    {
        var svc = new UiZoomService(TempPath());
        for (var i = 0; i < 50; i++) svc.ZoomOut();
        svc.ZoomFactor.Should().Be(UiZoomService.MinZoom);
    }

    [Fact]
    public void ResetZoom_returns_to_default()
    {
        var svc = new UiZoomService(TempPath());
        svc.ZoomIn();
        svc.ZoomIn();
        svc.ResetZoom();
        svc.ZoomFactor.Should().Be(UiZoomService.DefaultZoom);
    }

    [Fact]
    public void SetZoom_snaps_to_005_grid()
    {
        var svc = new UiZoomService(TempPath());
        svc.SetZoom(1.234);
        svc.ZoomFactor.Should().Be(1.25);
    }

    [Fact]
    public void Zoom_persists_across_instances()
    {
        var path = TempPath();
        try
        {
            var writer = new UiZoomService(path);
            writer.SetZoom(1.5);
            writer.FlushPendingSave(); // save is debounced; flush before reading

            var reader = new UiZoomService(path);
            reader.ZoomFactor.Should().Be(1.5);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_is_debounced_under_rapid_SetZoom()
    {
        var path = TempPath();
        try
        {
            var svc = new UiZoomService(path);
            for (var i = 0; i < 10; i++) svc.ZoomIn();

            // No flush: file should not exist yet because the debounce window
            // has not elapsed between rapid-fire calls.
            File.Exists(path).Should().BeFalse();

            svc.FlushPendingSave();
            File.Exists(path).Should().BeTrue();
            File.ReadAllText(path).Should().Contain(svc.ZoomFactor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ZoomChanged_fires_on_change_but_not_on_noop()
    {
        var svc = new UiZoomService(TempPath());
        var count = 0;
        svc.ZoomChanged += _ => count++;

        svc.SetZoom(UiZoomService.DefaultZoom); // same as default, no event
        svc.SetZoom(1.5);                       // change
        svc.SetZoom(1.5);                       // same again, no event

        count.Should().Be(1);
    }

    [Fact]
    public void Ephemeral_service_does_not_read_or_write_disk()
    {
        // Seed the default settings path with a non-default value. If the
        // ephemeral service touched it, ZoomFactor would come back as 1.7 —
        // and SetZoom on the ephemeral would overwrite the on-disk value.
        var path = UiZoomService.DefaultSettingsPath();
        string? backup = null;
        try
        {
            if (File.Exists(path)) backup = File.ReadAllText(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ \"zoom\": 1.7 }");

            var ephemeral = UiZoomService.CreateEphemeral();
            ephemeral.ZoomFactor.Should().Be(UiZoomService.DefaultZoom);

            ephemeral.SetZoom(2.0);
            File.ReadAllText(path).Should().Contain("1.7"); // not overwritten
        }
        finally
        {
            if (backup != null) File.WriteAllText(path, backup);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Corrupt_settings_file_falls_back_to_default()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not valid json");
            var svc = new UiZoomService(path);
            svc.ZoomFactor.Should().Be(UiZoomService.DefaultZoom);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
