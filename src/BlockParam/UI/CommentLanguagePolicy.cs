namespace BlockParam.UI;

/// <summary>
/// Picks the comment variant to show in the UI from a multilingual
/// <c>&lt;MultiLanguageText&gt;</c> dict (#26). Fallback chain:
/// <c>EditingLanguage</c> → <c>ReferenceLanguage</c> → active languages →
/// any non-empty value → null.
/// </summary>
public sealed class CommentLanguagePolicy
{
    private readonly string[] _preferenceOrder;

    public CommentLanguagePolicy(
        string? editingLanguage,
        string? referenceLanguage,
        IReadOnlyList<string>? activeLanguages)
    {
        var order = new List<string>();
        AddIfMissing(order, editingLanguage);
        AddIfMissing(order, referenceLanguage);
        if (activeLanguages != null)
        {
            foreach (var lang in activeLanguages)
                AddIfMissing(order, lang);
        }
        _preferenceOrder = order.ToArray();
    }

    private static void AddIfMissing(List<string> order, string? lang)
    {
        if (string.IsNullOrEmpty(lang)) return;
        foreach (var existing in order)
            if (string.Equals(existing, lang, StringComparison.OrdinalIgnoreCase))
                return;
        order.Add(lang!);
    }

    public string? Pick(IReadOnlyDictionary<string, string>? comments)
    {
        if (comments == null || comments.Count == 0) return null;

        foreach (var lang in _preferenceOrder)
        {
            if (comments.TryGetValue(lang, out var text) && !string.IsNullOrEmpty(text))
                return text;
        }

        // Fall back to any non-empty value — covers legacy empty-key entries and
        // languages present on the member but not in the project's language set.
        foreach (var v in comments.Values)
            if (!string.IsNullOrEmpty(v)) return v;

        return null;
    }
}
