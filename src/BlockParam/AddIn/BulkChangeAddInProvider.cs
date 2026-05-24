using Siemens.Engineering;
using Siemens.Engineering.AddIn;
using Siemens.Engineering.AddIn.Menu;
using BlockParam.Diagnostics;
using BlockParam.UI.Controls.PillMultiSelect;

namespace BlockParam.AddIn;

/// <summary>
/// Registers the Bulk Change Add-In in the TIA Portal project tree context menu.
/// </summary>
public class BulkChangeAddInProvider : ProjectTreeAddInProvider
{
    private readonly TiaPortal _tiaPortal;

    static BulkChangeAddInProvider()
    {
        // The pill control's diagnostic shims (#141) flow through an
        // injectable sink so the control stays vendorable into other repos
        // with zero project-internal dependencies. Wire it to our own
        // partial-trust-safe Log so nothing changes in the runtime log.
        MultiSelectLog.Sink = msg => Log.Information("{Msg}", msg);
    }

    public BulkChangeAddInProvider(TiaPortal tiaPortal)
    {
        _tiaPortal = tiaPortal;
    }

    protected override IEnumerable<ContextMenuAddIn> GetContextMenuAddIns()
    {
        yield return new BulkChangeContextMenu(_tiaPortal);
#if DIAGNOSTICS
        // DEV-ONLY diagnostics scenario runner. Compiled in only when built
        // with -p:Diagnostics=true; absent from the shipped marketplace build.
        // See DiagnosticsScenarioMenu.cs.
        yield return new DiagnosticsScenarioMenu(_tiaPortal);
#endif
    }
}

public static class BulkChangeAddInProviderInfo
{
    public const string MenuTitle = "BlockParam...";
}
