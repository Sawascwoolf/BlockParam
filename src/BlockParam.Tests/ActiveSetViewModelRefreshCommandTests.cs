using System;
using System.Collections.Generic;
using System.Linq;
using BlockParam.Models;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Coverage for the previously-unreachable <c>RefreshDataBlocksCommand</c>
/// affordance (issue #155 follow-up): the command existed and was correctly
/// implemented, but no XAML bound it, so there was no way for a user to
/// invalidate <c>ProjectDbEnumerationCache</c> short of restarting TIA
/// Portal. This asserts the VM-level contract now surfaced by the new pill
/// row refresh button:
/// <list type="bullet">
///   <item>executing the command invokes the host's cache-busting
///       callback (<c>onRefreshDataBlocks</c>, wired by
///       <c>BulkChangeContextMenu</c> to <c>_projectDbCache.Invalidate</c>);</item>
///   <item>a normal re-open of the dropdown serves the in-VM cached list
///       (enumerate delegate called once), but Refresh always forces a
///       real re-enumeration — the delegate is called again rather than
///       served from that cache.</item>
/// </list>
///
/// The builder-style <see cref="Harness"/> mirrors
/// <c>ActiveSetViewModelCommandTests</c>'s harness (copied locally — no
/// shared state across test classes, per that file's own convention).
/// </summary>
public class ActiveSetViewModelRefreshCommandTests
{
    [Fact]
    public void RefreshDataBlocksCommand_InvokesHostCallback_AndForcesReEnumeration()
    {
        var enumerateCallCount = 0;
        var callbackInvoked = false;
        var results = new[] { new DataBlockSummary("Alpha", "") };

        var harness = new Harness(Snap(Db("Anchor")))
            .WithEnumerateDataBlocks(() => { enumerateCallCount++; return results; })
            .WithSwitchToDataBlock(_ => "<Block/>")
            .WithOnRefreshDataBlocks(() => callbackInvoked = true);

        // Prime the in-VM list the way opening the dropdown would, and
        // confirm a second open reuses it without re-enumerating — this is
        // the staleness the refresh affordance exists to break out of.
        harness.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        enumerateCallCount.Should().Be(1, "opening the dropdown does the first enumeration");

        harness.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        enumerateCallCount.Should().Be(1, "a second open must reuse the in-VM cached list, not re-enumerate");

        harness.Vm.RefreshDataBlocksCommand.Execute(null);

        callbackInvoked.Should().BeTrue(
            "refresh must invoke the host's callback (session-cache invalidation) before reloading");
        enumerateCallCount.Should().Be(2,
            "refresh must force a real re-enumeration rather than serving the in-VM cached list");
    }

    [Fact]
    public void RefreshDataBlocksCommand_WithoutHostCallback_StillForcesReEnumeration()
    {
        // Not every host wires onRefreshDataBlocks (e.g. DevLauncher / older
        // callers using the legacy ctor overload). The command must still
        // reload from the enumerate delegate even when there is no
        // session-cache to invalidate.
        var enumerateCallCount = 0;
        var results = new[] { new DataBlockSummary("Alpha", "") };

        var harness = new Harness(Snap(Db("Anchor")))
            .WithEnumerateDataBlocks(() => { enumerateCallCount++; return results; })
            .WithSwitchToDataBlock(_ => "<Block/>");

        harness.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        enumerateCallCount.Should().Be(1);

        harness.Vm.RefreshDataBlocksCommand.Execute(null);

        enumerateCallCount.Should().Be(2,
            "refresh re-enumerates even with no host callback wired");
    }

    [Fact]
    public void RefreshDataBlocksCommand_RaisesPropertyChanged_ForPlcCandidateList()
    {
        // Regression: refresh re-enumerated correctly (the fresh list landed in
        // _availableDataBlocks) but raised no notification, so the "+ PLC"
        // popup — bound to ActiveSet.InactiveProjectPlcs / CanAddPlc — kept
        // rendering its pre-refresh candidates. Observed live in TIA: a PLC
        // added mid-session stayed invisible after clicking refresh even
        // though the log showed the new blocks being enumerated.
        var plcs = new List<DataBlockSummary> { new DataBlockSummary("Alpha", "", plcName: "PLC_1", number: 1) };

        var harness = new Harness(Snap(Db("Anchor", "PLC_1")))
            .WithEnumerateDataBlocks(() => plcs)
            .WithSwitchToDataBlock(_ => "<Block/>");

        harness.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        // The host calls RebuildPlcPills on every active-set change; do it
        // once here so the row actually holds PLC_1's pill (a pill-less row
        // would report PLC_1 itself as an add-candidate).
        harness.Vm.RebuildPlcPills();
        harness.Vm.InactiveProjectPlcs.Should().BeEmpty("only PLC_1 exists and it is already in the row");

        // A new PLC appears in the project after the first enumeration.
        plcs.Add(new DataBlockSummary("Beta", "", plcName: "PLC_2", number: 2));

        var raised = new List<string>();
        harness.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        harness.Vm.RefreshDataBlocksCommand.Execute(null);

        raised.Should().Contain(nameof(ActiveSetViewModel.InactiveProjectPlcs),
            "the '+ PLC' popup's ItemsSource binding only re-reads on notification");
        raised.Should().Contain(nameof(ActiveSetViewModel.CanAddPlc),
            "the '+ PLC' button's Visibility binding only re-reads on notification");
        harness.Vm.InactiveProjectPlcs.Should().Contain("PLC_2",
            "the newly enumerated PLC must become an add-candidate");
    }

