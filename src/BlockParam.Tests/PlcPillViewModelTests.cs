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

    // ---------- helpers (copied locally; no shared state) ----------

    private static DataBlockListItem Item(string name, string plc) =>
        new DataBlockListItem(
            new DataBlockSummary(name, "", plcName: plc, number: 1),
            isActive: false,
            isAnchor: false);
}
