using System.Globalization;
using Newtonsoft.Json;
using BlockParam.Services;

namespace BlockParam.Config;

/// <summary>
/// Defines value constraints for a member (min/max, allowed values).
/// Min/Max accepts both numeric values ("min": 5) and TIA literals ("min": "T#500ms").
/// </summary>
public class ValueConstraint
{
    [JsonProperty("min")]
    public object? Min { get; set; }

    [JsonProperty("max")]
    public object? Max { get; set; }

    [JsonProperty("allowedValues")]
    public List<object>? AllowedValues { get; set; }

    [JsonProperty("requireTagTableValue")]
    public bool RequireTagTableValue { get; set; }

    /// <summary>True if Min or Max is set (non-null, non-empty).</summary>
    [JsonIgnore]
    public bool HasMinMax => !IsEmpty(Min) || !IsEmpty(Max);

    /// <summary>
    /// Validates a value against this constraint and optionally against the TIA data type format.
    /// Returns null if valid, or an error message if invalid.
    /// </summary>
    public string? Validate(string value, string? datatype = null, ISet<string>? knownConstants = null)
    {
        // 1. Data type format validation (if datatype provided)
        if (datatype != null)
        {
            var typeError = TiaDataTypeValidator.Validate(value, datatype, knownConstants);
            if (typeError != null) return typeError;
        }

        // 2. Check Min/Max range
        if (HasMinMax)
        {
            // Known constants skip Min/Max — they resolve at TIA runtime
            if (knownConstants != null && knownConstants.Contains(value))
            {
                // Skip — constant value is only known at TIA runtime
            }
            else if (datatype != null)
            {
                if (TiaDataTypeValidator.SupportsMinMax(datatype))
                {
                    var error = ValidateRange(value, datatype);
                    if (error != null) return error;
                }
            }
            else
            {
                // Fallback: standard double parsing when no datatype provided
                if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture, out var numericValue))
                {
                    if (TryResolveLimit(Min, null, out var minVal) && numericValue < minVal)
                        return $"Value {value} is below minimum {Min}.";
                    if (TryResolveLimit(Max, null, out var maxVal) && numericValue > maxVal)
                        return $"Value {value} is above maximum {Max}.";
                }
                else
                {
                    return $"'{value}' is not a valid number.";
                }
            }
        }

        // 3. Check allowed values list
        if (AllowedValues is { Count: > 0 })
        {
            var stringValues = AllowedValues.Select(v => v.ToString()).ToList();
            if (!stringValues.Contains(value))
                return $"Value '{value}' is not in the list of allowed values.";
        }

        return null;
    }

    /// <summary>
    /// Validates value against Min/Max using TIA-aware parsing.
    /// Handles both numeric limits (5) and TIA-literal limits ("T#500ms").
    /// </summary>
    private string? ValidateRange(string value, string datatype)
    {
        if (!TiaDataTypeValidator.TryParseNumericValue(value, datatype, out var parsedValue))
            return null; // Can't parse → skip range check

        if (TryResolveLimit(Min, datatype, out var minVal) && parsedValue < minVal)
            return $"Value {value} is below minimum {Min}.";

        if (TryResolveLimit(Max, datatype, out var maxVal) && parsedValue > maxVal)
            return $"Value {value} is above maximum {Max}.";

        return null;
    }

    /// <summary>
    /// Resolves a Min/Max limit (object?) to a comparable double.
    /// Handles: double from JSON, long from JSON, string TIA-literal ("T#500ms"), numeric string ("42").
    /// </summary>
    private static bool TryResolveLimit(object? limit, string? datatype, out double result)
    {
        result = 0;
        if (IsEmpty(limit)) return false;

        // JSON numeric types
        if (limit is double d) { result = d; return true; }
        if (limit is long l) { result = l; return true; }
        if (limit is int i) { result = i; return true; }

        // String: could be TIA literal ("T#500ms") or numeric string ("42")
        if (limit is string s)
        {
            if (!string.IsNullOrEmpty(datatype)
                && TiaDataTypeValidator.TryParseNumericValue(s, datatype, out result))
                return true;

            // Fallback: plain numeric string
            return double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out result);
        }

        return false;
    }

    private static bool IsEmpty(object? value) =>
        value == null || (value is string s && string.IsNullOrEmpty(s));
}
