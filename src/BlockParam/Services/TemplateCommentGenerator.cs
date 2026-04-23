using System.Text.RegularExpressions;
using BlockParam.Config;
using BlockParam.Models;

namespace BlockParam.Services;

/// <summary>
/// Generates comments from a template string with placeholders.
/// Placeholders: {db}, {parent}, {self}, {memberName}, {memberName.comment}, {memberName.name}
/// Uses tag table lookups for .comment and .name resolution.
/// Templates are now defined per-rule via MemberRule.CommentTemplate.
/// </summary>
public class TemplateCommentGenerator
{
    private static readonly Regex PlaceholderRegex = new(@"\{([^}]+)\}", RegexOptions.Compiled);

    private readonly TagTableCache? _tagTableCache;
    private readonly BulkChangeConfig _config;

    public TemplateCommentGenerator(BulkChangeConfig config, TagTableCache? tagTableCache = null)
    {
        _config = config;
        _tagTableCache = tagTableCache;
    }

    /// <summary>
    /// Generates a comment for a UDT instance from the template.
    /// Reads child member start values and resolves tag table constants.
    /// </summary>
    public string Generate(DataBlockInfo db, MemberNode udtInstance, string template,
        string? language = null, Func<MemberNode, string?>? valueResolver = null)
    {
        var result = template;

        // Simple placeholders
        result = result.Replace("{db}", db.Name);
        result = result.Replace("{parent}", udtInstance.Parent?.Name ?? "");
        result = result.Replace("{self}", udtInstance.Name);

        // Member value and tag-table placeholders
        var placeholders = PlaceholderRegex.Matches(result);
        foreach (Match ph in placeholders)
        {
            var key = ph.Groups[1].Value;
            if (key is "db" or "parent" or "self") continue;

            if (key.EndsWith(".comment"))
            {
                var memberName = key[..^8]; // Remove ".comment"
                var resolved = ResolveTagTableField(udtInstance, memberName,
                    e => (language != null ? e.GetComment(language) : e.Comment) ?? e.Value, valueResolver);
                result = result.Replace(ph.Value, resolved);
            }
            else if (key.EndsWith(".value"))
            {
                var memberName = key[..^6]; // Remove ".value"
                var resolved = ResolveTagTableField(udtInstance, memberName, e => e.Value, valueResolver);
                result = result.Replace(ph.Value, resolved);
            }
            else if (key.EndsWith(".name"))
            {
                var memberName = key[..^5]; // Remove ".name"
                var resolved = ResolveTagTableField(udtInstance, memberName, e => e.Name, valueResolver);
                result = result.Replace(ph.Value, resolved);
            }
            else
            {
                // Plain member value placeholder: {moduleId} → start value (or pending value)
                var child = FindChildMember(udtInstance, key);
                var childValue = child != null ? (valueResolver?.Invoke(child) ?? child.StartValue) : null;
                result = result.Replace(ph.Value, childValue ?? "");
            }
        }

        return result;
    }

    /// <summary>
    /// Generates comments for all unique UDT instances in scope using per-rule templates.
    /// For each UDT parent, looks up the matching comment rule via config.GetCommentRule().
    /// Only UDT instances with a matching rule (that has a commentTemplate) are included.
    /// </summary>
    public List<(MemberNode Target, string Comment)> GenerateForScope(
        DataBlockInfo db, IReadOnlyList<MemberNode> scopeMembers, string? language = null,
        Func<MemberNode, string?>? valueResolver = null)
    {
        // Collect all ancestor nodes that could have comment templates.
        // Scope contains leaf members; walk up the tree to find UDT instances
        // and their parents (e.g., moduleId → messageConfig_UDT → driveConfig).
        var candidates = new HashSet<MemberNode>();
        foreach (var m in scopeMembers)
        {
            var node = m.Parent;
            while (node != null)
            {
                candidates.Add(node);
                node = node.Parent;
            }
        }

        var results = new List<(MemberNode Target, string Comment)>();

        foreach (var inst in candidates)
        {
            var rule = _config.GetCommentRule(inst);
            if (rule?.CommentTemplate == null) continue;

            var comment = Generate(db, inst, rule.CommentTemplate, language, valueResolver);
            results.Add((inst, comment));
        }

        return results;
    }

    /// <summary>
    /// Finds a child member by name. If not found directly, searches one level deeper
    /// (e.g., parent struct → first UDT child → member). This allows the same
    /// placeholders to work at both UDT and parent level.
    /// </summary>
    private static MemberNode? FindChildMember(MemberNode node, string memberName)
    {
        var direct = node.Children.FirstOrDefault(c => c.Name == memberName);
        if (direct != null) return direct;

        // Search one level deeper (first child that has the named grandchild)
        foreach (var child in node.Children)
        {
            var grandchild = child.Children.FirstOrDefault(c => c.Name == memberName);
            if (grandchild != null) return grandchild;
        }
        return null;
    }

    private string ResolveTagTableField(
        MemberNode udtInstance, string memberName,
        Func<TagTableEntry, string> fieldSelector,
        Func<MemberNode, string?>? valueResolver = null)
    {
        var child = FindChildMember(udtInstance, memberName);
        var effectiveValue = child != null ? (valueResolver?.Invoke(child) ?? child.StartValue) : null;
        if (effectiveValue == null) return "";

        if (_tagTableCache == null) return effectiveValue;

        // Find the rule for this child to get the tag table reference
        var rule = _config.GetRule(child!);
        if (rule?.TagTableReference == null) return effectiveValue;

        var entries = _tagTableCache.GetEntriesByPattern(rule.TagTableReference.TableName);
        // Match by numeric value or by constant name (TIA stores ConstantName in StartValue)
        var entry = entries.FirstOrDefault(e => e.Value == effectiveValue)
                 ?? entries.FirstOrDefault(e => e.Name == effectiveValue);

        return entry != null ? fieldSelector(entry) : effectiveValue;
    }
}
