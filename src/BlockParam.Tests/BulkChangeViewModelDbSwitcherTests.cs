using BlockParam.Config;
using BlockParam.Licensing;
using BlockParam.Models;
using BlockParam.Services;
using BlockParam.SimaticML;
using BlockParam.UI;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Coverage for #59 — the DB-switcher dropdown on the dialog header. Verifies
/// the lazy-load + cache contract, the refresh-button re-enumeration, and the
/// staged-changes confirm prompt before discarding.
/// </summary>
public class BulkChangeViewModelDbSwitcherTests
{
    private record SwitcherHarness(
        BulkChangeViewModel Vm,
        FakeMessageBox Mbx,
        Func<int> EnumerateCallCount,
        Func<int> SwitchCallCount,
        Func<string?> LastSwitchedTo);

    private static SwitcherHarness CreateVm(
        IReadOnlyList<DataBlockSummary>? initialList = null,
        bool yesOnConfirm = true)
    {
        // Two real DBs from the test fixtures so a switch produces a parseable tree.
        var primary = TestFixtures.LoadXml("flat-db.xml");
        var secondary = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var primaryInfo = parser.Parse(primary);
        var secondaryInfo = parser.Parse(secondary);

        var enumerated = initialList ?? new[]
        {
            new DataBlockSummary(primaryInfo.Name, ""),
            new DataBlockSummary(secondaryInfo.Name, "Recipe"),
        };

        int enumerateCount = 0;
        int switchCount = 0;
        string? lastSwitch = null;

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var usageTracker = Substitute.For<IUsageTracker>();
        usageTracker.GetStatus().Returns(new UsageStatus(0, 3));
        usageTracker.GetInlineStatus().Returns(new UsageStatus(0, 10));
        usageTracker.RecordInlineEdit().Returns(true);

        var mbx = new FakeMessageBox(yesOnConfirm);

        var vm = new BulkChangeViewModel(
            primaryInfo, primary,
            new HierarchyAnalyzer(), bulkService, usageTracker, configLoader,
            messageBox: mbx,
            enumerateDataBlocks: () =>
            {
                enumerateCount++;
                return enumerated;
            },
            switchToDataBlock: summary =>
            {
                switchCount++;
                lastSwitch = summary.Name;
                return string.Equals(summary.Name, primaryInfo.Name, StringComparison.Ordinal)
                    ? primary
                    : secondary;
            });

        return new SwitcherHarness(vm, mbx, () => enumerateCount, () => switchCount, () => lastSwitch);
    }

    [Fact]
    public void HasDataBlockSwitcher_TrueWhenCallbacksWired()
    {
        var h = CreateVm();
        h.Vm.HasDataBlockSwitcher.Should().BeTrue();
    }

    [Fact]
    public void OpenDropdown_LazyEnumerates_ThenCachesForSubsequentOpens()
    {
        var h = CreateVm();

        // First open: enumerates once and populates the filtered list.
        h.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        h.EnumerateCallCount().Should().Be(1);
        h.Vm.IsDataBlocksDropdownOpen.Should().BeTrue();
        h.Vm.FilteredDataBlocks.Should().HaveCount(2);

        // Close and reopen: enumeration MUST NOT run again — cache hit.
        h.Vm.IsDataBlocksDropdownOpen = false;
        h.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        h.EnumerateCallCount().Should().Be(1);
        h.Vm.IsDataBlocksDropdownOpen.Should().BeTrue();
    }

    [Fact]
    public void RefreshCommand_ReEnumerates_EvenAfterCacheHit()
    {
        var h = CreateVm();

        h.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        h.EnumerateCallCount().Should().Be(1);

        // Refresh button: bypass the cache and call the source again.
        h.Vm.RefreshDataBlocksCommand.Execute(null);
        h.EnumerateCallCount().Should().Be(2);
    }

