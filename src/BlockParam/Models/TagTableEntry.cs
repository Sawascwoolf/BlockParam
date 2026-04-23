namespace BlockParam.Models;

/// <summary>
/// Represents a single constant entry from a TIA Portal tag table.
/// </summary>
public class TagTableEntry
{
    public TagTableEntry(string name, string value, string dataType, string? comment = null,
        Dictionary<string, string>? comments = null)
    {
        Name = name;
        Value = value;
        DataType = dataType;
        Comment = comment;
        Comments = comments ?? new Dictionary<string, string>();
    }

    /// <summary>Constant name (e.g. "MODULE_FOERDERER_1")</summary>
    public string Name { get; }

    /// <summary>Constant value (e.g. "42")</summary>
    public string Value { get; }

    /// <summary>Data type (e.g. "Int")</summary>
    public string DataType { get; }

    /// <summary>Default comment (first available language)</summary>
    public string? Comment { get; }

    /// <summary>Comments by culture name (e.g. "en-GB" → "TP307")</summary>
    public Dictionary<string, string> Comments { get; }

    /// <summary>Returns comment for a specific language, falling back to default.</summary>
    public string? GetComment(string language) =>
        Comments.TryGetValue(language, out var c) ? c : Comment;

    public override string ToString() => $"{Name} = {Value} ({DataType})";
}
