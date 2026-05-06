using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using BlockParam.Diagnostics;
using BlockParam.Models;

namespace BlockParam.Services;

/// <summary>
/// Walks a TIA Portal project's device / PLC / block tree to surface the inputs
/// that the BulkChange context menu needs: the live <see cref="PlcSoftware"/>
/// for a right-clicked DB, every PLC in the project, every DB on every PLC, and
/// reverse lookups for the in-dialog DB-switcher (#59).
///
/// Pulled out of <see cref="BlockParam.AddIn.BulkChangeContextMenu"/> in #81 so
/// the entry-point class no longer mixes tree traversal with VM construction —
/// each cross-PLC bug we hit (e.g. <c>displayPlcName</c> getting passed where
/// <c>summary.PlcName</c> should have been used) traced back to that tangle.
/// All TIA-tree walks now live behind <see cref="IProjectDiscovery"/> so callers
/// can stub them in tests.
/// </summary>
public interface IProjectDiscovery
{
    /// <summary>
    /// Walks <paramref name="block"/>'s parent chain to locate the owning
    /// <see cref="PlcSoftware"/>. Returns null if the block is detached
    /// (defensive: TIA shouldn't surface detached blocks via the context menu,
    /// but DevLauncher fixtures can).
    /// </summary>
    PlcSoftware? FindPlcSoftware(DataBlock block);

    /// <summary>
    /// Yields every (PlcSoftware, displayName) pair in the project. Per-item
    /// failures are swallowed so one mis-configured device cannot hide the rest.
    /// </summary>
    IEnumerable<(PlcSoftware plc, string name)> EnumerateAllPlcSoftwares(Project project);

    /// <summary>
    /// Counts <see cref="PlcSoftware"/> instances in <paramref name="project"/>.
    /// Drives the DB-switcher header decision: single-PLC projects (≈85% of
    /// users) suppress the redundant PLC prefix in chip headers (#59 follow-up).
    /// </summary>
    int CountPlcSoftwares(Project? project);

    /// <summary>
    /// Looks up a <see cref="PlcSoftware"/> by its display name. Used by the
    /// switch-DB / multi-DB add paths to resolve a <see cref="DataBlockSummary"/>
    /// against its owning PLC, not the dialog's launch PLC (#58 cross-PLC).
    /// </summary>
    PlcSoftware? FindPlcSoftwareByName(Project project, string plcName);

    /// <summary>
    /// Project-wide DB enumeration for the in-dialog DB-switcher dropdown
    /// (#59). Lazy + on-demand: only invoked when the user opens the dropdown,
    /// then cached for the dialog session by the VM.
    /// </summary>
    IReadOnlyList<DataBlockSummary> EnumerateDataBlocks(Project? project);

    /// <summary>
    /// Resolves a <see cref="DataBlockSummary"/> back to the live
    /// <see cref="DataBlock"/> on the given PLC. Folder path is part of the
    /// match so two DBs with the same name in different folders don't collide.
    /// Returns null if the DB has been deleted / renamed since the dropdown was
    /// populated.
    /// </summary>
    DataBlock? ResolveDataBlock(PlcSoftware plcSoftware, DataBlockSummary summary);
}

/// <summary>
/// Production <see cref="IProjectDiscovery"/> against the live TIA Openness API.
/// </summary>
public sealed class ProjectDiscovery : IProjectDiscovery
{
    public PlcSoftware? FindPlcSoftware(DataBlock block)
    {
        IEngineeringObject? current = block;
        while (current != null)
        {
            if (current is PlcSoftware plc) return plc;
            current = current.Parent;
        }
        return null;
    }

    public IEnumerable<(PlcSoftware plc, string name)> EnumerateAllPlcSoftwares(Project project)
    {
        foreach (Device device in project.Devices)
        {
            foreach (var item in EnumerateDeviceItemsRecursive(device.DeviceItems))
            {
                PlcSoftware? plc = null;
                try { plc = item.GetService<SoftwareContainer>()?.Software as PlcSoftware; }
                catch { /* skip silently; next device item may still yield a PLC */ }
                if (plc != null) yield return (plc, SafeGetPlcName(plc));
            }
        }
    }

    public int CountPlcSoftwares(Project? project)
    {
        if (project == null) return 0;
        int count = 0;
        try
        {
            foreach (Device device in project.Devices)
            {
                foreach (var item in EnumerateDeviceItemsRecursive(device.DeviceItems))
                {
                    try
                    {
                        var container = item.GetService<SoftwareContainer>();
                        if (container?.Software is PlcSoftware) count++;
                    }
                    catch { /* per-item failures shouldn't bring the whole walk down */ }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PLC count walk failed; suppressing PLC prefix");
            return 1;
        }
        return count;
    }

    public PlcSoftware? FindPlcSoftwareByName(Project project, string plcName)
    {
        foreach (var (plc, name) in EnumerateAllPlcSoftwares(project))
        {
            if (string.Equals(name, plcName, StringComparison.Ordinal)) return plc;
        }
        return null;
    }

    public IReadOnlyList<DataBlockSummary> EnumerateDataBlocks(Project? project)
    {
        var list = new List<DataBlockSummary>();
        if (project == null) return list;
        foreach (var (plc, plcName) in EnumerateAllPlcSoftwares(project))
        {
            foreach (var (db, folderPath) in EnumerateDataBlocksRecursive(plc.BlockGroup, parentPath: null))
            {
                bool isInstance = false;
                try { isInstance = db.GetType().Name.IndexOf("Instance", StringComparison.OrdinalIgnoreCase) >= 0; }
                catch { /* type-name probe is best-effort — mislabeled badge is harmless */ }
                int? dbNumber = null;
                try { dbNumber = db.Number; }
                catch { /* freshly-created blocks may not have a number assigned yet */ }
                list.Add(new DataBlockSummary(
                    db.Name,
                    folderPath ?? "",
                    blockType: isInstance ? "InstanceDB" : "GlobalDB",
                    isInstanceDb: isInstance,
                    plcName: plcName,
                    number: dbNumber));
            }
        }
        Log.Information("DB enumeration: {Count} block(s) across project", list.Count);
        return list;
    }

    public DataBlock? ResolveDataBlock(PlcSoftware plcSoftware, DataBlockSummary summary)
    {
        foreach (var (db, folderPath) in EnumerateDataBlocksRecursive(plcSoftware.BlockGroup, parentPath: null))
        {
            if (string.Equals(db.Name, summary.Name, StringComparison.Ordinal)
                && string.Equals(folderPath ?? "", summary.FolderPath ?? "", StringComparison.Ordinal))
            {
                return db;
            }
        }
        return null;
    }

    internal static string SafeGetPlcName(PlcSoftware plc)
    {
        try { return plc.Name ?? ""; }
        catch { return ""; }
    }

    private static IEnumerable<DeviceItem> EnumerateDeviceItemsRecursive(DeviceItemComposition items)
    {
        foreach (DeviceItem item in items)
        {
            yield return item;
            foreach (var child in EnumerateDeviceItemsRecursive(item.DeviceItems))
                yield return child;
        }
    }

    private static IEnumerable<(DataBlock db, string? groupPath)> EnumerateDataBlocksRecursive(
        PlcBlockGroup group, string? parentPath)
    {
        foreach (var block in group.Blocks)
        {
            if (block is DataBlock db) yield return (db, parentPath);
        }
        foreach (var sub in group.Groups)
        {
            var subPath = parentPath == null ? sub.Name : $"{parentPath}/{sub.Name}";
            foreach (var entry in EnumerateDataBlocksRecursive(sub, subPath))
                yield return entry;
        }
    }
}
