using System.Collections.ObjectModel;
using System.Linq;

namespace BlockParam.UI;

/// <summary>
/// Bulk-preview collection slice (#80 slice 5).
///
/// <para>
/// Owns the live preview of rows that would be staged when the user
/// clicks Set, plus the section-header summary / conflict-overlap
/// readouts derived from it. The preview itself never mutates any tree
/// node — entries are computed by the host VM from the current scope ×
/// NewValue state, and the slice just renders them.
/// </para>
///
/// <para>
/// The summary string composes with the current NewValue (host-owned),
/// so the slice accepts it via a constructor callback rather than
/// pulling NewValue into its surface.
/// </para>
/// </summary>
public class BulkPreviewViewModel : ViewModelBase
{
    private readonly Func<string> _getTargetValue;

    public BulkPreviewViewModel(Func<string> getTargetValue)
    {
        _getTargetValue = getTargetValue;
        Entries = new ObservableCollection<BulkPreviewEntry>();
    }

    /// <summary>
    /// Live preview rows. Cleared and rebuilt by the host whenever
    /// target / scope / value changes. On Set, entries are transferred to
    /// pending and the collection is cleared.
    /// </summary>
    public ObservableCollection<BulkPreviewEntry> Entries { get; }

    public bool HasEntries => Entries.Count > 0;
    public int Count => Entries.Count;

    /// <summary>Preview rows whose node already has a pending edit — they'd overwrite it on Set.</summary>
    public int ConflictCount => Entries.Count(e => e.HasPendingConflict);

    public bool HasConflict => ConflictCount > 0;

    public string ConflictWarning
    {
        get
        {
            int n = ConflictCount;
            if (n == 0) return "";
            return n == 1
                ? "⚠ 1 overlap with pending edits — will be overwritten."
                : $"⚠ {n} overlap with pending edits — will be overwritten.";
        }
    }

    /// <summary>
    /// Summary shown in the section header, e.g. "90 ⇢ 85" when all rows share
    /// the same original value, or "{count} targets" otherwise.
    /// </summary>
    public string Summary
    {
        get
        {
            if (Entries.Count == 0) return "";
            var firstOrig = Entries[0].OriginalValue;
            bool homogeneous = Entries.All(e =>
                string.Equals(e.OriginalValue, firstOrig, StringComparison.Ordinal));
            if (homogeneous && !string.IsNullOrEmpty(firstOrig))
                return $"{firstOrig} ⇢ {_getTargetValue()}";
            return $"{Entries.Count} targets";
        }
    }

    /// <summary>Remove every preview row.</summary>
    public void Clear() => Entries.Clear();

    /// <summary>Append a single preview row.</summary>
    public void Add(BulkPreviewEntry entry) => Entries.Add(entry);

    /// <summary>
    /// Nudge all derived bindings (count / conflict / summary). Called by
    /// the host after a Clear-then-Add(...) batch so the inspector header
    /// updates in one render pass.
    /// </summary>
    public void RaiseDerivedChanged()
    {
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ConflictCount));
        OnPropertyChanged(nameof(HasConflict));
        OnPropertyChanged(nameof(ConflictWarning));
    }
}
