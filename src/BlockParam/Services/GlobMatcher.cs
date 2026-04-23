using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BlockParam.Services;

/// <summary>
/// Simple glob/wildcard matcher. Supports * as "any characters".
/// If no * is present, input is treated as implicit "input*" (starts-with).
/// Caches compiled Regex patterns for performance in autocomplete scenarios.
/// </summary>
public static class GlobMatcher
{
    private const int MaxCacheSize = 256;
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    /// <summary>
    /// Matches a value against a glob pattern. Case-insensitive.
    /// </summary>
    public static bool IsMatch(string value, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return true;
        if (string.IsNullOrEmpty(value)) return false;

        var regex = Cache.GetOrAdd(pattern, CompilePattern);

        // Evict cache if it grows too large (prevents unbounded memory in long sessions)
        if (Cache.Count > MaxCacheSize)
            Cache.Clear();

        return regex.IsMatch(value);
    }

    private static Regex CompilePattern(string pattern)
    {
        var effectivePattern = pattern.Contains('*') ? pattern : pattern + "*";
        var regexPattern = "^" +
            Regex.Escape(effectivePattern).Replace("\\*", ".*") +
            "$";
        return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
