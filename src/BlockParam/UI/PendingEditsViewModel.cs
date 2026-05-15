using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BlockParam.Localization;
using BlockParam.Services;

namespace BlockParam.UI;

/// <summary>
/// Pending-edits + existing-issues collections slice (#80 slice 4).
///
/// <para>
/// Owns the two side-panel collections (<see cref="PendingEdits"/> +
/// <see cref="ExistingIssues"/>), their badge / count properties, and
/// the rebuild routines that repopulate them from the tree.
/// </para>
///
/// <para>
/// The underlying <c>PendingEditStore</c> stays on the host VM because
/// 20+ Apply-pipeline / value-edit / manual-selection sites mutate it
/// directly. The slice exposes <see cref="RaisePendingCountChanged"/>
/// so the host can notify after store mutations, and accepts the
/// pending-count via a constructor callback so
/// <see cref="PendingInlineEditCount"/> stays in sync without the
/// slice taking a reference to the store.
/// </para>
/// </summary>
public class PendingEditsViewModel : ViewModelBase
{
    private readonly Func<int> _getPendingCount;

    public PendingEditsViewModel(Func<int> getPendingCount)
    {
        _getPendingCount = getPendingCount;
        PendingEdits = new ObservableCollection<PendingEditEntry>();
        ExistingIssues = new ObservableCollection<ExistingIssueEntry>();
    }

    /// <summary>
    /// Aggregated view of every node that currently has a pending inline edit.
    /// Rebuilt whenever <see cref="PendingInlineEditCount"/> changes.
    /// </summary>
    public ObservableCollection<PendingEditEntry> PendingEdits { get; }

    /// <summary>
    /// Findings produced by running the validator over the *existing* StartValues
    /// when the dialog opens (and after every tree refresh / inline edit). Read-only —
    /// these are pre-existing rule violations the user can fix manually, not pending
    /// edits. They never block Apply (#26).
    /// </summary>
    public ObservableCollection<ExistingIssueEntry> ExistingIssues { get; }

    public bool HasPendingEdits => PendingEdits.Count > 0;
    public bool HasExistingIssues => ExistingIssues.Count > 0;
    public int ExistingIssuesCount => ExistingIssues.Count;

    /// <summary>Number of individual inline edits waiting to be applied.</summary>
    public int PendingInlineEditCount => _getPendingCount();

    /// <summary>Status text showing pending inline edits count.</summary>
    public string? PendingStatusText
    {
        get
        {
            var count = PendingInlineEditCount;
            if (count == 0) return null;
            return count == 1
                ? Res.Format("Pending_StatusText_Singular", count)
                : Res.Format("Pending_StatusText_Plural", count);
        }
    }

    /// <summary>
    /// Count of pending entries whose staged value fails validation (#11).
    /// Derived from the tree so it stays in sync with inline-edit validation.
    /// </summary>
    public int InvalidPendingCount => PendingEdits.Count(e => e.Node.HasInlineError);

    public bool HasInvalidPending => InvalidPendingCount > 0;

    /// <summary>"N of M invalid" summary shown on the sidebar header badge.</summary>
    public string InvalidPendingBadge
    {
        get
        {
            var total = PendingEdits.Count;
            var invalid = InvalidPendingCount;
            return invalid == 0 ? "" : Res.Format("Pending_InvalidBadge", invalid, total);
        }
    }

    /// <summary>
    /// Rebuild <see cref="PendingEdits"/> from the current tree state.
    /// Caller passes the pre-built BulkPreview VM set so overwritten flags
    /// can be set on each entry without the slice scanning the preview itself.
    /// Identity is <see cref="MemberNodeViewModel"/> reference — not path
    /// string — so two DBs sharing a path never falsely alias (#82 / #121).
    /// </summary>
    public void Rebuild(IEnumerable<MemberNodeViewModel> roots,
        HashSet<MemberNodeViewModel>? bulkNodes)
    {
        PendingEdits.Clear();
        CollectPendingEntries(roots, bulkNodes);
        OnPropertyChanged(nameof(HasPendingEdits));
        RaiseInvalidPendingChanged();
    }

    /// <summary>
    /// Rebuild <see cref="ExistingIssues"/> by running the validator over
    /// every leaf's existing StartValue. Mutates the leaf VMs'
    /// <c>HasExistingViolation</c> / <c>ExistingViolationMessage</c> so the
    /// tree's row decorations stay in sync (#26).
    /// </summary>
    public void RebuildExistingIssues(IEnumerable<MemberNodeViewModel> roots, MemberValidator validator)
    {
        ExistingIssues.Clear();
        foreach (var root in roots)
            ScanExistingViolations(root, validator);
        OnPropertyChanged(nameof(HasExistingIssues));
        OnPropertyChanged(nameof(ExistingIssuesCount));
    }

    /// <summary>
    /// Nudge bindings on <see cref="PendingInlineEditCount"/> /
    /// <see cref="PendingStatusText"/>. The host calls this after store
    /// mutations that don't fall under <see cref="Rebuild"/>.
    /// </summary>
    public void RaisePendingCountChanged()
    {
        OnPropertyChanged(nameof(PendingInlineEditCount));
        OnPropertyChanged(nameof(PendingStatusText));
    }

    /// <summary>
    /// Nudge bindings on the invalid-pending badge after a single inline-edit
    /// state flip (host call site: <c>OnSingleValueEdited</c>).
    /// </summary>
    public void RaiseInvalidPendingChanged()
    {
        OnPropertyChanged(nameof(InvalidPendingCount));
        OnPropertyChanged(nameof(HasInvalidPending));
        OnPropertyChanged(nameof(InvalidPendingBadge));
    }

    private void CollectPendingEntries(IEnumerable<MemberNodeViewModel> nodes,
        HashSet<MemberNodeViewModel>? bulkNodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsPendingInlineEdit)
            {
                // VM reference equality is unambiguous: two DBs that share a
                // path string produce two distinct MemberNodeViewModel instances
                // and will never alias (#82 / #121).
                bool overwritten = bulkNodes != null && bulkNodes.Contains(node);
                PendingEdits.Add(new PendingEditEntry(
                    node,
                    node.StartValue ?? "",
                    node.PendingValue ?? "",
                    willBeOverwrittenByBulk: overwritten));
            }
            CollectPendingEntries(node.Children, bulkNodes);
        }
    }

    private void ScanExistingViolations(MemberNodeViewModel node, MemberValidator validator)
    {
        if (node.IsLeaf && !string.IsNullOrEmpty(node.StartValue))
        {
            var error = validator.Validate(node.Model, node.StartValue);
            if (error != null)
            {
                node.HasExistingViolation = true;
                node.ExistingViolationMessage = error;
                ExistingIssues.Add(new ExistingIssueEntry(
                    node, node.StartValue ?? "", error, node.RuleHint));
            }
            else if (node.HasExistingViolation)
            {
                node.HasExistingViolation = false;
                node.ExistingViolationMessage = null;
            }
        }
        foreach (var child in node.Children)
            ScanExistingViolations(child, validator);
    }
}
