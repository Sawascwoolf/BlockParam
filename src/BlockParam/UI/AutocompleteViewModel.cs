using System.Collections.Generic;
using System.Linq;
using BlockParam.Services;

namespace BlockParam.UI;

/// <summary>
/// Autocomplete suggestion slice (#80 slice 3).
///
/// <para>
/// Data holder for the inline-autocomplete overlay: the full candidate
/// pool, the currently-displayed filtered subset, and the glob provider
/// used by per-row inline matching. Owns the term-matching logic
/// (previously duplicated across four host-VM methods).
/// </para>
///
/// <para>
/// The host VM keeps the public methods that compose with non-slice
/// state (<c>AcceptSuggestion</c> mutates <c>NewValue</c>;
/// <c>GetSuggestionsForMember</c> reads tag-table cache + config rules;
/// <c>ReloadSuggestions</c> reads the selected member). Those methods
/// drive the slice via <see cref="SetCandidates"/> /
/// <see cref="ClearFiltered"/> / <see cref="ApplyFilter"/> /
/// <see cref="ShowAll"/> / <see cref="Toggle"/>.
/// </para>
/// </summary>
public class AutocompleteViewModel : ViewModelBase
{
    private GlobSuggestionProvider? _suggestionProvider;
    private IReadOnlyList<AutocompleteSuggestion> _suggestions = Array.Empty<AutocompleteSuggestion>();
    private IReadOnlyList<AutocompleteSuggestion> _filteredSuggestions = Array.Empty<AutocompleteSuggestion>();
    private bool _suppressSuggestions;

    /// <summary>Suggestion provider for glob-based per-row matching (null = no suggestions).</summary>
    public GlobSuggestionProvider? SuggestionProvider
    {
        get => _suggestionProvider;
        private set => SetProperty(ref _suggestionProvider, value);
    }

    /// <summary>Full candidate pool for the currently-selected member.</summary>
    public IReadOnlyList<AutocompleteSuggestion> Suggestions
    {
        get => _suggestions;
        private set => SetProperty(ref _suggestions, value);
    }

    /// <summary>Filtered subset currently rendered in the overlay.</summary>
    public IReadOnlyList<AutocompleteSuggestion> FilteredSuggestions
    {
        get => _filteredSuggestions;
        private set
        {
            if (SetProperty(ref _filteredSuggestions, value))
                OnPropertyChanged(nameof(HasFilteredSuggestions));
        }
    }

    public bool HasFilteredSuggestions => _filteredSuggestions.Count > 0;

    /// <summary>
    /// When true, <see cref="ApplyFilter"/> short-circuits to empty —
    /// host sets this around bulk-edit operations that would otherwise
    /// re-open the overlay.
    /// </summary>
    public bool SuppressSuggestions
    {
        get => _suppressSuggestions;
        set => _suppressSuggestions = value;
    }

    /// <summary>
    /// Replace the candidate pool. Clears the visible filtered list as a
    /// side effect — callers that want the overlay re-rendered should
    /// follow with <see cref="ApplyFilter"/> / <see cref="ShowAll"/>.
    /// </summary>
    public void SetCandidates(
        IReadOnlyList<AutocompleteSuggestion> suggestions,
        GlobSuggestionProvider? provider)
    {
        Suggestions = suggestions;
        SuggestionProvider = provider;
        FilteredSuggestions = Array.Empty<AutocompleteSuggestion>();
    }

    /// <summary>Clear the candidate pool and any rendered filter.</summary>
    public void ClearCandidates()
    {
        Suggestions = Array.Empty<AutocompleteSuggestion>();
        SuggestionProvider = null;
        FilteredSuggestions = Array.Empty<AutocompleteSuggestion>();
    }

    /// <summary>Hide the overlay without touching the candidate pool.</summary>
    public void ClearFiltered() =>
        FilteredSuggestions = Array.Empty<AutocompleteSuggestion>();

    /// <summary>
    /// Re-render the filtered overlay from the current candidate pool +
    /// the given filter text. Empty filter when the user clears the
    /// textbox keeps the overlay open if it was already open; an empty
    /// pool or <see cref="SuppressSuggestions"/> hides the overlay.
    /// </summary>
    public void ApplyFilter(string filter)
    {
        if (_suggestions.Count == 0 || _suppressSuggestions)
        {
            FilteredSuggestions = Array.Empty<AutocompleteSuggestion>();
            return;
        }

        if (string.IsNullOrWhiteSpace(filter))
        {
            FilteredSuggestions = Array.Empty<AutocompleteSuggestion>();
            return;
        }

        FilteredSuggestions = Match(_suggestions, filter);
    }

    /// <summary>Open the overlay (showing all when filter is empty).</summary>
    public void ShowAll(string filter)
    {
        if (_suggestions.Count == 0) return;
        FilteredSuggestions = string.IsNullOrEmpty(filter)
            ? _suggestions.ToList()
            : Match(_suggestions, filter);
    }

    /// <summary>Toggle the overlay: hide if open, open with filter otherwise.</summary>
    public void Toggle(string filter)
    {
        if (_filteredSuggestions.Count > 0)
        {
            FilteredSuggestions = Array.Empty<AutocompleteSuggestion>();
            return;
        }
        ShowAll(filter);
    }

    /// <summary>
    /// Term-matching shared by overlay filters and per-row inline lookup
    /// (<c>BulkChangeViewModel.GetSuggestionsForMember</c>). All space-
    /// separated terms must match somewhere in DisplayName / Value /
    /// Comment, case-insensitive.
    /// </summary>
    public static IReadOnlyList<AutocompleteSuggestion> Match(
        IEnumerable<AutocompleteSuggestion> source, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return source as IReadOnlyList<AutocompleteSuggestion> ?? source.ToList();

        var terms = filter.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return source.Where(s => terms.All(term =>
            s.DisplayName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
            s.Value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
            (s.Comment != null && s.Comment.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)))
            .ToList();
    }
}
