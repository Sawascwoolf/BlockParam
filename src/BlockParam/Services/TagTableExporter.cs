using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;
using BlockParam.Diagnostics;

namespace BlockParam.Services;

/// <summary>
/// Exports every <see cref="PlcTagTable"/> in a PLC to per-table XML files.
/// Used by the autocomplete / constant-resolver path: when the user requests
/// tag-table refresh, the cached XML on disk is the source of truth that
/// <see cref="TagTableConstantResolver"/> reads.
///
/// Walk is recursive (#63 fix): the previous implementation only handled root
/// + one subgroup level, silently dropping anything deeper — real customer
/// projects nest 4+ levels and the missing constants surfaced as ~200 false
/// "value out of range" inspector entries.
/// </summary>
public interface ITagTableExporter
{
    /// <summary>
    /// Wipes <paramref name="exportDir"/> of stale <c>*.xml</c> files, then
    /// exports every tag table in <paramref name="plcSoftware"/> to that
    /// directory. Returns the number of tables exported.
    /// </summary>
    int Export(PlcSoftware plcSoftware, string exportDir);
}

public sealed class TagTableExporter : ITagTableExporter
{
    public int Export(PlcSoftware plcSoftware, string exportDir)
    {
        Directory.CreateDirectory(exportDir);

        // Wipe stale exports from previous runs so renamed/moved/deleted tables
        // don't linger as ghost constants in the validator cache.
        foreach (var stale in Directory.GetFiles(exportDir, "*.xml"))
        {
            try { File.Delete(stale); }
            catch (Exception ex) { Log.Warning(ex, "Could not delete stale tag table cache file {Path}", stale); }
        }

        int count = 0;
        foreach (var table in EnumerateTagTablesRecursive(plcSoftware.TagTableGroup))
        {
            // TIA enforces tag-table name uniqueness across the PLC, so a flat
            // <table.Name>.xml layout has no collisions and lets rule references
            // by table name match the file regardless of nesting depth (#63).
            var filePath = Path.Combine(exportDir, $"{SafeFileName.Sanitize(table.Name)}.xml");
            table.Export(new FileInfo(filePath), ExportOptions.WithDefaults);
            count++;
        }
        return count;
    }

    private static IEnumerable<PlcTagTable> EnumerateTagTablesRecursive(PlcTagTableGroup group)
    {
        foreach (var table in group.TagTables)
            yield return table;

        foreach (var sub in group.Groups)
            foreach (var table in EnumerateTagTablesRecursive(sub))
                yield return table;
    }
}
