using System.IO;
using Siemens.Engineering.SW.Blocks;
using BlockParam.Diagnostics;
using BlockParam.Models;
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
/// (#81) — every active DB is built through the same factory now; before
/// extraction this lived there as ~120 lines of nested lambdas.
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
    ///
    /// <paramref name="forceRefresh"/> bypasses the export XML cache (#140) and
    /// always re-exports from TIA Openness.
    /// </summary>
    ActiveDb? Build(DataBlock initialSelection, string plcName, bool forceRefresh = false);
}

public sealed class ActiveDbFactory : IActiveDbFactory
{
    private readonly IBlockExporter _exporter;
    private readonly ITiaPortalAdapter _adapter;
    private readonly string _tempDir;
    private readonly IConstantResolver? _constantResolver;
    private readonly UdtSetPointResolver _udtResolver;
    private readonly UdtCommentResolver _commentResolver;
    private readonly IDbExportCache _cache;
    private readonly string _projectScope;

    public ActiveDbFactory(
        IBlockExporter exporter,
        ITiaPortalAdapter adapter,
        string tempDir,
        IConstantResolver? constantResolver,
        UdtSetPointResolver udtResolver,
        UdtCommentResolver commentResolver,
        IDbExportCache cache,
        string projectScope)
    {
        _exporter = exporter;
        _adapter = adapter;
        _tempDir = tempDir;
        _constantResolver = constantResolver;
        _udtResolver = udtResolver;
        _commentResolver = commentResolver;
        _cache = cache;
        _projectScope = projectScope;
    }

    public ActiveDb? Build(DataBlock initialSelection, string plcName, bool forceRefresh = false)
    {
        // TIA's ImportBlock(Override) disposes the previous DataBlock instance
        // on every Apply, so we re-resolve after each import. The captured
        // local is shared across both lambdas via the closure, so the
        // re-assignment is visible on the next Apply call (#19).
        DataBlock liveDb = initialSelection;

        using var buildTimer = OpenTiming.Stage("build",
            $"db={initialSelection.Name} plc={plcName}");

        var cacheKey = DbExportCache.KeyFor(
            _projectScope, plcName, initialSelection.Name, initialSelection.Number);

        // Change-discriminator: a freshness token derived from the block's
        // Openness ModifiedDate. null => unreadable timestamp => cache disabled
        // for this open (always re-export) so a stale parse can never be served
        // (#140). Mirrors the UDT cache's ModifiedDate gating.
        var freshToken = _adapter.TryGetModifiedToken(initialSelection);
        var tokenReadable = freshToken != null;

        // Decide hit/miss/stale/disabled/forced via the pure policy so the
        // branching is unit-tested without TIA types; the predictor it yields
        // is the OPEN-TIMING verdict the TIA-required verification reads.
        string cachedXml = null!;
        var matching = tokenReadable
            && _cache.TryGet(cacheKey, freshToken!, out cachedXml);
        var outcome = DbExportCacheDecision.Decide(
            forceRefresh, tokenReadable, matching, _cache.HasEntry(cacheKey));
        buildTimer.AddPredictors(DbExportCacheDecision.Predictor(outcome));

        string xml;
        if (outcome == DbCacheOutcome.Hit)
        {
            xml = cachedXml;
            Log.Information("DB cache hit (skipping export+parse re-export) for {DbName}", initialSelection.Name);
        }
        else
        {
            string xmlPath = null!;
            using (var exportTimer = OpenTiming.Stage("export",
                $"db={initialSelection.Name} plc={plcName}"))
            {
                if (!_exporter.TryExportWithCompilePrompt(liveDb,
                        () => xmlPath = _adapter.ExportBlock(liveDb, _tempDir)))
                {
                    Log.Information("DB skipped (user declined compile): {DbName}", initialSelection.Name);
                    return null;
                }
            }

            using (var readTimer = OpenTiming.Stage("read",
                $"db={initialSelection.Name} plc={plcName}"))
            {
                xml = File.ReadAllText(xmlPath);
                readTimer.AddPredictors($"xmlBytes={xml.Length}");
            }

            // Only worth caching when we have a token to validate it against;
            // a tokenless entry (Disabled) could never produce a future hit.
            if (tokenReadable)
            {
                // Re-read the token AFTER export: an inconsistent block makes
                // TryExportWithCompilePrompt compile (mutate) it, so the token
                // captured at the top of Build no longer describes the state
                // this XML represents. Caching the pre-compile token would make
                // every subsequent unchanged open miss forever (next open reads
                // the post-compile ModifiedDate). Tag the entry with the token
                // for the state we actually exported; fall back to the pre-read
                // if the post-read fails so the entry stays valid (#140).
                var exportedToken = _adapter.TryGetModifiedToken(liveDb) ?? freshToken;
                _cache.Set(cacheKey, exportedToken!, xml);
            }
        }

        DataBlockInfo info;
        using (var parseTimer = OpenTiming.Stage("parse",
            $"db={initialSelection.Name} plc={plcName}"))
        {
            var parser = new SimaticMLParser(_constantResolver, _udtResolver, _commentResolver);
            info = parser.Parse(xml);
            var totalMembers = info.AllMembers().Count();
            parseTimer.AddPredictors($"topMembers={info.Members.Count} totalMembers={totalMembers}");
        }

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
            // The DB content changed — next open of this identity must re-export (#140).
            _cache.Invalidate(cacheKey);
            Log.Information("Import completed for {DbName}", info.Name);
        };

        return new ActiveDb(info, xml, onApply, plcName: plcName);
    }
}
