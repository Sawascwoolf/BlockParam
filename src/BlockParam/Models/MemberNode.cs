namespace BlockParam.Models;

/// <summary>
/// Represents a single member in a Data Block's interface hierarchy.
/// Immutable after construction by the parser.
/// </summary>
public class MemberNode
{
    public MemberNode(
        string name,
        string datatype,
        string? startValue,
        string path,
        MemberNode? parent,
        IReadOnlyList<MemberNode> children,
        bool isSetPoint = false,
        string? comment = null,
        bool isArrayElement = false,
        string? unresolvedBound = null,
        IReadOnlyDictionary<string, string>? comments = null)
    {
        Name = name;
        Datatype = datatype;
        StartValue = startValue;
        Path = path;
        Parent = parent;
        Children = children;
        IsSetPoint = isSetPoint;
        IsArrayElement = isArrayElement;
        UnresolvedBound = unresolvedBound;

        if (comments != null && comments.Count > 0)
        {
            Comments = comments;
            Comment = comment ?? comments.Values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
        }
        else if (!string.IsNullOrEmpty(comment))
        {
            Comments = new Dictionary<string, string> { [""] = comment! };
            Comment = comment;
        }
        else
        {
            Comments = EmptyComments;
            Comment = null;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyComments
        = new Dictionary<string, string>(0);

    public string Name { get; }
    public string Datatype { get; }
    public string? StartValue { get; }
    public string Path { get; }
    public MemberNode? Parent { get; }
    public IReadOnlyList<MemberNode> Children { get; }

    /// <summary>True if this member has the TIA "SetPoint" attribute set to true.</summary>
    public bool IsSetPoint { get; }

    /// <summary>Member comment from TIA (first non-empty language, for legacy callers).</summary>
    public string? Comment { get; }

    /// <summary>
    /// All multilingual comment variants keyed by TIA culture name (e.g. "de-DE", "en-US").
    /// Empty key is used when a single legacy comment was passed without a language.
    /// </summary>
    public IReadOnlyDictionary<string, string> Comments { get; }

    /// <summary>Datatype is a UDT reference (quoted name like "UDT_Message")</summary>
    public bool IsUdtInstance => Datatype.StartsWith("\"") && Datatype.EndsWith("\"");

    /// <summary>Datatype is an inline Struct</summary>
    public bool IsStruct => Datatype.Equals("Struct", StringComparison.OrdinalIgnoreCase);

    /// <summary>Datatype is an Array</summary>
    public bool IsArray => Datatype.StartsWith("Array[", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True if this node represents a single index of an enclosing array
    /// (e.g. <c>Motors[3]</c>). The parent of such a node is the array member.
    /// </summary>
    public bool IsArrayElement { get; }

    /// <summary>
    /// Non-null for array members that were not expanded into per-index children.
    /// Either the first unresolved symbolic bound token (e.g. "MAX_VALVES") or a
    /// size-cap marker (e.g. "(too large: 1,000,000 elements)"). Used by the UI
    /// to flag such arrays and surface the reason.
    /// </summary>
    public string? UnresolvedBound { get; }

    /// <summary>Nesting depth (0 = direct child of Section, 1 = child of a struct/UDT, etc.)</summary>
    public int Depth
    {
        get
        {
            var depth = 0;
            var current = Parent;
            while (current != null)
            {
                depth++;
                current = current.Parent;
            }
            return depth;
        }
    }

    /// <summary>True if this member has no children (leaf node with a start value)</summary>
    public bool IsLeaf => Children.Count == 0;

    public override string ToString() => $"{Path} ({Datatype}) = {StartValue ?? "(none)"}";
}
