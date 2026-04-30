using System.IO;
using Newtonsoft.Json;
using BlockParam.Diagnostics;
using BlockParam.Services;

namespace BlockParam.Config;

/// <summary>
/// Loads rule files from a shared directory and merges them.
/// Files are loaded alphabetically for deterministic ordering.
/// Only files with "version": "1.0" are recognized as BlockParam rules.
/// </summary>
public class RulesDirectoryLoader
{
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

        if (!Directory.Exists(directoryPath))
        {
            result.Warnings.Add($"Rules directory not found: {directoryPath}");
            return result;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directoryPath, "*.json");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase); // Alphabetical
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
            var fileName = Path.GetFileName(file);
            if (skipFileNames != null && fileName != null && skipFileNames.Contains(fileName))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(file);
                var config = JsonConvert.DeserializeObject<BulkChangeConfig>(json);

                if (config == null) continue;

                // Sentinel check: only files with version field are BlockParam rules
                if (string.IsNullOrEmpty(config.Version))
                {
                    result.Warnings.Add($"Skipped non-rule file (no version): {Path.GetFileName(file)}");
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
                            result.Warnings.Add($"Skipped rule with invalid pattern in {Path.GetFileName(file)}: {error}");
                            continue;
                        }
                    }
                    rule.Source = ruleSource;
                    result.Rules.Add(rule);
                }

            }
            catch (JsonException ex)
            {
                result.Warnings.Add($"Invalid JSON in {Path.GetFileName(file)}: {ex.Message}");
                Log.Warning(ex, "Invalid JSON in rule file: {File}", file);
            }
            catch (IOException ex)
            {
                result.Warnings.Add($"Cannot read {Path.GetFileName(file)}: {ex.Message}");
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
