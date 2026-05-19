using System.Globalization;
using System.IO;
using BlockParam.Diagnostics;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW.Blocks;

namespace BlockParam.Services;

/// <summary>
/// Implements ITiaPortalAdapter using the real TIA Portal Openness API.
/// </summary>
public class TiaPortalAdapter : ITiaPortalAdapter
{
    private readonly TiaPortal _tiaPortal;

    public TiaPortalAdapter(TiaPortal tiaPortal)
    {
        _tiaPortal = tiaPortal;
    }

    public void CompileBlock(object dataBlock)
    {
        var block = (PlcBlock)dataBlock;

        // Try GetService<ICompilable> on the block itself
        var compilable = block.GetService<ICompilable>();
        if (compilable != null)
        {
            var result = compilable.Compile();
            Log.Information("Compiled {Block}: {State}", block.Name, result.State);
            return;
        }

        // Fallback: compile via parent group
        var group = block.Parent as PlcBlockGroup;
        if (group != null)
        {
            var groupCompilable = group.GetService<ICompilable>();
            if (groupCompilable != null)
            {
                var result = groupCompilable.Compile();
                Log.Information("Compiled group for {Block}: {State}", block.Name, result.State);
                return;
            }
        }

        Log.Warning("No ICompilable service found for {Block}", block.Name);
    }

    public string ExportBlock(object dataBlock, string targetDirectory)
    {
        var block = (DataBlock)dataBlock;
        Directory.CreateDirectory(targetDirectory);

        var filePath = Path.Combine(targetDirectory, $"{block.Name}.xml");
        if (File.Exists(filePath))
            File.Delete(filePath);
        block.Export(new FileInfo(filePath), ExportOptions.WithDefaults);
        return filePath;
    }

    public void ImportBlock(object blockGroup, string xmlPath)
    {
        var group = (PlcBlockGroup)blockGroup;
        group.Blocks.Import(new FileInfo(xmlPath), ImportOptions.Override);
    }

    public string BackupBlock(object dataBlock, string backupDirectory)
    {
        var block = (DataBlock)dataBlock;
        var backupDir = Path.Combine(backupDirectory, "backup");
        Directory.CreateDirectory(backupDir);

        var backupPath = Path.Combine(backupDir, $"{block.Name}_backup_{DateTime.Now:yyyyMMdd_HHmmss}.xml");
        block.Export(new FileInfo(backupPath), ExportOptions.WithDefaults);
        return backupPath;
    }

    public void RestoreFromBackup(object blockGroup, string backupPath)
    {
        var group = (PlcBlockGroup)blockGroup;
        group.Blocks.Import(new FileInfo(backupPath), ImportOptions.Override);
    }

    public IDisposable? AcquireExclusiveAccess(string description)
    {
        return _tiaPortal.ExclusiveAccess(description);
    }

    /// <summary>
    /// Sets a member's StartValue via the Openness Direct API. Callers must
    /// NOT pass an empty/whitespace value: SetAttribute("StartValue", "")
    /// does not revert to default. Clears are routed to the XML strategy by
    /// BulkChangeService.RecommendStrategy (issue #142).
    /// </summary>
    public void SetStartValueDirect(object member, string value)
    {
        var engineeringObject = (IEngineeringObject)member;
        engineeringObject.SetAttribute("StartValue", value);
    }

    public string? TryGetModifiedToken(object dataBlock)
    {
        // Defensive on purpose (mirrors UdtCacheRefresher's ModifiedDate
        // handling): any failure to read a usable timestamp returns null so
        // ActiveDbFactory falls back to an unconditional re-export rather than
        // risk serving a stale cached parse. UTC round-trip ("o") keeps the
        // token stable across DST, matching the GetLastWriteTime UTC fix.
        try
        {
            var raw = ((DataBlock)dataBlock).GetAttribute("ModifiedDate");
            if (raw == null) return null;
            if (raw is DateTime dt)
                return dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            return Convert.ToDateTime(raw, CultureInfo.InvariantCulture)
                .ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read ModifiedDate for DB — export cache disabled for this open");
            return null;
        }
    }

    public string GetBlockName(object dataBlock)
    {
        return ((DataBlock)dataBlock).Name;
    }

    public object GetBlockGroup(object dataBlock)
    {
        var block = (PlcBlock)dataBlock;
        return block.Parent;
    }
}
