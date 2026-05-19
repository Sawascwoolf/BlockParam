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

## DB Consistency Detection (TIA-verified V20, 2026-05-19, issue #147)

Captured with the `#147` diagnostic probe (`InconsistencyProbe`, build
v0.147.0.0) against two real **inconsistent** `InstanceDB` blocks. The
make-or-break question for #147 was: *can a DB's inconsistency be detected
without triggering the export (which throws and, after the follow-up compile,
mutates the block)?* — **Yes.**

- **`block.GetAttribute("IsConsistent")` → `System.Boolean`. Non-throwing,
  non-mutating, ~2–4 ms.** Returned `False` for inconsistent DBs. This is the
  cheap up-front consistency probe; an aggregated single-prompt pre-scan over
  the whole multi-DB selection does **not** need a probe-export.
- Only `IsConsistent` is valid. `Consistent`, `IsConsistant`,
  `ConsistencyState`, `CompileState`, `CompilationState`, `IsCompiled`,
  `CompiledDate` all throw `EngineeringNotSupportedException`. Spelling trap:
  the date attribute is **`CompileDate`** (Read), not `CompiledDate`.
- Full `GetAttributeInfos()` surface of a V20 `InstanceDB` (27 attrs):
  `AssignedProDiagFB(RW)`, `AutoNumber(RW)`, `CodeModifiedDate(R)`,
  `CompileDate(R)`, `CreationDate(R)`, `DBAccessibleFromOPCUA(R)`,
  `DBAccessibleFromWebserver(RW)`, `HeaderAuthor(R)`, `HeaderFamily(R)`,
  `HeaderName(R)`, `HeaderVersion(R)`, `InstanceOfName(R)`,
  `InstanceOfNumber(R)`, `InstanceOfType(R)`, `InterfaceModifiedDate(R)`,
  **`IsConsistent(R)`**, `IsKnowHowProtected(R)`, `IsOnlyStoredInLoadMemory(R)`,
  `IsWriteProtectedInAS(R)`, `MemoryLayout(RW)`, `ModifiedDate(R)`, `Name(RW)`,
  `Namespace(R)`, `Number(RW)`, `ParameterModified(R)`,
  `ProgrammingLanguage(R)`, `StructureModified(R)`.
- **Exact inconsistent-export failure** (informs `InconsistencyDetector`):
  `block.Export(...)` throws `Siemens.Engineering.EngineeringTargetInvocationException`
  with message `Error when calling method 'Export' of type '...InstanceDB'.`
  + blank line + `Inconsistent blocks and PLC data types (UDT) cannot be
  exported.` It is **single-level** (no distinct InnerException) and writes
  **no partial file**. `InconsistencyDetector.Matches()` returns **True** for
  it — the existing en-US `"inconsistent"` marker is correct for real V20;
  keep it as the defensive fallback signal.
- `block.GetService<ICompilable>()` returns a non-null
  `Siemens.Engineering.Compiler.CompileProvider` **directly on the block** —
  the parent-`PlcBlockGroup` fallback in `TiaPortalAdapter.CompileBlock` is a
  safety net, not the normal path.
- **Not yet verified** (no TIA at write time): whether `IsConsistent` flips to
  `True` after `ICompilable.Compile()`; whether the attribute exists on plain
  global `DataBlock` / `ArrayDB` (only `InstanceDB` tested). A fix should fall
  back to the export-throw `InconsistencyDetector` path if the attribute read
  ever throws for an exotic subtype.

**#147 fix shape:** a small sibling service reads `IsConsistent` for the
selected DBs *before* the per-DB `Build` loop in `BulkChangeContextMenu`, then
shows **one** consolidated prompt (reuse `Db_InconsistentPrompt` /
`Udt_InconsistentPromptTitle` with a `{0}`-joined name list — no new resx key)
or one batch compile + summary. Expose it as a mockable `ITiaPortalAdapter`
member mirroring `TryGetModifiedToken` so DevLauncher/tests still work; leave
the per-DB `TryExportWithCompilePrompt` retry as the fallback for the single-DB
and Apply paths. (`BulkChangeContextMenu` is hotspot #81 and `ActiveDbFactory`
sits in #140's seam — add a sibling, don't enlarge either.)

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
