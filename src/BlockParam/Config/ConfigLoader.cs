using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using BlockParam.Diagnostics;
using BlockParam.Services;
using BlockParam.Services.Storage;
using BlockParam.Updates;

namespace BlockParam.Config;

/// <summary>
/// Loads and merges rule files from 3 directories (Project > Local > Shared).
/// The optional config.json only stores the shared rulesDirectory path.
///
/// File I/O is routed through <see cref="IBlockParamStorage"/> so tests can
/// substitute an in-memory fake and so the "no new <c>File.*</c> /
/// <c>Directory.*</c> outside the storage layer" guardrail (#85) stays
/// satisfied. The string-path constructor remains for legacy callers.
/// </summary>
public class ConfigLoader
{
    private readonly IBlockParamStorage _storage;
    private readonly string? _configPath;
    private readonly string? _scriptedRulesDirOverride;
    private string? _tiaProjectPath;
    private BulkChangeConfig? _cachedConfig;
    private bool _loaded;

    /// <summary>
    /// Override the managed-config probe path (default
    /// <c>%PROGRAMDATA%\BlockParam\config.json</c>). Tests set this so a
    /// real machine-wide file on the dev/CI box can't taint the result.
    /// </summary>
    internal string? ManagedConfigPathOverride { get; set; }

    public ConfigLoader(string? configPath = null)
        : this(FileSystemBlockParamStorage.Instance, configPath, scriptedRulesDirOverride: null)
    {
    }

    /// <summary>
    /// Scripted-only overload (DevLauncher capture mode): ignores config.json
    /// plus the local/project rule directories and loads ONLY from the given
    /// directory. Keeps captures reproducible by preventing the developer's
    /// personal %APPDATA% config from leaking into marketing screenshots.
    /// </summary>
    internal ConfigLoader(string? configPath, string? scriptedRulesDirOverride)
        : this(FileSystemBlockParamStorage.Instance, configPath, scriptedRulesDirOverride)
    {
    }

    /// <summary>
    /// Storage-injecting constructor for tests and any future caller that
    /// needs to swap out the file system (in-memory fake, restricted-perms
    /// view, etc.). Production callers should keep using the string-path
    /// constructors above.
    /// </summary>
    internal ConfigLoader(
        IBlockParamStorage storage,
        string? configPath,
        string? scriptedRulesDirOverride = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _configPath = configPath;
        _scriptedRulesDirOverride = scriptedRulesDirOverride;
    }

    /// <summary>
    /// Returns the merged config from: TIA project rules + local rules + shared rules.
    /// The optional config.json is only read for the rulesDirectory setting.
    /// </summary>
    public BulkChangeConfig? GetConfig()
    {
        if (_loaded)
            return _cachedConfig;

        _loaded = true;

        // Scripted-only fast path: if a capture script forced a rules dir,
        // load ONLY from there and skip every other source.
        if (_scriptedRulesDirOverride != null)
        {
            var loader = new RulesDirectoryLoader(_storage);
            var only = loader.LoadFromDirectory(_scriptedRulesDirOverride,
                ruleSource: RuleSource.Shared);
            foreach (var w in only.Warnings)
                Log.Warning("ScriptedRules: {Warning}", w);

            _cachedConfig = new BulkChangeConfig
            {
                Version = "1.0",
                RulesDirectory = _scriptedRulesDirOverride,
            };
            _cachedConfig.Rules.AddRange(only.Rules);
            return _cachedConfig;
        }

        // 1. Load config.json only for the rulesDirectory setting
        string? sharedRulesDir = ReadSharedRulesDirectory();

        // 2. Load local rule files
        var localRulesDir = GetLocalRulesDirectory();
        _storage.EnsureDirectory(StoragePath.FromAbsolute(localRulesDir));

        var dirLoader = new RulesDirectoryLoader(_storage);
        var localResult = dirLoader.LoadFromDirectory(localRulesDir, ruleSource: RuleSource.Local);
        var localFileNames = new HashSet<string>(
            EnumerateJsonFileNames(localRulesDir),
            StringComparer.OrdinalIgnoreCase);

        // 3. Load shared rule files (skip files that exist locally)
        LoadResult sharedResult = new();
        if (!string.IsNullOrEmpty(sharedRulesDir))
        {
            var resolvedSharedDir = ResolveRulesDirectory(sharedRulesDir!);

            sharedResult = dirLoader.LoadFromDirectory(resolvedSharedDir,
                skipFileNames: localFileNames, ruleSource: RuleSource.Shared);

            foreach (var w in sharedResult.Warnings)
                Log.Warning("SharedRules: {Warning}", w);
        }

        // 4. Load TIA project rules (highest specificity)
        LoadResult projectResult = new();
        if (!string.IsNullOrEmpty(_tiaProjectPath))
        {
            var projectRulesDir = GetTiaProjectRulesDirectory();
            if (projectRulesDir != null
                && _storage.DirectoryExists(StoragePath.FromAbsolute(projectRulesDir)))
            {
                projectResult = dirLoader.LoadFromDirectory(projectRulesDir,
                    ruleSource: RuleSource.TiaProject);

                foreach (var w in projectResult.Warnings)
                    Log.Warning("ProjectRules: {Warning}", w);
            }
        }

        // 5. Build merged config
        _cachedConfig = new BulkChangeConfig
        {
            Version = "1.0",
            RulesDirectory = sharedRulesDir
        };

        // Rules: all sources aggregated — GetRule() picks the most specific match
        _cachedConfig.Rules.AddRange(projectResult.Rules);
        _cachedConfig.Rules.AddRange(localResult.Rules);
        _cachedConfig.Rules.AddRange(sharedResult.Rules);

        Log.Information("Config merged: {RuleCount} rules ({ProjectCount} project, {LocalCount} local, {SharedCount} shared)",
            _cachedConfig.Rules.Count, projectResult.Rules.Count,
            localResult.Rules.Count, sharedResult.Rules.Count);

        return _cachedConfig;
    }

