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
using BlockParam.Models;
using BlockParam.Services;
using BlockParam.SimaticML;
using BlockParam.UI;
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
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}")
            .WriteTo.File(logPath, outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}")
            .CreateLogger();
        Log.Information("DevLauncher log: {Path}", logPath);

        // --capture-license <out-dir>             #20: license-dialog visual states
        if (args.Length >= 2 && args[0] == "--capture-license")
        {
            LicenseCapture.Run(Path.GetFullPath(args[1]));
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
        var usageTracker = new LicensedUsageTracker(licenseService, freeTracker);

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
            tagTableCache: tagTableCache,
            tagTableDir: tagTableDir,
            licenseService: licenseService,
            udtDir: udtDir,
            udtResolver: udtResolver,
            commentResolver: commentResolver,
            enumerateDataBlocks: () => EnumerateDevLauncherDbs(tiaExportDir),
            switchToDataBlock: summary =>
            {
                var path = Path.Combine(tiaExportDir, summary.Name + ".xml");
                if (!File.Exists(path))
                    throw new FileNotFoundException($"Fixture missing: {path}");
                var newXml = File.ReadAllText(path);
                var newParser = new SimaticMLParser(switchConstantResolver, udtResolver, commentResolver);
                dbInfo = newParser.Parse(newXml);
                xml = newXml;
                Log.Information("DevLauncher DB switch: now editing {Name}", dbInfo.Name);
                return newXml;
            },
            updateCheckService: updateCheckService);

        licenseService.StartHeartbeat();
        var dialog = new BulkChangeDialog(vm);

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
    /// </summary>
    private static IReadOnlyList<DataBlockSummary> EnumerateDevLauncherDbs(string dir)
    {
        if (!Directory.Exists(dir)) return Array.Empty<DataBlockSummary>();
        var list = new List<DataBlockSummary>();
        foreach (var path in Directory.GetFiles(dir, "*.xml"))
        {
            // *_modified.xml are Apply-output artifacts saved by the onApply
            // callback above. They duplicate the original's internal Name,
            // which collides on the stash-key (Plc + Folder + Name) and
            // causes a second stash to overwrite the first. Real TIA can't
            // produce two GlobalDBs with the same Name, so the product code
            // assumes uniqueness — exclude the artifacts here.
            if (Path.GetFileNameWithoutExtension(path).EndsWith("_modified",
                    StringComparison.Ordinal))
                continue;

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(path);
                var dbElement = doc.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName == "SW.Blocks.GlobalDB" ||
                    e.Name.LocalName == "SW.Blocks.InstanceDB");
                if (dbElement == null) continue;

                var blockType = dbElement.Name.LocalName.Replace("SW.Blocks.", "");
                list.Add(new DataBlockSummary(
                    Path.GetFileNameWithoutExtension(path),
                    folderPath: "",
                    blockType: blockType,
                    isInstanceDb: blockType == "InstanceDB"));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DevLauncher: skipping {Path} (not a parseable DB XML)", path);
            }
        }
        return list;
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
            List<Scene> scenes)
        {
            OutputDir = outputDir;
            Scale = scale;
            Viewport = viewport;
            FixturePath = fixturePath;
            UdtDir = udtDir;
            TagTableDir = tagTableDir;
            RulesDir = rulesDir;
            Scenes = scenes;
        }
        public string OutputDir { get; }
        public double Scale { get; }
        public Viewport? Viewport { get; }
        public string? FixturePath { get; }
        public string? UdtDir { get; }
        public string? TagTableDir { get; }
        public string? RulesDir { get; }
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
            return new CapturePlan(
                outputDir: outputDir,
                scale: scale,
                viewport: script.Viewport,
                fixturePath: fixturePath,
                udtDir: udtDir,
                tagTableDir: tagTableDir,
                rulesDir: rulesDir,
                scenes: script.Scenes);
        }

        return null;
    }
}
