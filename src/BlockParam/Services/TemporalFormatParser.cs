using System.Globalization;
using System.Text.RegularExpressions;

namespace BlockParam.Services;

/// <summary>
/// The TIA temporal datatype families this parser recognizes.
/// String keys (including the underscored and short aliases like
/// <c>Time_Of_Day</c>, <c>Tod</c>, <c>DT</c>) map onto these via
/// <see cref="TemporalFormatParser.TryResolveType"/>.
/// </summary>
public enum TemporalDataType
{
    Time,
    LTime,
    S5Time,
    Date,
    LDate,
    TimeOfDay,
    LTimeOfDay,
    DateTime,
    LDateTime,
}

/// <summary>
/// Normalized numeric value parsed from a TIA temporal literal.
/// Time / LTime / S5Time / TimeOfDay / LTimeOfDay yield milliseconds;
/// Date / LDate / DateTime / LDateTime yield <see cref="System.DateTime.Ticks"/>.
/// </summary>
public readonly struct TemporalValue
{
    public TemporalDataType Kind { get; }
    public double NumericValue { get; }

    public TemporalValue(TemporalDataType kind, double numericValue)
    {
        Kind = kind;
        NumericValue = numericValue;
    }
}

/// <summary>
/// Owns the regex grammar + range-aware parsing for TIA's six temporal
/// literal families (T#, LT#, S5T#, D#, TOD#/LTOD#, DT#/LDT#). Extracted
/// from <see cref="TiaDataTypeValidator"/> (issue #171) so the regexes
/// and their downstream validation live together with a single test
/// fixture for the whole family.
/// </summary>
/// <remarks>
/// Partial-trust safe: uses only the same <c>static readonly Regex</c> +
/// <see cref="RegexOptions.Compiled"/> pattern already proven under TIA's
/// Add-In Loader sandbox (cf. <c>MultiSelectLog.cs</c>). No readonly-struct
/// field access — <see cref="TemporalValue"/> is only constructed and
/// returned via <c>out</c>, never stored as a readonly field.
/// </remarks>
public static class TemporalFormatParser
{
    private static readonly Regex TimeComponentsRegex = new(
        @"^-?(\d+d)?(\d+h)?(\d+m(?!s))?(\d+s)?(\d+ms)?(\d+us)?(\d+ns)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DateRegex = new(
        @"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    private static readonly Regex TimeOfDayRegex = new(
        @"^\d{1,2}:\d{2}:\d{2}$", RegexOptions.Compiled);

    private static readonly Regex LTimeOfDayRegex = new(
        @"^\d{1,2}:\d{2}:\d{2}\.\d{1,9}$", RegexOptions.Compiled);

    private static readonly Regex DateTimeRegex = new(
        @"^\d{4}-\d{2}-\d{2}-\d{1,2}:\d{2}:\d{2}$", RegexOptions.Compiled);

    private static readonly Regex LDateTimeRegex = new(
        @"^\d{4}-\d{2}-\d{2}-\d{1,2}:\d{2}:\d{2}\.\d{1,9}$", RegexOptions.Compiled);

