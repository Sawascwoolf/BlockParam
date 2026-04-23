using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace BlockParam.SimaticML;

/// <summary>
/// Parses TIA Portal array type strings like "Array[0..4] of Int",
/// "Array[5..17] of Real", "Array[1..MAX_VALVES] of \"UDT_Motor\"",
/// or multi-dimensional "Array[0..2, 0..1] of Int".
/// </summary>
public static class ArrayTypeParser
{
    // Captures "Array[...] of ELEMENT" where ELEMENT may be quoted (UDT), primitive,
    // or "Struct". Bracket content is parsed separately so we can handle multiple
    // comma-separated ranges.
    private static readonly Regex ArrayPattern = new(
        @"^\s*Array\s*\[(?<bounds>[^\]]+)\]\s+of\s+(?<elem>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RangePattern = new(
        @"^\s*(?<low>[^\.\s]+)\s*\.\.\s*(?<high>[^\.\s]+)\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Attempts to parse a datatype string as an array type.
    /// Returns false if the string is not an array.
    /// </summary>
    public static bool TryParse(string datatype, [NotNullWhen(true)] out ArrayTypeInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(datatype)) return false;

        var match = ArrayPattern.Match(datatype);
        if (!match.Success) return false;

        var boundsText = match.Groups["bounds"].Value;
        var elementType = match.Groups["elem"].Value.Trim();

        var dimensions = new List<ArrayDimension>();
        foreach (var rangeText in boundsText.Split(','))
        {
            var rangeMatch = RangePattern.Match(rangeText);
            if (!rangeMatch.Success) return false;
            dimensions.Add(new ArrayDimension(
                StripSurroundingQuotes(rangeMatch.Groups["low"].Value.Trim()),
                StripSurroundingQuotes(rangeMatch.Groups["high"].Value.Trim())));
        }

        info = new ArrayTypeInfo(dimensions, elementType);
        return true;
    }

    // TIA V20 exports quote constant names used as array bounds, e.g.
    // Array["MOD_TP307"..2] of Struct. The cache stores constant names without
    // quotes, so we unquote here for correct resolver lookups.
    private static string StripSurroundingQuotes(string token)
    {
        if (token.Length >= 2 && token[0] == '"' && token[token.Length - 1] == '"')
            return token.Substring(1, token.Length - 2);
        return token;
    }
}

/// <summary>
/// Structured info about an array type. Bounds are kept as raw tokens so the
/// caller can decide whether each is a numeric literal or a constant name.
/// </summary>
public class ArrayTypeInfo
{
    public ArrayTypeInfo(IReadOnlyList<ArrayDimension> dimensions, string elementType)
    {
        Dimensions = dimensions;
        ElementType = elementType;
    }

    public IReadOnlyList<ArrayDimension> Dimensions { get; }

    /// <summary>
    /// Element type as written in the source (e.g. "Int", "Real", "Struct",
    /// "\"UDT_Motor\"" — quoted for UDT references).
    /// </summary>
    public string ElementType { get; }

    public bool IsMultiDimensional => Dimensions.Count > 1;
}

public class ArrayDimension
{
    public ArrayDimension(string lowerBoundToken, string upperBoundToken)
    {
        LowerBoundToken = lowerBoundToken;
        UpperBoundToken = upperBoundToken;
    }

    /// <summary>Raw lower-bound token as it appears in the datatype string.</summary>
    public string LowerBoundToken { get; }

    /// <summary>Raw upper-bound token as it appears in the datatype string.</summary>
    public string UpperBoundToken { get; }

    public bool LowerIsLiteral => int.TryParse(LowerBoundToken, out _);
    public bool UpperIsLiteral => int.TryParse(UpperBoundToken, out _);
}
