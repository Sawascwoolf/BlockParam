# Key C# Code Patterns for TIA Portal Add-In Development

## Pattern 1: Add-In Entry Point

```csharp
using Siemens.Engineering.AddIn;
using Siemens.Engineering.AddIn.Menu;

// View Provider - determines where the context menu appears
[ExportAddInProvider(typeof(BulkChangeAddInProvider))]
public class BulkChangeAddInProvider : ProjectTreeAddInProvider
{
    protected override MenuSelectionProvider GetSelection()
    {
        return new MenuSelectionProvider(new BulkChangeContextMenu());
    }
}

// Context Menu Implementation
public class BulkChangeContextMenu : ContextMenuAddIn
{
    public BulkChangeContextMenu() : base("Bulk Change Tool") { }

    protected override void BuildContextMenuItems(
        ContextMenuAddInRoot addInRootSubmenu)
    {
        addInRootSubmenu.Items.AddActionItem<IEngineeringObject>(
            "Modify Start Values",
            OnClick,
            OnUpdateStatus,
            OnError);
    }

    private void OnClick(MenuSelectionProvider<IEngineeringObject> provider)
    {
        // Access selected objects and perform operations
    }

    private MenuStatus OnUpdateStatus(
        MenuSelectionProvider<IEngineeringObject> provider)
    {
        return MenuStatus.Enabled;
    }

    private void OnError(Exception ex)
    {
        AddInMessageBox.Show(ex.ToString(), "Error",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
```

## Pattern 2: Iterate All Data Blocks in Project

```csharp
using Siemens.Engineering.SW.Blocks;

void ProcessAllDataBlocks(PlcBlockGroup group, List<DataBlock> result)
{
    foreach (PlcBlock block in group.Blocks)
    {
        if (block is DataBlock dataBlock)
        {
            result.Add(dataBlock);
        }
    }
    foreach (PlcBlockGroup subGroup in group.Groups)
    {
        ProcessAllDataBlocks(subGroup, result);
    }
}

// Usage:
var allDBs = new List<DataBlock>();
ProcessAllDataBlocks(plcSoftware.BlockGroup, allDBs);

// Or using Openness.Extensions:
IEnumerable<PlcBlock> allBlocks = plcSoftware.AllBlocks();
var allDBs = allBlocks.OfType<DataBlock>().ToList();
```

## Pattern 3: Export DB, Modify StartValues, Reimport

```csharp
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using System.Xml.Linq;

// 1. Export the data block
DataBlock db = plcSoftware.BlockGroup.Blocks.Find("MyDB") as DataBlock;
string exportPath = Path.Combine(Path.GetTempPath(), $"{db.Name}.xml");
db.Export(new FileInfo(exportPath), ExportOptions.WithDefaults);

// 2. Load and modify the XML
XDocument doc = XDocument.Load(exportPath);
XNamespace ns = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

var members = doc.Descendants(ns + "Member");
foreach (var member in members)
{
    if (member.Attribute("Name")?.Value == "Speed")
    {
        var startValue = member.Element(ns + "StartValue");
        if (startValue != null)
            startValue.Value = "2000";
    }
}

// 3. Save modified XML
string modifiedPath = Path.Combine(Path.GetTempPath(), $"{db.Name}_modified.xml");
doc.Save(modifiedPath);

// 4. Reimport (Override replaces the existing block)
plcSoftware.BlockGroup.Blocks.Import(
    new FileInfo(modifiedPath), ImportOptions.Override);
```

## Pattern 4: Direct API Access (Without XML Export)

```csharp
// Reading a start value from a member
object startValue = member.GetAttribute("StartValue");

// Writing/modifying a start value
member.SetAttribute("StartValue", "100");
```

## Pattern 5: Bulk Modify StartValues Across Multiple DBs

```csharp
void BulkModifyStartValues(
    PlcSoftware plcSoftware,
    string memberName,
    string newStartValue,
    Func<DataBlock, bool> dbFilter = null)
{
    var allDBs = new List<DataBlock>();
    CollectDataBlocks(plcSoftware.BlockGroup, allDBs);

    string tempDir = Path.Combine(Path.GetTempPath(), "BulkChange");
    Directory.CreateDirectory(tempDir);

    XNamespace ns = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

    foreach (var db in allDBs)
    {
        if (dbFilter != null && !dbFilter(db))
            continue;

        string exportPath = Path.Combine(tempDir, $"{db.Name}.xml");
        db.Export(new FileInfo(exportPath), ExportOptions.WithDefaults);

        XDocument doc = XDocument.Load(exportPath);
        bool modified = false;

        foreach (var member in doc.Descendants(ns + "Member"))
        {
            if (member.Attribute("Name")?.Value == memberName)
            {
                var sv = member.Element(ns + "StartValue");
                if (sv != null)
                {
                    sv.Value = newStartValue;
                    modified = true;
                }
            }
        }

        if (modified)
        {
            doc.Save(exportPath);
            // Find parent group for reimport
            var parentGroup = db.Parent as PlcBlockGroup
                ?? plcSoftware.BlockGroup;
            parentGroup.Blocks.Import(
                new FileInfo(exportPath), ImportOptions.Override);
        }
    }
}
```

## Pattern 6: Analyze DB Structure (Build Tree View)

```csharp
class MemberInfo
{
    public string Name { get; set; }
    public string Datatype { get; set; }
    public string StartValue { get; set; }
    public string Path { get; set; }
    public List<MemberInfo> Children { get; set; } = new();
}

List<MemberInfo> ParseMembers(XElement parent, XNamespace ns, string pathPrefix = "")
{
    var result = new List<MemberInfo>();

    foreach (var member in parent.Elements(ns + "Member"))
    {
        string name = member.Attribute("Name")?.Value ?? "";
        string path = string.IsNullOrEmpty(pathPrefix) ? name : $"{pathPrefix}.{name}";

        var info = new MemberInfo
        {
            Name = name,
            Datatype = member.Attribute("Datatype")?.Value ?? "",
            StartValue = member.Element(ns + "StartValue")?.Value ?? "",
            Path = path,
            Children = ParseMembers(member, ns, path)
        };

        result.Add(info);
    }

    return result;
}
```

## Key References
- [Siemens Openness Code Snippets (GitHub)](https://github.com/siemens/tia-portal-openness-code-snippets)
- [CodeGeneratorOpenness (GitHub)](https://github.com/mking2203/CodeGeneratorOpenness)
- [TiaExportBlocks (GitHub)](https://github.com/cezar1/TiaExportBlocks)
- [TiaUtilities (GitHub)](https://github.com/Parozzz/TiaUtilities)