    private static readonly Dictionary<string, TemporalDataType> TypeKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Time"] = TemporalDataType.Time,
            ["LTime"] = TemporalDataType.LTime,
            ["S5Time"] = TemporalDataType.S5Time,
            ["Date"] = TemporalDataType.Date,
            ["LDate"] = TemporalDataType.LDate,
            ["Time_Of_Day"] = TemporalDataType.TimeOfDay,
            ["TimeOfDay"] = TemporalDataType.TimeOfDay,
            ["Tod"] = TemporalDataType.TimeOfDay,
            ["LTime_Of_Day"] = TemporalDataType.LTimeOfDay,
            ["LTimeOfDay"] = TemporalDataType.LTimeOfDay,
            ["LTod"] = TemporalDataType.LTimeOfDay,
            ["Date_And_Time"] = TemporalDataType.DateTime,
            ["DateTime"] = TemporalDataType.DateTime,
            ["DT"] = TemporalDataType.DateTime,
            ["LDate_And_Time"] = TemporalDataType.LDateTime,
            ["LDateTime"] = TemporalDataType.LDateTime,
            ["LDT"] = TemporalDataType.LDateTime,
        };

    /// <summary>True when <paramref name="typeKey"/> names any TIA temporal family.</summary>
    public static bool IsTemporalType(string? typeKey)
        => !string.IsNullOrEmpty(typeKey) && TypeKeys.ContainsKey(typeKey!);

    /// <summary>Maps a TIA type-key string (including underscore / short aliases) onto the enum.</summary>
    public static bool TryResolveType(string? typeKey, out TemporalDataType type)
    {
        if (string.IsNullOrEmpty(typeKey))
        {
            type = default;
            return false;
        }
        return TypeKeys.TryGetValue(typeKey!, out type);
    }

    /// <summary>
    /// Parses a TIA temporal literal (e.g. <c>T#2h30m</c>, <c>D#2024-01-15</c>,
    /// <c>TOD#12:30:00</c>, <c>DT#2024-01-15-12:30:00</c>) according to the
    /// expected datatype, normalizing into <see cref="TemporalValue"/>.
    /// </summary>
    public static bool TryParse(string? literal, TemporalDataType expected, out TemporalValue parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(literal)) return false;
        var v = literal!.Trim();

        switch (expected)
        {
            case TemporalDataType.Time:
            case TemporalDataType.S5Time:
                if (TryParseTimeLiteral(v, "T#", out var t)
                    || TryParseTimeLiteral(v, "S5T#", out t))
                {
                    parsed = new TemporalValue(expected, t);
                    return true;
                }
                return false;

            case TemporalDataType.LTime:
                if (TryParseTimeLiteral(v, "LT#", out var lt))
                {
                    parsed = new TemporalValue(expected, lt);
                    return true;
                }
                return false;

            case TemporalDataType.Date:
            case TemporalDataType.LDate:
                if (TryParseDateLiteral(v, "D#", out var d))
                {
                    parsed = new TemporalValue(expected, d);
                    return true;
                }
                return false;

            case TemporalDataType.TimeOfDay:
                if (TryParseTimeOfDayLiteral(v, "TOD#", out var tod))
                {
                    parsed = new TemporalValue(expected, tod);
                    return true;
                }
                return false;

            case TemporalDataType.LTimeOfDay:
                if (TryParseTimeOfDayLiteral(v, "LTOD#", out var ltod))
                {
                    parsed = new TemporalValue(expected, ltod);
                    return true;
                }
                return false;

            case TemporalDataType.DateTime:
                if (TryParseDateTimeLiteral(v, "DT#", out var dt))
                {
                    parsed = new TemporalValue(expected, dt);
                    return true;
                }
                return false;

            case TemporalDataType.LDateTime:
                if (TryParseDateTimeLiteral(v, "LDT#", out var ldt))
                {
                    parsed = new TemporalValue(expected, ldt);
                    return true;
                }
                return false;
        }
        return false;
    }

    // --- Format-only validators (used by TiaDataTypeValidator's Validate paths) ---

    /// <summary>Body of a T# / LT# / S5T# literal (the part after the prefix).</summary>
    public static bool IsValidTimeBody(string body)
        => body != null && body.Length > 0
           && body.Replace("-", "").Length > 0
           && TimeComponentsRegex.IsMatch(body);

    /// <summary>Body of a D# literal (<c>YYYY-MM-DD</c>).</summary>
    public static bool IsValidDateBody(string body)
        => body != null && DateRegex.IsMatch(body);

    /// <summary>Body of a TOD# literal (<c>HH:MM:SS</c>, no fractional seconds).</summary>
    public static bool IsValidTimeOfDayBody(string body)
        => body != null && TimeOfDayRegex.IsMatch(body);

    /// <summary>Body of an LTOD# literal — accepts the TOD# form too (fractional seconds optional).</summary>
    public static bool IsValidLTimeOfDayBody(string body)
        => body != null && (LTimeOfDayRegex.IsMatch(body) || TimeOfDayRegex.IsMatch(body));

    /// <summary>Body of a DT# literal (<c>YYYY-MM-DD-HH:MM:SS</c>).</summary>
    public static bool IsValidDateTimeBody(string body)
        => body != null && DateTimeRegex.IsMatch(body);

    /// <summary>Body of an LDT# literal — accepts the DT# form too (fractional seconds optional).</summary>
    public static bool IsValidLDateTimeBody(string body)
        => body != null && (LDateTimeRegex.IsMatch(body) || DateTimeRegex.IsMatch(body));

    // --- Internal numeric parsers ---

    private static bool TryParseTimeLiteral(string value, string prefix, out double ms)
    {
        ms = 0;
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var body = value.Substring(prefix.Length);
        if (!IsValidTimeBody(body))
            return false;

        var match = TimeComponentsRegex.Match(body);
        bool negative = body.StartsWith("-");
        double total = 0;

        if (match.Groups[1].Success) total += ParseDigits(match.Groups[1].Value) * 86_400_000.0;
        if (match.Groups[2].Success) total += ParseDigits(match.Groups[2].Value) * 3_600_000.0;
        if (match.Groups[3].Success) total += ParseDigits(match.Groups[3].Value) * 60_000.0;
        if (match.Groups[4].Success) total += ParseDigits(match.Groups[4].Value) * 1_000.0;
        if (match.Groups[5].Success) total += ParseDigitsMultiSuffix(match.Groups[5].Value, "ms");
        if (match.Groups[6].Success) total += ParseDigitsMultiSuffix(match.Groups[6].Value, "us") * 0.001;
        if (match.Groups[7].Success) total += ParseDigitsMultiSuffix(match.Groups[7].Value, "ns") * 0.000001;

        ms = negative ? -total : total;
        return true;
    }

    private static long ParseDigits(string component)
    {
        var digits = component.Substring(0, component.Length - 1);
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var val) ? val : 0;
    }

    private static long ParseDigitsMultiSuffix(string component, string suffix)
    {
        var digits = component.Substring(0, component.Length - suffix.Length);
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var val) ? val : 0;
    }

    private static bool TryParseDateLiteral(string value, string prefix, out double ticks)
    {
        ticks = 0;
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var body = value.Substring(prefix.Length);
        if (System.DateTime.TryParseExact(body, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            ticks = date.Ticks;
            return true;
        }
        return false;
    }

    private static bool TryParseTimeOfDayLiteral(string value, string prefix, out double ms)
    {
        ms = 0;
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var body = value.Substring(prefix.Length);
        if (TimeSpan.TryParseExact(body, new[] { @"h\:mm\:ss", @"hh\:mm\:ss",
            @"h\:mm\:ss\.FFFFFFF", @"hh\:mm\:ss\.FFFFFFF" },
            CultureInfo.InvariantCulture, out var tod))
        {
            ms = tod.TotalMilliseconds;
            return true;
        }
        return false;
    }

    private static bool TryParseDateTimeLiteral(string value, string prefix, out double ticks)
    {
        ticks = 0;
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var body = value.Substring(prefix.Length);
        // TIA uses a dash between date and time: 2024-01-15-12:30:00
        if (System.DateTime.TryParseExact(body,
            new[] { "yyyy-MM-dd-H:mm:ss", "yyyy-MM-dd-HH:mm:ss",
                    "yyyy-MM-dd-H:mm:ss.FFFFFFF", "yyyy-MM-dd-HH:mm:ss.FFFFFFF" },
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            ticks = dt.Ticks;
            return true;
        }
        return false;
    }
}
