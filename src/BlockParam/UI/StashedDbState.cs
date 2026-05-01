using System.Collections.ObjectModel;
using BlockParam.Models;

namespace BlockParam.UI;

/// <summary>
/// In-memory snapshot of a DB's pending inline edits captured when the user
/// switches away from it without applying (#59). Lives for the dialog session
/// only — closed dialog → stash gone. Re-applied to the live tree when the
/// user switches back to the same DB.
/// </summary>
public class StashedDbState
{
    public StashedDbState(
        DataBlockSummary summary,
        IReadOnlyList<StashedEditEntry> edits)
    {
        Summary = summary;
        Edits = new ObservableCollection<StashedEditEntry>(edits);
    }

    /// <summary>The DB this stash belongs to.</summary>
    public DataBlockSummary Summary { get; }

    /// <summary>Per-edit snapshot rows used by the inspector section.</summary>
    public ObservableCollection<StashedEditEntry> Edits { get; }

    public string DbName => Summary.Name;
    public string FolderPath => Summary.FolderPath;
    public int Count => Edits.Count;

    /// <summary>
    /// " / " when this stash carries a PLC name, otherwise empty. Lets the
    /// XAML header ("PENDING IN {PLC} / {DB}") collapse the prefix without
    /// a visibility converter when single-PLC hosts stash with PlcName="".
    /// </summary>
    public string PlcSeparator =>
        string.IsNullOrEmpty(Summary.PlcName) ? "" : " / ";
}

/// <summary>
/// Per-row data for a stashed-DB inspector section (#59). Inert snapshot —
/// no live <see cref="MemberNodeViewModel"/> reference because the tree the
/// edit was made in is gone (the dialog is on a different DB now).
/// </summary>
public class StashedEditEntry
{
    public StashedEditEntry(string path, string originalValue, string pendingValue)
    {
        Path = path;
        OriginalValue = originalValue;
        PendingValue = pendingValue;
    }

    public string Path { get; }
    public string OriginalValue { get; }
    public string PendingValue { get; }

    public string Name
    {
        get
        {
            var idx = Path.LastIndexOf('.');
            return idx < 0 ? Path : Path.Substring(idx + 1);
        }
    }

    /// <summary>Last up-to-three path segments joined with " › ".</summary>
    public string ShortPath
    {
        get
        {
            var segments = Path.Split('.');
            return string.Join(" › ",
                segments.Skip(System.Math.Max(0, segments.Length - 3)));
        }
    }
}
