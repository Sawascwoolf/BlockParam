using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BlockParam.Config;
using BlockParam.Licensing;
using BlockParam.Localization;
using BlockParam.Models;
using BlockParam.Services;
using BlockParam.SimaticML;
using BlockParam.UI;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Regression tests for the cascade seams that PR #104's review caught
/// (#105). Each test locks in a guarantee that the existing unit-test
/// surface missed in slices 1–5:
///
/// <list type="number">
///   <item><c>RebuildAfterActiveSetChanged_RefreshesBulkPreviewBindings</c>
///     — after any active-set transition, the bulk-preview slice's
///     derived bindings (HasEntries, Count, Summary, ConflictCount,
///     HasConflict, ConflictWarning) must refresh, not just Entries.
///     Without this, the inspector header badge / summary line keeps
///     the pre-clear state until the next unrelated PropertyChanged
///     sweep.</item>
///   <item><c>ExecuteApplyMultiDb_PartialCommit_RefreshesPendingDisplay</c>
///     — when the second DB's import cancels after the first DB's
///     succeeded, the host must clear pending edits on the committed DB
///     and re-raise PendingInlineEditCount / PendingStatusText so the
///     bound count + tooltip don't stay showing the pre-commit total.</item>
///   <item><c>Derived_StringProperties_RouteThroughResX</c> — pending
///     status text and conflict warnings must compose from
///     <see cref="Res"/> resource keys, not inline English. Catches the
///     "slice author copy-pasted a one-line getter and forgot the Res
///     hop" regression class.</item>
/// </list>
/// </summary>
public class BulkChangeViewModelDerivedBindingsTests
{
    // ----- 1. RebuildAfterActiveSetChanged refreshes BulkPreview bindings -----

    [Fact]
    public void RebuildAfterActiveSetChanged_RefreshesBulkPreviewBindings()
    {
        var (vm, _, _, _, _) = CreateMultiDbVm();

        // Seed bulk-preview entries directly. Production paths route through
        // ComputeBulkPreview (host-side, gated on scope/manual-mode), but
        // that's outside this test's scope — what we're locking in is that
        // the cascade's Clear+RaiseDerivedChanged pair fires after an
        // active-set change, regardless of how the entries got there.
        var anchor = vm.Tree.RootMembers.First(r => r.Datatype == "DB" && r.Name == "FlatDB");
        var leaves = anchor.AllDescendants().Where(n => n.IsLeaf).Take(2).ToList();
        vm.BulkPreview.Add(new BulkPreviewEntry(leaves[0], "0", "1", hasPendingConflict: false));
        vm.BulkPreview.Add(new BulkPreviewEntry(leaves[1], "0", "1", hasPendingConflict: true));
        vm.BulkPreview.RaiseDerivedChanged();
        vm.BulkPreview.Entries.Should().HaveCount(2);

        var changed = new List<string?>();
        vm.BulkPreview.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Trigger an active-set transition that funnels through
        // RebuildAfterActiveSetChanged. Solo onto the anchor (no pending
        // edits anywhere → no prompt) → clean cascade test.
        vm.ActiveSet.SoloActiveDbByReference(vm.AllActiveDbs[0]);

        vm.BulkPreview.Entries.Should().BeEmpty("cascade cleared the preview");
        changed.Should().Contain(nameof(BulkPreviewViewModel.HasEntries));
        changed.Should().Contain(nameof(BulkPreviewViewModel.Count));
        changed.Should().Contain(nameof(BulkPreviewViewModel.Summary));
        changed.Should().Contain(nameof(BulkPreviewViewModel.ConflictCount));
        changed.Should().Contain(nameof(BulkPreviewViewModel.HasConflict));
        changed.Should().Contain(nameof(BulkPreviewViewModel.ConflictWarning));
    }

    // ----- 2. Partial-commit refreshes pending display -----

