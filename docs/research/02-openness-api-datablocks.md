# Openness API for Data Block Manipulation

## Key Namespaces

- `Siemens.Engineering` - core TIA Portal access (`TiaPortal`, `Project`)
- `Siemens.Engineering.HW` - hardware/device access (`Device`, `DeviceItem`)
- `Siemens.Engineering.SW.Blocks` - block manipulation (`PlcBlock`, `DataBlock`, `PlcBlockGroup`)
- `Siemens.Engineering.SW.ExternalSources` - source generation/import

## Object Hierarchy

```
TiaPortal instance
  -> Project
    -> Devices -> DeviceItems
      -> SoftwareContainer -> PlcSoftware
        -> BlockGroup (PlcBlockGroup)
          -> Blocks (PlcBlockComposition) -- contains PlcBlock/DataBlock instances
          -> Groups (PlcBlockUserGroupComposition) -- nested subgroups
```

## Connecting to TIA Portal (Standalone Openness App)

```csharp
foreach (TiaPortalProcess tiaPortalProcess in TiaPortal.GetProcesses())
{
    TiaPortal tiaPortal = tiaPortalProcess.Attach();
    Project project = tiaPortal.Projects[0];
}
```

> Note: In an Add-In context, the `TiaPortal` and `Project` are available via
> the `MenuSelectionProvider` - no manual connection needed.

## Accessing PlcSoftware

```csharp
// Direct approach
SoftwareContainer softwareContainer =
    ((IEngineeringServiceProvider)deviceItem).GetService<SoftwareContainer>();
PlcSoftware plcSoftware = softwareContainer.Software as PlcSoftware;

// Using Openness.Extensions NuGet package
IEnumerable<PlcSoftware> plcs = project.AllPlcSoftwares();
```

## Iterating Through Blocks (Recursive)

```csharp
void HandleBlockGroup(PlcBlockGroup blockGroup)
{
    foreach (PlcBlock block in blockGroup.Blocks)
    {
        if (block is DataBlock db)
        {
            // Process data block
        }
    }
    foreach (PlcBlockGroup nestedGroup in blockGroup.Groups)
    {
        HandleBlockGroup(nestedGroup);
    }
}

// Entry point:
HandleBlockGroup(plcSoftware.BlockGroup);

// Or using extensions:
IEnumerable<PlcBlock> allBlocks = plcSoftware.AllBlocks();
```

## Two Approaches for Modifying Start Values

### Approach A: Direct API (GetAttribute/SetAttribute)

Documented in V20 Openness docs: "Accessing DB value parameter without export".

```csharp
// Reading a start value
object startValue = member.GetAttribute("StartValue");

// Writing a start value
member.SetAttribute("StartValue", "100");
```

**Pros**: No file I/O, faster for single changes
**Cons**: Requires navigating the member hierarchy programmatically

### Approach B: XML Export/Import (SimaticML)

```csharp
// Export
PlcBlock block = plcSoftware.BlockGroup.Blocks.Find("MyDB");
block.Export(new FileInfo(@"C:\export\MyDB.xml"), ExportOptions.WithDefaults);

// Modify XML (see 03-simaticml-xml-format.md)

// Reimport
plcSoftware.BlockGroup.Blocks.Import(
    new FileInfo(@"C:\export\MyDB_modified.xml"),
    ImportOptions.Override);
```

**Pros**: Batch modification of many values at once, full structural view
**Cons**: File I/O overhead, XML must match current interface definition

### Recommendation for Bulk Changes

For bulk operations across many DBs, the **XML Export/Import approach** is likely
more practical:
1. Export all target DBs to XML
2. Parse and display structure to user
3. Apply bulk modifications to XML
4. Reimport modified XMLs

For single-DB quick edits, the **direct API** may be simpler.

## ExclusiveAccess for Bulk Performance

When making many API calls (bulk edits), wrap operations in `ExclusiveAccess`
to reduce system overhead (no undo stack, no UI updates, no system events per call):

```csharp
using (ExclusiveAccess exclusiveAccess = tiaPortal.ExclusiveAccess("Bulk editing..."))
{
    // All modifications here run with reduced overhead
    foreach (var db in targetDataBlocks)
    {
        // Export, modify, reimport
    }
}
```

This is **critical** for our bulk-change use case.

## Important Limitations

- XML reimport requires the interface structure to match the current definition
- Deleting/reimporting blocks may reset other properties not in the XML
- `ImportOptions.Override` replaces the existing block entirely
- Compile after import may be necessary
- Windows user must be member of the `Siemens TIA Openness` Windows group

## Key References
- [Openness: Accessing DB Values Without Export (V20)](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/accessing-db-value-parameter-without-export)
- [Openness: Exporting Blocks](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/exporting-blocks)
- [Openness: Object Model](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/tia-portal-openness-object/blocks-and-types-of-the-tia-portal-openness-object-model)
- [Siemens Openness Code Snippets (GitHub)](https://github.com/siemens/tia-portal-openness-code-snippets)
