using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Ready-made tooltip formatter strategies for the common hover-text cases.
/// Code-only hosts call these directly via the <c>TooltipFormatter</c> CLR
/// escape hatch; XAML hosts use the <c>TooltipMode</c> DP which maps to these
/// methods internally.
/// </summary>
/// <example>
/// Code-only (arbitrary source type):
/// <code>
/// pill.TooltipFormatter = items =>
///     PillTooltipFormatters.FullNames(items, x => x.Name);
/// </code>
/// XAML host (enum DP, no generic type needed):
/// <code>
/// &lt;PillMultiSelect TooltipMode="FullNames" ... /&gt;
/// </code>
/// </example>
public static class PillTooltipFormatters
{
    /// <summary>
    /// One display name per line. Useful when the trigger collapses or
    /// abbreviates the visible label and you want users to recover the full
    /// names on hover.
    /// </summary>
    /// <typeparam name="T">Any source-item type.</typeparam>
    /// <param name="selected">The selected items to format (typically pre-filtered).</param>
    /// <param name="display">Extracts the full display name from <typeparamref name="T"/>.</param>
    public static string FullNames<T>(IReadOnlyList<T> selected, Func<T, string> display) =>
        string.Join("\n", selected.Select(display));

    /// <summary>
    /// "<c>Abbrev — Display</c>" per line. Useful when the trigger only shows
    /// abbreviations and users also want the expanded mapping for context.
    /// </summary>
    /// <typeparam name="T">Any source-item type.</typeparam>
    /// <param name="selected">The selected items to format (typically pre-filtered).</param>
    /// <param name="display">Extracts the full display name from <typeparamref name="T"/>.</param>
    /// <param name="abbreviation">Extracts the short-form abbreviation from <typeparamref name="T"/>.</param>
    public static string AbbrevAndFullNames<T>(
        IReadOnlyList<T> selected,
        Func<T, string> display,
        Func<T, string> abbreviation) =>
        string.Join("\n", selected.Select(t => $"{abbreviation(t)} — {display(t)}"));

    // ── Internal row-adapted versions for the UserControl ────────────────────

    /// <summary>
    /// Row-adapted <see cref="FullNames{T}"/> used internally by the
    /// UserControl when <see cref="PillTooltipMode.FullNames"/> is set.
    /// </summary>
    internal static string FullNamesRows(IReadOnlyList<PillRowViewModel> rows) =>
        FullNames(rows, r => r.Display);

    /// <summary>
    /// Row-adapted <see cref="AbbrevAndFullNames{T}"/> used internally by
    /// the UserControl when <see cref="PillTooltipMode.AbbrevAndFullNames"/> is set.
    /// </summary>
    internal static string AbbrevAndFullNamesRows(IReadOnlyList<PillRowViewModel> rows) =>
        AbbrevAndFullNames(rows, r => r.Display, r => r.Abbreviation);
}
