using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Pure function: a selected-item list + threshold options → the comma-joined
/// string the trigger pill displays. Two independent overflow stages:
/// (1) abbreviation — swap each item's display string for its abbreviation
/// when the list grows past either entry-count or char-count threshold;
/// (2) collapse — keep the first N rendered tokens and append "+M more" for
/// the rest. Both can apply to the same selection.
/// </summary>
/// <remarks>
/// The generic overload works on any source type <c>T</c> — code-only hosts
/// can call it directly with their own domain objects. The UserControl uses
/// it internally with <see cref="MultiSelectRowViewModel"/> rows via
/// <see cref="FormatRows"/>, which pre-binds the row-accessor lambdas so
/// call sites in the code-behind remain concise.
/// </remarks>
public static class PillOverflowFormatter
{
    /// <summary>
    /// Formats <paramref name="selected"/> into a trigger-pill summary string
    /// according to <paramref name="options"/>.
    /// </summary>
    /// <typeparam name="T">Any source-item type.</typeparam>
    /// <param name="selected">The currently selected items (pre-filtered to selected only).</param>
    /// <param name="display">Extracts the full display name from <typeparamref name="T"/>.</param>
    /// <param name="abbreviation">Extracts the short-form abbreviation from <typeparamref name="T"/>.</param>
    /// <param name="options">Threshold options that control abbreviation and collapse behaviour.</param>
    public static string Format<T>(
        IReadOnlyList<T> selected,
        Func<T, string> display,
        Func<T, string> abbreviation,
        PillOverflowOptions options)
    {
        if (selected.Count == 0) return string.Empty;

        var useAbbrev = ShouldAbbreviate(selected, display, options);
        // When overflow tips us to abbreviations but a row has no
        // abbreviation (e.g. a TIA DB without a number), keep the full
        // display rather than rendering empty tokens like ", , " in the
        // trigger. The CollapseAfterEntries pass that runs next still
        // limits the total length via "+N more".
        var tokens = selected
            .Select(i =>
            {
                if (!useAbbrev) return display(i);
                var a = abbreviation(i);
                return string.IsNullOrEmpty(a) ? display(i) : a;
            })
            .ToList();

        if (options.CollapseAfterEntries is int max && tokens.Count > max)
        {
            var visible = tokens.Take(max);
            var hidden = tokens.Count - max;
            // Format string is host-overridable — see PillOverflowOptions.PlusMoreFormat.
            // Default is the English literal "+{0} more"; bind your own for localization.
            return string.Join(", ", visible) + ", "
                + string.Format(CultureInfo.CurrentCulture, options.PlusMoreFormat, hidden);
        }

        return string.Join(", ", tokens);
    }

    /// <summary>
    /// Convenience shim used internally by the UserControl. Adapts
    /// <see cref="MultiSelectRowViewModel"/> rows to the generic overload without
    /// forcing call sites to spell out the lambdas every time.
    /// </summary>
    internal static string FormatRows(
        IReadOnlyList<MultiSelectRowViewModel> selected,
        PillOverflowOptions options)
        => Format(selected, r => r.Display, r => r.Abbreviation, options);

    private static bool ShouldAbbreviate<T>(
        IReadOnlyList<T> selected,
        Func<T, string> display,
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
                sum += display(item).Length;
                if (sum > maxChars) return true;
            }
        }

        return false;
    }
}
