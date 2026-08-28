using BlockParam.Models;

namespace BlockParam.UI;

/// <summary>
/// Per-DB runtime state for the BulkChange dialog (#58). One instance per
/// Data Block currently active in the dialog — single-DB workflows hold one,
/// multi-DB workflows hold N. Owns the parsed structure, the current export
/// XML (mutated in place by Apply), and the host callback that imports the
/// modified XML back into TIA Portal.
///
/// Identity-by-DB is what lets multi-DB Apply route each pending edit to the
/// correct host import; the VM never assumes a single shared XML buffer.
/// </summary>
public class ActiveDb
{
    public ActiveDb(
        DataBlockInfo info,
        string xml,
        System.Action<string>? onApply = null,
        string? plcName = null,
        System.Action<string>? onRestore = null,
        System.Func<string?>? getLastBackupPath = null)
    {
        Info = info;
        Xml = xml;
        OnApply = onApply;
        PlcName = plcName ?? "";
        OnRestore = onRestore;
        GetLastBackupPath = getLastBackupPath;
    }

    /// <summary>
    /// Owning PLC for this DB (#58 review must-fix #4). Multi-PLC projects
    /// can host two DBs with identical names on different PLCs; identifying
    /// active rows / dropdown matches by (Name, PlcName) instead of Name
    /// alone keeps them distinct. Empty string for hosts that don't supply
    /// a PLC name (DevLauncher, single-PLC stand-ins) — matches the VM's
    /// own _currentPlcName fallback.
    /// </summary>
    public string PlcName { get; }

    /// <summary>Parsed structure of this DB. Reassigned by RefreshTree after a successful Apply.</summary>
    public DataBlockInfo Info { get; set; }

    /// <summary>
    /// Current SimaticML export of this DB. Apply mutates this in place
    /// (writes pending values, applies comment previews) before handing it
    /// to <see cref="OnApply"/> for import back into TIA.
    /// </summary>
    public string Xml { get; set; }

    /// <summary>
    /// Host callback that imports the modified XML for this DB into TIA
    /// Portal. Null when the dialog is in DevLauncher / read-only mode.
    /// Multi-DB Apply invokes one of these per active DB inside a single
    /// <c>ExclusiveAccess</c> block (#58).
    /// </summary>
    public System.Action<string>? OnApply { get; }

    /// <summary>
    /// Host callback that re-imports a previously written backup XML for this
    /// DB, undoing the last <see cref="OnApply"/> import (#192). The argument
    /// is the backup path handed out by <see cref="GetLastBackupPath"/>.
    ///
    /// Null when the host cannot restore (DevLauncher / read-only ActiveDbs
    /// added from the dropdown before per-DB host wiring). A null here means
    /// multi-DB Apply must NOT offer a rollback that it cannot complete —
    /// see <see cref="MultiDbRollbackCoordinator.CanOfferRollback"/>.
    /// </summary>
    public System.Action<string>? OnRestore { get; }

    /// <summary>
    /// Returns the path of the pre-import backup written during the most
    /// recent <see cref="OnApply"/> invocation, or null when this DB has not
    /// been imported in this session / the host writes no backup.
    ///
    /// A delegate rather than a mutable property on purpose: the path lives
    /// in <c>ActiveDbFactory</c>'s OnApply closure (which is what actually
    /// calls <c>ITiaPortalAdapter.BackupBlock</c>), and the anchor ActiveDb
    /// built by <c>BulkChangeContextMenu</c> is a thunk over whichever
    /// factory-built ActiveDb is currently focused — a plain field would go
    /// stale on every DB switch (#192).
    /// </summary>
    public System.Func<string?>? GetLastBackupPath { get; }
}