    [Fact]
    public void Filter_NarrowsToMatchingDbsOnly()
    {
        var h = CreateVm(initialList: new[]
        {
            new DataBlockSummary("DB_Unit_A", ""),
            new DataBlockSummary("DB_Unit_B", "Recipe"),
            new DataBlockSummary("DB_Sensors", ""),
        });

        h.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        h.Vm.DataBlockSearchText = "Sensors";

        h.Vm.FilteredDataBlocks.Should().HaveCount(1);
        h.Vm.FilteredDataBlocks[0].Name.Should().Be("DB_Sensors");
    }

    [Fact]
    public void SwitchToDataBlock_NoStagedChanges_SwitchesWithoutPrompt()
    {
        var h = CreateVm();
        h.Vm.OpenDataBlocksDropdownCommand.Execute(null);

        var target = h.Vm.FilteredDataBlocks.First(b => b.Name != h.Vm.CurrentDataBlockName);

        var ok = h.Vm.SwitchToDataBlock(target);

        ok.Should().BeTrue();
        h.SwitchCallCount().Should().Be(1);
        h.LastSwitchedTo().Should().Be(target.Name);
        h.Vm.CurrentDataBlockName.Should().Be(target.Name);
        h.Vm.IsDataBlocksDropdownOpen.Should().BeFalse();
        h.Mbx.AskYesNoCallCount.Should().Be(0,
            "no staged changes means no confirm prompt");
    }

    [Fact]
    public void SwitchToDataBlock_SameDb_NoOp()
    {
        var h = CreateVm();
        h.Vm.OpenDataBlocksDropdownCommand.Execute(null);

        var current = h.Vm.FilteredDataBlocks.First(b => b.Name == h.Vm.CurrentDataBlockName);
        var ok = h.Vm.SwitchToDataBlock(current);

        ok.Should().BeFalse("clicking the already-active DB just closes the dropdown");
        h.SwitchCallCount().Should().Be(0);
        h.Vm.IsDataBlocksDropdownOpen.Should().BeFalse();
    }

    [Fact]
    public void SwitchToDataBlock_StagedChanges_PromptsAndDiscardsOnYes()
    {
        var h = CreateVm(yesOnConfirm: true);

        // Stage an inline edit so PendingInlineEditCount > 0.
        var leaf = h.Vm.RootMembers.First(m => m.IsLeaf);
        leaf.EditableStartValue = "999";
        h.Vm.PendingInlineEditCount.Should().BeGreaterThan(0);

        h.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        var target = h.Vm.FilteredDataBlocks.First(b => b.Name != h.Vm.CurrentDataBlockName);

        var ok = h.Vm.SwitchToDataBlock(target);

        ok.Should().BeTrue();
        h.Mbx.AskYesNoCallCount.Should().Be(1, "the prompt is required when discarding staged edits");
        h.SwitchCallCount().Should().Be(1);
        h.Vm.PendingInlineEditCount.Should().Be(0, "Yes-on-confirm discards the staged edits");
        h.Vm.CurrentDataBlockName.Should().Be(target.Name);
    }

    [Fact]
    public void SwitchToDataBlock_StagedChanges_AbortsOnNo()
    {
        var h = CreateVm(yesOnConfirm: false);

        var leaf = h.Vm.RootMembers.First(m => m.IsLeaf);
        leaf.EditableStartValue = "999";
        var pendingBefore = h.Vm.PendingInlineEditCount;
        pendingBefore.Should().BeGreaterThan(0);
        var originalDb = h.Vm.CurrentDataBlockName;

        h.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        var target = h.Vm.FilteredDataBlocks.First(b => b.Name != originalDb);

        var ok = h.Vm.SwitchToDataBlock(target);

        ok.Should().BeFalse();
        h.SwitchCallCount().Should().Be(0, "No-on-confirm must not invoke the host switch callback");
        h.Vm.PendingInlineEditCount.Should().Be(pendingBefore, "staged edits must survive a cancelled switch");
        h.Vm.CurrentDataBlockName.Should().Be(originalDb);
    }

    private class FakeMessageBox : IMessageBoxService
    {
        private readonly bool _yes;
        public int AskYesNoCallCount { get; private set; }
        public FakeMessageBox(bool yes) { _yes = yes; }
        public bool AskYesNo(string message, string title) { AskYesNoCallCount++; return _yes; }
        public void ShowError(string message, string title) { }
    }
}
