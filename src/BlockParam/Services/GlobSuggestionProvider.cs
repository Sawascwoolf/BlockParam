using System.Collections;
using BlockParam.Models;

namespace BlockParam.Services;

/// <summary>
/// ISuggestionProvider implementation for the AutoCompleteTextBox NuGet package.
/// Wraps a list of AutocompleteSuggestions and filters using GlobMatcher.
/// </summary>
public class GlobSuggestionProvider
{
    private readonly IReadOnlyList<AutocompleteSuggestion> _entries;

    public GlobSuggestionProvider(IReadOnlyList<AutocompleteSuggestion> entries)
    {
        _entries = entries;
    }

    /// <summary>
    /// Returns filtered suggestions matching the filter string.
    /// Plain text = case-insensitive contains (search-box semantics). A
    /// literal "*" opts into full GlobMatcher glob semantics. Searches
    /// across DisplayName, Value, and Comment (OR match).
    /// </summary>
    public IEnumerable GetSuggestions(string filter)
    {
        if (string.IsNullOrEmpty(filter))
            return _entries;

        return _entries.Where(e =>
            MatchesFilter(e.DisplayName, filter) ||
            MatchesFilter(e.Value, filter) ||
            MatchesFilter(e.Comment ?? "", filter));
    }

    private static bool MatchesFilter(string value, string filter)
    {
        if (filter.Contains('*'))
            return GlobMatcher.IsMatch(value, filter);
        return value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
