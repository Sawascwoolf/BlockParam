namespace BlockParam.Services;

/// <summary>
/// Single source of truth for paths rooted at the "BlockParam" segment.
/// Replaces inline literals like <c>Path.Combine(..., "BlockParam", "logs")</c>
/// scattered across the codebase (#86) — and is the anchor the new
/// "no new path string literals" CLAUDE.md guardrail points at.
///
/// Only the three callers in files untouched by the freemium branch are
/// migrated in the first pass: <c>Diagnostics.Log</c>, <c>UiZoomService</c>,
/// and <c>OnlineLicenseService.DefaultSharedLicenseFilePath</c>. The
/// remaining sites in <c>ConfigLoader</c> and <c>BulkChangeContextMenu</c>
/// follow once the freemium branch merges.
/// </summary>
public static class AppDirectories
{
    private const string ProductFolder = "BlockParam";

    /// <summary>Per-user app data root: <c>%APPDATA%\BlockParam</c>.</summary>
    public static string AppData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ProductFolder);

    /// <summary>Machine-wide app data root: <c>%PROGRAMDATA%\BlockParam</c>.</summary>
    public static string ProgramData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ProductFolder);

    /// <summary>Per-machine TEMP root: <c>%TEMP%\BlockParam</c>.</summary>
    public static string Temp => Path.Combine(Path.GetTempPath(), ProductFolder);

    /// <summary>Rolling log directory under <see cref="AppData"/>.</summary>
    public static string LogsDir => Path.Combine(AppData, "logs");

    /// <summary>Per-user UI zoom / window-state settings file.</summary>
    public static string UiSettingsFile => Path.Combine(AppData, "ui-settings.json");

    /// <summary>
    /// Machine-wide shared license file. IT rolls out / rotates the key by
    /// writing here; every seat on the machine adopts it on next start.
    /// </summary>
    public static string SharedLicenseFile => Path.Combine(ProgramData, "license.key");
}
