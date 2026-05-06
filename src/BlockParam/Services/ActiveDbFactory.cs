using System.IO;
using Siemens.Engineering.SW.Blocks;
using BlockParam.Diagnostics;
using BlockParam.SimaticML;
using BlockParam.UI;

namespace BlockParam.Services;

/// <summary>
/// Builds an <see cref="ActiveDb"/> for one DataBlock — exports it via
/// <see cref="IBlockExporter"/>, parses the SimaticML, and wires up the OnApply
/// closure that re-imports the modified XML and refreshes the post-import live
/// reference.
///
/// Replaces the inline closure-chains in <see cref="BlockParam.AddIn.BulkChangeContextMenu"/>
/// (#81) — both the focused-DB factory shape and the companion-DB factory used
/// to live there as ~120 lines of nested lambdas.
/// </summary>
public interface IActiveDbFactory
{
    /// <summary>
    /// Exports + parses <paramref name="initialSelection"/> and returns a fully
    /// wired <see cref="ActiveDb"/>. The returned object's
    /// <see cref="ActiveDb.OnApply"/> closure tracks the live DataBlock instance
    /// across re-imports (TIA's <c>ImportBlock(Override)</c> disposes the old
    /// reference, #19).
    ///
    /// Returns null if the initial export fails — typically because the user
    /// declined the "compile inconsistent block" prompt. The dialog opens
    /// without that DB.
    /// </summary>
    ActiveDb? Build(DataBlock initialSelection, string plcName);
}

public sealed class ActiveDbFactory : IActiveDbFactory
{
    private readonly IBlockExporter _exporter;
    private readonly ITiaPortalAdapter _adapter;
    private readonly string _tempDir;
    private readonly IConstantResolver? _constantResolver;
    private readonly UdtSetPointResolver _udtResolver;
    private readonly UdtCommentResolver _commentResolver;

    public ActiveDbFactory(
        IBlockExporter exporter,
        ITiaPortalAdapter adapter,
        string tempDir,
        IConstantResolver? constantResolver,
        UdtSetPointResolver udtResolver,
        UdtCommentResolver commentResolver)
    {
        _exporter = exporter;
        _adapter = adapter;
        _tempDir = tempDir;
        _constantResolver = constantResolver;
        _udtResolver = udtResolver;
        _commentResolver = commentResolver;
    }

    public ActiveDb? Build(DataBlock initialSelection, string plcName)
    {
        // TIA's ImportBlock(Override) disposes the previous DataBlock instance
        // on every Apply, so we re-resolve after each import. The captured
        // local is shared across both lambdas via the closure, so the
        // re-assignment is visible on the next Apply call (#19).
        DataBlock liveDb = initialSelection;

        string xmlPath = null!;
        if (!_exporter.TryExportWithCompilePrompt(liveDb,
                () => xmlPath = _adapter.ExportBlock(liveDb, _tempDir)))
        {
            Log.Information("DB skipped (user declined compile): {DbName}", initialSelection.Name);
            return null;
        }
        var xml = File.ReadAllText(xmlPath);

        var parser = new SimaticMLParser(_constantResolver, _udtResolver, _commentResolver);
        var info = parser.Parse(xml);
        if (info.UnresolvedUdts.Count > 0)
        {
            Log.Information("DB {Name} references {Count} UDT(s) not in cache: {Types}",
                info.Name, info.UnresolvedUdts.Count, string.Join(", ", info.UnresolvedUdts));
        }
        Log.Information("Parsed DB {Name}: {MemberCount} top-level members, {TotalCount} total",
            info.Name, info.Members.Count, info.AllMembers().Count());

        Action<string> onApply = modifiedXml =>
        {
            Log.Information("Apply: writing modified XML for {DbName}", info.Name);

            // BackupBlock also exports; if a previous import left the block
            // inconsistent (#19) the same compile-prompt path catches it here.
            if (!_exporter.TryExportWithCompilePrompt(liveDb,
                    () => _adapter.BackupBlock(liveDb, _tempDir)))
            {
                Log.Information("Apply cancelled: user declined compile for {DbName}", info.Name);
                throw new OperationCanceledException(
                    "User declined to compile the inconsistent block.");
            }

            var modifiedPath = Path.Combine(_tempDir,
                $"{SafeFileName.Sanitize(info.Name)}_modified.xml");
            File.WriteAllText(modifiedPath, modifiedXml);
            var blockGroup = (PlcBlockGroup)_adapter.GetBlockGroup(liveDb);
            _adapter.ImportBlock(blockGroup, modifiedPath);

            var fresh = blockGroup.Blocks.Find(info.Name) as DataBlock;
            if (fresh != null)
            {
                liveDb = fresh;
            }
            else
            {
                Log.Warning(
                    "Could not re-resolve DataBlock '{DbName}' after import — next Apply may fail",
                    info.Name);
            }
            Log.Information("Import completed for {DbName}", info.Name);
        };

        return new ActiveDb(info, xml, onApply, plcName: plcName);
    }
}
