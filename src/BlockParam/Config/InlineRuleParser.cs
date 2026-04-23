using System.Text.RegularExpressions;
using Serilog;

namespace BlockParam.Config;

/// <summary>
/// Extracts inline BlockParam rule tokens from TIA member comments.
///
/// Syntax: <c>{bp_&lt;property&gt;=&lt;value&gt;}</c>, one or more tokens per comment.
/// Supported properties:
///   <list type="bullet">
///     <item><c>bp_varTable</c> — tag-table name or prefix for allowed values</item>
///     <item><c>bp_min</c> / <c>bp_max</c> — numeric or TIA-literal range bounds</item>
///     <item><c>bp_allowed</c> — comma-separated list of permitted values</item>
///     <item><c>bp_exclude</c> — true/false to hide from bulk operations</item>
///     <item><c>bp_comment</c> — comment template applied after a change</item>
///   </list>
///
/// Per issue #6, rules may live in any language variant of a comment; all
/// variants are scanned and merged. If the same property appears in two
/// languages with different values the first-seen wins and a warning is logged.
/// </summary>
public static class InlineRuleParser
{
    private static readonly Regex TokenRegex = new(
        @"\{bp_(?<key>[a-zA-Z][a-zA-Z0-9_]*)\s*=\s*(?<value>[^}]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses a single comment string and returns the extracted rule,
    /// or null if the comment contains no <c>{bp_*}</c> tokens.
    /// </summary>
    public static InlineCommentRule? Parse(string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return null;
        var rule = new InlineCommentRule();
        if (!ApplyTokens(comment!, rule, sourceLang: null, rule.Sources))
            return null;
        return rule;
    }

    /// <summary>
    /// Parses every language variant of a multilingual comment and merges
    /// the resulting rules. Returns null if no variant contains recognized
    /// tokens. Languages are iterated in alphabetical order by culture name
    /// so behaviour is deterministic regardless of dictionary insertion order.
    /// </summary>
    public static InlineCommentRule? Parse(IReadOnlyDictionary<string, string>? commentsByLang)
    {
        if (commentsByLang == null || commentsByLang.Count == 0) return null;

        var merged = new InlineCommentRule();
        var found = false;

        foreach (var kv in commentsByLang.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (ApplyTokens(kv.Value, merged, sourceLang: kv.Key, merged.Sources))
                found = true;
        }

        return found ? merged : null;
    }

    /// <summary>
    /// Extracts tokens from <paramref name="text"/> and writes them into
    /// <paramref name="rule"/>. Returns true if at least one recognized
    /// token was applied — unknown tokens alone do not count. Conflicts
    /// (same property set twice with genuinely different values) are
    /// logged; the first value wins.
    /// </summary>
    private static bool ApplyTokens(string text, InlineCommentRule rule, string? sourceLang,
        Dictionary<string, (string Lang, string Value)> sources)
    {
        var matches = TokenRegex.Matches(text);
        if (matches.Count == 0) return false;

        var appliedKnown = false;
        foreach (Match m in matches)
        {
            var key = m.Groups["key"].Value.ToLowerInvariant();
            var value = m.Groups["value"].Value.Trim();

            if (!TrySet(rule, key, value, out var normalizedKey))
            {
                Log.Logger.Warning(
                    "InlineRuleParser: unknown property 'bp_{Key}' (lang={Lang}, value='{Value}')",
                    key, sourceLang ?? "-", value);
                continue;
            }

            appliedKnown = true;

            if (sources.TryGetValue(normalizedKey, out var prev))
            {
                if (!string.Equals(prev.Value, value, StringComparison.Ordinal))
                {
                    Log.Logger.Warning(
                        "InlineRuleParser: conflict on 'bp_{Key}' — '{PrevLang}'='{PrevValue}' wins over '{ThisLang}'='{ThisValue}'",
                        normalizedKey, prev.Lang, prev.Value, sourceLang ?? "-", value);
                }
                continue;
            }

            sources[normalizedKey] = (sourceLang ?? "", value);
        }

        return appliedKnown;
    }

    /// <summary>
    /// Assigns a parsed token to the right field on <paramref name="rule"/>.
    /// Returns false for unknown keys. Existing (non-null) values are preserved
    /// so first-seen wins across language variants.
    /// </summary>
    private static bool TrySet(InlineCommentRule rule, string key, string value, out string normalizedKey)
    {
        normalizedKey = key;
        switch (key)
        {
            case "vartable":
                normalizedKey = "varTable";
                rule.VarTable ??= value;
                return true;
            case "min":
                rule.Min ??= value;
                return true;
            case "max":
                rule.Max ??= value;
                return true;
            case "allowed":
                rule.AllowedValues ??= SplitCsv(value);
                return true;
            case "exclude":
                rule.Exclude ??= ParseBool(value);
                return true;
            case "comment":
                rule.CommentTemplate ??= value;
                return true;
            default:
                return false;
        }
    }

    private static List<string> SplitCsv(string value)
        => value.Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

    private static bool ParseBool(string value)
        => value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value == "1"
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Converts an extracted inline rule into a <see cref="MemberRule"/> bound
    /// to the exact member path. The resulting rule carries <see cref="RuleSource.Inline"/>
    /// so specificity tiebreaking favours it over config-file rules.
    ///
    /// When <paramref name="isArrayMember"/> is true the pattern also matches the
    /// array's indexed elements (<c>foo</c>, <c>foo[0]</c>, <c>foo[0,1]</c>, …) so
    /// a rule placed on an array container applies to every element of that array.
    /// </summary>
    public static MemberRule ToMemberRule(InlineCommentRule inline, string memberPath, bool isArrayMember = false)
    {
        var escaped = Regex.Escape(memberPath);
        var pattern = isArrayMember
            ? "^" + escaped + @"(\[[\d,]+\])?$"
            : "^" + escaped + "$";

        var rule = new MemberRule
        {
            PathPattern = pattern,
            Source = RuleSource.Inline,
            CommentTemplate = inline.CommentTemplate,
            ExcludeFromSetpoints = inline.Exclude ?? false,
        };

        if (!string.IsNullOrEmpty(inline.VarTable))
        {
            rule.TagTableReference = new TagTableReference { TableName = inline.VarTable! };
        }

        if (inline.Min != null || inline.Max != null
            || (inline.AllowedValues != null && inline.AllowedValues.Count > 0))
        {
            rule.Constraints = new ValueConstraint
            {
                Min = inline.Min,
                Max = inline.Max,
                AllowedValues = inline.AllowedValues?.Cast<object>().ToList(),
            };
        }

        return rule;
    }
}

/// <summary>
/// Intermediate structure holding tokens extracted from one or more comment
/// language variants. Merged by <see cref="InlineRuleParser"/> before being
/// converted to a <see cref="MemberRule"/>.
/// </summary>
public class InlineCommentRule
{
    public string? VarTable { get; set; }
    public string? Min { get; set; }
    public string? Max { get; set; }
    public List<string>? AllowedValues { get; set; }
    public bool? Exclude { get; set; }
    public string? CommentTemplate { get; set; }

    /// <summary>
    /// Tracks the (language, raw value) each property was first set from — used
    /// for conflict detection across language variants. A repeated token with
    /// the same raw value is not a conflict.
    /// </summary>
    internal Dictionary<string, (string Lang, string Value)> Sources { get; }
        = new(StringComparer.OrdinalIgnoreCase);
}
