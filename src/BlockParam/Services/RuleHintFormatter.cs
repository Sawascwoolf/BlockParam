using System.Collections.Generic;
using System.Linq;
using BlockParam.Config;
using BlockParam.Localization;

namespace BlockParam.Services;

/// <summary>
/// Produces human-readable, localized hint strings describing the constraints
/// attached to a <see cref="MemberRule"/>. Shared by the bulk-change inspector
/// and inline-edit cells so both paths surface the same rule language.
/// </summary>
public static class RuleHintFormatter
{
    /// <summary>
    /// Returns a single-line hint like "Range: 0 – 100 · One of: OPEN, CLOSED"
    /// or null when the rule has no user-visible constraint.
    /// </summary>
    public static string? Format(MemberRule? rule)
    {
        if (rule == null) return null;

        var parts = new List<string>();

        var c = rule.Constraints;
        if (c != null)
        {
            bool hasMin = !IsEmpty(c.Min);
            bool hasMax = !IsEmpty(c.Max);
            if (hasMin && hasMax)
                parts.Add(Res.Format("Hint_Range", Display(c.Min!), Display(c.Max!)));
            else if (hasMin)
                parts.Add(Res.Format("Hint_Min", Display(c.Min!)));
            else if (hasMax)
                parts.Add(Res.Format("Hint_Max", Display(c.Max!)));

            if (c.AllowedValues is { Count: > 0 })
            {
                var values = string.Join(", ", c.AllowedValues.Select(v => v?.ToString() ?? ""));
                parts.Add(Res.Format("Hint_AllowedOf", values));
            }

            if (c.RequireTagTableValue && rule.TagTableReference != null)
                parts.Add(Res.Format("Hint_TagTable", rule.TagTableReference.TableName));
        }

        return parts.Count == 0 ? null : string.Join(Res.Get("Hint_Separator"), parts);
    }

    private static bool IsEmpty(object? value) =>
        value == null || (value is string s && string.IsNullOrEmpty(s));

    private static string Display(object value) =>
        value is string s ? s : System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
}
