using System;
using System.Collections.Generic;
using BlockParam.Models;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Command-level coverage for <see cref="DiffPreviewViewModel"/>'s
/// <c>ApplyCommand</c> / <c>CancelCommand</c>. <c>DiffPreviewServiceTests</c>
/// covers the diff computation service — NOT these VM commands.
///
/// <para>
/// The dialog signals close via the <see cref="DiffPreviewViewModel.RequestClose"/>
/// event and records the user's intent in
/// <see cref="DiffPreviewViewModel.Confirmed"/> (true = Apply, false =
/// Cancel). Tests subscribe to <c>RequestClose</c> to observe the close
/// signal without a real <c>Window</c>.
/// </para>
///
/// Contracts under test:
/// <list type="bullet">
///   <item><c>ApplyCommand</c> sets <c>Confirmed = true</c> and raises
///       <c>RequestClose</c>; its <c>CanExecute</c> is gated on
///       <c>ChangedCount &gt; 0</c>.</item>
///   <item><c>CancelCommand</c> sets <c>Confirmed = false</c> and raises
///       <c>RequestClose</c> unconditionally.</item>
/// </list>
/// </summary>
public class DiffPreviewViewModelTests
{
    [Fact]
    public void ApplyCommand_SetsConfirmedTrueAndSignalsClose()
    {
        var vm = MakeVm(changed: 2, total: 3);
        int closeSignals = 0;
        vm.RequestClose += () => closeSignals++;

        vm.ApplyCommand.Execute(null);

        vm.Confirmed.Should().BeTrue(
            "Apply records the user's intent to commit the staged changes");
        closeSignals.Should().Be(1,
            "Apply must signal the host to close the preview dialog");
    }

    [Fact]
    public void ApplyCommand_CanExecute_FalseWhenNoChanges_TrueWhenChanges()
    {
        var noChanges = MakeVm(changed: 0, total: 3);
        noChanges.ApplyCommand.CanExecute(null).Should().BeFalse(
            "applying with zero changed values is a no-op, so the guard blocks it");

        var withChanges = MakeVm(changed: 1, total: 3);
        withChanges.ApplyCommand.CanExecute(null).Should().BeTrue(
            "at least one changed value makes Apply meaningful");
    }

    [Fact]
    public void CancelCommand_SetsConfirmedFalseAndSignalsClose()
    {
        var vm = MakeVm(changed: 2, total: 3);
        int closeSignals = 0;
        vm.RequestClose += () => closeSignals++;

        vm.CancelCommand.Execute(null);

        vm.Confirmed.Should().BeFalse(
            "Cancel records that the user declined the staged changes");
        closeSignals.Should().Be(1,
            "Cancel must signal the host to close the preview dialog");
    }

    [Fact]
    public void CancelCommand_CanExecute_AlwaysTrueEvenWithZeroChanges()
    {
        // Unlike Apply, Cancel has no guard — the user must always be able
        // to back out of the preview regardless of the change count.
        var vm = MakeVm(changed: 0, total: 0);

        vm.CancelCommand.CanExecute(null).Should().BeTrue(
            "Cancel is unconditionally available so the dialog is escapable");
    }

    [Fact]
    public void ApplyThenCancel_LastInvocationWins_ConfirmedReflectsCancel()
    {
        // Defensive: if both commands fire (e.g. double interaction before
        // the dialog tears down), Confirmed must reflect the last gesture.
        var vm = MakeVm(changed: 2, total: 3);
        int closeSignals = 0;
        vm.RequestClose += () => closeSignals++;

        vm.ApplyCommand.Execute(null);
        vm.CancelCommand.Execute(null);

        vm.Confirmed.Should().BeFalse(
            "Cancel ran last so it overrides the earlier Apply confirmation");
        closeSignals.Should().Be(2,
            "each command independently signals close");
    }

    // ---------- helpers (copied locally; no shared state) ----------

    private static DiffPreviewViewModel MakeVm(int changed, int total)
    {
        var entries = new List<DiffEntry>();
        // First `changed` entries differ (old != new); the rest are equal
        // so DiffEntry.IsChanged is false → ChangedCount == changed.
        for (int i = 0; i < changed; i++)
            entries.Add(new DiffEntry($"M{i}", "Int", oldValue: "0", newValue: "1"));
        for (int i = changed; i < total; i++)
            entries.Add(new DiffEntry($"M{i}", "Int", oldValue: "7", newValue: "7"));

        return new DiffPreviewViewModel(
            dbName: "DB_Test",
            memberName: "Speed",
            newValue: "1",
            entries: entries);
    }
}
