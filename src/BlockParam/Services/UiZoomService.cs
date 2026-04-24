using System;
using System.IO;
using Newtonsoft.Json;
using Serilog;

namespace BlockParam.Services;

/// <summary>
/// Persists the user's UI zoom factor across sessions.
/// Backed by %APPDATA%\BlockParam\ui-settings.json so each workstation keeps
/// its own preference — zoom is very display-specific and shouldn't roam.
/// </summary>
public class UiZoomService
{
    public const double MinZoom = 0.7;
    public const double MaxZoom = 2.5;
    public const double StepZoom = 0.1;
    public const double DefaultZoom = 1.2;

    private readonly string _settingsPath;
    private double _zoomFactor = DefaultZoom;
    private bool _loaded;

    public event Action<double>? ZoomChanged;

    private static UiZoomService? _shared;
    public static UiZoomService Shared => _shared ??= new UiZoomService();

    /// <summary>
    /// Replaces the shared singleton. Used by DevLauncher --capture to swap
    /// in an <see cref="CreateEphemeral"/> instance so video captures don't
    /// inherit the developer's personal %APPDATA% zoom.
    /// Call BEFORE any dialog is constructed.
    /// </summary>
    public static void ReplaceShared(UiZoomService service) => _shared = service;

    /// <summary>
    /// Creates an in-memory-only zoom service that neither reads from nor
    /// writes to disk, so it always starts at <see cref="DefaultZoom"/>.
    /// Also disables <see cref="AutoResizeWindow"/> since capture mode drives
    /// window size via its own viewport config.
    /// </summary>
    public static UiZoomService CreateEphemeral()
    {
        var s = new UiZoomService(settingsPath: "");
        s.AutoResizeWindow = false;
        return s;
    }

    /// <summary>
    /// When true (default), ZoomHost scales the window dimensions
    /// proportionally as the zoom changes so scaled content fits. Capture
    /// mode sets this false because the scene viewport is authoritative.
    /// </summary>
    public bool AutoResizeWindow { get; set; } = true;

    public UiZoomService() : this(DefaultSettingsPath()) { }

    public UiZoomService(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    private bool PersistenceEnabled => !string.IsNullOrEmpty(_settingsPath);

    public static string DefaultSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BlockParam", "ui-settings.json");

    public double ZoomFactor
    {
        get
        {
            EnsureLoaded();
            return _zoomFactor;
        }
    }

    public void ZoomIn() => SetZoom(_zoomFactor + StepZoom);
    public void ZoomOut() => SetZoom(_zoomFactor - StepZoom);
    public void ResetZoom() => SetZoom(DefaultZoom);

    public void SetZoom(double factor)
    {
        EnsureLoaded();
        var clamped = Clamp(Round(factor));
        if (Math.Abs(clamped - _zoomFactor) < 0.0001) return;

        _zoomFactor = clamped;
        Save();
        ZoomChanged?.Invoke(_zoomFactor);
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        if (!PersistenceEnabled || !File.Exists(_settingsPath)) return;

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var parsed = JsonConvert.DeserializeObject<UiSettingsDto>(json);
            if (parsed != null && parsed.Zoom > 0)
                _zoomFactor = Clamp(parsed.Zoom);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Logger.Warning(ex, "UiZoomService: cannot read {Path} — using default zoom", _settingsPath);
        }
    }

    private void Save()
    {
        if (!PersistenceEnabled) return;
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonConvert.SerializeObject(new UiSettingsDto { Zoom = _zoomFactor }, Formatting.Indented);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Logger.Warning(ex, "UiZoomService: cannot save {Path}", _settingsPath);
        }
    }

    private static double Clamp(double v) => Math.Max(MinZoom, Math.Min(MaxZoom, v));
    private static double Round(double v) => Math.Round(v * 20) / 20.0; // snap to 0.05 grid

    private class UiSettingsDto
    {
        [JsonProperty("zoom")]
        public double Zoom { get; set; }
    }
}
