using System.Collections.Generic;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// One entry in the trigger pill's summary line. Either an individual
/// selected row, or a single token representing one fully-checked group
/// (every child selected) — the "Engineering" bundle.
/// </summary>
internal readonly struct PillTriggerToken
{
    public PillTriggerToken(string display, string abbreviation, int? groupMemberCount)
    {
        Display = display;
        Abbreviation = abbreviation;
        GroupMemberCount = groupMemberCount;
    }

    /// <summary>Long form shown by overflow rules / tooltip full-name modes.</summary>
    public string Display { get; }

    /// <summary>Short form shown by the trigger pill's default comma-join.</summary>
    public string Abbreviation { get; }

    /// <summary>
    /// <c>null</c> for individual row tokens; otherwise the total membership
    /// of the bundled group (so the tooltip can render "Engineering (5)").
    /// </summary>
    public int? GroupMemberCount { get; }

    /// <summary>True when this token bundles a fully-checked group.</summary>
    public bool IsGroup => GroupMemberCount.HasValue;
}

/// <summary>
/// Projects a list of currently-selected <see cref="MultiSelectRowViewModel"/> rows
/// onto a list of <see cref="PillTriggerToken"/> by collapsing each
/// fully-checked group into a single token at the position of its first
/// selected member. Rows whose group is partial (or rows with no group)
/// emit their own individual token.
/// </summary>
/// <remarks>
/// Bundling is a no-op when grouping is not configured — every row has
/// <see cref="MultiSelectRowViewModel.OwningGroup"/> = null, the if-branch never
/// matches, and the output is one token per row in source order. So callers
/// can route through this unconditionally without special-casing the
/// flat-list path.
/// </remarks>
internal static class PillTriggerTokenBuilder
{
    internal static IReadOnlyList<PillTriggerToken> Build(IReadOnlyList<MultiSelectRowViewModel> selectedRows)
    {
        if (selectedRows.Count == 0)
            return System.Array.Empty<PillTriggerToken>();

        var tokens = new List<PillTriggerToken>(selectedRows.Count);
        var seenGroups = new HashSet<MultiSelectGroupViewModel>();

        foreach (var row in selectedRows)
        {
            if (row.OwningGroup is { IsSelected: true } group)
            {
                // Bundle: emit once at the position of the first selected child.
                // Subsequent rows in the same group are absorbed into this token.
                if (seenGroups.Add(group))
                    tokens.Add(new PillTriggerToken(group.Header, group.Header, group.TotalCount));
            }
            else
            {
                // Partial group, or no group at all — individual row token.
                tokens.Add(new PillTriggerToken(row.Display, row.Abbreviation, null));
            }
        }

        return tokens;
    }
}
