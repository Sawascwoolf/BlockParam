using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Pure function: a selected-item list + threshold options → the comma-joined
/// string the trigger pill displays. Two independent overflow stages:
/// (1) abbreviation — swap each item's Display for its Abbreviation when the
/// list grows past either entry-count or char-count threshold;
/// (2) collapse — keep the first N rendered tokens and append "+M more" for
/// the rest. Both can apply to the same selection.
/// </summary>
internal static class PillOverflowFormatter
{
    public static string Format(
        IReadOnlyList<PillRowViewModel> selected,
        PillOverflowOptions options)
    {
        if (selected.Count == 0) return string.Empty;

        var useAbbrev = ShouldAbbreviate(selected, options);
        var tokens = selected
            .Select(i => useAbbrev ? i.Abbreviation : i.Display)
            .ToList();

        if (options.CollapseAfterEntries is int max && tokens.Count > max)
        {
            var visible = tokens.Take(max);
            var hidden = tokens.Count - max;
            // Format string is host-overridable — see PillOverflowOptions.PlusMoreFormat.
            // Default pulls localized "+{0} more" / "+{0} weitere" from BlockParam's resx.
            return string.Join(", ", visible) + ", "
                + string.Format(CultureInfo.CurrentCulture, options.PlusMoreFormat, hidden);
        }

        return string.Join(", ", tokens);
    }

    private static bool ShouldAbbreviate(
        IReadOnlyList<PillRowViewModel> selected,
        PillOverflowOptions options)
    {
        if (options.AbbreviateAfterEntries is int maxEntries && selected.Count > maxEntries)
            return true;

        if (options.AbbreviateAfterChars is int maxChars)
        {
            // 2 chars per separator (", "); cheap to compute exactly without
            // building the joined string.
            var sum = (selected.Count - 1) * 2;
            foreach (var item in selected)
            {
                sum += item.Display.Length;
                if (sum > maxChars) return true;
            }
        }

        return false;
    }
}
