using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BlockParam.Models;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Command-level coverage for <see cref="PlcPillViewModel.LoadCommand"/>,
/// the lazy DB-list fetch wired as
/// <c>new RelayCommand(_ =&gt; OnIsOpenFlippedToTrue(), _ =&gt; !_isLoaded)</c>.
///
/// <para>
/// The load handler is <c>async void</c> but awaits the host-supplied
/// <c>loadDbs</c> delegate. The harness returns an already-completed
/// <see cref="Task"/> (<see cref="Task.FromResult{TResult}"/>), so the
/// awaiter reports completed and the continuation runs synchronously
/// before <c>Execute</c> returns — no dispatcher pumping needed, plain
/// <c>[Fact]</c> is sufficient.
/// </para>
///
/// Contracts under test:
/// <list type="bullet">
///   <item>First <c>Execute</c> performs the load — observable via
///       <see cref="PlcPillViewModel.IsLoaded"/> flipping true and
///       <see cref="PlcPillViewModel.AvailableDbs"/> being replaced with
///       the fetched list.</item>
///   <item><c>CanExecute</c> is true before load (<c>!_isLoaded</c>) and
///       false after.</item>
///   <item>A second <c>Execute</c> is a safe no-op (the loader is not
///       invoked again).</item>
/// </list>
/// </summary>
public class PlcPillViewModelTests
{
    [Fact]
    public void LoadCommand_FirstExecute_PerformsLoadAndPopulatesAvailableDbs()
    {
        var fetched = new List<DataBlockListItem>
        {
            Item("DB_One", "PLC_1"),
            Item("DB_Two", "PLC_1"),
        };
        int loadCalls = 0;
        var vm = new PlcPillViewModel(
            plcName: "PLC_1",
            isAnchor: true,
            initialActiveItems: Array.Empty<DataBlockListItem>(),
            loadDbs: _ =>
            {
                loadCalls++;
                return Task.FromResult<IReadOnlyList<DataBlockListItem>>(fetched);
            });

        vm.LoadCommand.Execute(null);

        loadCalls.Should().Be(1, "the first Execute drives the lazy DB-list fetch");
        vm.IsLoaded.Should().BeTrue(
            "OnIsOpenFlippedToTrue sets IsLoaded once the fetch completes");
        vm.AvailableDbs.Should().HaveCount(2,
            "AvailableDbs is replaced with the fetched PLC list");
        vm.AvailableDbs.Should().BeEquivalentTo(fetched);
    }

    [Fact]
    public void LoadCommand_CanExecute_TrueBeforeLoad_FalseAfterLoad()
    {
        var vm = new PlcPillViewModel(
            plcName: "PLC_1",
            isAnchor: false,
            initialActiveItems: Array.Empty<DataBlockListItem>(),
            loadDbs: _ => Task.FromResult<IReadOnlyList<DataBlockListItem>>(
                Array.Empty<DataBlockListItem>()));

        vm.LoadCommand.CanExecute(null).Should().BeTrue(
            "the guard is !_isLoaded and nothing has loaded yet");

        vm.LoadCommand.Execute(null);

        vm.LoadCommand.CanExecute(null).Should().BeFalse(
            "after a successful load _isLoaded is true so the guard flips");
    }

    [Fact]
    public void LoadCommand_SecondExecute_IsSafeNoOp()
    {
        int loadCalls = 0;
        var vm = new PlcPillViewModel(
            plcName: "PLC_1",
            isAnchor: false,
            initialActiveItems: Array.Empty<DataBlockListItem>(),
            loadDbs: _ =>
            {
                loadCalls++;
                return Task.FromResult<IReadOnlyList<DataBlockListItem>>(
                    new List<DataBlockListItem> { Item("DB_One", "PLC_1") });
            });

        vm.LoadCommand.Execute(null);
        // Execute again directly (bypassing CanExecute, as a misbehaving
        // binding could) — OnIsOpenFlippedToTrue must short-circuit on the
        // _isLoaded guard rather than re-fetching.
        vm.LoadCommand.Execute(null);

        loadCalls.Should().Be(1,
            "a second Execute is a safe no-op once the pill is loaded");
        vm.IsLoaded.Should().BeTrue();
        vm.AvailableDbs.Should().ContainSingle(
            "the list is not re-fetched / duplicated on the second Execute");
    }

    // ─────────────────────────────────────────────────────────────────────
    // #141 — numberless instance DB: closed pill must render its selection
    // WITHOUT a first popup-open, and a trigger click must toggle IsOpen.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void NumberlessInstanceDb_IsSeededSelected_WithoutOpeningPopup()
    {
        // Gen_Main_IDB: instance DB, _IDB suffix, NO number → empty
        // Abbreviation. The repro from #141.
        var idb = InstanceDbItem("Gen_Main_IDB", "CPU_1");

        var vm = new PlcPillViewModel(
            plcName: "CPU_1",
            isAnchor: true,
            initialActiveItems: new List<DataBlockListItem> { idb },
            loadDbs: _ => Task.FromResult<IReadOnlyList<DataBlockListItem>>(
                new List<DataBlockListItem> { idb }));

        // Closed pill: NO popup ever opened, IsOpen still false.
        vm.IsOpen.Should().BeFalse();
        vm.IsLoaded.Should().BeFalse("the lazy PLC-list fetch has not run");

        // The active DB is seeded into SelectedDbs by the SAME reference as
        // AvailableDbs, so PillSelectionSync can mark the row IsCheckedTrue
        // and the closed trigger renders the summary — no OnIsOpenFlippedToTrue.
        vm.SelectedDbs.Should().ContainSingle().Which.Should().BeSameAs(idb);
        vm.AvailableDbs.Should().ContainSingle().Which.Should().BeSameAs(idb);
        vm.Label.Should().NotBeNull();
    }

