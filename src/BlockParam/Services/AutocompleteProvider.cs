using BlockParam.Config;
using BlockParam.Models;

namespace BlockParam.Services;

/// <summary>
/// Provides autocomplete suggestions for a member based on config rules and tag tables.
/// Priority: 1. TagTableReference → tag table entries, 2. AllowedValues from config.
/// </summary>
public class AutocompleteProvider
{
    private readonly ConfigLoader _configLoader;
    private readonly TagTableCache? _tagTableCache;

    public AutocompleteProvider(ConfigLoader configLoader, TagTableCache? tagTableCache = null)
    {
        _configLoader = configLoader;
        _tagTableCache = tagTableCache;
    }

    /// <summary>
    /// Returns filtered suggestions for a MemberNode using pathPattern matching.
    /// </summary>
    public IReadOnlyList<AutocompleteSuggestion> GetSuggestions(
        MemberNode member, string filter)
    {
        var config = _configLoader.GetConfig();
        if (config == null) return Array.Empty<AutocompleteSuggestion>();

        var rule = config.GetRule(member);
        if (rule == null) return Array.Empty<AutocompleteSuggestion>();

        // Priority 1: Tag table entries
        if (rule.TagTableReference != null && _tagTableCache != null)
        {
            var entries = _tagTableCache.GetEntriesByPattern(rule.TagTableReference.TableName);
            if (entries.Count > 0)
                return FilterEntries(entries, filter);
        }

        // Priority 2: AllowedValues from constraints
        if (rule.Constraints?.AllowedValues is { Count: > 0 })
        {
            var entries = rule.Constraints.AllowedValues
                .Select(v => new AutocompleteSuggestion(v.ToString()!, v.ToString()!))
                .ToList();
            return FilterSuggestions(entries, filter);
        }

        return Array.Empty<AutocompleteSuggestion>();
    }

    /// <summary>
    /// Returns true if suggestions are available for this member (without filtering).
    /// </summary>
    public bool HasSuggestions(MemberNode member)
    {
        return GetSuggestions(member, "").Count > 0;
    }

    private static IReadOnlyList<AutocompleteSuggestion> FilterEntries(
        IReadOnlyList<TagTableEntry> entries, string filter)
    {
        if (string.IsNullOrEmpty(filter))
            return entries.Select(e => new AutocompleteSuggestion(e.Value, e.Name, e.Comment)).ToList();

        return entries
            .Where(e =>
                MatchesFilter(e.Name, filter) ||
                MatchesFilter(e.Value, filter) ||
                (e.Comment != null && MatchesFilter(e.Comment, filter)))
            .Select(e => new AutocompleteSuggestion(e.Value, e.Name, e.Comment))
            .ToList();
    }

    private static IReadOnlyList<AutocompleteSuggestion> FilterSuggestions(
        List<AutocompleteSuggestion> suggestions, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return suggestions;

        return suggestions
            .Where(s =>
                MatchesFilter(s.DisplayName, filter) ||
                MatchesFilter(s.Value, filter))
            .ToList();
    }

    /// <summary>
    /// Autocomplete filter semantics: plain text is case-insensitive contains
    /// (what users expect from a search box — typing "103" finds "V-10103").
    /// A literal "*" opts into full glob semantics via GlobMatcher so power
    /// users can still write prefix/suffix/anchored patterns.
    /// </summary>
    private static bool MatchesFilter(string value, string filter)
    {
        if (filter.Contains('*'))
            return GlobMatcher.IsMatch(value, filter);
        return value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

public class AutocompleteSuggestion : System.ComponentModel.INotifyPropertyChanged
{
    public AutocompleteSuggestion(string value, string displayName, string? comment = null)
    {
        Value = value;
        DisplayName = displayName;
        Comment = comment;
    }

    public string Value { get; }
    public string DisplayName { get; }
    public string? Comment { get; }

    private bool _isHoverPreview;
    /// <summary>
    /// Set by capture scripts to paint a hover-style highlight on the entry
    /// about to be clicked — makes the selection visible in a still frame
    /// before the next scene applies AcceptSuggestion. Always false in
    /// interactive use.
    /// </summary>
    public bool IsHoverPreview
    {
        get => _isHoverPreview;
        set
        {
            if (_isHoverPreview == value) return;
            _isHoverPreview = value;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(IsHoverPreview)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Returns the constant name — used by ComboBox when selecting.</summary>
    public override string ToString() => DisplayName;
}
