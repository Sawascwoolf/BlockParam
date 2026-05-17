// DEV-ONLY scenario runner — must never be merged to main / shipped.
// Intentionally lives on branch chore/open-timing-instrumentation only.
// Release builds on this branch deploy fine; the menu is stripped at merge time
// by keeping this branch off main forever.

using System.Diagnostics;
using Siemens.Engineering;
using Siemens.Engineering.AddIn.Menu;
using Siemens.Engineering.SW.Blocks;
using BlockParam.Diagnostics;
using BlockParam.Models;
using BlockParam.Services;
using BlockParam.SimaticML;

namespace BlockParam.AddIn;

/// <summary>
/// Adds a "BlockParam ▸ Run diagnostics scenarios" context-menu entry to the
/// TIA Portal project tree. Visible on DataBlock selection; runs a scripted
/// sequence of Openness operations per selected DB, timing and logging each
/// step as machine-parseable SCENARIO lines.
///
/// Strings: this menu never ships and is never localised — plain string constants
/// are used deliberately. Do NOT add Strings.resx keys for these strings; they
/// would then have to be kept out of the shipping resources.
/// </summary>
public sealed class DiagnosticsScenarioMenu : ContextMenuAddIn
{
    // Caption used for the TIA context-menu submenu root (must be non-null).
    // This must differ from BulkChangeAddInProviderInfo.MenuTitle so TIA keeps
    // the two sub-menus separate.
    private const string SubmenuTitle = "BlockParam Diagnostics";

    // Caption shown on the single action item inside the submenu.
    private const string ActionCaption = "Run diagnostics scenarios";

    // Machine-parseable prefix for all structured log lines this runner emits.
    private const string ScenarioTag = "SCENARIO";

    // Sentinel value written in place of the original during set_export_restore.
    private const string SentinelValue = "42";

    private readonly TiaPortal _tiaPortal;

    public DiagnosticsScenarioMenu(TiaPortal tiaPortal) : base(SubmenuTitle)
    {
        _tiaPortal = tiaPortal;
    }

    protected override void BuildContextMenuItems(ContextMenuAddInRoot addInRootSubmenu)
    {
        // Build path must be trivial (<200 ms per CLAUDE.md). No I/O here.
        addInRootSubmenu.Items.AddActionItem<IEngineeringObject>(
            ActionCaption,
            OnClick,
            OnUpdateStatus);
    }

    // ---------------------------------------------------------------------------
    // Data-driven scenario list — add new permutations here, not in menu code.
    // ---------------------------------------------------------------------------
    private static readonly IReadOnlyList<ScenarioDef> Scenarios = new[]
    {
        new ScenarioDef("export_twice",         RunExportTwice),
        new ScenarioDef("set_export_restore",   RunSetExportRestore),
        new ScenarioDef("clear_export_restore", RunClearExportRestore),
    };

    // ---------------------------------------------------------------------------
    // Menu callbacks
    // ---------------------------------------------------------------------------

    private void OnClick(MenuSelectionProvider<IEngineeringObject> provider)
    {
        // Synchronous on TIA's thread — blocking is expected for a dev tool.
        // Do NOT try to solve the #146 threading problem here.
        try
        {
            var selectedBlocks = provider.GetSelection<DataBlock>().ToList();
            if (selectedBlocks.Count == 0) return;

            Log.Information("{Tag} === Diagnostic scenario run started: {Count} DB(s) selected ===",
                ScenarioTag, selectedBlocks.Count);

            var project    = _tiaPortal.Projects.FirstOrDefault();
            var discovery  = new ProjectDiscovery();
            var adapter    = new TiaPortalAdapter(_tiaPortal);
            var prompt     = new SilentUserPrompt();          // never blocks on a headless run
            var exporter   = new BlockExporter(adapter, prompt);
            var scope      = ProjectScope.ForPath(project?.Path?.FullName);
            var tempDir    = AppDirectories.TempScope(scope);
            var writer     = new SimaticMLWriter();

            foreach (var db in selectedBlocks)
            {
                RunScenariosForDb(db, discovery, adapter, exporter, writer, tempDir);
            }

            Log.Information("{Tag} === Diagnostic scenario run complete ===", ScenarioTag);
        }
        catch (Exception ex)
        {
            // Top-level guard: never throw out of a TIA menu callback.
            Log.Error(ex, "{Tag} Unhandled exception in scenario runner top-level", ScenarioTag);
        }
    }

    private MenuStatus OnUpdateStatus(MenuSelectionProvider<IEngineeringObject> provider)
        => provider.GetSelection<DataBlock>().Any() ? MenuStatus.Enabled : MenuStatus.Disabled;

