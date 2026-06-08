using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
/// Issue #182: regression gate for <c>SuppressClosePromptsScripted</c> on
/// <see cref="BulkChangeDialog"/>. The dialog's XAML is too resource-heavy
/// to instantiate in a headless runner, so we verify:
/// <list type="number">
///   <item>The property exists with the right shape (reflection gate).</item>
///   <item>The ViewModel path it exercises — <c>DiscardPendingSilent</c> —
///         clears pending edits <b>without</b> calling the message-box service
///         even when stashed edits exist.</item>
/// </list>
/// </summary>
public class CaptureModeClosePromptTests
{
    [Fact]
    public void SuppressClosePromptsScripted_property_exists_as_internal_bool()
    {
        var prop = typeof(BulkChangeDialog).GetProperty(
            "SuppressClosePromptsScripted",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        prop.Should().NotBeNull("capture-mode close-bypass depends on this property");
        prop!.DeclaringType.Should().Be(typeof(BulkChangeDialog));
        prop.PropertyType.Should().Be(typeof(bool));
        prop.CanRead.Should().BeTrue();
        prop.CanWrite.Should().BeTrue();
    }

    [Fact]
    public void DiscardPendingSilent_clears_pending_without_prompt_even_with_stashed_edits()
    {
        var env = BuildWithPendingAndStash();

        env.Vm.PendingInlineEditCount.Should().BeGreaterThan(0,
            "precondition: must have pending edits to exercise the discard path");
        env.Vm.ActiveSet.StashedDbs.Count.Should().BeGreaterThan(0,
            "precondition: must have stashed DBs to prove prompt is not required");

        env.Mbx.Reset();
        env.Vm.DiscardPendingSilent();

        env.Vm.PendingInlineEditCount.Should().Be(0,
            "SuppressClosePromptsScripted calls DiscardPendingSilent; " +
            "it must clear pending edits unconditionally");
        env.Mbx.TotalCallCount.Should().Be(0,
            "DiscardPendingSilent must never invoke a message-box prompt");
    }

    [Fact]
    public void DiscardPendingSilent_alone_does_not_clear_stashes()
    {
        var env = BuildWithPendingAndStash();
        var stashCountBefore = env.Vm.ActiveSet.StashedDbs.Count;

        env.Mbx.Reset();
        env.Vm.DiscardPendingSilent();

        env.Vm.ActiveSet.StashedDbs.Count.Should().Be(stashCountBefore,
            "DiscardPendingSilent should only clear active-DB pending edits; " +
            "ScriptedClose owns the full cleanup including stashes");
    }

    [Fact]
    public void ScriptedClose_clears_both_pending_and_stashes_without_prompt()
    {
        var env = BuildWithPendingAndStash();

        env.Vm.PendingInlineEditCount.Should().BeGreaterThan(0,
            "precondition: must have pending edits");
        env.Vm.ActiveSet.StashedDbs.Count.Should().BeGreaterThan(0,
            "precondition: must have stashed DBs");

        env.Mbx.Reset();
        BulkChangeDialog.ScriptedClose(env.Vm);

        env.Vm.PendingInlineEditCount.Should().Be(0,
            "ScriptedClose must discard all pending inline edits");
        env.Vm.ActiveSet.StashedDbs.Count.Should().Be(0,
            "ScriptedClose must clear stashed DBs to prevent in-memory leaks");
        env.Mbx.TotalCallCount.Should().Be(0,
            "ScriptedClose must never invoke a message-box prompt");
    }

    // ─────────────────── helpers ───────────────────

    private static TestEnv BuildWithPendingAndStash()
    {
        var parser = new SimaticMLParser();
        var anchorXml = TestFixtures.LoadXml("flat-db.xml");
        var anchorInfo = parser.Parse(anchorXml);
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var peerInfo = parser.Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 1000));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var mbx = new CallCountingMessageBox();

        var peerDb = new ActiveDb(peerInfo, peerXml,
            onApply: _ => { }, plcName: "");

        var summaries = new List<DataBlockSummary>
        {
            new DataBlockSummary(anchorInfo.Name, ""),
            new DataBlockSummary(peerInfo.Name, ""),
        };

        var vm = new BulkChangeViewModel(
            anchorInfo, anchorXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            onApply: _ => { },
            messageBox: mbx,
            enumerateDataBlocks: () => summaries,
            switchToDataBlock: s => peerXml,
            currentPlcName: "",
            additionalActiveDbs: new[] { peerDb },
            buildActiveDbForSummary: s =>
                new ActiveDb(peerInfo, peerXml, onApply: _ => { }, plcName: ""));

        // Stage a pending edit on the peer DB so the stash prompt triggers.
        var peerLeaf = vm.Tree.RootMembers
            .Where(r => r.Name == peerInfo.Name)
            .SelectMany(r => r.AllDescendants())
            .First(n => n.IsLeaf && !string.IsNullOrEmpty(n.StartValue));
        peerLeaf.EditableStartValue = peerLeaf.StartValue == "0" ? "1" : "0";

        // Remove the peer via the dropdown — triggers prompt.
        // Answer: Stash (StashAndSwitch) to create a stash entry.
        mbx.NextApplyStashResult = ApplyStashCancelResult.StashAndSwitch;
        vm.ActiveSet.OpenDataBlocksDropdownCommand.Execute(null);
        var peerRow = vm.ActiveSet.FilteredDataBlockItems.First(i => i.Name == peerInfo.Name);
        peerRow.IsActive = false;

        // Now stage a pending edit on the anchor DB so the close-path sees both.
        var anchorLeaf = vm.Tree.RootMembers
            .SelectMany(r => new[] { r }.Concat(r.AllDescendants()))
            .First(n => n.IsLeaf && !string.IsNullOrEmpty(n.StartValue));
        anchorLeaf.EditableStartValue = anchorLeaf.StartValue == "0" ? "1" : "0";

        return new TestEnv(vm, mbx);
    }

    private sealed record TestEnv(BulkChangeViewModel Vm, CallCountingMessageBox Mbx);

    private sealed class CallCountingMessageBox : IMessageBoxService
    {
        public int TotalCallCount { get; private set; }
        public ApplyStashCancelResult NextApplyStashResult { get; set; }

        public void Reset() => TotalCallCount = 0;

        public bool AskYesNo(string message, string title) { TotalCallCount++; return true; }
        public void ShowError(string message, string title) => TotalCallCount++;
        public void ShowInfo(string message, string title) => TotalCallCount++;

        public ApplyStashCancelResult AskApplyStashCancel(string message, string title)
        {
            TotalCallCount++;
            return NextApplyStashResult;
        }

        public AddOrReplaceResult AskAddOrReplace(string message, string title)
        {
            TotalCallCount++;
            return AddOrReplaceResult.Cancel;
        }

        public CloseWithStashResult AskCloseWithStash(string message, string title)
        {
            TotalCallCount++;
            return CloseWithStashResult.Cancel;
        }
    }
}
