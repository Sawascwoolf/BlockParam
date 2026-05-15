using System;
using System.Collections.Generic;
using BlockParam.Models;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Command-level coverage for <see cref="ActiveSetViewModel"/>'s DB-switcher
/// dropdown commands that the slice-8a / 8b suites don't exercise:
///
/// <list type="bullet">
///   <item><c>CloseDataBlocksDropdownCommand</c> — the open path is heavily
///       covered elsewhere; this asserts Close toggles
///       <see cref="ActiveSetViewModel.IsDataBlocksDropdownOpen"/> back to
///       false after the dropdown is opened.</item>
///   <item><c>RefreshDataBlocksCommand</c> <c>CanExecute</c> guard — it is
///       gated on a wired enumerate callback, so the predicate must report
///       false with no callback and true once one is supplied.</item>
/// </list>
///
/// The builder-style <see cref="Harness"/> mirrors
/// <c>ActiveSetViewModelMutatorTests</c>'s harness (copied locally — no
/// shared state across test classes).
/// </summary>
public class ActiveSetViewModelCommandTests
{
    [Fact]
    public void CloseDataBlocksDropdownCommand_AfterOpen_TogglesOpenStateBackToFalse()
    {
        var available = new[]
        {
            new DataBlockSummary("Alpha", ""),
            new DataBlockSummary("Beta", ""),
        };
        var harness = new Harness(Snap(Db("Anchor")))
            .WithEnumerateDataBlocks(() => available)
            .WithSwitchToDataBlock(_ => "<Block/>");

        // Reuse the well-covered open path to put the dropdown into the
        // open state, then assert Close flips it back.
        harness.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        harness.Vm.IsDataBlocksDropdownOpen.Should().BeTrue(
            "the open command is the documented precondition for Close");

        harness.Vm.CloseDataBlocksDropdownCommand.Execute(null);

        harness.Vm.IsDataBlocksDropdownOpen.Should().BeFalse(
            "Close must toggle the bound open-state back to closed");
    }

    [Fact]
    public void CloseDataBlocksDropdownCommand_RaisesPropertyChangedOnOpenState()
    {
        var harness = new Harness(Snap(Db("Anchor")));
        // Force the state open directly so the test is independent of the
        // open command's own side effects.
        harness.Vm.IsDataBlocksDropdownOpen = true;

        var raised = new List<string?>();
        harness.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        harness.Vm.CloseDataBlocksDropdownCommand.Execute(null);

        harness.Vm.IsDataBlocksDropdownOpen.Should().BeFalse(
            "Close sets the open-state to false");
        raised.Should().Contain(nameof(ActiveSetViewModel.IsDataBlocksDropdownOpen),
            "the bound open-state must notify so the popup closes in the UI");
    }

    [Fact]
    public void RefreshDataBlocksCommand_CanExecute_FalseWithoutEnumerateCallback()
    {
        // No enumerate callback wired → the guard's first clause is false.
        var harness = new Harness(Snap(Db("Anchor")));

        harness.Vm.RefreshDataBlocksCommand.CanExecute(null).Should().BeFalse(
            "the refresh guard requires a wired enumerate-DataBlocks callback");
    }

    [Fact]
    public void RefreshDataBlocksCommand_CanExecute_TrueWhenEnumerateCallbackWired()
    {
        // Same VM contract as the false case but with the callback present:
        // the guard flips to true, demonstrating the false→true transition
        // is driven solely by the enumerate-callback condition.
        var harness = new Harness(Snap(Db("Anchor")))
            .WithEnumerateDataBlocks(() => new[] { new DataBlockSummary("X", "") })
            .WithSwitchToDataBlock(_ => "<Block/>");

        harness.Vm.RefreshDataBlocksCommand.CanExecute(null).Should().BeTrue(
            "wiring the enumerate callback satisfies the refresh guard");
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
    /// Mirrors the harness in <c>ActiveSetViewModelMutatorTests</c> — copied
    /// here intentionally to keep the new class free of shared fixtures.
    /// </summary>
    private class Harness
    {
        private readonly ActiveSetState _initial;
        private Func<IReadOnlyList<DataBlockSummary>>? _enumerate;
        private Func<DataBlockSummary, string>? _switch;
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
            dispatcher: null);

        public Harness WithEnumerateDataBlocks(Func<IReadOnlyList<DataBlockSummary>> enumerate)
        { _enumerate = enumerate; return this; }

        public Harness WithSwitchToDataBlock(Func<DataBlockSummary, string> sw)
        { _switch = sw; return this; }
    }
}
