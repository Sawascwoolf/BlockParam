using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using BlockParam.Config;
using BlockParam.Licensing;
using BlockParam.Localization;
using BlockParam.Models;
using BlockParam.Services;
using BlockParam.SimaticML;
using BlockParam.UI;
using BlockParam.UI.Controls.PillMultiSelect;
using BlockParam.Updates;

namespace BlockParam.DevLauncher;

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Set up console + file logging for dev.
        // WPF has no console, so file sink is the authoritative channel.
        // Per-process file name (#16): a second concurrent DevLauncher must
        // not race the first instance for File.Delete on a shared path.
        var logPath = Path.Combine(
            Path.GetTempPath(), "BlockParam",
            $"devlauncher-{System.Diagnostics.Process.GetCurrentProcess().Id}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(logPath, outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Log.Information("DevLauncher log: {Path}", logPath);

        // Forward the pill control's diagnostic shims (#141) into Serilog
        // so the DevLauncher's log keeps the host->control DP-boundary
        // breadcrumbs. MultiSelectLog stays vendorable — see MultiSelectLog.cs.
        MultiSelectLog.Sink = msg => Log.Information("{Msg}", msg);

        // --capture-license <out-dir>             #20: license-dialog visual states
        if (args.Length >= 2 && args[0] == "--capture-license")
        {
            LicenseCapture.Run(Path.GetFullPath(args[1]));
            return;
        }

        // --capture-splash <out.png>              #125: pre-dialog loading splash
        if (args.Length >= 2 && args[0] == "--capture-splash")
        {
            SplashCapture.Run(Path.GetFullPath(args[1]));
            return;
        }

        // --demo-splash [slow|fast] [holdSeconds]  #127: LIVE splash + quip timing
        if (args.Length >= 1 && args[0] == "--demo-splash")
        {
            var slow = !(args.Length >= 2 && args[1].Equals("fast", StringComparison.OrdinalIgnoreCase));
            var hold = args.Length >= 3 && int.TryParse(args[2], out var h) ? h : 8;
            SplashDemo.Run(slow, hold);
            return;
        }

        // --capture-rules <out.png> [<rules-dir>]  website hero: rules editor
        if (args.Length >= 2 && args[0] == "--capture-rules")
        {
            var rulesDir = args.Length >= 3
                ? args[2]
                : Path.GetFullPath("assets/fixtures/rules");
            RulesCapture.Run(Path.GetFullPath(args[1]), rulesDir);
            return;
        }

        // --capture-pill <out-dir>                 PillMultiSelect demo: closed + open popup
        if (args.Length >= 2 && args[0] == "--capture-pill")
        {
            PillMultiSelectCapture.Run(Path.GetFullPath(args[1]));
            return;
        }

        // --capture-pill-db <out-dir>              Multi-PLC DB pill demo (overflow + wrap)
        if (args.Length >= 2 && args[0] == "--capture-pill-db")
        {
            PillMultiSelectCapture.RunDb(Path.GetFullPath(args[1]));
            return;
        }

        // --capture-pill-grouped <out-dir>         Grouped popup: tri-state headers, collapsed group, search expands
        if (args.Length >= 2 && args[0] == "--capture-pill-grouped")
        {
            PillMultiSelectCapture.RunGrouped(Path.GetFullPath(args[1]));
            return;
        }

        // --capture-pill-grouped-bundled <out-dir> Closed-pill bundling: fully-selected groups collapse to one token
        if (args.Length >= 2 && args[0] == "--capture-pill-grouped-bundled")
        {
            PillMultiSelectCapture.RunGroupedBundled(Path.GetFullPath(args[1]));
            return;
        }

        // --capture-pill-row <out.png>             Pill row (PlcPills + "+ PLC"): all scenes stitched into one PNG
        if (args.Length >= 2 && args[0] == "--capture-pill-row")
        {
            PillRowCapture.Run(Path.GetFullPath(args[1]));
            return;
        }

        // --demo-pill                              Interactive multi-PLC pill demo (no auto-close)
        if (args.Length >= 1 && args[0] == "--demo-pill")
        {
            PillMultiSelectCapture.RunDbInteractive();
            return;
        }

        // --plc <name>   Fake an owning PLC for the anchor DB (and any
        // unprefixed fixtures). Lets the multi-PLC title / chip-group-header
        // branch be exercised without TIA: the VM hides the PLC chrome iff
        // _currentPlcName is empty (UI/BulkChangeViewModel.cs:1093), so
        // passing this arg flips the branch. Combine with a
        // <PLC>__<DBName>.xml fixture (see EnumerateDevLauncherDbs) to add
        // peers on a different PLC and exercise the multi-PLC chip groups.
        string? anchorPlc = null;
        {
            int idx = Array.IndexOf(args, "--plc");
            if (idx >= 0)
            {
                if (idx + 1 >= args.Length)
                {
                    MessageBox.Show("--plc requires a value.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                anchorPlc = args[idx + 1];
                args = args.Take(idx).Concat(args.Skip(idx + 2)).ToArray();
            }
        }

        // --- Parse capture arguments ---
        // --capture <out.png> [<dbName>]          one-shot single scene
        // --capture-script <script.json>          multi-scene JSON-driven
        var capturePlan = TryParseCapturePlan(args, out var captureDbArg);
        if (capturePlan is FailedPlan fail)
        {
            MessageBox.Show(fail.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (capturePlan != null)
        {
            // Force English for marketing screenshots regardless of OS culture.
            var en = new CultureInfo("en-US");
            Thread.CurrentThread.CurrentCulture = en;
            Thread.CurrentThread.CurrentUICulture = en;
            CultureInfo.DefaultThreadCurrentCulture = en;
            CultureInfo.DefaultThreadCurrentUICulture = en;

            // Force the hardcoded default zoom so captures don't inherit the
            // developer's personal %APPDATA%\BlockParam\ui-settings.json.
            UiZoomService.ReplaceShared(UiZoomService.CreateEphemeral());
        }

        // 1. Find DB XML. Capture plan fixture wins; bare arg next; else defaults.
        var tiaExportDir = Path.Combine(Path.GetTempPath(), "BlockParam");

        // Capture-script peer seeding + anchor PLC. Multi-PLC scenes
        // (mdb02..mdb28) need EnumerateDevLauncherDbs to find peer DBs under
        // distinct PLCs, but the dev's %TEMP%\BlockParam\ usually has only
        // whatever they last opened in TIA. Copy the manifest's peer_fixtures
        // in with <Plc>__<DbName>.xml names so the picker has something to
        // show — and override anchorPlc from the manifest so the main fixture
        // is grouped under its declared PLC too.
        if (capturePlan is CapturePlan seedPlan)
        {
            if (!string.IsNullOrEmpty(seedPlan.AnchorPlc))
                anchorPlc = seedPlan.AnchorPlc;

            if (seedPlan.PeerFixtures is { Count: > 0 })
            {
                Directory.CreateDirectory(tiaExportDir);
                foreach (var peer in seedPlan.PeerFixtures)
                {
                    if (!File.Exists(peer.Path))
                    {
                        Log.Warning("Peer fixture not found: {Path}", peer.Path);
                        continue;
                    }
                    var dbName = Path.GetFileNameWithoutExtension(peer.Path);
                    var destName = $"{peer.Plc}__{dbName}.xml";
                    var destPath = Path.Combine(tiaExportDir, destName);
                    // Skip-if-exists: %TEMP%\BlockParam may contain a real TIA
                    // export the dev is actively iterating on. The anchor DB's
                    // own filename is in peer_fixtures, so silent overwrite
                    // would clobber a real export on every capture run. Delete
                    // the destination file by hand to force a reseed.
                    if (File.Exists(destPath))
                    {
                        Log.Information(
                            "Peer fixture skipped (already present, delete to reseed): {Dest}",
                            destName);
                        continue;
                    }
                    File.Copy(peer.Path, destPath);
                    Log.Information("Seeded peer fixture: {Dest}", destName);
                }
            }
        }
        string? xmlPath;
        var dbArg = (capturePlan as CapturePlan)?.FixturePath
                    ?? captureDbArg
                    ?? (args.Length > 0 && !args[0].StartsWith("--") ? args[0] : null);
        if (dbArg != null)
        {
            var candidates = new[]
            {
                dbArg,
                Path.Combine(tiaExportDir, dbArg),
                Path.Combine(tiaExportDir, dbArg.EndsWith(".xml") ? dbArg : dbArg + ".xml"),
            };
            xmlPath = candidates.FirstOrDefault(File.Exists);
            if (xmlPath == null)
            {
                MessageBox.Show($"DB XML not found for argument: {dbArg}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        else
        {
            xmlPath = FindFile(
                Path.Combine(tiaExportDir, "TP307.xml"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "demo-db.xml"),
                Path.Combine("src", "BlockParam.DevLauncher", "demo-db.xml"));
        }

        if (xmlPath == null)
        {
            MessageBox.Show("No DB XML found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Log.Information("Loading DB from {Path}", xmlPath);
        var xml = File.ReadAllText(xmlPath);

        // Load UDT cache if present (for SetPoint + per-instance Comment fallback).
        // Script mode may override with a committed fixture dir for reproducibility.
        var udtDir = (capturePlan as CapturePlan)?.UdtDir
                     ?? Path.Combine(tiaExportDir, "UdtTypes");
        var udtResolver = new UdtSetPointResolver();
        var commentResolver = new UdtCommentResolver();
        if (Directory.Exists(udtDir))
        {
            udtResolver.LoadFromDirectory(udtDir);
            commentResolver.LoadFromDirectory(udtDir);
            Log.Information("UDT cache loaded: {Count} types from {Dir}", udtResolver.TypeCount, udtDir);
        }
        else
        {
            Log.Warning("No UDT cache at {Dir}", udtDir);
        }

        // 2. Config — use real config.json + rules directory. Capture mode
        // may force a specific rules dir to keep screenshots reproducible
        // regardless of the developer's %APPDATA% config.
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlockParam", "config.json");
        var scriptedRulesDir = (capturePlan as CapturePlan)?.RulesDir;
        var configLoader = scriptedRulesDir != null
            ? new ConfigLoader(File.Exists(configPath) ? configPath : null, scriptedRulesDir)
            : new ConfigLoader(File.Exists(configPath) ? configPath : null);
        Log.Information("Config: {Path} (exists={Exists}) scriptedRulesDir={Scripted}",
            configPath, File.Exists(configPath), scriptedRulesDir ?? "(none)");

        // 3. Tag tables — use real exported XMLs if available.
        // Must be loaded BEFORE the parser runs so symbolic array bounds
        // like Array[1..MAX_VALVES] can be resolved during tree construction.
        var tagTableDir = (capturePlan as CapturePlan)?.TagTableDir
                          ?? Path.Combine(tiaExportDir, "TagTables");
        TagTableCache? tagTableCache = null;
        if (Directory.Exists(tagTableDir) && Directory.GetFiles(tagTableDir, "*.xml").Length > 0)
        {
            tagTableCache = new TagTableCache(new XmlFileTagTableReader(tagTableDir));
            Log.Information("TagTables from {Dir}: {Tables}",
                tagTableDir, string.Join(", ", tagTableCache.GetTableNames()));
        }
        else
        {
            Log.Warning("No tag tables found at {Dir}", tagTableDir);
        }

        IConstantResolver? constantResolver = tagTableCache != null
            ? new TagTableConstantResolver(tagTableCache)
            : null;
        var parser = new SimaticMLParser(constantResolver, udtResolver, commentResolver);
        var dbInfo = parser.Parse(xml);
        if (dbInfo.UnresolvedUdts.Count > 0)
            Log.Warning("Unresolved UDTs: {Types}", string.Join(", ", dbInfo.UnresolvedUdts));

        // 4. Services
        var logger = new ChangeLogger();
        var bulkService = new BulkChangeService(logger, configLoader);
        var analyzer = new HierarchyAnalyzer();
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlockParam");
        var freeTracker = new LocalUsageTracker(
            Path.Combine(Path.GetTempPath(), "BlockParam_dev_usage.dat"));

        var serverUrl = configLoader.ReadLicenseServerUrl() ?? OnlineLicenseService.DefaultServerUrl;
        var licenseService = new OnlineLicenseService(
            appDataDir,
            serverUrl,
            sharedLicenseFilePath: OnlineLicenseService.DefaultSharedLicenseFilePath);

        // Capture mode bypasses the freemium counter so Apply always works
        // regardless of how many prior runs have accumulated (#96). Interactive
        // DevLauncher and the shipped Add-In continue to use the real tracker.
        IUsageTracker usageTracker = capturePlan is CapturePlan
            ? new UnlimitedUsageTracker()
            : new LicensedUsageTracker(licenseService, freeTracker);

        // Update check (#61): mirror BulkChangeContextMenu wiring so the badge
        // and dialog are exercisable without TIA. Uses the BlockParam assembly
        // version (not DevLauncher's) so the badge text reads e.g. "v1.0.2 → vX.Y.Z".
        var blockParamVersion = typeof(BulkChangeViewModel).Assembly.GetName().Version
            ?? new Version(0, 0, 0);
        var updateCheckService = new UpdateCheckService(
            fetcher: new GitHubReleaseFetcher(),
            currentVersion: VersionTag.FromSystemVersion(blockParamVersion),
            cachePath: Path.Combine(appDataDir, "update-check.json"),
            readSettings: () => configLoader.ReadUpdateCheckSettings());

        // 5. Show dialog
        var app = new Application();
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "UNHANDLED EXCEPTION");
            e.Handled = true; // prevent crash, log instead
        };

        // In capture-script mode inject a ScriptedMessageBoxService so
        // multi-DB prompts (Apply/Stash/Cancel, Add-or-Replace) return the
        // scene's canned answer without hanging for user input (#96).
        // Interactive mode keeps the real WpfMessageBoxService (null → default).
        BlockParam.UI.IMessageBoxService? messageBoxService =
            capturePlan is CapturePlan
                ? new ScriptedMessageBoxService()
                : null;

        // DB-switcher (#59) for DevLauncher: simulate the project block tree
        // by enumerating every *.xml under %TEMP%\BlockParam\ that parses as a
        // SimaticML DB. Lets the dropdown be exercised without a real TIA
        // project — pick a few real exports and drop them in the dir.
        IConstantResolver? switchConstantResolver = constantResolver;
        var vm = new BulkChangeViewModel(
            dbInfo, xml, analyzer, bulkService, usageTracker, configLoader,
            onApply: modifiedXml =>
            {
                Log.Information("Apply: XML modified ({Len} chars)", modifiedXml.Length);
                // Save to temp for inspection
                var outPath = Path.Combine(tiaExportDir, $"{dbInfo.Name}_modified.xml");
                File.WriteAllText(outPath, modifiedXml);
                Log.Information("Saved to {Path}", outPath);
            },
            messageBox: messageBoxService,
            tagTableCache: tagTableCache,
            tagTableDir: tagTableDir,
            licenseService: licenseService,
            udtDir: udtDir,
            udtResolver: udtResolver,
            commentResolver: commentResolver,
            updateCheckService: updateCheckService,
            currentPlcName: anchorPlc,
            enumerateDataBlocks: () => EnumerateDevLauncherDbs(tiaExportDir, anchorPlc),
            switchToDataBlock: summary =>
            {
                var path = ResolveFixturePath(tiaExportDir, summary)
                    ?? throw new FileNotFoundException(
                        $"Fixture missing for switch: {summary.PlcName}__{summary.Name}.xml or {summary.Name}.xml");
                var newXml = File.ReadAllText(path);
                var newParser = new SimaticMLParser(switchConstantResolver, udtResolver, commentResolver);
                dbInfo = newParser.Parse(newXml);
                xml = newXml;
                Log.Information("DevLauncher DB switch: now editing {Name}", dbInfo.Name);
                return newXml;
            },
            // Peer-add path: when the user checks a second DB in the
            // dropdown, the VM calls this to build a fully-wired ActiveDb
            // (not the read-only _switchToDataBlock fallback). Each peer
            // gets its own OnApply that writes <Name>_modified.xml, so
            // multi-DB Apply, stash/restore, anchor handoff, and §F-style
            // quota-decrement scenarios are exercisable without TIA.
            buildActiveDbForSummary: summary =>
            {
                var path = ResolveFixturePath(tiaExportDir, summary);
                if (path == null)
                {
                    Log.Warning("DevLauncher peer-add: fixture missing for {Plc}__{Name}",
                        summary.PlcName, summary.Name);
                    return null;
                }
                var peerXml = File.ReadAllText(path);
                var peerParser = new SimaticMLParser(switchConstantResolver, udtResolver, commentResolver);
                var peerInfo = peerParser.Parse(peerXml);
                Action<string> peerOnApply = modifiedXml =>
                {
                    var outPath = Path.Combine(tiaExportDir, $"{peerInfo.Name}_modified.xml");
                    File.WriteAllText(outPath, modifiedXml);
                    Log.Information("Apply (peer {Name}): saved to {Path}", peerInfo.Name, outPath);
                };
                Log.Information("DevLauncher peer-add: {Name} (plc='{Plc}')", peerInfo.Name, summary.PlcName);
                return new ActiveDb(peerInfo, peerXml, peerOnApply, plcName: summary.PlcName);
            });

        licenseService.StartHeartbeat();
        var dialog = new BulkChangeDialog(vm);

        // #127 integration demo: with --with-splash, run the REAL pre-dialog
        // splash → quip → dialog handoff exactly as BulkChangeContextMenu does
        // (interactive only; capture/script modes keep the deterministic path).
        // The splash paints on its own STA thread while we simulate the slow
        // Openness prep with a sleep; it closes the moment the dialog renders.
        if (capturePlan is not CapturePlan
            && args.Any(a => a.Equals("--with-splash", StringComparison.OrdinalIgnoreCase)))
        {
            var quipKey = LoadingHumorService.PickKey();
            var splash = new LoadingSplashController(Res.Get("Splash_Title"), Res.Get(quipKey));
            splash.Show();
            splash.Report(Res.Get("Splash_Preparing"));
            splash.Report(Res.Format("Splash_ExportingDb", dbInfo.Name));
            Log.Information("Integration: splash up, quip '{Key}'. Simulating ~2.5s prep before the dialog opens…", quipKey);
            Thread.Sleep(2500); // > 1.5s so the quip surfaces before the dialog
            dialog.ContentRendered += (_, _) => splash.Close();
        }

        if (capturePlan is CapturePlan plan)
        {
            if (plan.Viewport is { } vp)
            {
                dialog.Width = vp.Width;
                dialog.Height = vp.Height;
            }
            dialog.ContentRendered += (_, _) =>
            {
                // Defer one dispatcher cycle so the initial layout settles,
                // then iterate scenes sequentially on the UI thread.
                dialog.Dispatcher.BeginInvoke(new Action(() =>
                    RunScenes(dialog, vm, plan)),
                    System.Windows.Threading.DispatcherPriority.Background);
            };
        }

        app.Run(dialog);
        licenseService.StopHeartbeat();
        licenseService.Dispose();
    }

    private static void RunScenes(BulkChangeDialog dialog, BulkChangeViewModel vm, CapturePlan plan)
    {
        foreach (var scene in plan.Scenes)
        {
            Log.Information("Scene {Id}: applying state", scene.Id);

            // Per-scene viewport override takes precedence over the script-level one.
            var effective = scene.Viewport ?? plan.Viewport;

            // Chapter cards are rendered out-of-band by
            // assets/screenshots/workflow/chapters/render-chapters.sh (SVG
            // template → Inkscape → wf<NN>_ch_<slug>.png). External scenes
            // (kind=external) are TIA Portal painpoint screenshots dropped
            // into the workflow folder by hand — neither category is rendered
            // by the dialog capture loop, so we skip them here so we don't
            // overwrite the source PNG with a dialog snapshot.
            if (string.Equals(scene.Kind, "chapter", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scene.Kind, "external", StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("Scene {Id}: skipping {Kind} scene (PNG provided out-of-band)", scene.Id, scene.Kind);
                continue;
            }

            if (effective != null)
            {
                dialog.Width = effective.Width;
                dialog.Height = effective.Height;
                // Let WPF fully measure / arrange against the new size before
                // the scene's state changes touch the flat list — otherwise
                // row virtualization can land on stale heights.
                dialog.Dispatcher.Invoke(() => { },
                    System.Windows.Threading.DispatcherPriority.Render);
            }

            SceneApplier.Apply(scene, vm, dialog);

            // WPF re-queries ICommand.CanExecute only in response to input
            // events (mouse, focus). Headless scene changes don't produce
            // any — InvalidateRequerySuggested queues the requery at
            // Background priority, which is LOWER than Render, so without
            // an explicit pump the snapshot happens before IsEnabled
            // propagates. A ContextIdle-priority pump waits for every
            // higher-priority job (Render, Input, Background) to drain.
            CommandManager.InvalidateRequerySuggested();
            dialog.Dispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.ContextIdle);

            // Force measure/arrange after the requery (button IsEnabled /
            // visual-state changes may invalidate layout), then one more
            // Render-priority pump to flush the final visual update before
            // we snap.
            dialog.UpdateLayout();
            dialog.Dispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.Render);

            var outPath = Path.Combine(plan.OutputDir, scene.Filename);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            CaptureWindowToPng(dialog, outPath, plan.Scale);
            Log.Information("Scene {Id}: saved {Path}", scene.Id, outPath);
        }
        // OnClosing's "unsaved changes" prompt uses MessageBox.Show directly
        // (bypassing the scripted IMessageBoxService), so without this flag
        // the headless run hangs on a real modal whenever the last scene
        // leaves pending or stashed edits.
        dialog.SuppressClosePromptsScripted = true;
        dialog.Close();
    }

    internal static void CaptureWindowToPng(Window window, string outputPath, double scale)
    {
        var width = (int)Math.Ceiling(window.ActualWidth * scale);
        var height = (int)Math.Ceiling(window.ActualHeight * scale);
        var dpi = 96.0 * scale;

        var rtb = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(outputPath);
        encoder.Save(fs);
    }

    /// <summary>
    /// DevLauncher stand-in for project DB enumeration (#59): every <c>*.xml</c>
    /// under <c>%TEMP%\BlockParam\</c> whose root element is one of the SimaticML
    /// DB block tags is offered as a switchable target.
    ///
    /// Filename convention for cross-PLC fixtures: <c>&lt;PlcName&gt;__&lt;DbName&gt;.xml</c>
    /// (double-underscore separator) sets <see cref="DataBlockSummary.PlcName"/>
    /// to <c>&lt;PlcName&gt;</c> and <see cref="DataBlockSummary.Name"/> to
    /// <c>&lt;DbName&gt;</c>. Files without the prefix inherit
    /// <paramref name="anchorPlc"/> (empty when <c>--plc</c> isn't passed),
    /// so they group with the anchor in the chip toolbar.
    /// </summary>
    internal static IReadOnlyList<DataBlockSummary> EnumerateDevLauncherDbs(string dir, string? anchorPlc)
    {
        if (!Directory.Exists(dir)) return Array.Empty<DataBlockSummary>();
        var list = new List<DataBlockSummary>();
        foreach (var path in Directory.GetFiles(dir, "*.xml"))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);

            // *_modified.xml are Apply-output artifacts saved by the onApply
            // callback above. They duplicate the original's internal Name,
            // which collides on the stash-key (Plc + Folder + Name) and
            // causes a second stash to overwrite the first. Real TIA can't
            // produce two GlobalDBs with the same Name, so the product code
            // assumes uniqueness — exclude the artifacts here.
            if (fileName.EndsWith("_modified", StringComparison.Ordinal))
                continue;

            // Split <PLC>__<DbName>. The double-underscore is the separator;
            // single underscores are normal in DB names (DB_StashTest_2).
            string plcName = anchorPlc ?? "";
            string dbName = fileName;
            int sepIdx = fileName.IndexOf("__", StringComparison.Ordinal);
            if (sepIdx > 0 && sepIdx + 2 < fileName.Length)
            {
                plcName = fileName.Substring(0, sepIdx);
                dbName = fileName.Substring(sepIdx + 2);
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(path);
                var dbElement = doc.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName == "SW.Blocks.GlobalDB" ||
                    e.Name.LocalName == "SW.Blocks.InstanceDB");
                if (dbElement == null) continue;

                var blockType = dbElement.Name.LocalName.Replace("SW.Blocks.", "");

                // TIA exports the block number inside the DB element's
                // AttributeList as <Number>4</Number>. ProjectDiscovery
                // populates this for real Openness sessions; the DevLauncher
                // enumerator must do the same or the pill trigger flips from
                // "DB42" to a full name when the popup-open reload swaps in
                // a number-less summary.
                int? dbNumber = null;
                var numberElement = dbElement.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Number"
                        && e.Parent?.Name.LocalName == "AttributeList");
                if (numberElement != null
                    && int.TryParse(numberElement.Value, out var parsed))
                {
                    dbNumber = parsed;
                }

                list.Add(new DataBlockSummary(
                    dbName,
                    folderPath: "",
                    blockType: blockType,
                    isInstanceDb: blockType == "InstanceDB",
                    plcName: plcName,
                    number: dbNumber));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DevLauncher: skipping {Path} (not a parseable DB XML)", path);
            }
        }
        return list;
    }

    /// <summary>
    /// Resolves <paramref name="summary"/> back to a fixture file under
    /// <paramref name="tiaExportDir"/>. Prefers the PLC-prefixed filename
    /// (<c>&lt;PlcName&gt;__&lt;Name&gt;.xml</c>) when <c>PlcName</c> is set,
    /// falls back to the bare <c>&lt;Name&gt;.xml</c> so unprefixed fixtures
    /// keep working when the user passes <c>--plc</c>. Returns null if
    /// neither form exists — both <c>switchToDataBlock</c> and
    /// <c>buildActiveDbForSummary</c> handle that.
    /// </summary>
    internal static string? ResolveFixturePath(string tiaExportDir, DataBlockSummary summary)
    {
        if (!string.IsNullOrEmpty(summary.PlcName))
        {
            var prefixed = Path.Combine(tiaExportDir, $"{summary.PlcName}__{summary.Name}.xml");
            if (File.Exists(prefixed)) return prefixed;
        }
        var bare = Path.Combine(tiaExportDir, $"{summary.Name}.xml");
        return File.Exists(bare) ? bare : null;
    }

    private static string? FindFile(params string[] candidates)
    {
        foreach (var path in candidates)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    // --- Capture-plan plumbing ---

    private abstract class CapturePlanBase { }
    private sealed class FailedPlan : CapturePlanBase
    {
        public FailedPlan(string message) { Message = message; }
        public string Message { get; }
    }
    private sealed class CapturePlan : CapturePlanBase
    {
        public CapturePlan(string outputDir, double scale, Viewport? viewport,
            string? fixturePath, string? udtDir, string? tagTableDir, string? rulesDir,
            string? anchorPlc, List<PeerFixture>? peerFixtures,
            List<Scene> scenes)
        {
            OutputDir = outputDir;
            Scale = scale;
            Viewport = viewport;
            FixturePath = fixturePath;
            UdtDir = udtDir;
            TagTableDir = tagTableDir;
            RulesDir = rulesDir;
            AnchorPlc = anchorPlc;
            PeerFixtures = peerFixtures;
            Scenes = scenes;
        }
        public string OutputDir { get; }
        public double Scale { get; }
        public Viewport? Viewport { get; }
        public string? FixturePath { get; }
        public string? UdtDir { get; }
        public string? TagTableDir { get; }
        public string? RulesDir { get; }
        public string? AnchorPlc { get; }
        public List<PeerFixture>? PeerFixtures { get; }
        public List<Scene> Scenes { get; }
    }

    /// <summary>
    /// Parses --capture / --capture-script into a unified plan.
    /// Returns null if no capture flag is present (interactive mode).
    /// </summary>
    private static CapturePlanBase? TryParseCapturePlan(string[] args, out string? legacyDbArg)
    {
        legacyDbArg = null;
        if (args.Length == 0) return null;

        if (args[0] == "--capture")
        {
            if (args.Length < 2)
                return new FailedPlan("--capture requires an output PNG path.");

            var outFile = Path.GetFullPath(args[1]);
            legacyDbArg = args.Length >= 3 ? args[2] : "DB_ProcessPlant_A1";
            return new CapturePlan(
                outputDir: Path.GetDirectoryName(outFile)!,
                scale: 2.0,
                viewport: null,
                fixturePath: null,
                udtDir: null,
                tagTableDir: null,
                rulesDir: null,
                anchorPlc: null,
                peerFixtures: null,
                scenes: new List<Scene>
                {
                    new Scene { Id = "default", Filename = Path.GetFileName(outFile), Dialog = "main" },
                });
        }

        if (args[0] == "--capture-script")
        {
            if (args.Length < 2)
                return new FailedPlan("--capture-script requires a script path.");

            var scriptPath = Path.GetFullPath(args[1]);
            if (!File.Exists(scriptPath))
                return new FailedPlan($"Script not found: {scriptPath}");

            var (script, baseDir) = CaptureScriptLoader.Load(scriptPath);
            var outputDir = CaptureScriptLoader.ResolveRelative(baseDir, script.OutputDir ?? ".");
            var fixturePath = script.Fixture != null
                ? CaptureScriptLoader.ResolveRelative(baseDir, script.Fixture)
                : null;
            var udtDir = script.UdtDir != null
                ? CaptureScriptLoader.ResolveRelative(baseDir, script.UdtDir)
                : null;
            var tagTableDir = script.TagTableDir != null
                ? CaptureScriptLoader.ResolveRelative(baseDir, script.TagTableDir)
                : null;
            var rulesDir = script.RulesDir != null
                ? CaptureScriptLoader.ResolveRelative(baseDir, script.RulesDir)
                : null;
            // DPI 96 = 1x, 192 = 2x; convert to scale factor.
            var scale = (script.Dpi ?? 192.0) / 96.0;

            // Resolve peer fixture paths to absolute now (the consumer at scene
            // run time has no idea where baseDir is).
            List<PeerFixture>? peerFixtures = null;
            if (script.PeerFixtures is { Count: > 0 })
            {
                peerFixtures = script.PeerFixtures
                    .Select(p => new PeerFixture
                    {
                        Plc = p.Plc,
                        Path = CaptureScriptLoader.ResolveRelative(baseDir, p.Path),
                    })
                    .ToList();
            }

            return new CapturePlan(
                outputDir: outputDir,
                scale: scale,
                viewport: script.Viewport,
                fixturePath: fixturePath,
                udtDir: udtDir,
                tagTableDir: tagTableDir,
                rulesDir: rulesDir,
                anchorPlc: script.AnchorPlc,
                peerFixtures: peerFixtures,
                scenes: script.Scenes);
        }

        return null;
    }
}