    private IEnumerable<string> EnumerateJsonFileNames(string directoryPath)
    {
        var dir = StoragePath.FromAbsolute(directoryPath);
        if (!_storage.DirectoryExists(dir)) yield break;
        foreach (var f in _storage.EnumerateFiles(dir, "*.json"))
            yield return f.FileName;
    }

    private string? ReadSharedRulesDirectory() => TryReadConfig("config file")?.RulesDirectory;

    public string? ReadLicenseServerUrl() => TryReadConfig("license server URL")?.LicenseServerUrl;

    /// <summary>
    /// Reads the optional <c>language</c> field from config.json (#50).
    /// Returns null when unset, empty, or the file is missing/unreadable —
    /// caller falls back to the OS culture in that case.
    /// </summary>
    public string? ReadLanguage()
    {
        var raw = TryReadConfig("language")?.Language;
        return string.IsNullOrWhiteSpace(raw) ? null : raw!.Trim();
    }

    /// <summary>
    /// Reads the in-app update-check settings (#61) merging in this order
    /// (later wins): user config → managed override at
    /// <c>%PROGRAMDATA%\BlockParam\config.json</c>. The managed file mirrors
    /// the licensing pattern from #20 — IT can deploy a single
    /// <c>{"updateCheck":{"enabled":false}}</c> file to disable checks
    /// fleet-wide on air-gapped engineering networks.
    /// </summary>
    public UpdateCheckSettings ReadUpdateCheckSettings()
    {
        var settings = TryReadConfig("update-check")?.UpdateCheck ?? new UpdateCheckSettings();

        var managed = ReadManagedUpdateCheckSettings();
        if (managed != null)
        {
            // Only fields the admin actually set should win.
            if (managed.EnabledExplicit) settings.Enabled = managed.Settings.Enabled;
            if (managed.IncludePrereleasesExplicit)
                settings.IncludePrereleases = managed.Settings.IncludePrereleases;
        }
        return settings;
    }

