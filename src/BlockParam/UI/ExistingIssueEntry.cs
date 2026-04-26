using System.Linq;

namespace BlockParam.UI;

/// <summary>
/// One row in the right inspector's "Issues" section (#26).
/// Mirrors a member whose *current* StartValue (already in the DB before the
/// user opened the dialog) violates a configured rule. Read-only — these are
/// findings, not edits, and they do NOT block Apply.
/// </summary>
public class ExistingIssueEntry
{
    public ExistingIssueEntry(MemberNodeViewModel node, string currentValue,
        string message, string? hint)
    {
        Node = node;
        CurrentValue = currentValue;
        Message = message;
        Hint = hint;
    }

    public MemberNodeViewModel Node { get; }
    public string CurrentValue { get; }
    public string Message { get; }
    public string? Hint { get; }

    public string Path => Node.Path;

    /// <summary>Last up-to-three path segments joined with " › " (matches PendingEditEntry).</summary>
    public string ShortPath
    {
        get
        {
            var segments = Node.Path.Split('.');
            return string.Join(" › ", segments.Skip(System.Math.Max(0, segments.Length - 3)));
        }
    }
}
