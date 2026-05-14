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
/// Coverage for #59 — the in-dialog DB-switcher dropdown:
/// <list type="bullet">
///   <item>lazy-load + cache enumeration on first open;</item>
///   <item>refresh-button re-enumeration;</item>
///   <item>3-way prompt on switch with staged edits (Apply / Keep / Cancel);</item>
///   <item>per-DB stash that survives switches and restores on return;</item>
///   <item>orphan-edit drop when the DB structure has changed since stashing.</item>
/// </list>
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
        YesNoCancelResult promptResult = YesNoCancelResult.No)
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
        usageTracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var mbx = new FakeMessageBox(promptResult);

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
    public void Header_ShowsPlcPrefix_WhenHostSuppliesPlcName()
    {
        // Host wires a PLC name → CurrentPlcName / HasCurrentPlcName flip on
        // and the window title prefixes the DB with "{PLC} / ".
        var primary = TestFixtures.LoadXml("flat-db.xml");
        var parser = new SimaticMLParser();
        var primaryInfo = parser.Parse(primary);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var usageTracker = Substitute.For<IUsageTracker>();
        usageTracker.GetStatus().Returns(new UsageStatus(0, 3));

        var vm = new BulkChangeViewModel(
            primaryInfo, primary,
            new HierarchyAnalyzer(), bulkService, usageTracker, configLoader,
            currentPlcName: "PLC_Line1");

        vm.HasCurrentPlcName.Should().BeTrue();
        vm.CurrentPlcName.Should().Be("PLC_Line1");
        vm.Title.Should().Contain("PLC_Line1 / " + primaryInfo.Name);
    }

    [Fact]
    public void Header_OmitsPlcPrefix_WhenHostSuppliesNothing()
    {
        // Single-PLC / DevLauncher: no PlcName → no prefix, no badge.
        var h = CreateVm();
        h.Vm.HasCurrentPlcName.Should().BeFalse();
        h.Vm.CurrentPlcName.Should().Be("");
        h.Vm.Title.Should().NotContain(" / ");
    }

    [Fact]
    public void OpenDropdown_LazyEnumerates_ThenCachesForSubsequentOpens()
    {
        var h = CreateVm();

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
    public void StashKey_IncludesPlcName_SoSameDbNameAcrossPlcsDoesNotCollide()
    {
        // Regression: a multi-PLC project can have two DBs that share the
        // same name + folder but live on different PLCs. The stash key must
        // separate them so closing one doesn't overwrite the other's stash.
        // Driven via the live chip-close + Keep gesture.
        var aXml = TestFixtures.LoadXml("flat-db.xml");
        var bXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var aInfo = parser.Parse(aXml);
        var bInfo = parser.Parse(bXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var usageTracker = Substitute.For<IUsageTracker>();
        usageTracker.GetStatus().Returns(new UsageStatus(0, 3));
        usageTracker.RecordUsage(Arg.Any<int>()).Returns(true);
        var mbx = new FakeMessageBox(YesNoCancelResult.No);   // Keep on chip-close

        // Anchor on PLC_Line1 + peer on PLC_Line2 from the start so the
        // chip-close gesture has another DB to fall back on.
        var peer = new ActiveDb(bInfo, bXml, onApply: null, plcName: "PLC_Line2");

        var vm = new BulkChangeViewModel(
            aInfo, aXml,
            new HierarchyAnalyzer(), bulkService, usageTracker, configLoader,
            messageBox: mbx,
            currentPlcName: "PLC_Line1",
            additionalActiveDbs: new[] { peer });

        // Stage on the anchor (PLC_Line1). Use EditableStartValue (production
        // path) so the PendingEditStore is populated — CountPendingEditsForDb
        // reads from the store to decide whether to prompt before remove.
        var anchorRoot = vm.Tree.RootMembers.First(r => r.Name == aInfo.Name);
        anchorRoot.AllDescendants().First(n => n.IsLeaf).EditableStartValue = "111";

        // Remove anchor → Keep (stash).
        var anchorDbA1 = vm.AllActiveDbs.First(d => d.Info.Name == aInfo.Name);
        vm.RequestRemoveActiveDb(anchorDbA1);

        vm.StashedDbs.Should().HaveCount(1);
        vm.StashedDbs[0].Summary.PlcName.Should().Be("PLC_Line1",
            "stash must record which PLC the edits came from — without the PlcName " +
            "in the StashKey, two DBs with identical (name, folder) on different PLCs " +
            "would collide.");
    }

    [Fact]
    public void StashKeyedByNameAndFolder_TwoDbsSameNameDifferentFolders_StashIndependently()
    {
        // Stash key includes folder path so two DBs with the same name in
        // different folders don't alias. Driven via chip-close + Keep on a
        // 2-DB session where the anchor lives at "" and the peer at
        // "Recipe".
        var aXml = TestFixtures.LoadXml("flat-db.xml");
        var bXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var aInfo = parser.Parse(aXml);
        var bInfo = parser.Parse(bXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var usageTracker = Substitute.For<IUsageTracker>();
        usageTracker.GetStatus().Returns(new UsageStatus(0, 3));
        usageTracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var mbx = new FakeMessageBox(YesNoCancelResult.No);   // Keep on chip-close
        var peer = new ActiveDb(bInfo, bXml, onApply: null, plcName: "");

        var vm = new BulkChangeViewModel(
            aInfo, aXml,
            new HierarchyAnalyzer(), bulkService, usageTracker, configLoader,
            messageBox: mbx,
            additionalActiveDbs: new[] { peer });

        // Stage on the anchor. Use EditableStartValue (production path) so the
        // PendingEditStore is populated — CountPendingEditsForDb reads from the
        // store to decide whether to prompt before remove.
        var anchorRoot = vm.Tree.RootMembers.First(r => r.Name == aInfo.Name);
        anchorRoot.AllDescendants().First(n => n.IsLeaf).EditableStartValue = "777";

        // Remove anchor → Keep (stash).
        var anchorDbA2 = vm.AllActiveDbs.First(d => d.Info.Name == aInfo.Name);
        vm.RequestRemoveActiveDb(anchorDbA2);

        vm.StashedDbs.Should().HaveCount(1);
        vm.StashedDbs[0].DbName.Should().Be(aInfo.Name);
        vm.StashedDbs[0].FolderPath.Should().Be("",
            "FolderPath is part of the stash key — without it, same-named DBs in " +
            "different folders would collide.");
    }

    [Fact]
    public void Stash_SurvivesApplyOnRemainingActiveDb()
    {
        // Regression for the mid-cycle Apply path (#59 review): user stages
        // on A, chip-×'s A with Keep (A enters the stash), then stages on
        // the new anchor B and Applies. A's stash must survive B's Apply —
        // CommitChanges only iterates active DBs; the stash dictionary is
        // independent and must stay untouched.
        var aXml = TestFixtures.LoadXml("flat-db.xml");
        var bXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var aInfo = parser.Parse(aXml);
        var bInfo = parser.Parse(bXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var usageTracker = Substitute.For<IUsageTracker>();
        usageTracker.GetStatus().Returns(new UsageStatus(0, 3));
        usageTracker.RecordUsage(Arg.Any<int>()).Returns(true);

        int applyCount = 0;
        var mbx = new FakeMessageBox(YesNoCancelResult.No);   // Keep on chip-close
        var peer = new ActiveDb(bInfo, bXml,
            onApply: _ => applyCount++,
            plcName: "");

        var vm = new BulkChangeViewModel(
            aInfo, aXml,
            new HierarchyAnalyzer(), bulkService, usageTracker, configLoader,
            onApply: _ => applyCount++,
            messageBox: mbx,
            additionalActiveDbs: new[] { peer });

        // 1. Stage on A (anchor). Use EditableStartValue (production path) so
        // the PendingEditStore is populated — CountPendingEditsForDb reads from
        // the store to decide whether to prompt before remove.
        var anchorRoot = vm.Tree.RootMembers.First(r => r.Name == aInfo.Name);
        anchorRoot.AllDescendants().First(n => n.IsLeaf).EditableStartValue = "111";

        // 2. Remove A with Keep → A stashed, B becomes sole active anchor.
        var anchorDbA3 = vm.AllActiveDbs.First(d => d.Info.Name == aInfo.Name);
        vm.RequestRemoveActiveDb(anchorDbA3);
        vm.AllActiveDbs.Should().ContainSingle()
            .Which.Info.Name.Should().Be(bInfo.Name);
        vm.StashedDbs.Should().HaveCount(1);
        vm.StashedDbs[0].DbName.Should().Be(aInfo.Name);

        // 3. Stage on B (now anchor) and Apply.
        vm.Tree.RootMembers.First(m => m.IsLeaf).EditableStartValue = "222";
        vm.HasPendingChanges = true;   // Apply path normally toggles this; set explicitly so CommitChanges fires.
        vm.CommitChanges().Should().BeTrue();

        applyCount.Should().Be(1, "only B is active — exactly one OnApply fires");

        // 4. A's stash must still be there.
        vm.StashedDbs.Should().HaveCount(1);
        vm.StashedDbs[0].DbName.Should().Be(aInfo.Name);
        vm.StashedDbs[0].Edits.Should().HaveCount(1);
        vm.StashedDbs[0].Edits[0].PendingValue.Should().Be("111",
            "Apply on B doesn't touch the stash dictionary");
    }

    /// <summary>
    /// Convenience enum shared across DB-switcher tests. Yes/No/Cancel maps
    /// onto the three named outcomes for each typed prompt method.
    /// </summary>
    private enum YesNoCancelResult { Yes, No, Cancel }

    private class FakeMessageBox : IMessageBoxService
    {
        private readonly YesNoCancelResult _result;
        public int AskYesNoCallCount { get; private set; }
        /// <summary>Counts any 3-way prompt call (ApplyStashCancel, AddOrReplace, CloseWithStash).</summary>
        public int AskYesNoCancelCallCount { get; private set; }
        public FakeMessageBox(YesNoCancelResult result) { _result = result; }
        public bool AskYesNo(string message, string title) { AskYesNoCallCount++; return true; }
        public void ShowError(string message, string title) { }
        public void ShowInfo(string message, string title) { }
        public ApplyStashCancelResult AskApplyStashCancel(string message, string title)
        {
            AskYesNoCancelCallCount++;
            return _result switch
            {
                YesNoCancelResult.Yes => ApplyStashCancelResult.ApplyAndSwitch,
                YesNoCancelResult.No  => ApplyStashCancelResult.StashAndSwitch,
                _                     => ApplyStashCancelResult.Cancel,
            };
        }
        public AddOrReplaceResult AskAddOrReplace(string message, string title)
        {
            AskYesNoCancelCallCount++;
            return _result switch
            {
                YesNoCancelResult.Yes => AddOrReplaceResult.Add,
                YesNoCancelResult.No  => AddOrReplaceResult.Replace,
                _                     => AddOrReplaceResult.Cancel,
            };
        }
        public CloseWithStashResult AskCloseWithStash(string message, string title)
        {
            AskYesNoCancelCallCount++;
            return _result switch
            {
                YesNoCancelResult.Yes => CloseWithStashResult.ApplyActive,
                YesNoCancelResult.No  => CloseWithStashResult.DiscardAll,
                _                     => CloseWithStashResult.Cancel,
            };
        }
    }
}
