using BlockParam.Config;
using BlockParam.Models;

namespace BlockParam.Services;

/// <summary>
/// Validates a value against tag table entries when requireTagTableValue is set.
/// </summary>
public class TagTableValidator
{
    private readonly TagTableCache _cache;

    public TagTableValidator(TagTableCache cache)
    {
        _cache = cache;
    }

    /// <summary>
    /// Validates that the value exists in the tag table entries referenced by the rule.
    /// Returns null if valid, or an error message if invalid.
    /// </summary>
    public string? Validate(string value, MemberRule rule)
    {
        if (rule.Constraints?.RequireTagTableValue != true)
            return null;

        if (rule.TagTableReference == null)
            return null;

        var entries = _cache.GetEntriesByPattern(rule.TagTableReference.TableName);

        if (entries.Any(e => e.Value == value))
            return null;

        return $"Value '{value}' is not a valid constant from tag tables matching '{rule.TagTableReference.TableName}'.";
    }
}
