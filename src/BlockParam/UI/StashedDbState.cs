using System.Collections.ObjectModel;
using BlockParam.Models;

namespace BlockParam.UI;

/// <summary>
/// In-memory snapshot of a DB's pending inline edits captured when the user
/// switches away from it without applying (#59). Lives for the dialog session
/// only — closed dialog → stash gone. Re-applied to the live tree when the
/// user switches back to the same DB.
/// </summary>
public class StashedDbState : ViewModelBase
{
    private bool _isExpanded = true;

    // #190: whether the "{PLC} / " chrome renders in the "PENDING IN ..."
    // header. Driven by the host's plcCount > 1, not by Summary.PlcName
    // being empty — Summary.PlcName is identity and is always the real PLC
    // name now (see ActiveSetViewModel.CaptureStashForDb). Defaults to false
    // so existing callers that don't care about display chrome are unaffected.
    private readonly bool _showPlcChrome;

    public StashedDbState(
        DataBlockSummary summary,
        IReadOnlyList<StashedEditEntry> edits,
        bool showPlcChrome = false)
    {
        Summary = summary;
        Edits = new ObservableCollection<StashedEditEntry>(edits);
        _showPlcChrome = showPlcChrome;
    }

    /// <summary>The DB this stash belongs to.</summary>
    public DataBlockSummary Summary { get; }

    /// <summary>Per-edit snapshot rows used by the inspector section.</summary>
    public ObservableCollection<StashedEditEntry> Edits { get; }

    public string DbName => Summary.Name;
    public string FolderPath => Summary.FolderPath;
    public int Count => Edits.Count;

    /// <summary>
    /// Whether the per-edit rows are visible. Per-section so each stash
    /// remembers its own collapsed state across re-renders. Defaults to
    /// expanded so the user sees their stashed work right after a switch.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// The PLC name to actually render in the "PENDING IN {PLC} / {DB}"
    /// header, or "" when chrome is off (#190). <see cref="Summary"/>'s
    /// PlcName is always the real PLC name (identity); this property is the
    /// display gate on top of it — bind to this, not <c>Summary.PlcName</c>.
    /// </summary>
    public string PlcNameForDisplay => _showPlcChrome ? Summary.PlcName : "";

    /// <summary>
    /// " / " when <see cref="PlcNameForDisplay"/> is non-empty, otherwise
    /// empty. Lets the XAML header collapse the prefix without a visibility
    /// converter — single-PLC sessions (chrome off) and hosts that supply no
    /// PLC name both stay tidy.
    /// </summary>
    public string PlcSeparator =>
        string.IsNullOrEmpty(PlcNameForDisplay) ? "" : " / ";
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
