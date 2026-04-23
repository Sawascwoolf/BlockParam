using System.Linq;

namespace BlockParam.UI;

/// <summary>
/// One row in the right inspector's "Bulk preview" section.
/// Reflects what would happen if the user committed the current
/// bulk edit to pending — it is NOT itself a pending edit.
/// </summary>
public class BulkPreviewEntry
{
    public BulkPreviewEntry(MemberNodeViewModel node, string originalValue,
        string previewValue, bool hasPendingConflict)
    {
        Node = node;
        OriginalValue = originalValue;
        PreviewValue = previewValue;
        HasPendingConflict = hasPendingConflict;
    }

    public MemberNodeViewModel Node { get; }
    public string OriginalValue { get; }
    public string PreviewValue { get; }
    public bool HasPendingConflict { get; }

    public string Path => Node.Path;

    /// <summary>Last up-to-three path segments, joined with " › " for display.</summary>
    public string ShortPath
    {
        get
        {
            var segments = Node.Path.Split('.');
            return string.Join(" \u203A ", segments.Skip(System.Math.Max(0, segments.Length - 3)));
        }
    }
}
