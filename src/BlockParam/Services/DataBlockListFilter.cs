using BlockParam.Models;

namespace BlockParam.Services;

/// <summary>
/// Filtering + sorting for the DB-switcher dropdown (#59).
/// Sorts by full folder path then name, mirroring how TIA's project tree
/// shows them. Filtering is case-insensitive contains across name and folder
/// (matches what the autocomplete dropdown does for tag tables — keeps the
/// keyboard model identical so users don't have to relearn it).
/// </summary>
public static class DataBlockListFilter
{
    public static IReadOnlyList<DataBlockSummary> Sort(IEnumerable<DataBlockSummary> blocks)
    {
        return blocks
            .OrderBy(b => b.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<DataBlockSummary> Filter(
        IReadOnlyList<DataBlockSummary> blocks, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return blocks;

        var trimmed = query.Trim();
        return blocks
            .Where(b =>
                b.Name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0 ||
                b.FolderPath.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
    }
}
