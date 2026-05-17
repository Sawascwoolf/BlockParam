using System.Linq;

namespace BlockParam.UI;

/// <summary>
/// One row in the right inspector's "Pending edits" section.
/// Mirrors a node that currently carries a <see cref="MemberNodeViewModel.PendingValue"/>.
/// </summary>
public class PendingEditEntry
{
    public PendingEditEntry(MemberNodeViewModel node, string originalValue,
        string pendingValue, bool willBeOverwrittenByBulk, string dbLabel = "")
    {
        Node = node;
        OriginalValue = originalValue;
        PendingValue = pendingValue;
        WillBeOverwrittenByBulk = willBeOverwrittenByBulk;
        DbLabel = dbLabel ?? "";
    }

    public MemberNodeViewModel Node { get; }
    public string OriginalValue { get; }
    public string PendingValue { get; }

    /// <summary>
    /// Collision-safe owning-DB display string (#145), resolved via the
    /// shared <see cref="ActiveDbDisplayName"/> formatter so it matches the
    /// tree's DB group header exactly. Empty when only one DB is active —
    /// the qualifier would be needless noise in single-DB sessions. When
    /// non-empty the row template renders a "DB: {label}" prefix so two
    /// edits on the same member path in different DBs stay distinguishable.
    /// </summary>
    public string DbLabel { get; }

    /// <summary>True when <see cref="DbLabel"/> should be rendered (multi-DB).</summary>
    public bool HasDbLabel => !string.IsNullOrEmpty(DbLabel);

    /// <summary>Localized "DB: {label}" string shown on the row in multi-DB sessions.</summary>
    public string DbLabelDisplay =>
        HasDbLabel ? BlockParam.Localization.Res.Format("Pending_DbQualifier", DbLabel) : "";

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
