using Siemens.Engineering;
using Siemens.Engineering.AddIn;
using Siemens.Engineering.AddIn.Menu;

namespace BlockParam.AddIn;

/// <summary>
/// Registers the Bulk Change Add-In in the TIA Portal project tree context menu.
/// </summary>
public class BulkChangeAddInProvider : ProjectTreeAddInProvider
{
    private readonly TiaPortal _tiaPortal;

    public BulkChangeAddInProvider(TiaPortal tiaPortal)
    {
        _tiaPortal = tiaPortal;
    }

    protected override IEnumerable<ContextMenuAddIn> GetContextMenuAddIns()
    {
        yield return new BulkChangeContextMenu(_tiaPortal);
    }
}

public static class BulkChangeAddInProviderInfo
{
    public const string MenuTitle = "BlockParam...";
}
