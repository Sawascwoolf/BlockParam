using BlockParam.Models;

namespace BlockParam.Services;

/// <summary>
/// Filtering + sorting for the DB-switcher dropdown (#59).
/// Sorts by full folder path then name, mirroring how TIA's project tree
/// shows them. Filtering is case-insensitive contains on the DB name plus
/// the numeric block ID — PLC programmers reference DBs both by symbolic
/// name (<c>DB_ProcessPlant_A1</c>) and by number (<c>DB17</c>), so both
/// have to find their target with one keystroke pattern. Folder paths are
/// not searched: typing fragments of project tree paths is rarely the
/// intent and the noise hurts the common case.
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
        return blocks.Where(b => Matches(b, trimmed)).ToList();
    }

    private static bool Matches(DataBlockSummary b, string trimmed)
    {
        // Bare-digit query: route exclusively to number match. Substring
        // matches inside names like "DB_Tank1" would otherwise drown the
        // picker for a single-key query.
        if (IsAllDigits(trimmed))
        {
            return b.Number is int n
                && n.ToString().IndexOf(trimmed, StringComparison.Ordinal) >= 0;
        }
        if (b.Name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (b.Number is int num
            && $"DB{num}".IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        return false;
    }

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (var ch in s) if (ch < '0' || ch > '9') return false;
        return true;
    }
}