    // ---------------------------------------------------------------------------
    // Per-DB dispatch
    // ---------------------------------------------------------------------------

    private void RunScenariosForDb(
        DataBlock db,
        ProjectDiscovery discovery,
        ITiaPortalAdapter adapter,
        IBlockExporter exporter,
        SimaticMLWriter writer,
        string tempDir)
    {
        var dbName  = db.Name;
        var plcSw   = discovery.FindPlcSoftware(db);
        var plcName = plcSw != null ? ProjectDiscovery.SafeGetPlcName(plcSw) : "";

        Log.Information("{Tag} --- starting scenarios for db={DbName} plc={Plc} ---",
            ScenarioTag, dbName, plcName);

        var ctx = new ScenarioContext(db, dbName, plcName, adapter, exporter, writer, tempDir);

        foreach (var scenario in Scenarios)
        {
            try
            {
                scenario.Run(ctx);
            }
            catch (Exception ex)
            {
                Log.Error(ex,
                    "{Tag} scenario={Scenario} db={Db} plc={Plc} result=fail",
                    ScenarioTag, scenario.Name, dbName, plcName);
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Scenario implementations (static — no state, all args through context)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// export_twice — export the block once, then export it again.
    /// This is the #140 evidence: is the second export faster (warm caches)?
    /// </summary>
    private static void RunExportTwice(ScenarioContext ctx)
    {
        // Step 1 — first export.
        string xml1 = "";
        var sw1 = Stopwatch.StartNew();
        try
        {
            ctx.Exporter.TryExportWithCompilePrompt(ctx.Db,
                () => xml1 = System.IO.File.ReadAllText(
                    ctx.Adapter.ExportBlock(ctx.Db, ctx.TempDir)));
            sw1.Stop();
            Log.Information(
                "{Tag} scenario=export_twice step=export1 db={Db} plc={Plc} ms={Ms} xmlBytes={Bytes}",
                ScenarioTag, ctx.DbName, ctx.PlcName, sw1.ElapsedMilliseconds, xml1.Length);
        }
        catch (Exception ex)
        {
            sw1.Stop();
            Log.Error(ex,
                "{Tag} scenario=export_twice step=export1 db={Db} plc={Plc} ms={Ms} result=fail",
                ScenarioTag, ctx.DbName, ctx.PlcName, sw1.ElapsedMilliseconds);
            return;
        }

        // Step 2 — second export.
        string xml2 = "";
        var sw2 = Stopwatch.StartNew();
        try
        {
            ctx.Exporter.TryExportWithCompilePrompt(ctx.Db,
                () => xml2 = System.IO.File.ReadAllText(
                    ctx.Adapter.ExportBlock(ctx.Db, ctx.TempDir)));
            sw2.Stop();
            Log.Information(
                "{Tag} scenario=export_twice step=export2 db={Db} plc={Plc} ms={Ms} xmlBytes={Bytes}",
                ScenarioTag, ctx.DbName, ctx.PlcName, sw2.ElapsedMilliseconds, xml2.Length);
        }
        catch (Exception ex)
        {
            sw2.Stop();
            Log.Error(ex,
                "{Tag} scenario=export_twice step=export2 db={Db} plc={Plc} ms={Ms} result=fail",
                ScenarioTag, ctx.DbName, ctx.PlcName, sw2.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// set_export_restore — capture original start value, set a sentinel, export,
    /// restore original, verify. Self-reverting so every run starts clean.
    /// </summary>
    private static void RunSetExportRestore(ScenarioContext ctx)
    {
        RunMutateExportRestore(ctx, "set_export_restore", SentinelValue);
    }

    /// <summary>
    /// clear_export_restore — capture original start value, clear it, export,
    /// restore original, verify. Self-reverting.
    /// </summary>
    private static void RunClearExportRestore(ScenarioContext ctx)
    {
        RunMutateExportRestore(ctx, "clear_export_restore", newValue: "");
    }

    /// <summary>
    /// Shared body for set_export_restore and clear_export_restore.
    /// Option-B safety: capture original → mutate → export → restore → verify.
    /// Uses the same XML export/import paths the product uses (no raw File.* outside
    /// those paths; no new path literals — all dirs come from AppDirectories via ctx).
    /// </summary>
    private static void RunMutateExportRestore(
        ScenarioContext ctx, string scenarioName, string newValue)
    {
        // --- Step 1: export to read current XML and pick target member ---
        string originalXml = "";
        try
        {
            var sw = Stopwatch.StartNew();
            ctx.Exporter.TryExportWithCompilePrompt(ctx.Db,
                () => originalXml = System.IO.File.ReadAllText(
                    ctx.Adapter.ExportBlock(ctx.Db, ctx.TempDir)));
            sw.Stop();
            Log.Information(
                "{Tag} scenario={Scenario} step=export_read db={Db} plc={Plc} ms={Ms} xmlBytes={Bytes}",
                ScenarioTag, scenarioName, ctx.DbName, ctx.PlcName,
                sw.ElapsedMilliseconds, originalXml.Length);
        }
        catch (Exception ex)
        {
            Log.Error(ex,
                "{Tag} scenario={Scenario} step=export_read db={Db} plc={Plc} result=fail",
                ScenarioTag, scenarioName, ctx.DbName, ctx.PlcName);
            return;
        }

        // --- Step 2: find first writable scalar leaf member ---
        DataBlockInfo info;
        try
        {
            var parser = new SimaticMLParser(null, new UdtSetPointResolver(), new UdtCommentResolver());
            info = parser.Parse(originalXml);
        }
        catch (Exception ex)
        {
            Log.Error(ex,
                "{Tag} scenario={Scenario} step=parse db={Db} plc={Plc} result=fail",
                ScenarioTag, scenarioName, ctx.DbName, ctx.PlcName);
            return;
        }

        // Pick the first top-level scalar leaf with a defined start value.
        // Deterministic: same member chosen on every run against the same DB.
        var target = info.Members.FirstOrDefault(m => m.IsLeaf && !m.IsArray);
        if (target == null)
        {
            Log.Warning(
                "{Tag} scenario={Scenario} db={Db} plc={Plc} result=skip reason=no_scalar_leaf",
                ScenarioTag, scenarioName, ctx.DbName, ctx.PlcName);
            return;
        }

        var originalValue = target.StartValue ?? "";
        Log.Information(
            "{Tag} scenario={Scenario} step=target_selected db={Db} plc={Plc} member={Member} originalValue={OrigVal}",
            ScenarioTag, scenarioName, ctx.DbName, ctx.PlcName, target.Path, originalValue);

        // --- Step 3: mutate XML in memory ---
        string mutatedXml;
        try
        {
            var result = ctx.Writer.ModifyStartValues(originalXml, new[] { target }, newValue);
            if (result.HasErrors)
            {
                Log.Warning(
                    "{Tag} scenario={Scenario} step=mutate db={Db} plc={Plc} result=fail errors={Errs}",
                    ScenarioTag, scenarioName, ctx.DbName, ctx.PlcName,
                    string.Join("; ", result.Errors));
                return;
            }
            mutatedXml = result.ModifiedXml;
        }
        catch (Exception ex)
        {
            Log.Error(ex,
                "{Tag} scenario={Scenario} step=mutate db={Db} plc={Plc} result=fail",
                ScenarioTag, scenarioName, ctx.DbName, ctx.PlcName);
            return;
        }

        // --- Step 4: import mutated XML back into TIA ---
        var blockGroup = ctx.Adapter.GetBlockGroup(ctx.Db);
        DataBlock liveDb = ctx.Db; // track live reference across import (#19)
        try
        {
            var mutatedPath = System.IO.Path.Combine(
                ctx.TempDir,
                $"{SafeFileName.Sanitize(ctx.DbName)}_scenario_mutated.xml");
            System.IO.File.WriteAllText(mutatedPath, mutatedXml);

            var sw = Stopwatch.StartNew();
            ctx.Adapter.ImportBlock(blockGroup, mutatedPath);
            sw.Stop();

            // Re-resolve live reference after import (TIA invalidates old handle, #19).
            var blockGroupTyped = (Siemens.Engineering.SW.Blocks.PlcBlockGroup)blockGroup;
            liveDb = blockGroupTyped.Blocks.Find(ctx.DbName) as DataBlock ?? ctx.Db;

            // Re-export to measure the post-mutate export time.
            string postMutateXml = "";
            var exportSw = Stopwatch.StartNew();
            ctx.Exporter.TryExportWithCompilePrompt(liveDb,
                () => postMutateXml = System.IO.File.ReadAllText(
                    ctx.Adapter.ExportBlock(liveDb, ctx.TempDir)));
            exportSw.Stop();

            Log.Information(
                "{Tag} scenario={Scenario} step=import_and_export db={Db} plc={Plc} importMs={IMs} exportMs={EMs} xmlBytes={Bytes}",
                ScenarioTag, scenarioName, ctx.DbName, ctx.PlcName,
                sw.ElapsedMilliseconds, exportSw.ElapsedMilliseconds, postMutateXml.Length);
        }
        catch (Exception ex)
        {
            Log.Error(ex,
                "{Tag} scenario={Scenario} step=import_and_export db={Db} plc={Plc} result=fail",
                ScenarioTag, scenarioName, ctx.DbName, ctx.PlcName);
            // Still attempt restore below — do not return early.
        }

        // --- Step 5: restore original XML (Option-B self-revert) ---
        bool restoreOk = false;
        try
        {
            var restorePath = System.IO.Path.Combine(
                ctx.TempDir,
                $"{SafeFileName.Sanitize(ctx.DbName)}_scenario_restore.xml");
            System.IO.File.WriteAllText(restorePath, originalXml);

            var sw = Stopwatch.StartNew();
            ctx.Adapter.ImportBlock(blockGroup, restorePath);
            sw.Stop();

            // Re-resolve after restore import.
            var blockGroupTyped = (Siemens.Engineering.SW.Blocks.PlcBlockGroup)blockGroup;
            liveDb = blockGroupTyped.Blocks.Find(ctx.DbName) as DataBlock ?? liveDb;

            // --- Step 6: verify restore by re-exporting and re-reading ---
            string verifyXml = "";
            ctx.Exporter.TryExportWithCompilePrompt(liveDb,
                () => verifyXml = System.IO.File.ReadAllText(
                    ctx.Adapter.ExportBlock(liveDb, ctx.TempDir)));

            if (!string.IsNullOrEmpty(verifyXml))
            {
                var verifyParser = new SimaticMLParser(null, new UdtSetPointResolver(), new UdtCommentResolver());
                var verifyInfo   = verifyParser.Parse(verifyXml);
                var verifyMember = verifyInfo.AllMembers()
                    .FirstOrDefault(m => m.Path == target.Path);
                var restoredValue = verifyMember?.StartValue ?? "";
                restoreOk = restoredValue == originalValue;

                if (!restoreOk)
                {
                    Log.Warning(
                        "{Tag} scenario={Scenario} step=verify db={Db} plc={Plc} member={Member} expected={Exp} got={Got} restoreOk=false",
                        ScenarioTag, scenarioName, ctx.DbName, ctx.PlcName,
                        target.Path, originalValue, restoredValue);
                }
            }

            Log.Information(
                "{Tag} scenario={Scenario} step=restore db={Db} plc={Plc} ms={Ms} restoreOk={Ok}",
                ScenarioTag, scenarioName, ctx.DbName, ctx.PlcName,
                sw.ElapsedMilliseconds, restoreOk);
        }
        catch (Exception ex)
        {
            Log.Error(ex,
                "{Tag} scenario={Scenario} step=restore db={Db} plc={Plc} restoreOk=false result=fail",
                ScenarioTag, scenarioName, ctx.DbName, ctx.PlcName);
        }
    }

    // ---------------------------------------------------------------------------
    // Supporting types (private — no public surface contamination)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Bundles the per-DB inputs so scenario methods have a clean signature
    /// without a long parameter list.
    /// </summary>
    private sealed class ScenarioContext
    {
        public ScenarioContext(
            DataBlock db,
            string dbName,
            string plcName,
            ITiaPortalAdapter adapter,
            IBlockExporter exporter,
            SimaticMLWriter writer,
            string tempDir)
        {
            Db       = db;
            DbName   = dbName;
            PlcName  = plcName;
            Adapter  = adapter;
            Exporter = exporter;
            Writer   = writer;
            TempDir  = tempDir;
        }

        public DataBlock         Db       { get; }
        public string            DbName   { get; }
        public string            PlcName  { get; }
        public ITiaPortalAdapter Adapter  { get; }
        public IBlockExporter    Exporter { get; }
        public SimaticMLWriter   Writer   { get; }
        public string            TempDir  { get; }
    }

    /// <summary>
    /// A named, runnable scenario entry in the data-driven list.
    /// </summary>
    private sealed class ScenarioDef
    {
        public ScenarioDef(string name, Action<ScenarioContext> run)
        {
            Name = name;
            _run = run;
        }

        public string Name { get; }
        private readonly Action<ScenarioContext> _run;

        public void Run(ScenarioContext ctx) => _run(ctx);
    }

    /// <summary>
    /// A no-op <see cref="IUserPrompt"/> for the scenario runner — we never want
    /// an interactive compile prompt blocking a scripted run. If a block is
    /// inconsistent the export action throws and the step is logged as failed.
    /// </summary>
    private sealed class SilentUserPrompt : IUserPrompt
    {
        public bool AskYesNo(string title, string message)
        {
            Log.Warning("{Tag} SilentUserPrompt: compile prompt suppressed ({Title})", ScenarioTag, title);
            return false;
        }

        public void ShowError(string title, string message)
        {
            Log.Warning("{Tag} SilentUserPrompt: error suppressed ({Title}): {Msg}", ScenarioTag, title, message);
        }
    }
}