    [Fact]
    public void ExecuteApplyMultiDb_PartialCommit_RefreshesPendingDisplay()
    {
        // The "committed DB clears, cancelled DB retains its edits" half of
        // the contract is what production needs. The 8-line cascade fix from
        // PR #104 commit c15ab21 added RefreshPendingAndPreview at the early
        // return; this test fails if that call disappears in a future edit.
        var anchorXml = TestFixtures.LoadXml("flat-db.xml");
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var anchorInfo = parser.Parse(anchorXml);
        var peerInfo = parser.Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        // Peer's OnApply throws OperationCanceledException → simulates the
        // user dismissing the second DB's TIA import dialog. Anchor's
        // OnApply records that it succeeded.
        var anchorApplied = false;
        var peerDb = new ActiveDb(peerInfo, peerXml,
            onApply: _ => throw new OperationCanceledException("simulated user cancel"));

        var vm = new BulkChangeViewModel(
            anchorInfo, anchorXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            onApply: _ => anchorApplied = true,
            additionalActiveDbs: new[] { peerDb });

        // Stage one inline edit in each DB through the production
        // EditableStartValue path so PendingEdits picks them up.
        var anchorRoot = vm.Tree.RootMembers.First(r => r.Datatype == "DB" && r.Name == anchorInfo.Name);
        var peerRoot = vm.Tree.RootMembers.First(r => r.Datatype == "DB" && r.Name == peerInfo.Name);
        var anchorLeaf = anchorRoot.AllDescendants().First(n => n.IsLeaf && !string.IsNullOrEmpty(n.StartValue));
        var peerLeaf = peerRoot.AllDescendants().First(n => n.IsLeaf && !string.IsNullOrEmpty(n.StartValue));
        anchorLeaf.EditableStartValue = anchorLeaf.StartValue == "0" ? "1" : "0";
        peerLeaf.EditableStartValue = peerLeaf.StartValue == "0" ? "1" : "0";

        vm.Pending.PendingInlineEditCount.Should().Be(2, "setup: 1 edit per DB staged");

        var pendingNotifications = new List<string?>();
        ((INotifyPropertyChanged)vm.Pending).PropertyChanged
            += (_, e) => pendingNotifications.Add(e.PropertyName);

        // ApplyCommand → ExecuteApply → ExecuteApplyMultiDb → anchor commits,
        // peer throws → partial-commit branch clears anchor's pending state
        // and calls RefreshPendingAndPreview.
        vm.ApplyCommand.Execute(null);

        anchorApplied.Should().BeTrue("anchor's OnApply ran before the peer threw");
        vm.Pending.PendingInlineEditCount.Should().Be(1,
            "anchor's edit got cleared by ClearPendingValuesForDb; peer's survived the cancel");
        pendingNotifications.Should().Contain(nameof(PendingEditsViewModel.PendingInlineEditCount),
            "the binding cannot repaint without this notification");
        pendingNotifications.Should().Contain(nameof(PendingEditsViewModel.PendingStatusText),
            "status text is derived from the count");
    }

    // ----- 3. Derived string properties route through Res.X -----

    [Fact]
    public void PendingStatusText_RouteThroughResX_Plural()
    {
        // Catches the "slice author wrote `=> $\"{Count} pending\"` instead
        // of going through Res" regression class. Test asserts byte-equality
        // against the Res key's formatted output — any inline-English fork
        // would diverge from a localized build.
        var (vm, _, _, _, _) = CreateMultiDbVm();
        StageEdits(vm, "FlatDB", count: 2);

        vm.Pending.PendingInlineEditCount.Should().Be(2);
        vm.Pending.PendingStatusText.Should().Be(Res.Format("Pending_StatusText_Plural", 2),
            "PendingStatusText must compose via the Res key, not inline strings");
    }

    [Fact]
    public void PendingStatusText_RouteThroughResX_Singular()
    {
        var (vm, _, _, _, _) = CreateMultiDbVm();
        StageEdits(vm, "FlatDB", count: 1);

        vm.Pending.PendingInlineEditCount.Should().Be(1);
        vm.Pending.PendingStatusText.Should().Be(Res.Format("Pending_StatusText_Singular", 1));
    }

