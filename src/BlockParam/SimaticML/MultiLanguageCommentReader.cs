using System.Xml.Linq;
using static BlockParam.SimaticML.SimaticMLElements;
using static BlockParam.SimaticML.XmlHelpers;

namespace BlockParam.SimaticML;

/// <summary>
/// Shared reader for SimaticML <c>&lt;Comment&gt;</c> elements, which contain
/// one or more <c>&lt;MultiLanguageText Lang="xx-YY"&gt;</c> children keyed by
/// TIA culture name.
/// </summary>
internal static class MultiLanguageCommentReader
{
    /// <summary>
    /// Collects every <c>&lt;MultiLanguageText&gt;</c> child into a dict keyed by
    /// culture name. Entries without a <c>Lang</c> attribute use an empty-string
    /// key. Returns null if <paramref name="commentElement"/> is null or has no
    /// non-empty entries, so callers can distinguish "no comment" from "empty dict".
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Read(XElement? commentElement)
    {
        if (commentElement == null) return null;

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variant in LocalElements(commentElement, MultiLanguageText))
        {
            var lang = variant.Attribute(Lang)?.Value ?? "";
            var text = variant.Value;
            if (string.IsNullOrEmpty(text)) continue;
            dict[lang] = text;
        }
        return dict.Count == 0 ? null : dict;
    }
}
