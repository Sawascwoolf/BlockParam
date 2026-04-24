using System.Collections.Generic;
using System.Globalization;
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
    /// or null when the rule has no user-visible constraint. When no rule
    /// constraint applies at all, falls back to the datatype's implicit range
    /// (e.g. "Int: -32768 – 32767") so typed fields still get useful guidance.
    /// </summary>
    public static string? Format(MemberRule? rule, string? datatype = null)
    {
        var parts = new List<string>();

        var c = rule?.Constraints;
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

            if (c.RequireTagTableValue && rule!.TagTableReference != null)
                parts.Add(Res.Format("Hint_TagTable", rule.TagTableReference.TableName));
        }

        // Fallback only when no rule-visible constraint was added — a datatype
        // range appended next to "One of: OPEN, CLOSED" or a tag-table hint
        // would misleadingly suggest numeric literals are also accepted.
        if (parts.Count == 0)
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
        // Min/Max rendered with InvariantCulture so the hint matches what the
        // TIA parser actually accepts — showing "32.767" to a de-DE user would
        // otherwise read as a valid Int literal (it isn't; TIA uses '.'-less
        // grouping or none at all, with the decimal dot reserved for Real).
        return key switch
        {
            "SInt"  => IntRange(key, sbyte.MinValue, sbyte.MaxValue),
            "Int"   => IntRange(key, short.MinValue, short.MaxValue),
            "DInt"  => IntRange(key, int.MinValue, int.MaxValue),
            "LInt"  => IntRange(key, long.MinValue, long.MaxValue),
            "USInt" => IntRange(key, byte.MinValue, byte.MaxValue),
            "UInt"  => IntRange(key, ushort.MinValue, ushort.MaxValue),
            "UDInt" => IntRange(key, uint.MinValue, uint.MaxValue),
            "ULInt" => IntRange(key, ulong.MinValue, ulong.MaxValue),
            "Byte"  => IntRange(key, byte.MinValue, byte.MaxValue),
            "Word"  => IntRange(key, ushort.MinValue, ushort.MaxValue),
            "DWord" => IntRange(key, uint.MinValue, uint.MaxValue),
            "LWord" => IntRange(key, ulong.MinValue, ulong.MaxValue),
            "Real"  => Res.Format("Hint_Datatype_Float", key),
            "LReal" => Res.Format("Hint_Datatype_Float", key),
            "Bool"  => Res.Get("Hint_Datatype_Bool"),
            _       => null,
        };
    }

    private static string IntRange(string type, long min, long max) =>
        Res.Format("Hint_Datatype_IntRange", type,
            min.ToString(CultureInfo.InvariantCulture),
            max.ToString(CultureInfo.InvariantCulture));

    private static string IntRange(string type, ulong min, ulong max) =>
        Res.Format("Hint_Datatype_IntRange", type,
            min.ToString(CultureInfo.InvariantCulture),
            max.ToString(CultureInfo.InvariantCulture));

    private static bool IsEmpty(object? value) =>
        value == null || (value is string s && string.IsNullOrEmpty(s));

    private static string Display(object value) =>
        value is string s ? s : System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
}
