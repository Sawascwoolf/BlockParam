using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using BlockParam.Models;

namespace BlockParam.Services;

/// <summary>
/// Matches a MemberNode against a path pattern.
/// Pattern syntax: Regex on the dot-separated member path, with {udt:TypeName}
/// tokens that match segments whose ancestor has the specified UDT datatype,
/// and {childUdt:TypeName} tokens that match nodes having a direct child of
/// the specified UDT type.
///
/// PathPattern values are raw .NET regex. {udt:TypeName} tokens are expanded
/// before the regex is evaluated. UDT type names are compared without quotes.
/// The member itself is excluded from ancestor matching (only parents are checked).
/// </summary>
public static class PathPatternMatcher
{
    private static readonly Regex UdtTokenRegex = new(@"\{udt:([^}]+)\}", RegexOptions.Compiled);
    private static readonly Regex ChildUdtTokenRegex = new(@"\{childUdt:([^}]+)\}", RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private const int MaxCacheSize = 256;

    /// <summary>
    /// Checks if a MemberNode matches the given path pattern.
    /// When includeSelf is true, the member itself is added to the ancestor chain
    /// so {udt:TypeName}$ can match the node directly (used for comment rules on UDT instances).
    /// </summary>
    public static bool IsMatch(MemberNode member, string pattern, bool includeSelf = false)
    {
        if (string.IsNullOrEmpty(pattern)) return false;

        try
        {
            // Pre-check {childUdt:TypeName} tokens: member must have a child of that UDT type
            var childUdtTokens = ChildUdtTokenRegex.Matches(pattern);
            foreach (Match token in childUdtTokens)
            {
                var requiredType = token.Groups[1].Value;
                if (!member.Children.Any(c =>
                    string.Equals(c.Datatype.Trim('"'), requiredType, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
            // Strip {childUdt:} tokens — they constrain the node, not the path
            var effectivePattern = ChildUdtTokenRegex.Replace(pattern, "");
            // Clean up leftover dots from stripping only when tokens were actually removed
            if (childUdtTokens.Count > 0)
            {
                effectivePattern = effectivePattern.Replace("..", ".").TrimEnd('.');
                if (effectivePattern.EndsWith(".$")) effectivePattern = effectivePattern[..^2] + "$";
                if (string.IsNullOrEmpty(effectivePattern) || effectivePattern == "$")
                    effectivePattern = ".*";
            }

            if (!UdtTokenRegex.IsMatch(effectivePattern))
            {
                return GetOrCompile(effectivePattern).IsMatch(member.Path);
            }

            // Build ancestor chain (parents only by default, or including self)
            var ancestors = BuildAncestorChain(member, includeSelf);
            return MatchWithUdtTokens(member, effectivePattern, ancestors);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Calculates the specificity score of a pattern. Higher = more specific.
    ///
    /// Scoring (inspired by CSS/Drupal specificity):
    /// - Exact path anchored with ^ and $: +100
    /// - Each {udt:TypeName} token: +20 (type-level constraint)
    /// - Each literal segment (not .* or .*?): +10
    /// - Datatype filter on the rule: +5
    /// - Pattern anchored at end ($): +3
    /// - Pattern anchored at start (^): +3
    /// - Pure wildcard (.*): +1
    ///
    /// Source bonus (added by caller, not computed here):
    /// - Shared rules: +0
    /// - Local rules: +50
    /// - TIA project rules: +200
    ///
    /// The source bonus is a tiebreaker — a highly specific shared rule
    /// still beats a generic TIA project rule.
    /// </summary>
    public static int CalculateSpecificity(string? pathPattern, string? _unused, string? datatype,
        int sourceBonus = 0)
    {
        if (string.IsNullOrEmpty(pathPattern))
            return 0;

        int score = 0;

        var pattern = pathPattern!;

        // Strip {udt:} and {childUdt:} tokens and count them
        var udtTokens = UdtTokenRegex.Matches(pattern);
        score += udtTokens.Count * 20;
        var childUdtTokens = ChildUdtTokenRegex.Matches(pattern);
        score += childUdtTokens.Count * 20;

        // Count literal segments (parts between regex wildcards)
        var stripped = UdtTokenRegex.Replace(pattern, "PLACEHOLDER");
        var parts = stripped.Split('.');
        foreach (var part in parts)
        {
            var trimmed = part.Trim('^', '$');
            if (trimmed != ".*" && trimmed != ".*?" && trimmed != ""
                && !trimmed.Contains(".*") && !trimmed.Contains("["))
                score += 10; // Literal segment
        }

        // Anchoring bonuses
        if (pattern.StartsWith("^")) score += 3;
        if (pattern.EndsWith("$")) score += 3;
        if (pattern.StartsWith("^") && pattern.EndsWith("$")) score += 100; // Exact path

        // Datatype filter
        if (!string.IsNullOrEmpty(datatype)) score += 5;

        // Minimum score for any matching pattern
        if (score == 0) score = 1;

        // Source bonus (tiebreaker for same-specificity rules from different sources)
        score += sourceBonus;

        return score;
    }

    /// <summary>
    /// Validates that a pattern string is a valid regex (optionally with {udt:} tokens).
    /// Returns null if valid, or an error message if invalid.
    /// </summary>
    public static string? ValidatePattern(string pattern)
    {
        try
        {
            // Strip {udt:...} and {childUdt:...} tokens before validating regex
            var stripped = UdtTokenRegex.Replace(pattern, "placeholder");
            stripped = ChildUdtTokenRegex.Replace(stripped, "");
            _ = new Regex(stripped);
            return null;
        }
        catch (ArgumentException ex)
        {
            return $"Invalid regex pattern: {ex.Message}";
        }
    }

    private static bool MatchWithUdtTokens(
        MemberNode member, string pattern,
        List<(string Name, string Datatype)> ancestors)
    {
        var expandedPattern = pattern;
        var tokens = UdtTokenRegex.Matches(pattern);
        int searchStartIndex = 0;

        foreach (Match token in tokens)
        {
            var udtType = token.Groups[1].Value;
            var matchResult = FindAncestorByUdtType(ancestors, udtType, searchStartIndex);

            if (matchResult == null)
                return false;

            var (ancestor, foundIndex) = matchResult.Value;

            // Replace only this specific token occurrence (use index-based replacement)
            var tokenStart = expandedPattern.IndexOf(token.Value, StringComparison.Ordinal);
            if (tokenStart < 0) return false;

            expandedPattern = expandedPattern.Remove(tokenStart, token.Value.Length)
                .Insert(tokenStart, Regex.Escape(ancestor.Name));

            // Next search starts after this ancestor (positional semantics)
            searchStartIndex = foundIndex + 1;
        }

        return GetOrCompile(expandedPattern).IsMatch(member.Path);
    }

    private static ((string Name, string Datatype) Ancestor, int Index)? FindAncestorByUdtType(
        List<(string Name, string Datatype)> ancestors, string udtType, int startIndex)
    {
        for (int i = startIndex; i < ancestors.Count; i++)
        {
            var cleanType = ancestors[i].Datatype.Trim('"');
            if (string.Equals(cleanType, udtType, StringComparison.OrdinalIgnoreCase))
                return (ancestors[i], i);
        }
        return null;
    }

    /// <summary>
    /// Builds ancestor chain from root to direct parent.
    /// When includeSelf is true, appends the member itself at the end.
    /// </summary>
    private static List<(string Name, string Datatype)> BuildAncestorChain(
        MemberNode member, bool includeSelf = false)
    {
        var chain = new List<(string Name, string Datatype)>();
        var current = member.Parent; // Start from parent, not member itself
        while (current != null)
        {
            chain.Add((current.Name, current.Datatype));
            current = current.Parent;
        }
        chain.Reverse();
        if (includeSelf)
            chain.Add((member.Name, member.Datatype));
        return chain;
    }

    private static Regex GetOrCompile(string pattern)
    {
        var regex = Cache.GetOrAdd(pattern, p =>
            new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout));

        if (Cache.Count > MaxCacheSize)
            Cache.Clear();

        return regex;
    }
}
