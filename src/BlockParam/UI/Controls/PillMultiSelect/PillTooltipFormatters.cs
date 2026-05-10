using System.Collections.Generic;
using System.Linq;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Ready-made <see cref="PillMultiSelectInternalState.TooltipFormatter"/>
/// strategies for the common cases. Callers can pass these directly to
/// <see cref="PillMultiSelect.TooltipFormatter"/>:
/// <c>pill.TooltipFormatter = PillTooltipFormatters.FullNames;</c>
/// </summary>
internal static class PillTooltipFormatters
{
    /// <summary>
    /// One <see cref="PillRowViewModel.Display"/> per line.
    /// Useful when the trigger collapses or abbreviates the visible label
    /// and you want users to recover the full names on hover.
    /// </summary>
    public static string FullNames(IReadOnlyList<PillRowViewModel> selected) =>
        string.Join("\n", selected.Select(i => i.Display));

    /// <summary>
    /// "<c>Abbrev — Display</c>" per line. Useful when the trigger only
    /// shows abbreviations and users may also want the mapping for context.
    /// </summary>
    public static string AbbrevAndFullNames(IReadOnlyList<PillRowViewModel> selected) =>
        string.Join("\n", selected.Select(i => $"{i.Abbreviation} — {i.Display}"));
}
