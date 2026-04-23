using System.Linq;

namespace BlockParam.UI;

/// <summary>
/// One row in the right inspector's "Pending edits" section.
/// Mirrors a node that currently carries a <see cref="MemberNodeViewModel.PendingValue"/>.
/// </summary>
public class PendingEditEntry
{
    public PendingEditEntry(MemberNodeViewModel node, string originalValue,
        string pendingValue, bool willBeOverwrittenByBulk)
    {
        Node = node;
        OriginalValue = originalValue;
        PendingValue = pendingValue;
        WillBeOverwrittenByBulk = willBeOverwrittenByBulk;
    }

    public MemberNodeViewModel Node { get; }
    public string OriginalValue { get; }
    public string PendingValue { get; }

    /// <summary>True if the currently-active bulk preview targets this same node.</summary>
    public bool WillBeOverwrittenByBulk { get; }

    public string Path => Node.Path;

    /// <summary>Last up-to-three path segments joined with " › ".</summary>
    public string ShortPath
    {
        get
        {
            var segments = Node.Path.Split('.');
            return string.Join(" \u203A ", segments.Skip(System.Math.Max(0, segments.Length - 3)));
        }
    }
}
