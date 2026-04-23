using System.Globalization;
using System.Text.RegularExpressions;
using BlockParam.Localization;

namespace BlockParam.Services;

/// <summary>
/// Validates values against TIA Portal data type literal formats.
/// Returns null if valid, or an error message with format example if invalid.
/// </summary>
public static class TiaDataTypeValidator
{
    // Known literal prefixes that indicate a typed value, not a constant name
    private static readonly string[] KnownPrefixes =
        { "T#", "LT#", "S5T#", "D#", "TOD#", "LTOD#", "DT#", "LDT#" };

    private static readonly Dictionary<string, Func<string, string?>> Validators =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Bool"] = ValidateBool,
            ["SInt"] = v => ValidateSignedInteger(v, sbyte.MinValue, sbyte.MaxValue, "SInt"),
            ["Int"] = v => ValidateSignedInteger(v, short.MinValue, short.MaxValue, "Int"),
            ["DInt"] = v => ValidateSignedInteger(v, int.MinValue, int.MaxValue, "DInt"),
            ["LInt"] = v => ValidateSignedInteger(v, long.MinValue, long.MaxValue, "LInt"),
            ["USInt"] = v => ValidateUnsignedInteger(v, byte.MinValue, byte.MaxValue, "USInt"),
            ["UInt"] = v => ValidateUnsignedInteger(v, ushort.MinValue, ushort.MaxValue, "UInt"),
            ["UDInt"] = v => ValidateUnsignedInteger(v, uint.MinValue, uint.MaxValue, "UDInt"),
            ["ULInt"] = v => ValidateUnsignedInteger(v, ulong.MinValue, ulong.MaxValue, "ULInt"),
            ["Byte"] = v => ValidateUnsignedInteger(v, byte.MinValue, byte.MaxValue, "Byte"),
            ["Word"] = v => ValidateUnsignedInteger(v, ushort.MinValue, ushort.MaxValue, "Word"),
            ["DWord"] = v => ValidateUnsignedInteger(v, uint.MinValue, uint.MaxValue, "DWord"),
            ["LWord"] = v => ValidateUnsignedInteger(v, ulong.MinValue, ulong.MaxValue, "LWord"),
            ["Real"] = v => ValidateFloat(v, "Real"),
            ["LReal"] = v => ValidateFloat(v, "LReal"),
            ["String"] = ValidateString,
            ["WString"] = ValidateString,
            ["Char"] = ValidateChar,
            ["WChar"] = ValidateChar,
            ["Time"] = v => ValidateTimePrefix(v, "T#", "Time", "T#500ms, T#1s, T#2h30m"),
            ["LTime"] = v => ValidateTimePrefix(v, "LT#", "LTime", "LT#500ns, LT#1us, LT#5ms"),
            ["S5Time"] = v => ValidateTimePrefix(v, "S5T#", "S5Time", "S5T#500ms, S5T#2s"),
            ["Date"] = v => ValidateDatePrefix(v, "D#", "Date"),
            ["LDate"] = v => ValidateDatePrefix(v, "D#", "LDate"),
            ["Time_Of_Day"] = v => ValidateTimeOfDay(v, "TOD#", "Time_Of_Day"),
            ["TimeOfDay"] = v => ValidateTimeOfDay(v, "TOD#", "TimeOfDay"),
            ["Tod"] = v => ValidateTimeOfDay(v, "TOD#", "Tod"),
            ["LTime_Of_Day"] = v => ValidateLTimeOfDay(v, "LTOD#", "LTime_Of_Day"),
            ["LTimeOfDay"] = v => ValidateLTimeOfDay(v, "LTOD#", "LTimeOfDay"),
            ["LTod"] = v => ValidateLTimeOfDay(v, "LTOD#", "LTod"),
            ["Date_And_Time"] = v => ValidateDateTime(v, "DT#", "Date_And_Time"),
            ["DateTime"] = v => ValidateDateTime(v, "DT#", "DateTime"),
            ["DT"] = v => ValidateDateTime(v, "DT#", "DT"),
            ["LDate_And_Time"] = v => ValidateLDateTime(v, "LDT#", "LDate_And_Time"),
            ["LDateTime"] = v => ValidateLDateTime(v, "LDT#", "LDateTime"),
            ["LDT"] = v => ValidateLDateTime(v, "LDT#", "LDT"),
        };

    // Regex for time components: optional negative, then one or more groups of digits+unit
    private static readonly Regex TimeComponentsRegex = new(
        @"^-?(\d+d)?(\d+h)?(\d+m(?!s))?(\d+s)?(\d+ms)?(\d+us)?(\d+ns)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex for date: YYYY-MM-DD
    private static readonly Regex DateRegex = new(
        @"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    // Regex for time of day: HH:MM:SS (optional .mmm fractional)
    private static readonly Regex TimeOfDayRegex = new(
        @"^\d{1,2}:\d{2}:\d{2}$", RegexOptions.Compiled);

    private static readonly Regex LTimeOfDayRegex = new(
        @"^\d{1,2}:\d{2}:\d{2}\.\d{1,9}$", RegexOptions.Compiled);

    // Regex for datetime: YYYY-MM-DD-HH:MM:SS
    private static readonly Regex DateTimeRegex = new(
        @"^\d{4}-\d{2}-\d{2}-\d{1,2}:\d{2}:\d{2}$", RegexOptions.Compiled);

    private static readonly Regex LDateTimeRegex = new(
        @"^\d{4}-\d{2}-\d{2}-\d{1,2}:\d{2}:\d{2}\.\d{1,9}$", RegexOptions.Compiled);

    /// <summary>
    /// Validates a value against the TIA literal format for the given data type.
    /// Returns null if valid, or an error message with format example if invalid.
    /// </summary>
    public static string? Validate(string value, string datatype, ISet<string>? knownConstants = null)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (string.IsNullOrEmpty(datatype)) return null;
        if (IsKnownConstant(value, knownConstants)) return null;

        var key = datatype.Trim('"');
        if (Validators.TryGetValue(key, out var validator))
            return validator(value);

        return null; // Unknown type (UDT, Array, Struct, etc.) → no validation
    }

    /// <summary>
    /// Tries to parse a numeric value from a TIA literal, handling base prefixes (16#, 8#, 2#).
    /// Returns true if the value is numeric and was parsed successfully.
    /// </summary>
    public static bool TryParseNumericValue(string value, string datatype, out double result)
    {
        result = 0;
        if (string.IsNullOrEmpty(value)) return false;

        // Only attempt parsing for supported types
        if (!SupportsMinMax(datatype)) return false;

        var key = datatype?.Trim('"') ?? "";

        // Temporal types: parse to milliseconds or ticks
        if (TemporalTypes.Contains(key))
            return TryParseTemporalValue(value, key, out result);

        // Try base-prefixed parsing first (16#, 8#, 2#)
        if (TryParseBasePrefixed(value, out var prefixedValue))
        {
            result = prefixedValue;
            return true;
        }

        // Standard decimal parsing
        return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out result);
    }

    private static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SInt", "Int", "DInt", "LInt",
        "USInt", "UInt", "UDInt", "ULInt",
        "Byte", "Word", "DWord", "LWord",
        "Real", "LReal"
    };

    private static readonly HashSet<string> TemporalTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Time", "LTime", "S5Time",
        "Date", "LDate",
        "Time_Of_Day", "TimeOfDay", "Tod",
        "LTime_Of_Day", "LTimeOfDay", "LTod",
        "Date_And_Time", "DateTime", "DT",
        "LDate_And_Time", "LDateTime", "LDT"
    };

    /// <summary>
    /// Returns whether the given data type supports Min/Max constraints.
    /// Includes numeric types and temporal types (Time, Date, etc.).
    /// Returns true for empty/null datatype (conservative: allow Min/Max when type unknown).
    /// </summary>
    public static bool SupportsMinMax(string? datatype)
    {
        if (string.IsNullOrEmpty(datatype)) return true;
        var key = datatype!.Trim('"');
        return NumericTypes.Contains(key) || TemporalTypes.Contains(key);
    }

    /// <summary>
    /// Tries to parse a value with base prefix notation (16#, 8#, 2#).
    /// Supports underscore separators in the digit part.
    /// </summary>
    internal static bool TryParseBasePrefixed(string value, out double result)
    {
        result = 0;
        if (value == null) return false;

        var trimmed = value.Trim();

        if (trimmed.StartsWith("16#", StringComparison.OrdinalIgnoreCase))
        {
            var hex = trimmed.Substring(3).Replace("_", "");
            if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexVal))
            {
                result = hexVal;
                return true;
            }
            return false;
        }

        if (trimmed.StartsWith("8#", StringComparison.OrdinalIgnoreCase))
        {
            var octal = trimmed.Substring(2).Replace("_", "");
            try
            {
                result = Convert.ToUInt64(octal, 8);
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (trimmed.StartsWith("2#", StringComparison.OrdinalIgnoreCase))
        {
            var binary = trimmed.Substring(2).Replace("_", "");
            try
            {
                result = Convert.ToUInt64(binary, 2);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    // --- Temporal parsing for Min/Max ---

    /// <summary>
    /// Routes temporal value parsing to the correct method based on type.
    /// All temporal types are normalized to milliseconds for comparison.
    /// </summary>
    private static bool TryParseTemporalValue(string value, string typeKey, out double result)
    {
        result = 0;
        var v = value.Trim();

        // Time / LTime / S5Time → parse T#/LT#/S5T# to milliseconds
        if (typeKey.Equals("Time", StringComparison.OrdinalIgnoreCase)
            || typeKey.Equals("S5Time", StringComparison.OrdinalIgnoreCase))
            return TryParseTimeLiteral(v, "T#", out result)
                || TryParseTimeLiteral(v, "S5T#", out result);

        if (typeKey.Equals("LTime", StringComparison.OrdinalIgnoreCase))
            return TryParseTimeLiteral(v, "LT#", out result);

        // Date / LDate → parse D#YYYY-MM-DD to ticks
        if (typeKey.Equals("Date", StringComparison.OrdinalIgnoreCase)
            || typeKey.Equals("LDate", StringComparison.OrdinalIgnoreCase))
            return TryParseDateLiteral(v, "D#", out result);

        // TimeOfDay variants → parse TOD#HH:MM:SS to milliseconds
        if (typeKey.Equals("Time_Of_Day", StringComparison.OrdinalIgnoreCase)
            || typeKey.Equals("TimeOfDay", StringComparison.OrdinalIgnoreCase)
            || typeKey.Equals("Tod", StringComparison.OrdinalIgnoreCase))
            return TryParseTimeOfDayLiteral(v, "TOD#", out result);

        if (typeKey.Equals("LTime_Of_Day", StringComparison.OrdinalIgnoreCase)
            || typeKey.Equals("LTimeOfDay", StringComparison.OrdinalIgnoreCase)
            || typeKey.Equals("LTod", StringComparison.OrdinalIgnoreCase))
            return TryParseTimeOfDayLiteral(v, "LTOD#", out result);

        // DateTime variants → parse DT#/LDT# to ticks
        if (typeKey.Equals("Date_And_Time", StringComparison.OrdinalIgnoreCase)
            || typeKey.Equals("DateTime", StringComparison.OrdinalIgnoreCase)
            || typeKey.Equals("DT", StringComparison.OrdinalIgnoreCase))
            return TryParseDateTimeLiteral(v, "DT#", out result);

        if (typeKey.Equals("LDate_And_Time", StringComparison.OrdinalIgnoreCase)
            || typeKey.Equals("LDateTime", StringComparison.OrdinalIgnoreCase)
            || typeKey.Equals("LDT", StringComparison.OrdinalIgnoreCase))
            return TryParseDateTimeLiteral(v, "LDT#", out result);

        return false;
    }

    /// <summary>
    /// Parses T#2h30m15s500ms into total milliseconds.
    /// Supports components: d, h, m, s, ms, us, ns. Optional leading minus.
    /// </summary>
    private static bool TryParseTimeLiteral(string value, string prefix, out double ms)
    {
        ms = 0;
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var body = value.Substring(prefix.Length);
        var match = TimeComponentsRegex.Match(body);
        if (!match.Success || body.Length == 0 || body.Replace("-", "").Length == 0)
            return false;

        bool negative = body.StartsWith("-");
        double total = 0;

        if (match.Groups[1].Success) total += ParseDigits(match.Groups[1].Value, 'd') * 86_400_000.0;
        if (match.Groups[2].Success) total += ParseDigits(match.Groups[2].Value, 'h') * 3_600_000.0;
        if (match.Groups[3].Success) total += ParseDigits(match.Groups[3].Value, 'm') * 60_000.0;
        if (match.Groups[4].Success) total += ParseDigits(match.Groups[4].Value, 's') * 1_000.0;
        if (match.Groups[5].Success) total += ParseDigitsMultiSuffix(match.Groups[5].Value, "ms");
        if (match.Groups[6].Success) total += ParseDigitsMultiSuffix(match.Groups[6].Value, "us") * 0.001;
        if (match.Groups[7].Success) total += ParseDigitsMultiSuffix(match.Groups[7].Value, "ns") * 0.000001;

        ms = negative ? -total : total;
        return true;
    }

    /// <summary>Extracts digits from a time component like "30m" → 30.</summary>
    private static long ParseDigits(string component, char suffix)
    {
        var digits = component.Substring(0, component.Length - 1);
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var val) ? val : 0;
    }

    /// <summary>Extracts digits from a multi-char suffix component like "500ms" → 500.</summary>
    private static long ParseDigitsMultiSuffix(string component, string suffix)
    {
        var digits = component.Substring(0, component.Length - suffix.Length);
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var val) ? val : 0;
    }

    /// <summary>Parses D#2024-01-15 into ticks for comparison.</summary>
    private static bool TryParseDateLiteral(string value, string prefix, out double result)
    {
        result = 0;
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var body = value.Substring(prefix.Length);
        if (System.DateTime.TryParseExact(body, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            result = date.Ticks;
            return true;
        }
        return false;
    }

    /// <summary>Parses TOD#12:30:00 or LTOD#12:30:00.123 into milliseconds.</summary>
    private static bool TryParseTimeOfDayLiteral(string value, string prefix, out double result)
    {
        result = 0;
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var body = value.Substring(prefix.Length);
        if (TimeSpan.TryParseExact(body, new[] { @"h\:mm\:ss", @"hh\:mm\:ss",
            @"h\:mm\:ss\.FFFFFFF", @"hh\:mm\:ss\.FFFFFFF" },
            CultureInfo.InvariantCulture, out var tod))
        {
            result = tod.TotalMilliseconds;
            return true;
        }
        return false;
    }

    /// <summary>Parses DT#2024-01-15-12:30:00 or LDT# with fractional seconds into ticks.</summary>
    private static bool TryParseDateTimeLiteral(string value, string prefix, out double result)
    {
        result = 0;
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var body = value.Substring(prefix.Length);
        // TIA uses dash between date and time: 2024-01-15-12:30:00
        // Try multiple formats to cover with/without fractional seconds
        if (System.DateTime.TryParseExact(body,
            new[] { "yyyy-MM-dd-H:mm:ss", "yyyy-MM-dd-HH:mm:ss",
                    "yyyy-MM-dd-H:mm:ss.FFFFFFF", "yyyy-MM-dd-HH:mm:ss.FFFFFFF" },
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            result = dt.Ticks;
            return true;
        }
        return false;
    }

    // --- Private validator methods ---

    private static string? ValidateBool(string value)
    {
        var v = value.Trim();
        if (v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("false", StringComparison.OrdinalIgnoreCase)
            || v == "0" || v == "1")
            return null;

        return Res.Get("TypeValidation_Bool");
    }

    private static string? ValidateSignedInteger(string value, long min, long max, string typeName)
    {
        var v = value.Trim();

        // Try base-prefixed notation (16#, 8#, 2#)
        if (TryParseBasePrefixed(v, out var prefixedVal))
        {
            if (prefixedVal < min || prefixedVal > max)
                return Res.Format("TypeValidation_Integer", typeName, min, max);
            return null;
        }

        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            if (parsed < min || parsed > max)
                return Res.Format("TypeValidation_Integer", typeName, min, max);
            return null;
        }

        return Res.Format("TypeValidation_Integer", typeName, min, max);
    }

    private static string? ValidateUnsignedInteger(string value, ulong min, ulong max, string typeName)
    {
        var v = value.Trim();

        // Try base-prefixed notation (16#, 8#, 2#)
        if (TryParseBasePrefixed(v, out var prefixedVal))
        {
            if (prefixedVal < 0 || (ulong)prefixedVal > max)
                return Res.Format("TypeValidation_Integer", typeName, min, max);
            return null;
        }

        // Allow negative sign to give a better error message for unsigned types
        if (v.StartsWith("-"))
        {
            return Res.Format("TypeValidation_Integer", typeName, min, max);
        }

        if (ulong.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            if (parsed < min || parsed > max)
                return Res.Format("TypeValidation_Integer", typeName, min, max);
            return null;
        }

        return Res.Format("TypeValidation_Integer", typeName, min, max);
    }

    private static string? ValidateFloat(string value, string typeName)
    {
        var v = value.Trim();

        if (double.TryParse(v, NumberStyles.Float | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out _))
            return null;

        return Res.Format("TypeValidation_Float", typeName);
    }

    private static string? ValidateString(string value)
    {
        if (value.Length >= 2 && value.StartsWith("'") && value.EndsWith("'"))
            return null;

        return Res.Get("TypeValidation_String");
    }

    private static string? ValidateChar(string value)
    {
        if (value.Length >= 2 && value.StartsWith("'") && value.EndsWith("'"))
        {
            var content = value.Substring(1, value.Length - 2);
            if (content.Length == 1)
                return null;
        }

        return Res.Get("TypeValidation_Char");
    }

    private static string? ValidateTimePrefix(string value, string prefix, string typeName, string examples)
    {
        var v = value.Trim();

        if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var body = v.Substring(prefix.Length);
            if (TimeComponentsRegex.IsMatch(body) && body.Length > 0
                && body.Replace("-", "").Length > 0)
                return null;
        }

        return Res.Format("TypeValidation_Time", typeName, examples);
    }

    private static string? ValidateDatePrefix(string value, string prefix, string typeName)
    {
        var v = value.Trim();

        if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var body = v.Substring(prefix.Length);
            if (DateRegex.IsMatch(body))
                return null;
        }

        return Res.Get("TypeValidation_Date");
    }

    private static string? ValidateTimeOfDay(string value, string prefix, string typeName)
    {
        var v = value.Trim();

        if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var body = v.Substring(prefix.Length);
            if (TimeOfDayRegex.IsMatch(body))
                return null;
        }

        return Res.Get("TypeValidation_TimeOfDay");
    }

    private static string? ValidateLTimeOfDay(string value, string prefix, string typeName)
    {
        var v = value.Trim();

        if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var body = v.Substring(prefix.Length);
            // Accept both with and without fractional seconds
            if (LTimeOfDayRegex.IsMatch(body) || TimeOfDayRegex.IsMatch(body))
                return null;
        }

        return Res.Get("TypeValidation_TimeOfDay");
    }

    private static string? ValidateDateTime(string value, string prefix, string typeName)
    {
        var v = value.Trim();

        if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var body = v.Substring(prefix.Length);
            if (DateTimeRegex.IsMatch(body))
                return null;
        }

        return Res.Get("TypeValidation_DateTime");
    }

    private static string? ValidateLDateTime(string value, string prefix, string typeName)
    {
        var v = value.Trim();

        if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var body = v.Substring(prefix.Length);
            // Accept both with and without fractional seconds
            if (LDateTimeRegex.IsMatch(body) || DateTimeRegex.IsMatch(body))
                return null;
        }

        return Res.Get("TypeValidation_DateTime");
    }

    /// <summary>
    /// Determines if a value is likely a constant name (starts with a letter,
    /// no known literal prefix). Constants are passed through without validation.
    /// </summary>
    /// <summary>
    /// Checks if a value is a known constant name from loaded tag tables.
    /// Known constants bypass format validation since they resolve at TIA runtime.
    /// If no constants are provided, no values are bypassed.
    /// </summary>
    private static bool IsKnownConstant(string value, ISet<string>? knownConstants)
    {
        if (knownConstants == null || knownConstants.Count == 0) return false;
        return knownConstants.Contains(value);
    }
}