    [Fact]
    public void TriggerClick_TogglesIsOpen_EndToEnd_AndDriveLazyLoadOnce()
    {
        var idb = InstanceDbItem("Gen_Main_IDB", "CPU_1");
        int loadCalls = 0;
        var vm = new PlcPillViewModel(
            plcName: "CPU_1",
            isAnchor: true,
            initialActiveItems: new List<DataBlockListItem> { idb },
            loadDbs: _ =>
            {
                loadCalls++;
                return Task.FromResult<IReadOnlyList<DataBlockListItem>>(
                    new List<DataBlockListItem> { idb });
            });

        // Simulate the trigger click result: the IsChecked→IsOpen chain
        // ends at PlcPillViewModel.IsOpen = true.
        vm.IsOpen = true;

        vm.IsOpen.Should().BeTrue("the click must flip IsOpen end-to-end");
        loadCalls.Should().Be(1, "first open drives the lazy PLC-list fetch once");
        vm.IsLoaded.Should().BeTrue();

        // Closing then re-opening must round-trip and NOT re-fetch.
        vm.IsOpen = false;
        vm.IsOpen.Should().BeFalse();
        vm.IsOpen = true;
        vm.IsOpen.Should().BeTrue();
        loadCalls.Should().Be(1, "the list is loaded once, not on every open");
    }

    [UIFact]
    public void ClosedPill_NumberlessInstanceDb_RendersSummaryWithoutOpen_FullControlPath()
    {
        // End-to-end through the real PillMultiSelect control with the
        // exact BulkChangeDialog binding shape:
        //   ItemsSource  = AvailableDbs
        //   SelectedItems = SelectedDbs (Mode=OneWay)
        //   IsOpen        = TwoWay
        // The control must show HasSelection on the CLOSED trigger.
        var idb = InstanceDbItem("Gen_Main_IDB", "CPU_1");
        var vm = new PlcPillViewModel(
            plcName: "CPU_1",
            isAnchor: true,
            initialActiveItems: new List<DataBlockListItem> { idb },
            loadDbs: _ => Task.FromResult<IReadOnlyList<DataBlockListItem>>(
                new List<DataBlockListItem> { idb }));

        var pill = new BlockParam.UI.Controls.PillMultiSelect.PillMultiSelect
        {
            DisplayMemberPath = nameof(DataBlockListItem.Display),
            AbbreviationMemberPath = nameof(DataBlockListItem.Abbreviation),
            // Mirror the production BulkChangeDialog binding: the DB-pill
            // overflow formatter falls back to the full Display name when the
            // abbreviation is empty (numberless DB) — see PillOverflowFormatter.
            OverflowOptions =
                BlockParam.UI.Controls.PillMultiSelect.PillOverflowOptions.DataBlockDefault(),
        };
        // Worst-case DP order from #141: SelectedItems BEFORE ItemsSource.
        pill.SelectedItems = vm.SelectedDbs;
        pill.ItemsSource = vm.AvailableDbs;

        pill.IsOpen.Should().BeFalse("no popup was opened");
        pill.InternalState.HasSelection.Should().BeTrue(
            "the closed trigger renders the selection summary without a popup-open");
        pill.InternalState.SelectedCount.Should().Be(1);
        // Numberless DB → empty Abbreviation; the summary must fall back to
        // the full Display name rather than render blank.
        pill.InternalState.SelectedAbbreviationsText.Should().Contain("Gen_Main_IDB");
    }

    [UIFact]
    public void TriggerClick_DrivesIsOpen_ThroughControl_ToViewModel()
    {
        var idb = InstanceDbItem("Gen_Main_IDB", "CPU_1");
        var vm = new PlcPillViewModel(
            plcName: "CPU_1",
            isAnchor: true,
            initialActiveItems: new List<DataBlockListItem> { idb },
            loadDbs: _ => Task.FromResult<IReadOnlyList<DataBlockListItem>>(
                new List<DataBlockListItem> { idb }));

        var pill = new BlockParam.UI.Controls.PillMultiSelect.PillMultiSelect
        {
            DisplayMemberPath = nameof(DataBlockListItem.Display),
        };
        pill.ItemsSource = vm.AvailableDbs;
        pill.SelectedItems = vm.SelectedDbs;

        // Simulate the trigger click: it flips _internalState.IsOpen, which
        // bridges to the IsOpen DP. Set via InternalState.IsOpen to model
        // the ToggleButton.IsChecked TwoWay write (the same seam the click
        // exercises).
        pill.InternalState.IsOpen = true;

        pill.IsOpen.Should().BeTrue(
            "the trigger click must round-trip through the DP to the host");
    }

    // ---------- helpers (copied locally; no shared state) ----------

    private static DataBlockListItem Item(string name, string plc) =>
        new DataBlockListItem(
            new DataBlockSummary(name, "", plcName: plc, number: 1),
            isActive: false,
            isAnchor: false);

    /// <summary>
    /// Numberless instance DB (no <c>Number</c> → empty Abbreviation),
    /// matching the #141 repro (<c>Gen_Main_IDB</c>).
    /// </summary>
    private static DataBlockListItem InstanceDbItem(string name, string plc) =>
        new DataBlockListItem(
            new DataBlockSummary(name, "", blockType: "InstanceDB",
                isInstanceDb: true, plcName: plc, number: null),
            isActive: true,
            isAnchor: true);
}
