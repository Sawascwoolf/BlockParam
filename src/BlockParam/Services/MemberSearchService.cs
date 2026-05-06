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

    private static bool IsMatch(MemberNode member, string term) =>
        StringMatcher.MatchesAny(term,
            member.Name, member.Datatype, member.StartValue, member.Path);
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
