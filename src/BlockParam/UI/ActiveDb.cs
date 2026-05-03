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
        System.Action<string>? onApply = null)
    {
        Info = info;
        Xml = xml;
        OnApply = onApply;
    }

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
}
