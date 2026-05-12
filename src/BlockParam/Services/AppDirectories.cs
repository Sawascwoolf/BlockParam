using System;
using System.IO;

namespace BlockParam.Services;

/// <summary>
/// Single source of truth for paths rooted at the "BlockParam" segment (#86).
/// Replaces inline literals like <c>Path.Combine(..., "BlockParam", "logs")</c>
/// scattered across the codebase, and is the anchor the
/// "no new path string literals" CLAUDE.md guardrail points at.
/// </summary>
public static class AppDirectories
{
    private const string ProductFolder = "BlockParam";
    private const string ConfigFileName = "config.json";

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

    /// <summary>Per-user config file (rules dir, language, update-check, ...).</summary>
    public static string ConfigFile => Path.Combine(AppData, ConfigFileName);

    /// <summary>
    /// Machine-wide managed config file. IT rolls out a single
    /// <c>{"updateCheck":{"enabled":false}}</c> / shared rules dir here and
    /// the override wins over the per-user config on every seat.
    /// </summary>
    public static string ProgramDataConfigFile => Path.Combine(ProgramData, ConfigFileName);

    /// <summary>Per-user local rule directory under <see cref="AppData"/>.</summary>
    public static string LocalRulesDir => Path.Combine(AppData, "rules");

    /// <summary>State file driving the TEMP cache-cleanup schedule.</summary>
    public static string CacheCleanupStateFile => Path.Combine(AppData, "cache-cleanup.txt");

    /// <summary>
    /// Machine-wide shared license file. IT rolls out / rotates the key by
    /// writing here; every seat on the machine adopts it on next start.
    /// </summary>
    public static string SharedLicenseFile => Path.Combine(ProgramData, "license.key");

    /// <summary>
    /// Per-project scope cache root: <c>%TEMP%\BlockParam\{scope}</c>.
    /// Scope is derived from the project path (see <c>ProjectScope.ForPath</c>)
    /// so parallel TIA instances / switched projects cannot share cache dirs.
    /// </summary>
    public static string TempScope(string scope) => Path.Combine(Temp, scope);

    /// <summary>Cached tag-table XMLs for a given project scope.</summary>
    public static string TagTablesCacheDir(string scope) => Path.Combine(TempScope(scope), "TagTables");

    /// <summary>Cached UDT-type XMLs for a given project scope.</summary>
    public static string UdtTypesCacheDir(string scope) => Path.Combine(TempScope(scope), "UdtTypes");

    /// <summary>
    /// Project-embedded rules directory:
    /// <c>{tiaProjectPath}\UserFiles\BlockParam</c>. Returns null when the
    /// project path is null/empty so callers can short-circuit.
    /// </summary>
    public static string? ProjectRulesDir(string? tiaProjectPath)
    {
        if (string.IsNullOrEmpty(tiaProjectPath)) return null;
        return Path.Combine(tiaProjectPath, "UserFiles", ProductFolder);
    }
}
