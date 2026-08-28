using System.Linq;
using BlockParam.Diagnostics;
using BlockParam.Localization;

namespace BlockParam.UI;

/// <summary>
/// One DB that already reached TIA Portal during a multi-DB Apply, paired
/// with the pre-import backup written for it and the number of value changes
/// it was charged for (#192).
///
/// A plain sealed class, not a readonly struct — see the CLAUDE.md
/// partial-trust IL guardrail (instance calls on readonly-struct fields emit
/// unverifiable <c>ldflda</c> under TIA's Add-In Loader sandbox).
/// </summary>
public sealed class CommittedDbWrite
{
    public CommittedDbWrite(ActiveDb db, string? backupPath, int changes)
    {
        Db = db;
        BackupPath = backupPath;
        Changes = changes;
    }

    public ActiveDb Db { get; }

    /// <summary>
    /// Pre-import backup captured at commit time (read from
    /// <see cref="ActiveDb.GetLastBackupPath"/> right after the import
    /// returned, never later — a subsequent Apply attempt would overwrite it).
    /// Null when the host writes no backup for this DB.
    /// </summary>
    public string? BackupPath { get; }

    /// <summary>Value changes written for this DB — the quota units at stake.</summary>
    public int Changes { get; }

    public string DbName => Db.Info.Name;

    /// <summary>
    /// True when this DB can actually be put back: a real backup file path AND
    /// a host restore callback. Both halves are required — offering a rollback
    /// that can only be half-performed is worse than reporting the partial
    /// commit honestly.
    /// </summary>
    public bool CanRestore =>
        !string.IsNullOrEmpty(BackupPath) && Db.OnRestore != null;
}

/// <summary>What the user chose / what actually happened (#192).</summary>
public enum RollbackDecision
{
    /// <summary>User declined; every committed write stays in the project.</summary>
    Declined,

    /// <summary>Every committed DB was restored from its backup.</summary>
    RolledBack,

    /// <summary>
    /// At least one restore threw. The project is in a MIXED state — some DBs
    /// back at their pre-Apply content, some still holding the new values.
    /// </summary>
    RollbackFailed,
}

/// <summary>Result of the rollback flow, consumed by the ViewModel.</summary>
public sealed class MultiDbRollbackOutcome
{
    public MultiDbRollbackOutcome(
        RollbackDecision decision,
        string statusText,
        int refundedChanges,
        IReadOnlyList<ActiveDb> keptDbs,
        IReadOnlyList<ActiveDb> restoredDbs)
    {
        Decision = decision;
        StatusText = statusText;
        RefundedChanges = refundedChanges;
        KeptDbs = keptDbs;
        RestoredDbs = restoredDbs;
    }

    public RollbackDecision Decision { get; }

    /// <summary>Fully localized status line for the dialog's status bar.</summary>
    public string StatusText { get; }

    /// <summary>
    /// Quota units to credit back — the sum over the DBs that were actually
    /// restored. A user must never pay for writes that no longer exist.
    /// </summary>
    public int RefundedChanges { get; }

    /// <summary>DBs whose writes are still in the project (pending state must be cleared).</summary>
    public IReadOnlyList<ActiveDb> KeptDbs { get; }

    /// <summary>DBs put back to their pre-Apply content.</summary>
    public IReadOnlyList<ActiveDb> RestoredDbs { get; }
}

/// <summary>
/// Transactional rollback for multi-DB Apply (#192).
///
/// <para>
/// Before this class, <c>ExecuteApplyMultiDb</c>'s Phase-2 commit loop left a
/// half-applied project behind whenever a later DB's import threw: DB #1
/// written, DB #2 failed, nothing undone. The backup files always existed
/// (<c>ActiveDbFactory</c>'s OnApply closure calls
/// <c>ITiaPortalAdapter.BackupBlock</c> right before every <c>ImportBlock</c>)
/// — only the wiring was missing.
/// </para>
///
/// <para>
/// <b>Semantics: ask, then all-or-nothing.</b> A silent rollback would throw
/// away writes that worked; a silent partial commit hides a half-applied
/// project. So the user is shown the concrete facts — which DBs were written,
/// which one failed and with what error, where every backup file is — and
/// decides. "Yes" restores EVERY committed DB; "No" keeps the partial commit
/// and the status line names every backup file so a manual re-import stays
/// possible.
/// </para>
///
/// <para>
/// Lives outside <c>BulkChangeViewModel</c> on purpose (CLAUDE.md hotspot
/// rule): the ViewModel keeps only the wiring — build the pair list, charge /
/// credit quota, clear pending state — while prompt composition, the restore
/// loop and the escalation path live here where they are unit-testable
/// without a WPF dialog or a TIA install.
/// </para>
/// </summary>
public sealed class MultiDbRollbackCoordinator
{
    private readonly IMessageBoxService _messageBox;

    public MultiDbRollbackCoordinator(IMessageBoxService messageBox)
    {
        _messageBox = messageBox;
    }

