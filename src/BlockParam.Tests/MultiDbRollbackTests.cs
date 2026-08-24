using System;
using System.Collections.Generic;
using System.Linq;
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
/// Coverage for #192 — a real multi-DB rollback.
///
/// <para>
/// Before #192, a multi-DB Apply that failed on DB #2 left DB #1's write in
/// the project with nothing undone: the pre-import backups existed on disk
/// (<c>ActiveDbFactory</c> writes one right before every import) but nothing
/// carried the paths out of the closure. These tests pin the wiring and the
/// semantics the user chose: <b>ask, then all-or-nothing</b>.
/// </para>
///
/// <list type="bullet">
///   <item>a confirmed rollback restores EVERY already-committed DB;</item>
///   <item>declining keeps the partial commit and surfaces every backup path;</item>
///   <item>quota is credited back for exactly the DBs that went back;</item>
///   <item>a throwing restore escalates with its own message naming every
///   backup file — never a generic error.</item>
/// </list>
/// </summary>
public class MultiDbRollbackTests
{
    private const string AnchorBackup = @"C:\Temp\BlockParam\backup\DB_A_backup_20260101_101500.xml";
    private const string PeerBackup = @"C:\Temp\BlockParam\backup\DB_B_backup_20260101_101501.xml";

    // ---------- MultiDbRollbackCoordinator (pure orchestration) ----------

    [Fact]
    public void CanOfferRollback_FalseWhenNothingCommitted()
    {
        var coordinator = new MultiDbRollbackCoordinator(Substitute.For<IMessageBoxService>());

        coordinator.CanOfferRollback(new List<CommittedDbWrite>()).Should().BeFalse(
            "there is nothing to put back when the FIRST DB is the one that failed");
    }

    [Fact]
    public void CanOfferRollback_FalseWhenAnyCommittedDbHasNoBackupPath()
    {
        var coordinator = new MultiDbRollbackCoordinator(Substitute.For<IMessageBoxService>());
        var writes = new List<CommittedDbWrite>
        {
            Write(RestorableDb("DB_A", AnchorBackup, out _), AnchorBackup, changes: 2),
            // DevLauncher / dropdown-added read-only DB: imported, but no backup.
            Write(RestorableDb("DB_B", PeerBackup, out _), backupPath: null, changes: 1),
        };

        coordinator.CanOfferRollback(writes).Should().BeFalse(
            "an all-or-nothing rollback that can only be half-performed is worse " +
            "than an honest partial-commit report (#191)");
    }

    [Fact]
    public void CanOfferRollback_FalseWhenRestoreCallbackMissing()
    {
        var coordinator = new MultiDbRollbackCoordinator(Substitute.For<IMessageBoxService>());
        var readOnlyDb = new ActiveDb(
            new DataBlockInfo("DB_A", 1, "Optimized", "GlobalDB", Array.Empty<MemberNode>()),
            "<Block />", onApply: null, plcName: "",
            onRestore: null, getLastBackupPath: () => AnchorBackup);

        coordinator.CanOfferRollback(new[] { Write(readOnlyDb, AnchorBackup, 1) })
            .Should().BeFalse("a backup path alone cannot import anything");
    }

    [Fact]
    public void RunRollbackFlow_PromptNamesFailedDbErrorAndEveryBackupPath()
    {
        var messageBox = Substitute.For<IMessageBoxService>();
        messageBox.AskYesNo(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        var coordinator = new MultiDbRollbackCoordinator(messageBox);

        coordinator.RunRollbackFlow(
            TwoCommittedWrites(out _, out _),
            "DB_C",
            new InvalidOperationException("TIA import failed"));

        var prompt = (string)messageBox.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IMessageBoxService.AskYesNo))
            .GetArguments()[0]!;

