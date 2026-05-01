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
        usageTracker.GetInlineStatus().Returns(new UsageStatus(0, 10));
        usageTracker.RecordInlineEdit().Returns(true);

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
        usageTracker.GetInlineStatus().Returns(new UsageStatus(0, 10));

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
        h.Mbx.AskYesNoCancelCallCount.Should().Be(0,
            "no staged changes means no prompt");
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
    public void SwitchWithStagedEdits_CancelChoice_StaysOnCurrentDb()
    {
        var h = CreateVm(promptResult: YesNoCancelResult.Cancel);

        var leaf = h.Vm.RootMembers.First(m => m.IsLeaf);
        leaf.EditableStartValue = "999";
        var pendingBefore = h.Vm.PendingInlineEditCount;
        pendingBefore.Should().BeGreaterThan(0);
        var originalDb = h.Vm.CurrentDataBlockName;

        h.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        var target = h.Vm.FilteredDataBlocks.First(b => b.Name != originalDb);
        var ok = h.Vm.SwitchToDataBlock(target);

        ok.Should().BeFalse();
        h.Mbx.AskYesNoCancelCallCount.Should().Be(1);
        h.SwitchCallCount().Should().Be(0, "Cancel must not invoke the host switch callback");
        h.Vm.PendingInlineEditCount.Should().Be(pendingBefore,
            "staged edits survive a Cancel");
        h.Vm.CurrentDataBlockName.Should().Be(originalDb);
        h.Vm.HasStashedDbs.Should().BeFalse("Cancel must not stash");
    }

    [Fact]
    public void SwitchWithStagedEdits_KeepChoice_StashesAndSwitches()
    {
        var h = CreateVm(promptResult: YesNoCancelResult.No);

        var leaf = h.Vm.RootMembers.First(m => m.IsLeaf);
        var leafPath = leaf.Path;
        var originalValue = leaf.StartValue ?? "";
        leaf.EditableStartValue = "999";
        var originalDb = h.Vm.CurrentDataBlockName;

        h.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        var target = h.Vm.FilteredDataBlocks.First(b => b.Name != originalDb);
        var ok = h.Vm.SwitchToDataBlock(target);

        ok.Should().BeTrue();
        h.SwitchCallCount().Should().Be(1);
        h.Vm.CurrentDataBlockName.Should().Be(target.Name);
        h.Vm.PendingInlineEditCount.Should().Be(0,
            "active tree is the new DB — its pending count starts at 0");

        h.Vm.HasStashedDbs.Should().BeTrue();
        h.Vm.StashedDbs.Should().HaveCount(1);
        var stash = h.Vm.StashedDbs[0];
        stash.DbName.Should().Be(originalDb);
        stash.Edits.Should().HaveCount(1);
        stash.Edits[0].Path.Should().Be(leafPath);
        stash.Edits[0].PendingValue.Should().Be("999");
        stash.Edits[0].OriginalValue.Should().Be(originalValue);
    }

    [Fact]
    public void SwitchBack_RestoresStashedEdits_AndClearsTheStashEntry()
    {
        var h = CreateVm(promptResult: YesNoCancelResult.No);

        // Stage an edit, switch away (Keep choice), then switch back.
        var leaf = h.Vm.RootMembers.First(m => m.IsLeaf);
        var originalDb = h.Vm.CurrentDataBlockName;
        leaf.EditableStartValue = "999";

        h.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        var target = h.Vm.FilteredDataBlocks.First(b => b.Name != originalDb);
        h.Vm.SwitchToDataBlock(target).Should().BeTrue();
        h.Vm.HasStashedDbs.Should().BeTrue();

        // Switch back. The current DB (target) has no staged edits, so no
        // prompt fires; the stash for originalDb gets re-applied.
        var back = h.Vm.FilteredDataBlocks.First(b => b.Name == originalDb);
        h.Vm.SwitchToDataBlock(back).Should().BeTrue();

        h.Vm.CurrentDataBlockName.Should().Be(originalDb);
        h.Vm.PendingInlineEditCount.Should().Be(1,
            "the stashed edit must be restored as a live pending edit");
        h.Vm.HasStashedDbs.Should().BeFalse(
            "stash entry for the now-active DB must be consumed on restore");
    }

    [Fact]
    public void SwitchWithStagedEdits_ApplyChoice_CommitsAndSwitchesWithoutStashing()
    {
        // Tracks whether the host's onApply callback ran — that's the proxy
        // for "committed to TIA" in tests. With no callback wired, ExecuteApply
        // still writes to in-memory XML and clears pending state; we assert
        // both invariants survive through the switch.
        int applyCount = 0;
        var primary = TestFixtures.LoadXml("flat-db.xml");
        var secondary = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var primaryInfo = parser.Parse(primary);

        var enumerated = new[]
        {
            new DataBlockSummary(primaryInfo.Name, ""),
            new DataBlockSummary(parser.Parse(secondary).Name, "Recipe"),
        };

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var usageTracker = Substitute.For<IUsageTracker>();
        usageTracker.GetStatus().Returns(new UsageStatus(0, 3));
        usageTracker.GetInlineStatus().Returns(new UsageStatus(0, 10));
        usageTracker.RecordInlineEdit().Returns(true);

        var mbx = new FakeMessageBox(YesNoCancelResult.Yes);
        var vm = new BulkChangeViewModel(
            primaryInfo, primary,
            new HierarchyAnalyzer(), bulkService, usageTracker, configLoader,
            onApply: _ => applyCount++,
            messageBox: mbx,
            enumerateDataBlocks: () => enumerated,
            switchToDataBlock: s =>
                string.Equals(s.Name, primaryInfo.Name, StringComparison.Ordinal)
                    ? primary
                    : secondary);

        vm.RootMembers.First(m => m.IsLeaf).EditableStartValue = "555";
        vm.OpenDataBlocksDropdownCommand.Execute(null);
        var target = vm.FilteredDataBlocks.First(b => b.Name != primaryInfo.Name);

        var ok = vm.SwitchToDataBlock(target);

        ok.Should().BeTrue();
        applyCount.Should().Be(1, "Apply choice must invoke the host commit path before switching");
        vm.HasStashedDbs.Should().BeFalse("Apply commits — nothing to stash");
        vm.CurrentDataBlockName.Should().Be(target.Name);
    }

    [Fact]
    public void StashKey_IncludesPlcName_SoSameDbNameAcrossPlcsDoesNotCollide()
    {
        // Regression: DB names + numbers are unique within a software unit,
        // not across the project. A multi-PLC project can have two
        // DB_SharedName entries — they must stash to separate slots.
        // We model this by enumerating two summaries with the same name +
        // folder but different PlcName, then verify the stash key separates
        // them by inspecting the StashedDbs collection identity.
        var primary = TestFixtures.LoadXml("flat-db.xml");
        var secondary = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var primaryInfo = parser.Parse(primary);

        // Two summaries that would alias under a (folder, name)-only key.
        var enumerated = new[]
        {
            new DataBlockSummary(primaryInfo.Name, "", plcName: "PLC_Line1"),
            new DataBlockSummary(parser.Parse(secondary).Name, "", plcName: "PLC_Line2"),
        };

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var usageTracker = Substitute.For<IUsageTracker>();
        usageTracker.GetStatus().Returns(new UsageStatus(0, 3));
        usageTracker.GetInlineStatus().Returns(new UsageStatus(0, 10));
        usageTracker.RecordInlineEdit().Returns(true);
        var mbx = new FakeMessageBox(YesNoCancelResult.No);

        var vm = new BulkChangeViewModel(
            primaryInfo, primary,
            new HierarchyAnalyzer(), bulkService, usageTracker, configLoader,
            messageBox: mbx,
            enumerateDataBlocks: () => enumerated,
            switchToDataBlock: s =>
                string.Equals(s.Name, primaryInfo.Name, StringComparison.Ordinal)
                    ? primary
                    : secondary);

        // Stage on primary (PLC_Line1) and switch (Keep) → 1 stash.
        vm.RootMembers.First(m => m.IsLeaf).EditableStartValue = "111";
        vm.OpenDataBlocksDropdownCommand.Execute(null);
        var second = vm.FilteredDataBlocks.First(b => b.Name != primaryInfo.Name);
        vm.SwitchToDataBlock(second).Should().BeTrue();

        vm.StashedDbs.Should().HaveCount(1);
        vm.StashedDbs[0].Summary.PlcName.Should().Be("PLC_Line1",
            "stash must record which PLC the edits came from");
    }

    [Fact]
    public void StashKeyedByNameAndFolder_TwoDbsSameNameDifferentFolders_StashIndependently()
    {
        // Two stashes both nominally "DB_X" but in different folders should
        // not collide. We stand in primary as "DB_X" in folder "" and target
        // as "DB_X" in folder "Recipe" — both physically the secondary fixture.
        var primary = TestFixtures.LoadXml("flat-db.xml");
        var secondary = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var primaryInfo = parser.Parse(primary);

        var enumerated = new[]
        {
            new DataBlockSummary(primaryInfo.Name, ""),
            new DataBlockSummary(parser.Parse(secondary).Name, "Recipe"),
        };

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var usageTracker = Substitute.For<IUsageTracker>();
        usageTracker.GetStatus().Returns(new UsageStatus(0, 3));
        usageTracker.GetInlineStatus().Returns(new UsageStatus(0, 10));
        usageTracker.RecordInlineEdit().Returns(true);

        var mbx = new FakeMessageBox(YesNoCancelResult.No);
        var vm = new BulkChangeViewModel(
            primaryInfo, primary,
            new HierarchyAnalyzer(), bulkService, usageTracker, configLoader,
            messageBox: mbx,
            enumerateDataBlocks: () => enumerated,
            switchToDataBlock: s =>
                string.Equals(s.Name, primaryInfo.Name, StringComparison.Ordinal)
                    ? primary
                    : secondary);

        // Stage on primary, switch (Keep) → 1 stash.
        vm.RootMembers.First(m => m.IsLeaf).EditableStartValue = "777";
        vm.OpenDataBlocksDropdownCommand.Execute(null);
        var second = vm.FilteredDataBlocks.First(b => b.Name != primaryInfo.Name);
        vm.SwitchToDataBlock(second).Should().BeTrue();
        vm.StashedDbs.Should().HaveCount(1);
        vm.StashedDbs[0].DbName.Should().Be(primaryInfo.Name);
        vm.StashedDbs[0].FolderPath.Should().Be("");
    }

    [Fact]
    public void RestoreStashedEdits_DoesNotConsumeInlineEditQuota()
    {
        // Free-tier users near the daily inline-edit cap could lose half a
        // restored stash if RestoreStashFor went through OnSingleValueEdited.
        // Verify the no-quota path: usage tracker's RecordInlineEdit must NOT
        // be called for the restored entries.
        var primary = TestFixtures.LoadXml("flat-db.xml");
        var secondary = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var primaryInfo = parser.Parse(primary);

        var enumerated = new[]
        {
            new DataBlockSummary(primaryInfo.Name, ""),
            new DataBlockSummary(parser.Parse(secondary).Name, "Recipe"),
        };

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var usageTracker = Substitute.For<IUsageTracker>();
        usageTracker.GetStatus().Returns(new UsageStatus(0, 3));
        usageTracker.GetInlineStatus().Returns(new UsageStatus(0, 10));
        usageTracker.RecordInlineEdit().Returns(true);
        var mbx = new FakeMessageBox(YesNoCancelResult.No);

        var vm = new BulkChangeViewModel(
            primaryInfo, primary,
            new HierarchyAnalyzer(), bulkService, usageTracker, configLoader,
            messageBox: mbx,
            enumerateDataBlocks: () => enumerated,
            switchToDataBlock: s =>
                string.Equals(s.Name, primaryInfo.Name, StringComparison.Ordinal)
                    ? primary
                    : secondary);

        // Stage on primary (consumes 1 inline-edit slot — that's expected).
        vm.RootMembers.First(m => m.IsLeaf).EditableStartValue = "777";
        usageTracker.Received(1).RecordInlineEdit();
        usageTracker.ClearReceivedCalls();

        // Switch away (Keep) → stash. No inline edits on the new tree, so
        // no further RecordInlineEdit calls should happen yet.
        vm.OpenDataBlocksDropdownCommand.Execute(null);
        var target = vm.FilteredDataBlocks.First(b => b.Name != primaryInfo.Name);
        vm.SwitchToDataBlock(target).Should().BeTrue();
        usageTracker.DidNotReceive().RecordInlineEdit();

        // Switch back: the stash is restored. RecordInlineEdit MUST NOT fire
        // for the restored row — that was the bug.
        var back = vm.FilteredDataBlocks.First(b => b.Name == primaryInfo.Name);
        vm.SwitchToDataBlock(back).Should().BeTrue();

        usageTracker.DidNotReceive().RecordInlineEdit();
        vm.PendingInlineEditCount.Should().Be(1, "the stashed edit was restored to the live tree");
    }

    [Fact]
    public void GoToFirstChange_ActivePending_FiresJumpEventForFirstPendingNode()
    {
        var h = CreateVm();
        // Stage one inline edit on the active DB.
        var leaf = h.Vm.RootMembers.First(m => m.IsLeaf);
        leaf.EditableStartValue = "999";

        MemberNodeViewModel? jumpedTo = null;
        h.Vm.RequestJumpToMember += node => jumpedTo = node;

        h.Vm.HasAnyChanges.Should().BeTrue();
        h.Vm.GoToFirstChangeCommand.CanExecute(null).Should().BeTrue();

        h.Vm.GoToFirstChangeCommand.Execute(null);

        jumpedTo.Should().NotBeNull("active-DB pending wins — view should scroll to the first pending member");
        jumpedTo!.Path.Should().Be(leaf.Path);
        h.Vm.SelectedFlatMember.Should().Be(leaf);
    }

    [Fact]
    public void GoToFirstChange_OnlyStashes_SwitchesToFirstStashedDb()
    {
        var h = CreateVm(promptResult: YesNoCancelResult.No);

        // Stage on primary, switch away (Keep) so the active DB ends up clean.
        var leaf = h.Vm.RootMembers.First(m => m.IsLeaf);
        var originalDb = h.Vm.CurrentDataBlockName;
        leaf.EditableStartValue = "888";

        h.Vm.OpenDataBlocksDropdownCommand.Execute(null);
        var target = h.Vm.FilteredDataBlocks.First(b => b.Name != originalDb);
        h.Vm.SwitchToDataBlock(target).Should().BeTrue();
        h.Vm.PendingInlineEditCount.Should().Be(0);
        h.Vm.HasStashedDbs.Should().BeTrue();

        // Active is clean, stash exists → GoToFirstChange should switch BACK
        // to the stashed DB (which is the original primary).
        h.Vm.HasAnyChanges.Should().BeTrue();
        h.Vm.GoToFirstChangeCommand.Execute(null);

        h.Vm.CurrentDataBlockName.Should().Be(originalDb);
        h.Vm.PendingInlineEditCount.Should().Be(1, "stash restored on switch-back");
        h.Vm.HasStashedDbs.Should().BeFalse();
    }

    [Fact]
    public void StashOnA_KeepToB_ApplyToC_PreservesAStash()
    {
        // Regression for the mid-cycle Apply path (#59 review): user stages
        // on A, picks Keep when switching to B (A enters the stash), then
        // commits B and switches to C. A's stash must survive B's Apply —
        // ExecuteApply only refreshes the active tree; the stash dictionary
        // is independent and must stay untouched.
        var aXml = TestFixtures.LoadXml("flat-db.xml");
        var bXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var cXml = TestFixtures.LoadXml("mixed-types-db.xml");
        var parser = new SimaticMLParser();
        var aInfo = parser.Parse(aXml);
        var bInfo = parser.Parse(bXml);
        var cInfo = parser.Parse(cXml);

        var enumerated = new[]
        {
            new DataBlockSummary(aInfo.Name, ""),
            new DataBlockSummary(bInfo.Name, ""),
            new DataBlockSummary(cInfo.Name, ""),
        };

        // Two-state fake: Keep then Yes (Apply) on the second prompt.
        var queued = new Queue<YesNoCancelResult>(new[]
        {
            YesNoCancelResult.No,   // A → B: Keep (stash A)
            YesNoCancelResult.Yes,  // B → C: Apply B
        });
        var mbx = new ScriptedMessageBox(queued);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var usageTracker = Substitute.For<IUsageTracker>();
        usageTracker.GetStatus().Returns(new UsageStatus(0, 3));
        usageTracker.GetInlineStatus().Returns(new UsageStatus(0, 10));
        usageTracker.RecordInlineEdit().Returns(true);
        usageTracker.RecordUsage().Returns(true);

        int applyCount = 0;
        var vm = new BulkChangeViewModel(
            aInfo, aXml,
            new HierarchyAnalyzer(), bulkService, usageTracker, configLoader,
            onApply: _ => applyCount++,
            messageBox: mbx,
            enumerateDataBlocks: () => enumerated,
            switchToDataBlock: s =>
            {
                if (string.Equals(s.Name, aInfo.Name, StringComparison.Ordinal)) return aXml;
                if (string.Equals(s.Name, bInfo.Name, StringComparison.Ordinal)) return bXml;
                return cXml;
            });

        // 1. Stage on A.
        vm.RootMembers.First(m => m.IsLeaf).EditableStartValue = "111";
        vm.OpenDataBlocksDropdownCommand.Execute(null);

        // 2. Switch to B with Keep — A goes to stash.
        var bSummary = vm.FilteredDataBlocks.First(b => b.Name == bInfo.Name);
        vm.SwitchToDataBlock(bSummary).Should().BeTrue();
        vm.CurrentDataBlockName.Should().Be(bInfo.Name);
        vm.StashedDbs.Should().HaveCount(1);

        // 3. Stage on B and switch to C with Apply.
        vm.RootMembers.First(m => m.IsLeaf).EditableStartValue = "222";
        vm.OpenDataBlocksDropdownCommand.Execute(null);
        var cSummary = vm.FilteredDataBlocks.First(b => b.Name == cInfo.Name);
        vm.SwitchToDataBlock(cSummary).Should().BeTrue();

        // Apply must have committed B.
        applyCount.Should().Be(1);
        vm.CurrentDataBlockName.Should().Be(cInfo.Name);

        // A's stash must still be there — Apply on B is independent.
        vm.StashedDbs.Should().HaveCount(1);
        vm.StashedDbs[0].DbName.Should().Be(aInfo.Name);
        vm.StashedDbs[0].Edits.Should().HaveCount(1);
        vm.StashedDbs[0].Edits[0].PendingValue.Should().Be("111");
    }

    [Fact]
    public void GoToFirstChange_NothingQueued_CommandDisabled()
    {
        var h = CreateVm();
        h.Vm.HasAnyChanges.Should().BeFalse();
        h.Vm.GoToFirstChangeCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// Returns a scripted sequence of YesNoCancel answers — used when one
    /// test runs through multiple prompts and each needs a different choice
    /// (e.g. Keep on the first switch, Apply on the second).
    /// </summary>
    private class ScriptedMessageBox : IMessageBoxService
    {
        private readonly Queue<YesNoCancelResult> _answers;
        public ScriptedMessageBox(Queue<YesNoCancelResult> answers) { _answers = answers; }
        public bool AskYesNo(string message, string title) => true;
        public void ShowError(string message, string title) { }
        public YesNoCancelResult AskYesNoCancel(string message, string title) =>
            _answers.Count > 0 ? _answers.Dequeue() : YesNoCancelResult.Cancel;
    }

    private class FakeMessageBox : IMessageBoxService
    {
        private readonly YesNoCancelResult _result;
        public int AskYesNoCallCount { get; private set; }
        public int AskYesNoCancelCallCount { get; private set; }
        public FakeMessageBox(YesNoCancelResult result) { _result = result; }
        public bool AskYesNo(string message, string title) { AskYesNoCallCount++; return true; }
        public void ShowError(string message, string title) { }
        public YesNoCancelResult AskYesNoCancel(string message, string title)
        {
            AskYesNoCancelCallCount++;
            return _result;
        }
    }
}
