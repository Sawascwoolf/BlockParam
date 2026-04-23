using BlockParam.Models;

namespace BlockParam.Services;

/// <summary>
/// Searches through a DataBlockInfo's member tree.
/// Matches against Name, Datatype, StartValue, and Path.
/// </summary>
public class MemberSearchService
{
    /// <summary>
    /// Searches for members matching the query string.
    /// Query is split by spaces — all terms must match (AND logic).
    /// Each term is matched case-insensitive against Name, Datatype, StartValue, and Path.
    /// </summary>
    public SearchResult Search(DataBlockInfo db, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new SearchResult(query, db.AllMembers().ToList());

        var terms = query.Trim()
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        var matches = db.AllMembers()
            .Where(m => terms.All(term => IsMatch(m, term)))
            .ToList();

        return new SearchResult(query, matches);
    }

    private static bool IsMatch(MemberNode member, string term)
    {
        // Use IndexOf instead of Contains(string, StringComparison) to avoid
        // dependency on System.Runtime.CompilerServices.Unsafe which is missing
        // in the TIA Portal host process.
        return member.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
            || member.Datatype.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
            || (member.StartValue?.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
            || member.Path.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

public class SearchResult
{
    public SearchResult(string query, IReadOnlyList<MemberNode> matches)
    {
        Query = query;
        Matches = matches;
    }

    public string Query { get; }
    public IReadOnlyList<MemberNode> Matches { get; }
    public int HitCount => Matches.Count;
    public bool IsEmpty => string.IsNullOrWhiteSpace(Query);
}
