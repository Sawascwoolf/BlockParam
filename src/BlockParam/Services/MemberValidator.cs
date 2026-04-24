using System;
using System.Linq;
using BlockParam.Config;
using BlockParam.Localization;
using BlockParam.Models;

namespace BlockParam.Services;

/// <summary>
/// Shared validator for a single member value. Consolidates the pipeline
/// (datatype format → rule constraints → tag-table membership) so the bulk
/// dialog, inline editor and pending-changes list all agree on what "valid"
/// means — and the hint we surface proactively matches the error we'd emit.
/// </summary>
public class MemberValidator
{
    private readonly BulkChangeConfig? _config;
    private readonly TagTableCache? _tagTableCache;

    public MemberValidator(BulkChangeConfig? config, TagTableCache? tagTableCache)
    {
        _config = config;
        _tagTableCache = tagTableCache;
    }

    /// <summary>
    /// Validates <paramref name="value"/> against <paramref name="member"/>'s
    /// datatype and any matching rule. Returns null when valid or the input
    /// is empty; otherwise a human-readable error message.
    /// </summary>
    public string? Validate(MemberNode member, string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;

        var datatype = member.Datatype;
        var constants = _tagTableCache?.GetAllConstantNames();

        var typeError = TiaDataTypeValidator.Validate(value!, datatype, constants);
        if (typeError != null) return typeError;

        var rule = _config?.GetRule(member);
        var ruleError = rule?.Constraints?.Validate(value!, datatype, constants);
        if (ruleError != null) return ruleError;

        if (rule?.Constraints?.RequireTagTableValue == true
            && rule.TagTableReference != null
            && _tagTableCache != null)
        {
            var entries = _tagTableCache.GetEntriesByPattern(rule.TagTableReference.TableName);
            var matches = entries.Any(e =>
                string.Equals(e.Name, value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Value, value, StringComparison.OrdinalIgnoreCase));
            if (!matches)
                return Res.Format("Validation_RequireTagTable", rule.TagTableReference.TableName);
        }

        return null;
    }

    /// <summary>Returns the formatted hint for <paramref name="member"/> or null.</summary>
    public string? GetHint(MemberNode member) =>
        RuleHintFormatter.Format(_config?.GetRule(member));
}
