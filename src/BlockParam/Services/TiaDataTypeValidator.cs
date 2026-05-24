using System.Globalization;
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

        // Temporal types: delegate to the shared parser (#171)
        if (TemporalFormatParser.TryResolveType(key, out var temporalType))
        {
            if (TemporalFormatParser.TryParse(value, temporalType, out var temporal))
            {
                result = temporal.NumericValue;
                return true;
            }
            return false;
        }

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

    /// <summary>
    /// Returns whether the given data type supports Min/Max constraints.
    /// Includes numeric types and temporal types (Time, Date, etc.).
    /// Returns true for empty/null datatype (conservative: allow Min/Max when type unknown).
    /// </summary>
    public static bool SupportsMinMax(string? datatype)
    {
        if (string.IsNullOrEmpty(datatype)) return true;
        var key = datatype!.Trim('"');
        return NumericTypes.Contains(key) || TemporalFormatParser.IsTemporalType(key);
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
            if (TemporalFormatParser.IsValidTimeBody(body))
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
            if (TemporalFormatParser.IsValidDateBody(body))
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
            if (TemporalFormatParser.IsValidTimeOfDayBody(body))
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
            if (TemporalFormatParser.IsValidLTimeOfDayBody(body))
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
            if (TemporalFormatParser.IsValidDateTimeBody(body))
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
            if (TemporalFormatParser.IsValidLDateTimeBody(body))
                return null;
        }

        return Res.Get("TypeValidation_DateTime");
    }

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
