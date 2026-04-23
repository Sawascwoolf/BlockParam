using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Serilog;

namespace BlockParam.Config;

/// <summary>
/// Loads and merges rule files from 3 directories (Project > Local > Shared).
/// The optional config.json only stores the shared rulesDirectory path.
/// </summary>
public class ConfigLoader
{
    private readonly string? _configPath;
    private readonly string? _scriptedRulesDirOverride;
    private string? _tiaProjectPath;
    private BulkChangeConfig? _cachedConfig;
    private bool _loaded;

    public ConfigLoader(string? configPath = null)
    {
        _configPath = configPath;
    }

    /// <summary>
    /// Scripted-only overload (DevLauncher capture mode): ignores config.json
    /// plus the local/project rule directories and loads ONLY from the given
    /// directory. Keeps captures reproducible by preventing the developer's
    /// personal %APPDATA% config from leaking into marketing screenshots.
    /// </summary>
    internal ConfigLoader(string? configPath, string? scriptedRulesDirOverride)
    {
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
            var loader = new RulesDirectoryLoader();
            var only = loader.LoadFromDirectory(_scriptedRulesDirOverride,
                ruleSource: RuleSource.Shared);
            foreach (var w in only.Warnings)
                Log.Logger.Warning("ScriptedRules: {Warning}", w);

            _cachedConfig = new BulkChangeConfig
            {
                Version = "1.0",
                RulesDirectory = _scriptedRulesDirOverride,
            };
            _cachedConfig.Rules.AddRange(only.Rules);
            Log.Logger.Information("Config (scripted): {RuleCount} rules from {Dir}",
                _cachedConfig.Rules.Count, _scriptedRulesDirOverride);
            return _cachedConfig;
        }

        // 1. Load config.json only for the rulesDirectory setting
        string? sharedRulesDir = ReadSharedRulesDirectory();

        // 2. Load local rule files
        var localRulesDir = GetLocalRulesDirectory();
        Directory.CreateDirectory(localRulesDir);

        var dirLoader = new RulesDirectoryLoader();
        var localResult = dirLoader.LoadFromDirectory(localRulesDir, ruleSource: RuleSource.Local);
        var localFileNames = new HashSet<string>(
            Directory.Exists(localRulesDir)
                ? Directory.GetFiles(localRulesDir, "*.json").Select(Path.GetFileName)
                    .Where(n => n != null).Select(n => n!)
                : Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        // 3. Load shared rule files (skip files that exist locally)
        LoadResult sharedResult = new();
        if (!string.IsNullOrEmpty(sharedRulesDir))
        {
            var resolvedSharedDir = ResolveRulesDirectory(sharedRulesDir);
            Log.Logger.Information("ConfigLoader: shared rulesDirectory={RulesDir}", resolvedSharedDir);

            sharedResult = dirLoader.LoadFromDirectory(resolvedSharedDir,
                skipFileNames: localFileNames, ruleSource: RuleSource.Shared);

            foreach (var w in sharedResult.Warnings)
                Log.Logger.Warning("SharedRules: {Warning}", w);
        }

        // 4. Load TIA project rules (highest specificity)
        LoadResult projectResult = new();
        if (!string.IsNullOrEmpty(_tiaProjectPath))
        {
            var projectRulesDir = GetTiaProjectRulesDirectory();
            if (projectRulesDir != null && Directory.Exists(projectRulesDir))
            {
                Log.Logger.Information("ConfigLoader: TIA project rules from {Dir}", projectRulesDir);
                projectResult = dirLoader.LoadFromDirectory(projectRulesDir,
                    ruleSource: RuleSource.TiaProject);

                foreach (var w in projectResult.Warnings)
                    Log.Logger.Warning("ProjectRules: {Warning}", w);
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

        Log.Logger.Information("Config merged: {RuleCount} rules ({ProjectCount} project, {LocalCount} local, {SharedCount} shared)",
            _cachedConfig.Rules.Count, projectResult.Rules.Count,
            localResult.Rules.Count, sharedResult.Rules.Count);

        return _cachedConfig;
    }

    private string? ReadSharedRulesDirectory() => TryReadConfig("config file")?.RulesDirectory;

    public string? ReadLicenseServerUrl() => TryReadConfig("license server URL")?.LicenseServerUrl;

    private BulkChangeConfig? TryReadConfig(string context)
    {
        if (string.IsNullOrEmpty(_configPath) || !File.Exists(_configPath))
            return null;

        try
        {
            return Deserialize(File.ReadAllText(_configPath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Logger.Warning(ex, "Cannot read {Context} from {Path}", context, _configPath);
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
    public string? GetTiaProjectRulesDirectory()
    {
        if (string.IsNullOrEmpty(_tiaProjectPath)) return null;
        return Path.Combine(_tiaProjectPath, "UserFiles", "BlockParam");
    }

    /// <summary>Returns the local rules directory path.</summary>
    public string GetLocalRulesDirectory()
    {
        if (!string.IsNullOrEmpty(_configPath))
        {
            var configDir = Path.GetDirectoryName(_configPath);
            if (configDir != null)
                return Path.Combine(configDir, "rules");
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlockParam", "rules");
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
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonConvert.SerializeObject(ruleFileContent, Formatting.Indented, new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        });
        File.WriteAllText(filePath, json);
        Log.Logger.Information("Saved rule file: {Path}", filePath);
        Invalidate();
    }

    /// <summary>
    /// Saves the shared rulesDirectory setting to config.json.
    /// </summary>
    public void SaveSharedRulesDirectory(string? rulesDirectory)
    {
        var targetPath = _configPath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlockParam", "config.json");

        var config = new BulkChangeConfig
        {
            Version = "1.0",
            RulesDirectory = string.IsNullOrWhiteSpace(rulesDirectory) ? null : rulesDirectory
        };

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonConvert.SerializeObject(config, Formatting.Indented, new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        });
        File.WriteAllText(targetPath, json);
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