    /// <summary>
    /// True when a rollback is both needed and completable: at least one DB
    /// already committed, and EVERY one of them has a backup file plus a
    /// restore callback. If any committed DB can't be restored the caller must
    /// fall back to the honest "no automatic rollback" report (#191) instead
    /// of offering a rollback it could only half-perform.
    /// </summary>
    public bool CanOfferRollback(IReadOnlyList<CommittedDbWrite> committed)
    {
        if (committed.Count == 0) return false;

        foreach (var write in committed)
        {
            if (write.CanRestore) continue;
            Log.Warning(
                "Multi-DB rollback not offered: '{DbName}' has no restorable backup " +
                "(path={BackupPath}, hasRestoreCallback={HasCallback})",
                write.DbName, write.BackupPath ?? "(none)", write.Db.OnRestore != null);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Shows the facts, asks the question, and performs the rollback if the
    /// user confirms. Only call when <see cref="CanOfferRollback"/> is true.
    /// </summary>
    public MultiDbRollbackOutcome RunRollbackFlow(
        IReadOnlyList<CommittedDbWrite> committed,
        string failedDbName,
        Exception failure)
    {
        var failureMessage = DescribeFailure(failure);
        Log.Warning(
            "Multi-DB Apply failed on '{FailedDb}' after {Count} DB(s) already committed: {Backups}",
            failedDbName, committed.Count,
            string.Join("; ", committed.Select(c => $"{c.DbName} => {c.BackupPath}")));

        var confirmed = _messageBox.AskYesNo(
            Res.Format("Rollback_Prompt_Message",
                failedDbName, failureMessage, committed.Count, BuildBackupList(committed)),
            Res.Get("Rollback_Prompt_Title"));

        if (!confirmed)
        {
            Log.Warning("Multi-DB rollback declined by user — partial commit kept");
            return new MultiDbRollbackOutcome(
                RollbackDecision.Declined,
                Res.Format("Rollback_Status_PartialKept",
                    failedDbName, failureMessage, committed.Count,
                    BuildInlineBackupList(committed)),
                refundedChanges: 0,
                keptDbs: committed.Select(c => c.Db).ToList(),
                restoredDbs: new List<ActiveDb>());
        }

        return ExecuteRollback(committed, failedDbName, failureMessage);
    }

    /// <summary>
    /// Restores every committed DB, newest write first. A throwing restore does
    /// NOT abort the remaining ones — each DB is an independent import, and
    /// stopping early would strand DBs that could still have been recovered.
    /// Failures are collected and escalated together.
    /// </summary>
    private MultiDbRollbackOutcome ExecuteRollback(
        IReadOnlyList<CommittedDbWrite> committed,
        string failedDbName,
        string failureMessage)
    {
        var restored = new List<CommittedDbWrite>();
        var failed = new List<(CommittedDbWrite Write, string Error)>();

        for (int i = committed.Count - 1; i >= 0; i--)
        {
            var write = committed[i];
            try
            {
                write.Db.OnRestore!(write.BackupPath!);
                restored.Add(write);
            }
            catch (Exception ex)
            {
                Log.Error(ex,
                    "Multi-DB rollback: restoring '{DbName}' from {BackupPath} FAILED",
                    write.DbName, write.BackupPath);
                failed.Add((write, DescribeFailure(ex)));
            }
        }

        var refunded = restored.Sum(r => r.Changes);

        if (failed.Count == 0)
        {
            Log.Information(
                "Multi-DB rollback complete: {Count} DB(s) restored, {Changes} change(s) creditable",
                restored.Count, refunded);
            return new MultiDbRollbackOutcome(
                RollbackDecision.RolledBack,
                Res.Format("Rollback_Status_RolledBack",
                    failedDbName, failureMessage, restored.Count),
                refundedChanges: refunded,
                keptDbs: new List<ActiveDb>(),
                restoredDbs: restored.Select(r => r.Db).ToList());
        }

        // The rollback itself broke. This is strictly worse than the original
        // failure — part of the project is at the old content, part at the new
        // — so it gets its own modal naming EVERY backup file and exactly which
        // DB is in which state. Never let this drain away into a generic error.
        _messageBox.ShowError(
            Res.Format("Rollback_Failed_Message",
                failedDbName, failureMessage,
                BuildRestoredList(restored),
                BuildFailedList(failed)),
            Res.Get("Rollback_Failed_Title"));

        Log.Error(null,
            "Multi-DB rollback INCOMPLETE: {Restored} restored, {Failed} still written ({Names})",
            restored.Count, failed.Count,
            string.Join(", ", failed.Select(f => f.Write.DbName)));

        return new MultiDbRollbackOutcome(
            RollbackDecision.RollbackFailed,
            Res.Format("Rollback_Status_RestoreFailed",
                failedDbName, restored.Count, failed.Count),
            refundedChanges: refunded,
            keptDbs: failed.Select(f => f.Write.Db).ToList(),
            restoredDbs: restored.Select(r => r.Db).ToList());
    }

    /// <summary>
    /// TIA's Openness exceptions routinely wrap the useful text one level
    /// down; an empty line here would make the whole prompt useless.
    /// </summary>
    private static string DescribeFailure(Exception ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return string.IsNullOrWhiteSpace(message) ? ex.GetType().Name : message;
    }

    private static string BuildBackupList(IReadOnlyList<CommittedDbWrite> committed) =>
        string.Join(Environment.NewLine, committed.Select(c =>
            Res.Format("Rollback_BackupLine", c.DbName, c.BackupPath ?? "")));

    private static string BuildRestoredList(IReadOnlyList<CommittedDbWrite> restored) =>
        restored.Count == 0
            ? Res.Get("Rollback_ListEmpty")
            : string.Join(Environment.NewLine, restored.Select(r =>
                Res.Format("Rollback_BackupLine", r.DbName, r.BackupPath ?? "")));

    private static string BuildFailedList(IReadOnlyList<(CommittedDbWrite Write, string Error)> failed) =>
        string.Join(Environment.NewLine, failed.Select(f =>
            Res.Format("Rollback_RestoreFailedLine",
                f.Write.DbName, f.Write.BackupPath ?? "", f.Error)));

    private static string BuildInlineBackupList(IReadOnlyList<CommittedDbWrite> committed) =>
        string.Join("; ", committed.Select(c =>
            Res.Format("Rollback_BackupInline", c.DbName, c.BackupPath ?? "")));
}
