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
    /// or null when the rule has no user-visible constraint. Includes datatype
    /// limits (e.g. "Int: -32768 – 32767") as a fallback so typed fields still
    /// get a useful hint when no rule override is defined.
    /// </summary>
    public static string? Format(MemberRule? rule, string? datatype = null)
    {
        var parts = new List<string>();

        var c = rule?.Constraints;
        bool hasRuleRange = false;
        if (c != null)
        {
            bool hasMin = !IsEmpty(c.Min);
            bool hasMax = !IsEmpty(c.Max);
            hasRuleRange = hasMin || hasMax;
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

            if (c.RequireTagTableValue && rule?.TagTableReference != null)
                parts.Add(Res.Format("Hint_TagTable", rule.TagTableReference.TableName));
        }

        // Fallback: when the rule leaves the range unconstrained, show the
        // implicit datatype range so the user still sees "what's allowed here".
        if (!hasRuleRange)
        {
            var typeHint = FormatDatatypeHint(datatype);
            if (typeHint != null) parts.Add(typeHint);
        }

        return parts.Count == 0 ? null : string.Join(Res.Get("Hint_Separator"), parts);
    }

    /// <summary>
    /// Back-compat overload matching the pre-datatype signature.
    /// </summary>
    public static string? Format(MemberRule? rule) => Format(rule, null);

    private static string? FormatDatatypeHint(string? datatype)
    {
        if (string.IsNullOrEmpty(datatype)) return null;
        var key = datatype!.Trim('"');
        return key switch
        {
            "SInt"  => Res.Format("Hint_Datatype_IntRange", key, sbyte.MinValue, sbyte.MaxValue),
            "Int"   => Res.Format("Hint_Datatype_IntRange", key, short.MinValue, short.MaxValue),
            "DInt"  => Res.Format("Hint_Datatype_IntRange", key, int.MinValue, int.MaxValue),
            "LInt"  => Res.Format("Hint_Datatype_IntRange", key, long.MinValue, long.MaxValue),
            "USInt" => Res.Format("Hint_Datatype_IntRange", key, byte.MinValue, byte.MaxValue),
            "UInt"  => Res.Format("Hint_Datatype_IntRange", key, ushort.MinValue, ushort.MaxValue),
            "UDInt" => Res.Format("Hint_Datatype_IntRange", key, uint.MinValue, uint.MaxValue),
            "ULInt" => Res.Format("Hint_Datatype_IntRange", key, ulong.MinValue, ulong.MaxValue),
            "Byte"  => Res.Format("Hint_Datatype_IntRange", key, byte.MinValue, byte.MaxValue),
            "Word"  => Res.Format("Hint_Datatype_IntRange", key, ushort.MinValue, ushort.MaxValue),
            "DWord" => Res.Format("Hint_Datatype_IntRange", key, uint.MinValue, uint.MaxValue),
            "LWord" => Res.Format("Hint_Datatype_IntRange", key, ulong.MinValue, ulong.MaxValue),
            "Real"  => Res.Format("Hint_Datatype_Float", key),
            "LReal" => Res.Format("Hint_Datatype_Float", key),
            "Bool"  => Res.Get("Hint_Datatype_Bool"),
            _       => null,
        };
    }

    private static bool IsEmpty(object? value) =>
        value == null || (value is string s && string.IsNullOrEmpty(s));

    private static string Display(object value) =>
        value is string s ? s : System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
}
