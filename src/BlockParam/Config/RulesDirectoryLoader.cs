using System.IO;
using Newtonsoft.Json;
using BlockParam.Diagnostics;
using BlockParam.Services;
using BlockParam.Services.Storage;

namespace BlockParam.Config;

/// <summary>
/// Loads rule files from a shared directory and merges them.
/// Files are loaded alphabetically for deterministic ordering.
/// Only files with "version": "1.0" are recognized as BlockParam rules.
///
/// File I/O is routed through <see cref="IBlockParamStorage"/> so the
/// storage-injecting <see cref="ConfigLoader"/> path stays disk-free in
/// tests, satisfying the "no new <c>File.*</c> / <c>Directory.*</c> outside
/// the storage layer" guardrail (#85).
/// </summary>
public class RulesDirectoryLoader
{
    private readonly IBlockParamStorage _storage;

    public RulesDirectoryLoader() : this(FileSystemBlockParamStorage.Instance) { }

    public RulesDirectoryLoader(IBlockParamStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    /// <summary>
    /// Loads all .json rule files from the directory.
    /// Tolerates missing/unreachable directories (returns empty with warning).
    /// </summary>
    /// <param name="skipFileNames">Filenames to skip (for dedup with local overrides).</param>
    /// <param name="ruleSource">Source tag for loaded rules (Shared, Local, TiaProject).</param>
    public LoadResult LoadFromDirectory(string directoryPath,
        HashSet<string>? skipFileNames = null,
        RuleSource ruleSource = RuleSource.Shared)
    {
        var result = new LoadResult();

        if (string.IsNullOrWhiteSpace(directoryPath))
            return result;

        var dir = StoragePath.FromAbsolute(directoryPath);
        if (!_storage.DirectoryExists(dir))
        {
            result.Warnings.Add($"Rules directory not found: {directoryPath}");
            return result;
        }

        StoragePath[] files;
        try
        {
            files = _storage.EnumerateFiles(dir, "*.json")
                .OrderBy(p => p.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Warnings.Add($"Cannot access rules directory: {ex.Message}");
            Log.Warning(ex, "Cannot access rules directory: {Path}", directoryPath);
            return result;
        }

        foreach (var file in files)
        {
            // Skip files that exist in the local override directory
            var fileName = file.FileName;
            if (skipFileNames != null && fileName != null && skipFileNames.Contains(fileName))
            {
                continue;
            }

            try
            {
                var json = _storage.ReadAllText(file);
                var config = JsonConvert.DeserializeObject<BulkChangeConfig>(json);

                if (config == null) continue;

                // Sentinel check: only files with version field are BlockParam rules
                if (string.IsNullOrEmpty(config.Version))
                {
                    result.Warnings.Add($"Skipped non-rule file (no version): {file.FileName}");
                    continue;
                }

                // Validate patterns
                foreach (var rule in config.Rules)
                {
                    if (!string.IsNullOrEmpty(rule.PathPattern))
                    {
                        var error = PathPatternMatcher.ValidatePattern(rule.PathPattern!);
                        if (error != null)
                        {
                            result.Warnings.Add($"Skipped rule with invalid pattern in {file.FileName}: {error}");
                            continue;
                        }
                    }
                    rule.Source = ruleSource;
                    result.Rules.Add(rule);
                }

            }
            catch (JsonException ex)
            {
                result.Warnings.Add($"Invalid JSON in {file.FileName}: {ex.Message}");
                Log.Warning(ex, "Invalid JSON in rule file: {File}", file);
            }
            catch (IOException ex)
            {
                result.Warnings.Add($"Cannot read {file.FileName}: {ex.Message}");
                Log.Warning(ex, "Cannot read rule file: {File}", file);
            }
        }

        Log.Information("Loaded {RuleCount} rules from {FileCount} files in {Dir}",
            result.Rules.Count, files.Length, directoryPath);

        return result;
    }
}

public class LoadResult
{
    public List<MemberRule> Rules { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