        prompt.Should().Contain("DB_C", "the user must see WHICH DB failed");
        prompt.Should().Contain("TIA import failed", "…and WHY it failed");
        prompt.Should().Contain("DB_A").And.Contain("DB_B",
            "…and which DBs were already written");
        prompt.Should().Contain(AnchorBackup).And.Contain(PeerBackup,
            "…and where every backup file is — this must not be a blind 'Rollback?'");
    }

    [Fact]
    public void RunRollbackFlow_Confirmed_RestoresEveryCommittedDb()
    {
        var messageBox = Substitute.For<IMessageBoxService>();
        messageBox.AskYesNo(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var coordinator = new MultiDbRollbackCoordinator(messageBox);
        var writes = TwoCommittedWrites(out var anchorRestores, out var peerRestores);

        var outcome = coordinator.RunRollbackFlow(
            writes, "DB_C", new InvalidOperationException("TIA import failed"));

        anchorRestores.Should().Equal(AnchorBackup);
        peerRestores.Should().Equal(PeerBackup);
        outcome.Decision.Should().Be(RollbackDecision.RolledBack);
        outcome.RestoredDbs.Should().HaveCount(2);
        outcome.KeptDbs.Should().BeEmpty("all-or-nothing: nothing is left written");
        outcome.RefundedChanges.Should().Be(3, "2 + 1 changes no longer exist in the project");
        outcome.StatusText.Should().Be(
            BlockParam.Localization.Res.Format(
                "Rollback_Status_RolledBack", "DB_C", "TIA import failed", 2));
    }

    [Fact]
    public void RunRollbackFlow_Declined_KeepsPartialCommitAndListsEveryBackupPath()
    {
        var messageBox = Substitute.For<IMessageBoxService>();
        messageBox.AskYesNo(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        var coordinator = new MultiDbRollbackCoordinator(messageBox);
        var writes = TwoCommittedWrites(out var anchorRestores, out var peerRestores);

        var outcome = coordinator.RunRollbackFlow(
            writes, "DB_C", new InvalidOperationException("TIA import failed"));

        anchorRestores.Should().BeEmpty("declining must not touch the project");
        peerRestores.Should().BeEmpty();
        outcome.Decision.Should().Be(RollbackDecision.Declined);
        outcome.KeptDbs.Should().HaveCount(2);
        outcome.RefundedChanges.Should().Be(0, "every write still exists — it stays charged");
        outcome.StatusText.Should().Contain(AnchorBackup).And.Contain(PeerBackup,
            "a kept partial commit must name every backup file so a manual " +
            "restore stays possible");
    }

    [Fact]
    public void RunRollbackFlow_RestoreThrows_EscalatesWithBackupPathsAndPerDbState()
    {
        var messageBox = Substitute.For<IMessageBoxService>();
        messageBox.AskYesNo(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var coordinator = new MultiDbRollbackCoordinator(messageBox);

        var anchorRestores = new List<string>();
        var anchor = new ActiveDb(
            Info("DB_A"), "<Block />", onApply: null, plcName: "",
            onRestore: anchorRestores.Add, getLastBackupPath: () => AnchorBackup);
        // The LAST committed DB is restored FIRST, so make that one throw and
        // assert the loop still recovered the other.
        var peer = new ActiveDb(
            Info("DB_B"), "<Block />", onApply: null, plcName: "",
            onRestore: _ => throw new InvalidOperationException("backup import rejected"),
            getLastBackupPath: () => PeerBackup);

        var outcome = coordinator.RunRollbackFlow(
            new[] { Write(anchor, AnchorBackup, 2), Write(peer, PeerBackup, 1) },
            "DB_C", new InvalidOperationException("TIA import failed"));

        anchorRestores.Should().ContainSingle(
            "one failing restore must not abort the others — every DB it can " +
            "still recover is one less DB the user has to import by hand")
            .Which.Should().Be(AnchorBackup);

        outcome.Decision.Should().Be(RollbackDecision.RollbackFailed);
        outcome.RestoredDbs.Should().ContainSingle().Which.Info.Name.Should().Be("DB_A");
        outcome.KeptDbs.Should().ContainSingle().Which.Info.Name.Should().Be("DB_B",
            "the DB whose restore failed is still holding the new values");
        outcome.RefundedChanges.Should().Be(2,
            "only the DB that really went back may be credited");
        outcome.StatusText.Should().Be(
            BlockParam.Localization.Res.Format(
                "Rollback_Status_RestoreFailed", "DB_C", 1, 1));
        outcome.StatusText.Should().NotBe(
            BlockParam.Localization.Res.Format(
                "Rollback_Status_RolledBack", "DB_C", "TIA import failed", 1),
            "a failed rollback must never read like a successful one");

        var error = messageBox.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IMessageBoxService.ShowError));
        var body = (string)error.GetArguments()[0]!;
        body.Should().Contain("DB_A").And.Contain(AnchorBackup);
        body.Should().Contain("DB_B").And.Contain(PeerBackup);
        body.Should().Contain("backup import rejected",
            "the restore's own error is the only clue why the project is mixed");
    }

    // ---------- ExecuteApplyMultiDb wiring ----------

    [Fact]
    public void Apply_MultiDb_PeerImportThrows_ConfirmedRollback_RestoresCommittedDbAndCreditsQuota()
    {
        var messageBox = Substitute.For<IMessageBoxService>();
        messageBox.AskYesNo(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        var scenario = FailingMultiDbApply(messageBox);

        scenario.Vm.ApplyCommand.Execute(null);

        scenario.AnchorRestores.Should().ContainSingle(
            "the anchor committed before the peer threw — its write must be undone " +
            "from the backup ActiveDbFactory wrote right before the import")
            .Which.Should().Be(AnchorBackup);
        scenario.Vm.StatusText.Should().Be(
            BlockParam.Localization.Res.Format(
                "Rollback_Status_RolledBack", scenario.PeerName, "TIA import failed", 1));

        scenario.Tracker.Received(1).RecordUsage(1);
        scenario.Tracker.Received(1).RefundUsage(1);
    }

    [Fact]
    public void Apply_MultiDb_PeerImportThrows_DeclinedRollback_KeepsWriteAndReportsBackupPath()
    {
        var messageBox = Substitute.For<IMessageBoxService>();
        messageBox.AskYesNo(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        var scenario = FailingMultiDbApply(messageBox);

        scenario.Vm.ApplyCommand.Execute(null);

        scenario.AnchorRestores.Should().BeEmpty("the user chose to keep the partial commit");
        scenario.Vm.StatusText.Should().Contain(AnchorBackup,
            "keeping a partial commit is only honest if the user can find the backup");
        scenario.Tracker.Received(1).RecordUsage(1);
        scenario.Tracker.DidNotReceive().RefundUsage(Arg.Any<int>());
        scenario.Vm.Pending.PendingInlineEditCount.Should().Be(1,
            "the committed DB's pending flag is cleared (its values are in TIA); " +
            "the failed DB keeps its edit for a retry");
    }

    [Fact]
    public void Apply_MultiDb_PeerImportThrows_NoBackupWiring_FallsBackToHonestNoRollbackMessage()
    {
        // Hosts without backup/restore wiring (DevLauncher, read-only DBs added
        // from the dropdown) must NOT be offered a rollback that cannot be
        // completed — they keep the #191 message.
        var messageBox = Substitute.For<IMessageBoxService>();
        var scenario = FailingMultiDbApply(messageBox, wireBackup: false);

        scenario.Vm.ApplyCommand.Execute(null);

        messageBox.DidNotReceive().AskYesNo(Arg.Any<string>(), Arg.Any<string>());
        scenario.Vm.StatusText.Should().Be(
            BlockParam.Localization.Res.Format(
                "Status_ErrorNoAutoRollback", "TIA import failed", AppDirectories.Temp));
    }

    // ---------- helpers ----------

    private sealed class Scenario
    {
        public Scenario(BulkChangeViewModel vm, IUsageTracker tracker,
                        List<string> anchorRestores, string peerName)
        {
            Vm = vm;
            Tracker = tracker;
            AnchorRestores = anchorRestores;
            PeerName = peerName;
        }

        public BulkChangeViewModel Vm { get; }
        public IUsageTracker Tracker { get; }
        public List<string> AnchorRestores { get; }
        public string PeerName { get; }
    }

    /// <summary>
    /// Two active DBs, one staged inline edit each. The anchor imports fine;
    /// the peer (second in AllActiveDbs order) throws a genuine TIA write
    /// failure — the exact partial-commit shape #192 is about.
    /// </summary>
    private static Scenario FailingMultiDbApply(
        IMessageBoxService messageBox, bool wireBackup = true)
    {
        var anchorXml = TestFixtures.LoadXml("flat-db.xml");
        var peerXml = TestFixtures.LoadXml("nested-struct-db.xml");
        var parser = new SimaticMLParser();
        var anchorInfo = parser.Parse(anchorXml);
        var peerInfo = parser.Parse(peerXml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var anchorRestores = new List<string>();
        var peerDb = new ActiveDb(
            peerInfo, peerXml,
            onApply: _ => throw new InvalidOperationException("TIA import failed"),
            plcName: "",
            onRestore: wireBackup ? new Action<string>(_ => { }) : null,
            getLastBackupPath: wireBackup ? new Func<string?>(() => PeerBackup) : null);

        var vm = new BulkChangeViewModel(
            anchorInfo, anchorXml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            onApply: _ => { },
            messageBox: messageBox,
            additionalActiveDbs: new[] { peerDb },
            onRestore: wireBackup ? new Action<string>(anchorRestores.Add) : null,
            getLastBackupPath: wireBackup ? new Func<string?>(() => AnchorBackup) : null);

        var anchorLeaf = vm.Tree.RootMembers[0].AllDescendants().First(n => n.IsLeaf);
        var peerLeaf = vm.Tree.RootMembers[1].AllDescendants().First(n => n.IsLeaf);
        anchorLeaf.EditableStartValue = anchorLeaf.StartValue == "0" ? "1" : "0";
        peerLeaf.EditableStartValue = peerLeaf.StartValue == "0" ? "1" : "0";

        return new Scenario(vm, tracker, anchorRestores, peerInfo.Name);
    }

    private static DataBlockInfo Info(string name) =>
        new DataBlockInfo(name, 1, "Optimized", "GlobalDB", Array.Empty<MemberNode>());

    private static ActiveDb RestorableDb(string name, string backupPath, out List<string> restores)
    {
        var captured = new List<string>();
        restores = captured;
        return new ActiveDb(
            Info(name), "<Block />", onApply: null, plcName: "",
            onRestore: captured.Add, getLastBackupPath: () => backupPath);
    }

    private static CommittedDbWrite Write(ActiveDb db, string? backupPath, int changes) =>
        new CommittedDbWrite(db, backupPath, changes);

    private static IReadOnlyList<CommittedDbWrite> TwoCommittedWrites(
        out List<string> anchorRestores, out List<string> peerRestores)
    {
        var anchor = RestorableDb("DB_A", AnchorBackup, out anchorRestores);
        var peer = RestorableDb("DB_B", PeerBackup, out peerRestores);
        return new[]
        {
            Write(anchor, AnchorBackup, changes: 2),
            Write(peer, PeerBackup, changes: 1),
        };
    }
}