    [Fact]
    public void BulkPreviewConflictWarning_RouteThroughResX()
    {
        // Same regression-class as PendingStatusText. Conflict-overlap count
        // is computed from Entries; staging entries directly is the focused
        // path — production's ComputeBulkPreview is exercised by other tests.
        var (vm, _, _, _, _) = CreateMultiDbVm();
        var roots = vm.Tree.RootMembers.First(r => r.Datatype == "DB");
        var leaves = roots.AllDescendants().Where(n => n.IsLeaf).Take(3).ToList();
        vm.BulkPreview.Add(new BulkPreviewEntry(leaves[0], "0", "1", hasPendingConflict: true));
        vm.BulkPreview.Add(new BulkPreviewEntry(leaves[1], "0", "1", hasPendingConflict: true));
        vm.BulkPreview.Add(new BulkPreviewEntry(leaves[2], "0", "1", hasPendingConflict: false));

        vm.BulkPreview.ConflictCount.Should().Be(2);
        vm.BulkPreview.ConflictWarning.Should().Be(Res.Format("BulkPreview_ConflictWarning_Plural", 2),
            "ConflictWarning must compose via the Res key");
    }

    [Fact]
    public void BulkPreviewConflictWarning_RouteThroughResX_Singular()
    {
        var (vm, _, _, _, _) = CreateMultiDbVm();
        var roots = vm.Tree.RootMembers.First(r => r.Datatype == "DB");
        var leaf = roots.AllDescendants().First(n => n.IsLeaf);
        vm.BulkPreview.Add(new BulkPreviewEntry(leaf, "0", "1", hasPendingConflict: true));

        vm.BulkPreview.ConflictCount.Should().Be(1);
        vm.BulkPreview.ConflictWarning.Should().Be(Res.Get("BulkPreview_ConflictWarning_Singular"));
    }

    [Fact]
    public void BulkPreviewConflictWarning_EmptyWhenNoConflicts()
    {
        var (vm, _, _, _, _) = CreateMultiDbVm();
        var roots = vm.Tree.RootMembers.First(r => r.Datatype == "DB");
        var leaf = roots.AllDescendants().First(n => n.IsLeaf);
        vm.BulkPreview.Add(new BulkPreviewEntry(leaf, "0", "1", hasPendingConflict: false));

        vm.BulkPreview.ConflictCount.Should().Be(0);
        vm.BulkPreview.ConflictWarning.Should().BeEmpty(
            "no conflicts → no warning string, regardless of how many entries are staged");
    }

    // ----- helpers -----

    private static (BulkChangeViewModel vm, IUsageTracker tracker,
                    string anchorXml, string peerXml,
                    List<string> applyOrder)
        CreateMultiDbVm()
    {
        var anchorXml = TestFixtures.LoadXml("flat-db.xml");
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var anchor = parser.Parse(anchorXml);
        var peer = parser.Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var applyOrder = new List<string>();
        var peerDb = new ActiveDb(peer, peerXml,
            onApply: _ => applyOrder.Add(peer.Name));

        var vm = new BulkChangeViewModel(
            anchor, anchorXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            onApply: _ => applyOrder.Add(anchor.Name),
            additionalActiveDbs: new[] { peerDb });

        return (vm, tracker, anchorXml, peerXml, applyOrder);
    }

    private static void StageEdits(BulkChangeViewModel vm, string dbName, int count)
    {
        var root = vm.Tree.RootMembers.First(r => r.Datatype == "DB" && r.Name == dbName);
        var leaves = root.AllDescendants()
            .Where(n => n.IsLeaf && !string.IsNullOrEmpty(n.StartValue))
            .Take(count)
            .ToList();
        if (leaves.Count < count)
            throw new InvalidOperationException(
                $"DB '{dbName}' has only {leaves.Count} primitive leaves with start values, requested {count}");
        foreach (var leaf in leaves)
            leaf.EditableStartValue = leaf.StartValue == "0" ? "1" : "0";
    }
}