    [Fact]
    public void AddPlcCommand_AddsExactlyTheClickedPlc_AndClosesPopup()
    {
        // The popup used to be a ListBox whose SelectionChanged handler ran a
        // second time after the ItemsSource swap, adding a PLC the user never
        // clicked (clicking "PLC_3" also added "PLC_1"). The command form
        // carries the intended PLC explicitly; this pins that contract.
        var plcs = new List<DataBlockSummary>
        {
            new DataBlockSummary("Alpha", "", plcName: "PLC_1", number: 1),
            new DataBlockSummary("Beta", "", plcName: "PLC_2", number: 2),
            new DataBlockSummary("Gamma", "", plcName: "PLC_3", number: 3),
        };

        var harness = new Harness(Snap(Db("Anchor", "PLC_2")))
            .WithEnumerateDataBlocks(() => plcs)
            .WithSwitchToDataBlock(_ => "<Block/>");

        harness.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        harness.Vm.RebuildPlcPills();
        harness.Vm.IsAddDbPopupOpen = true;
        harness.Vm.InactiveProjectPlcs.Should().BeEquivalentTo(new[] { "PLC_1", "PLC_3" });

        harness.Vm.AddPlcCommand.Execute("PLC_3");

        harness.Vm.PlcPills.Select(p => p.PlcName).Should().BeEquivalentTo(
            new[] { "PLC_2", "PLC_3" },
            "only the clicked PLC joins the row — PLC_1 must not come along");
        harness.Vm.IsAddDbPopupOpen.Should().BeFalse("the popup closes after a pick");
    }

    [Fact]
    public void AddPlcCommand_IgnoresNullAndEmptyParameters()
    {
        var plcs = new List<DataBlockSummary>
        {
            new DataBlockSummary("Alpha", "", plcName: "PLC_1", number: 1),
            new DataBlockSummary("Beta", "", plcName: "PLC_2", number: 2),
        };

        var harness = new Harness(Snap(Db("Anchor", "PLC_1")))
            .WithEnumerateDataBlocks(() => plcs)
            .WithSwitchToDataBlock(_ => "<Block/>");

        harness.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        harness.Vm.RebuildPlcPills();

        harness.Vm.AddPlcCommand.Execute(null);
        harness.Vm.AddPlcCommand.Execute("");

        harness.Vm.PlcPills.Select(p => p.PlcName).Should().BeEquivalentTo(new[] { "PLC_1" });
    }

    // ---------- helpers (copied locally; no shared state) ----------

    private static ActiveSetState Snap(params ActiveDb[] dbs)
        => new ActiveSetState(
            dbs,
            new Dictionary<string, StashedDbState>(),
            "");

    private static ActiveDb Db(string name, string plc = "")
    {
        var info = new DataBlockInfo(name, 1, "Optimized", "GlobalDB", Array.Empty<MemberNode>());
        return new ActiveDb(info, $"<Block name='{name}' />", onApply: null, plcName: plc);
    }

    /// <summary>
    /// Builder-style harness so each test only wires the callbacks it needs.
    /// Mirrors the harness in <c>ActiveSetViewModelCommandTests</c> — copied
    /// here intentionally to keep this class free of shared fixtures.
    /// </summary>
    private class Harness
    {
        private readonly ActiveSetState _initial;
        private Func<IReadOnlyList<DataBlockSummary>>? _enumerate;
        private Func<DataBlockSummary, string>? _switch;
        private Action? _onRefreshDataBlocks;
        private ActiveSetViewModel? _vm;

        public Harness(ActiveSetState initial) { _initial = initial; }

        public ActiveSetViewModel Vm => _vm ??= new ActiveSetViewModel(
            _initial,
            messageBox: null,
            pendingEditStore: null,
            getModelToDb: null,
            getStartValueForNode: _ => null,
            buildActiveDbForSummary: null,
            enumerateDataBlocks: _enumerate,
            switchToDataBlock: _switch,
            tryApplyActiveDbInPlace: null,
            restoreStashOntoLive: null,
            setStatus: _ => { },
            getPendingCount: () => 0,
            dispatcher: null,
            onRefreshDataBlocks: _onRefreshDataBlocks);

        public Harness WithEnumerateDataBlocks(Func<IReadOnlyList<DataBlockSummary>> enumerate)
        { _enumerate = enumerate; return this; }

        public Harness WithSwitchToDataBlock(Func<DataBlockSummary, string> sw)
        { _switch = sw; return this; }

        public Harness WithOnRefreshDataBlocks(Action onRefresh)
        { _onRefreshDataBlocks = onRefresh; return this; }
    }
}