    /// <summary>
    /// Persists user-edited update-check settings to the user config file.
    /// Preserves all other config keys (rules directory, language, ...).
    /// </summary>
    public void SaveUpdateCheckSettings(UpdateCheckSettings settings)
    {
        var targetPath = _configPath ?? AppDirectories.ConfigFile;
        var sp = StoragePath.FromAbsolute(targetPath);

        BulkChangeConfig config;
        if (_storage.FileExists(sp))
        {
            try { config = Deserialize(_storage.ReadAllText(sp)) ?? new BulkChangeConfig(); }
            catch { config = new BulkChangeConfig(); }
        }
        else
        {
            config = new BulkChangeConfig();
        }

        config.UpdateCheck = settings;
        if (string.IsNullOrEmpty(config.Version)) config.Version = "1.0";

        var json = JsonConvert.SerializeObject(config, Formatting.Indented, new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        });
        _storage.WriteAllText(sp, json);
        Invalidate();
    }

    private ManagedUpdateOverride? ReadManagedUpdateCheckSettings()
    {
        try
        {
            var managedPath = ManagedConfigPathOverride ?? AppDirectories.ProgramDataConfigFile;
            var sp = StoragePath.FromAbsolute(managedPath);
            if (!_storage.FileExists(sp)) return null;

            var json = _storage.ReadAllText(sp);
            var token = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(json);
            var node = token?["updateCheck"] as Newtonsoft.Json.Linq.JObject;
            if (node == null) return null;

            var settings = node.ToObject<UpdateCheckSettings>() ?? new UpdateCheckSettings();
            return new ManagedUpdateOverride
            {
                Settings = settings,
                EnabledExplicit = node.Property("enabled") != null,
                IncludePrereleasesExplicit = node.Property("includePrereleases") != null,
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UpdateCheck: cannot read managed override");
            return null;
        }
    }

    private sealed class ManagedUpdateOverride
    {
        public UpdateCheckSettings Settings { get; set; } = new();
        public bool EnabledExplicit { get; set; }
        public bool IncludePrereleasesExplicit { get; set; }
    }

    private BulkChangeConfig? TryReadConfig(string context)
    {
        if (string.IsNullOrEmpty(_configPath)) return null;
        var sp = StoragePath.FromAbsolute(_configPath!);
        if (!_storage.FileExists(sp)) return null;

        try
        {
            return Deserialize(_storage.ReadAllText(sp));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Cannot read {Context} from {Path}", context, _configPath);
            return null;
        }
    }

    /// <summary>
    /// Sets the TIA Portal project path for loading project-embedded rules.
    /// Call this before GetConfig(). Rules are loaded from {projectPath}\UserFiles\BlockParam\
    /// </summary>
    public void SetTiaProjectPath(string? projectPath)
    {
        if (_tiaProjectPath != projectPath)
        {
            _tiaProjectPath = projectPath;
            Invalidate();
        }
    }

    /// <summary>Returns the TIA project rules directory path.</summary>
    public string? GetTiaProjectRulesDirectory() => AppDirectories.ProjectRulesDir(_tiaProjectPath);

    /// <summary>Returns the local rules directory path.</summary>
    public string GetLocalRulesDirectory()
    {
        if (!string.IsNullOrEmpty(_configPath))
        {
            var configDir = Path.GetDirectoryName(_configPath);
            if (configDir != null)
                return Path.Combine(configDir, "rules");
        }
        return AppDirectories.LocalRulesDir;
    }

    private string ResolveRulesDirectory(string rulesDir)
    {
        if (Path.IsPathRooted(rulesDir))
            return Path.GetFullPath(rulesDir); // I-035: canonicalize path
        if (_configPath != null)
        {
            var configDir = Path.GetDirectoryName(_configPath);
            if (configDir != null)
                return Path.GetFullPath(Path.Combine(configDir, rulesDir)); // I-035
        }
        return rulesDir;
    }

    /// <summary>
    /// Deserializes a JSON string into a BulkChangeConfig.
    /// Useful for testing without file I/O.
    /// </summary>
    public static BulkChangeConfig? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return JsonConvert.DeserializeObject<BulkChangeConfig>(json);
    }

    /// <summary>
    /// Saves a single rule file to disk. Creates directory if needed.
    /// Invalidates cache.
    /// </summary>
    public void SaveRuleFile(string filePath, BulkChangeConfig ruleFileContent)
    {
        _storage.WriteAllText(StoragePath.FromAbsolute(filePath), SerializeRuleFile(ruleFileContent));
        Invalidate();
    }

    /// <summary>
    /// Serializes a rule file to the canonical on-disk JSON shape (camelCase,
    /// indented, null fields omitted). Shared by <see cref="SaveRuleFile"/> and
    /// the ConfigEditor export flow (#36) so an export/import round-trip on the
    /// same machine reproduces an identical file.
    /// </summary>
    public static string SerializeRuleFile(BulkChangeConfig ruleFileContent) =>
        JsonConvert.SerializeObject(ruleFileContent, Formatting.Indented, new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        });

    /// <summary>
    /// Saves the shared rulesDirectory setting to config.json.
    /// </summary>
    public void SaveSharedRulesDirectory(string? rulesDirectory)
    {
        var targetPath = _configPath ?? AppDirectories.ConfigFile;

        var config = new BulkChangeConfig
        {
            Version = "1.0",
            RulesDirectory = string.IsNullOrWhiteSpace(rulesDirectory) ? null : rulesDirectory
        };

        var json = JsonConvert.SerializeObject(config, Formatting.Indented, new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        });
        _storage.WriteAllText(StoragePath.FromAbsolute(targetPath), json);
        Invalidate();
    }

    /// <summary>Reloads the config from disk on next access.</summary>
    public void Invalidate()
    {
        _loaded = false;
        _cachedConfig = null;
    }
}

public class ConfigException : Exception
{
    public ConfigException(string message) : base(message) { }
    public ConfigException(string message, Exception inner) : base(message, inner) { }
}
