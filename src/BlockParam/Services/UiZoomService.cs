using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using BlockParam.Diagnostics;

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

    // 300ms coalesces a rapid Ctrl+scroll gesture into a single disk write —
    // without it the service touches ui-settings.json on every wheel tick.
    private const int SaveDebounceMs = 300;

    private readonly string _settingsPath;
    private readonly object _saveLock = new();
    private double _zoomFactor = DefaultZoom;
    private bool _loaded;
    private Timer? _saveTimer;
    private bool _savePending;

    public event Action<double>? ZoomChanged;

    private static UiZoomService? _shared;
    public static UiZoomService Shared => _shared ??= new UiZoomService();

    /// <summary>
    /// Replaces the shared singleton. Used by DevLauncher --capture to swap
    /// in an <see cref="CreateEphemeral"/> instance so video captures don't
    /// inherit the developer's personal %APPDATA% zoom.
    /// Call BEFORE any dialog is constructed. Internal: only capture-mode
    /// code should mutate the global zoom state.
    /// </summary>
    internal static void ReplaceShared(UiZoomService service) => _shared = service;

    /// <summary>
    /// Creates an in-memory-only zoom service that neither reads from nor
    /// writes to disk, so it always starts at <see cref="DefaultZoom"/>.
    /// </summary>
    public static UiZoomService CreateEphemeral() => new(settingsPath: "");

    public UiZoomService() : this(DefaultSettingsPath()) { }

    public UiZoomService(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    private bool PersistenceEnabled => !string.IsNullOrEmpty(_settingsPath);

    public static string DefaultSettingsPath() => AppDirectories.UiSettingsFile;

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
        ScheduleSave();
        ZoomChanged?.Invoke(_zoomFactor);
    }

    /// <summary>
    /// Forces any pending debounced save to flush synchronously. Useful for
    /// tests and for callers that want to persist the final value before
    /// spawning a subprocess that reads the same file.
    /// </summary>
    public void FlushPendingSave()
    {
        lock (_saveLock)
        {
            if (!_savePending) return;
            _saveTimer?.Dispose();
            _saveTimer = null;
            _savePending = false;
        }
        Save();
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
            Log.Warning(ex, "UiZoomService: cannot read {Path} — using default zoom", _settingsPath);
        }
    }

    private void ScheduleSave()
    {
        if (!PersistenceEnabled) return;
        lock (_saveLock)
        {
            _savePending = true;
            _saveTimer?.Dispose();
            _saveTimer = new Timer(_ =>
            {
                lock (_saveLock)
                {
                    if (!_savePending) return;
                    _savePending = false;
                }
                Save();
            }, null, SaveDebounceMs, Timeout.Infinite);
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
            Log.Warning(ex, "UiZoomService: cannot save {Path}", _settingsPath);
        }
    }

    private static double Clamp(double v) => Math.Max(MinZoom, Math.Min(MaxZoom, v));
    private static double Round(double v) => Math.Round(v * 20) / 20.0; // snap to 0.05 grid

    // Public so Newtonsoft.Json can reflect into its constructor and properties
    // when the addin runs under TIA's partial-trust CAS sandbox — a private
    // nested type fails JsonConvert.DeserializeObject with MethodAccessException
    // (no RestrictedMemberAccess in the Siemens publisher XSD).
    public class UiSettingsDto
    {
        [JsonProperty("zoom")]
        public double Zoom { get; set; }
    }
}
