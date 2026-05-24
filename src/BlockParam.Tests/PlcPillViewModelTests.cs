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
    // #141 — numberless instance DB regression guards.
    //
    // #141 was filed against v1.0.14 (two symptoms for a numberless instance
    // DB like Gen_Main_IDB: A = closed pill renders blank; B = clicking the
    // pill doesn't open the popup). Both were ALREADY FIXED on main before
    // PR #153 — symptom A by 7882cc1 ("always-on PLC label" + overflow
    // formatter Display-fallback for empty abbreviations + pre-populating
    // AvailableDbs with initialActiveItems), and symptom B works through the
    // standard ToggleButton.IsChecked → _internalState.IsOpen → IsOpen DP →
    // PlcPillViewModel.IsOpen two-way chain. These tests are regression
    // guards that pin BOTH working behaviours so neither can silently
    // regress; they pass on origin/main and on this branch (see PR #153
    // discussion for the fail-on-main investigation that found no remaining
    // defect — the prior #141 deferred-reconcile was non-load-bearing dead
    // code and was removed).
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

        // AvailableDbs is pre-populated with initialActiveItems (7882cc1) and
        // SelectedDbs is seeded with the SAME reference, so MultiSelectSelectionSync's
        // per-row seed can mark the row IsCheckedTrue and the closed trigger
        // renders the summary — no OnIsOpenFlippedToTrue / popup-open needed.
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

        // The IsChecked→IsOpen chain ends at PlcPillViewModel.IsOpen = true;
        // the end-to-end click is covered by the [UIFact] below — this asserts
        // the VM-side contract (open drives the lazy load exactly once).
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
        // Symptom A regression guard, end-to-end through the real
        // PillMultiSelect control with the exact BulkChangeDialog binding
        // shape:
        //   ItemsSource  = AvailableDbs
        //   SelectedItems = SelectedDbs (Mode=OneWay)
        //   IsOpen        = TwoWay
        // The CLOSED trigger must show HasSelection and the DB name without
        // any popup-open — driven purely by MultiSelectSelectionSync's per-row seed
        // + RaiseAggregatesChanged INPC push (no deferred reconcile exists).
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
    public void RealTriggerClick_NumberlessInstanceDb_TogglesViewModelIsOpen_EndToEnd()
    {
        // Symptom B regression guard: a REAL ToggleButton "click" on the
        // closed pill must toggle the popup open end-to-end to the host VM
        // for the numberless instance DB. Exercises the full production
        // chain: ToggleButtonAutomationPeer.Toggle() → ToggleButton.OnToggle
        // → IsChecked TwoWay binding → _internalState.IsOpen → OnInternal-
        // StatePropertyChanged → IsOpen DP → host TwoWay binding →
        // PlcPillViewModel.IsOpen → OnIsOpenFlippedToTrue (lazy load once).
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

        var pill = new BlockParam.UI.Controls.PillMultiSelect.PillMultiSelect
        {
            DisplayMemberPath = nameof(DataBlockListItem.Display),
            AbbreviationMemberPath = nameof(DataBlockListItem.Abbreviation),
            OverflowOptions =
                BlockParam.UI.Controls.PillMultiSelect.PillOverflowOptions.DataBlockDefault(),
        };

        // Mirror BulkChangeDialog.xaml exactly: the DataTemplate DataContext
        // is the PlcPillViewModel; IsOpen is a TwoWay binding to vm.IsOpen,
        // SelectedItems is OneWay, ItemsSource bound to AvailableDbs.
        pill.DataContext = vm;
        pill.SetBinding(
            BlockParam.UI.Controls.PillMultiSelect.PillMultiSelect.ItemsSourceProperty,
            new System.Windows.Data.Binding(nameof(PlcPillViewModel.AvailableDbs)));
        pill.SetBinding(
            BlockParam.UI.Controls.PillMultiSelect.PillMultiSelect.SelectedItemsProperty,
            new System.Windows.Data.Binding(nameof(PlcPillViewModel.SelectedDbs))
            { Mode = System.Windows.Data.BindingMode.OneWay });
        pill.SetBinding(
            BlockParam.UI.Controls.PillMultiSelect.PillMultiSelect.IsOpenProperty,
            new System.Windows.Data.Binding(nameof(PlcPillViewModel.IsOpen))
            { Mode = System.Windows.Data.BindingMode.TwoWay });

        var win = new System.Windows.Window { Width = 400, Height = 300, Content = pill };
        win.Show();
        try
        {
            pill.Measure(new System.Windows.Size(400, 300));
            pill.Arrange(new System.Windows.Rect(0, 0, 400, 300));
            pill.UpdateLayout();

            var trigger = FindVisualChild<System.Windows.Controls.Primitives.ToggleButton>(pill);
            trigger.Should().NotBeNull("the pill trigger ToggleButton is realised in the visual tree");

            vm.IsOpen.Should().BeFalse("popup starts closed");

            // REAL click via the ToggleButton's automation peer (the same
            // path WPF input drives): IToggleProvider.Toggle() → OnToggle().
            var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(trigger);
            var toggle = (System.Windows.Automation.Provider.IToggleProvider)
                peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle);
            toggle.Toggle();

            win.Dispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.Background);

            vm.IsOpen.Should().BeTrue(
                "a real trigger click toggles the popup open end-to-end to the host VM");
            loadCalls.Should().Be(1,
                "first open drives the lazy PLC-list fetch exactly once");
        }
        finally
        {
            win.Close();
        }
    }

    private static T? FindVisualChild<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        if (root is T hit) return hit;
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
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
