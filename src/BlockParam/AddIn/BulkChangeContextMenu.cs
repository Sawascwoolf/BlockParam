#if DIAGNOSTICS
using System.Diagnostics;
#endif
using System.IO;
using System.Threading;
using Siemens.Engineering;
using Siemens.Engineering.AddIn.Menu;
using Siemens.Engineering.SW.Blocks;
using BlockParam.Config;
using BlockParam.Diagnostics;
using BlockParam.Licensing;
using BlockParam.Localization;
using BlockParam.Models;
using BlockParam.Services;
using BlockParam.SimaticML;
using BlockParam.UI;
using BlockParam.Updates;

namespace BlockParam.AddIn;

public class BulkChangeContextMenu : ContextMenuAddIn
{
    private readonly TiaPortal _tiaPortal;
    private static int _tempCacheCleanupRan;

    // One cache per TIA Add-In load; OnClick is reused per right-click, so this
    // survives across opens and makes re-opening an unchanged DB skip the slow
    // Openness export + parse (#140).
    private readonly DbExportCache _dbExportCache = new DbExportCache();

    // #155: same per-Add-In-load lifetime as _dbExportCache. These collapse the
    // remaining per-open bottlenecks profiled in #155 — the ~8–12s project DB
    // enumeration, the ~4.6s tag-table re-export, and the ~2.7s UDT freshness
    // walk — to once per project scope per TIA session. The dialog's existing
    // explicit refresh affordances Invalidate them (the cross-open staleness
    // valve, sanctioned by the #155 correctness constraint).
    private readonly ProjectDbEnumerationCache _projectDbCache = new ProjectDbEnumerationCache();
    private readonly SessionScopeGate _tagTableExportGate = new SessionScopeGate("tag-table export");
    private readonly SessionScopeGate _udtValidationGate = new SessionScopeGate("UDT validation");

    /// <summary>
    /// Reads the optional <c>language</c> override from %APPDATA%\BlockParam\config.json
    /// (#50) and, if set, applies it to <c>Thread.CurrentUICulture</c>. When unset,
    /// the OS culture is used (which is what TIA itself defaults to for our addin).
    ///
    /// We deliberately do NOT try to mirror TIA's own UI-language setting —
    /// research showed Openness has no reliable hook for the live value, and
    /// the only documented one (<c>SettingsFolders[General][UserInterfaceLanguage]</c>)
    /// returns inconsistent results across addin reloads. This matches the
    /// approach Parozzz/TiaUtilities (the only fully-localized open-source TIA
    /// addin) uses: own setting, ignore TIA's UI-language dropdown.
    ///
    /// CAS sandbox forbids writes to <c>Thread.CurrentCulture</c> but allows
    /// <c>CurrentUICulture</c>, which is the one ResourceManager consults.
    /// </summary>
    private static void ApplyConfiguredLanguage()
    {
        try
        {
            var configPath = FindConfigFile();
            if (configPath == null) return;

            var lang = new ConfigLoader(configPath).ReadLanguage();
            if (string.IsNullOrEmpty(lang)) return;

            var culture = System.Globalization.CultureInfo.GetCultureInfo(lang);
            if (!Equals(System.Threading.Thread.CurrentThread.CurrentUICulture, culture))
            {
                System.Threading.Thread.CurrentThread.CurrentUICulture = culture;
                Log.Information("Applied config.json language override: {Culture}", culture.Name);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not apply config.json language; thread culture stays at {Culture}",
                System.Threading.Thread.CurrentThread.CurrentUICulture.Name);
        }
    }

    /// <summary>
    /// Runs the TEMP cache cleanup at most once per Add-In process, and at most
    /// once per <see cref="CacheCleanupSchedule.DefaultInterval"/> across sessions
    /// (state file in %APPDATA%\BlockParam\). Keeps %TEMP%\BlockParam\ bounded
    /// by removing orphan per-project scope dirs — #14 follow-up.
    /// </summary>
    private static void EnsureTempCacheCleaned()
    {
        if (Interlocked.Exchange(ref _tempCacheCleanupRan, 1) != 0) return;
        try
        {
            var stateFile = AppDirectories.CacheCleanupStateFile;
            if (!CacheCleanupSchedule.IsDue(stateFile)) return;

            var (files, dirs, nextRun) = TempCacheCleanup.Run(AppDirectories.Temp);
            CacheCleanupSchedule.SetNextRun(stateFile, nextRun);

            if (files > 0 || dirs > 0)
                Log.Information("TempCacheCleanup: removed {Files} file(s), {Dirs} dir(s); next sweep at {Next:yyyy-MM-dd HH:mm}",
                    files, dirs, nextRun);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TempCacheCleanup failed (non-fatal)");
        }
    }

    public BulkChangeContextMenu(TiaPortal tiaPortal) : base(BulkChangeAddInProviderInfo.MenuTitle)
    {
        _tiaPortal = tiaPortal;
    }

    protected override void BuildContextMenuItems(ContextMenuAddInRoot addInRootSubmenu)
    {
        // BuildContextMenuItems runs once at addin load (#50 research). The
        // label is then frozen for the entire TIA session — Siemens API has
        // no per-render hook. Apply our config.json language now so the label
        // matches the user's pref even though it can't live-flip.
        ApplyConfiguredLanguage();

        addInRootSubmenu.Items.AddActionItem<IEngineeringObject>(
            Res.Get("MenuTitle_EditStartValues"),
            OnClick,
            OnUpdateStatus);
    }

    private void OnClick(MenuSelectionProvider<IEngineeringObject> provider)
    {
        var prompt = new MessageBoxUserPrompt();
        LoadingSplashController? splash = null;
#if DIAGNOSTICS
        var totalSw = Stopwatch.StartNew();
        long buildMs = 0, configMs = 0;
#endif
        try
        {
            // Multi-DB selection (#58). Index 0 is the anchor (display role
            // for title / scope label); the rest are peer active DBs.
            var allSelected = provider.GetSelection<DataBlock>().ToList();
            if (allSelected.Count == 0) return;

            EnsureTempCacheCleaned();
            ApplyConfiguredLanguage();

            Log.Information("Bulk Change v{Version} clicked on DB: {DbName}{Extra}",
                typeof(BulkChangeContextMenu).Assembly.GetName().Version, allSelected[0].Name,
                allSelected.Count > 1 ? $" (+{allSelected.Count - 1} additional DB(s))" : "");
            Log.Information("UI culture: {Culture} (ResourceManager picks Strings.<lang>.resx satellite)",
                System.Threading.Thread.CurrentThread.CurrentUICulture.Name);

            // #125: indeterminate splash on its own STA dispatcher thread so
            // it keeps painting while the synchronous Openness export/parse
            // below blocks the TIA UI thread. Shown as fast as possible (no
            // flash-guard delay — prep time isn't predictable); strings are
            // localized here and pushed in.
            splash = new LoadingSplashController(Res.Get("Splash_Title"));
            splash.Show();
            splash.Report(Res.Get("Splash_Preparing"));

            // Per-project scope so parallel TIA instances / switched projects cannot share cache dirs (#14).
            var project = _tiaPortal.Projects.FirstOrDefault();
            var scope = ProjectScope.ForPath(project?.Path?.FullName);
            var tempDir = AppDirectories.TempScope(scope);
            var tagTableDir = AppDirectories.TagTablesCacheDir(scope);
            var udtDir = AppDirectories.UdtTypesCacheDir(scope);
            var appDataDir = AppDirectories.AppData;

            // Service composition. Each service is a single-responsibility seam (#81):
            // adapter (TIA Openness wrapping), discovery (tree walks), exporter (compile-prompt
            // UX), tag/UDT cache services (per-project XML on disk), factory (DB → ActiveDb).
            var adapter = new TiaPortalAdapter(_tiaPortal);
            var discovery = new ProjectDiscovery();
            var blockExporter = new BlockExporter(adapter, prompt);
            var tagTableExporter = new TagTableExporter();
            var udtCacheRefresher = new UdtCacheRefresher(prompt);

            var plcSoftware = discovery.FindPlcSoftware(allSelected[0]);
            var plcName = plcSoftware != null ? ProjectDiscovery.SafeGetPlcName(plcSoftware) : "";

            // Stage (a): UDT cache refresh.
            // Refresh UDT cache before parsing — out-of-date UDT XML produces wrong
            // setpoint / comment defaults at the leaf level.
            splash.Report(Res.Get("Splash_RefreshingUdtCache"));
            int udtRefreshedCount = 0;
            using (var t = OpenTiming.Stage("udtRefresh", $"plc={plcName}"))
            {
                if (plcSoftware != null)
                {
                    // #155 item 3: the full UDT freshness walk (583 types in the
                    // profiled project, ~2.7s) ran unconditionally on every open
                    // even when 0 were stale. Run it once per project scope per
                    // TIA session; the dialog's "Refresh UDT types" path
                    // Invalidates the gate (onInvalidateUdtSession) to force it.
                    bool ran = _udtValidationGate.RunOnce(scope, () =>
                        udtRefreshedCount = udtCacheRefresher.Refresh(plcSoftware, udtDir));
                    if (ran)
                        Log.Information("UDT cache validation: {Refreshed} stale file(s) re-exported", udtRefreshedCount);
                    t.AddPredictors($"udtGate={(ran ? "ran" : "skipped")}");
                }
                t.AddPredictors($"staleFlushed={udtRefreshedCount}");
            }

            // Stage (b): UDT resolver load.
            splash.Report(Res.Get("Splash_LoadingUdtCache"));
            var udtResolver = new UdtSetPointResolver();
            var commentResolver = new UdtCommentResolver();
            using (var t = OpenTiming.Stage("udtLoad", $"plc={plcName}"))
            {
                udtResolver.LoadFromDirectory(udtDir);
                commentResolver.LoadFromDirectory(udtDir);
                Log.Information("UDT cache loaded: {TypeCount} types from {Dir}", udtResolver.TypeCount, udtDir);
                t.AddPredictors($"typeCount={udtResolver.TypeCount}");
            }

            // Optional constant resolver from any previously cached tag tables so
            // symbolic array bounds (Array[1..MAX_VALVES]) expand. Empty cache → null,
            // and the user can refresh from inside the dialog.
            var constantResolver = TryBuildConstantResolver(tagTableDir);

            // PLC count drives whether the header shows a "{PLC} / " prefix (#59 follow-up).
            // Single source of truth so chip/dropdown PlcName stays consistent.
            var plcCount = discovery.CountPlcSoftwares(project);
            var displayPlcName = plcSoftware != null && plcCount > 1
                ? ProjectDiscovery.SafeGetPlcName(plcSoftware)
                : "";

            // Active DB factory. Encapsulates export + parse + OnApply closure
            // for every selected DB so OnClick no longer carries that logic.
            var dbFactory = new ActiveDbFactory(
                blockExporter, adapter, tempDir,
                constantResolver, udtResolver, commentResolver,
                _dbExportCache, scope);

            // Stage (c): per-DB ActiveDb build loop (overall).
            // Focused DB. Wrapped in a single-element holder so the in-dialog
            // DB switcher (#59) can swap which ActiveDb the VM's onApply targets
            // without re-entering the VM construction path.
            ActiveDb? focused;
            var additionalDbs = new List<ActiveDb>();
#if DIAGNOSTICS
            var buildSw = Stopwatch.StartNew();
#endif
            var totalDbs = allSelected.Count;
            splash.SetCounter(totalDbs > 1 ? Res.Format("Splash_Counter", 1, totalDbs) : string.Empty);
            focused = dbFactory.Build(allSelected[0], displayPlcName, splash);
            if (focused == null) return;
            var currentFocused = focused;

            // Additional active DBs (#58). Skipped if export fails (declined
            // compile, etc.).
            for (int i = 1; i < allSelected.Count; i++)
            {
                splash.SetCounter(Res.Format("Splash_Counter", i + 1, totalDbs));
                var c = dbFactory.Build(allSelected[i], displayPlcName, splash);
                if (c != null) additionalDbs.Add(c);
            }
            splash.SetCounter(string.Empty);
#if DIAGNOSTICS
            buildSw.Stop();
            buildMs = buildSw.ElapsedMilliseconds;
            Log.Information("OPEN-TIMING stage=buildAll plc={Plc} dbCount={DbCount} ms={Ms}",
                plcName, allSelected.Count, buildMs);
#endif
            if (additionalDbs.Count > 0)
                Log.Information("Multi-DB session: {N} additional DB(s) active alongside {Primary}",
                    additionalDbs.Count, focused.Info.Name);

            // Stage (d): config / project languages / licensing init.
#if DIAGNOSTICS
            var configSw = Stopwatch.StartNew();
#endif
            splash.Report(Res.Get("Splash_LoadingConfig"));
            // Build the rest of the VM dependencies (config / project languages /
            // licensing / update check). Pure construction — no TIA tree walks.
            var (configLoader, projectLanguages, editingLanguage, referenceLanguage) =
                LoadProjectConfigAndLanguages(project, appDataDir);
            var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
            var analyzer = new HierarchyAnalyzer();

            var freeTracker = new LocalUsageTracker(Path.Combine(appDataDir, "usage.dat"));
            var serverUrl = configLoader.ReadLicenseServerUrl() ?? OnlineLicenseService.DefaultServerUrl;
            var licenseService = new OnlineLicenseService(
                appDataDir, serverUrl,
                sharedLicenseFilePath: OnlineLicenseService.DefaultSharedLicenseFilePath);
            var usageTracker = new LicensedUsageTracker(licenseService, freeTracker);
            var updateCheckService = TryBuildUpdateCheckService(appDataDir, configLoader);
#if DIAGNOSTICS
            configSw.Stop();
            configMs = configSw.ElapsedMilliseconds;
            Log.Information("OPEN-TIMING stage=config plc={Plc} ms={Ms}", plcName, configMs);
#endif

            var vm = new BulkChangeViewModel(
                focused.Info, focused.Xml, analyzer, bulkService, usageTracker, configLoader,
                // Thunk: the focused ActiveDb may be swapped by switchToDataBlock,
                // so we route Apply through the latest reference, not a captured one.
                onApply: xml => currentFocused.OnApply!(xml),
                // #155 item 2: TagTableExporter.Export wipes + re-exports every
                // tag table from Openness (~4.6s) on first tag-table need of
                // each open. The XML is already on disk from a prior open, so
                // gate the export to once per project scope per TIA session.
                // "Refresh constants" Invalidates the gate (onInvalidateTagTableSession).
                onRefreshTagTables: plcSoftware != null
                    ? new Action(() => _tagTableExportGate.RunOnce(scope,
                        () => tagTableExporter.Export(plcSoftware, tagTableDir)))
                    : null,
                tagTableDir: plcSoftware != null ? tagTableDir : null,
                projectLanguages: projectLanguages,
                licenseService: licenseService,
                onRefreshUdtTypes: plcSoftware != null
                    ? new Action(() => { udtCacheRefresher.Refresh(plcSoftware, udtDir); })
                    : null,
                udtDir: udtDir,
                udtResolver: udtResolver,
                commentResolver: commentResolver,
                editingLanguage: editingLanguage,
                referenceLanguage: referenceLanguage,
                updateCheckService: updateCheckService,
                // #155 item 1: the ~8–12s project-wide DB walk was cached only
                // per-dialog (ActiveSetViewModel field), so it re-ran on the
                // first switcher interaction of every open. Route it through
                // the TIA-session-scoped cache; the switcher's Refresh button
                // Invalidates it via onRefreshDataBlocks.
                enumerateDataBlocks: project != null
                    ? new Func<IReadOnlyList<DataBlockSummary>>(
                        () => _projectDbCache.GetOrAdd(scope, () => discovery.EnumerateDataBlocks(project)))
                    : null,
                onRefreshDataBlocks: project != null
                    ? new Action(() => _projectDbCache.Invalidate(scope))
                    : null,
                currentPlcName: displayPlcName,
                switchToDataBlock: plcSoftware != null
                    ? new Func<DataBlockSummary, string>(summary =>
                    {
                        // Re-export + re-parse into a fresh ActiveDb. Resolve
                        // against the summary's owning PLC so cross-PLC switches
                        // work; fall back to the launch PLC if the project lookup
                        // fails (e.g. PLC renamed mid-session).
                        var sourcePlc = (project != null
                            ? discovery.FindPlcSoftwareByName(project, summary.PlcName)
                            : null) ?? plcSoftware;
                        var newDb = discovery.ResolveDataBlock(sourcePlc, summary)
                            ?? throw new InvalidOperationException(
                                $"DB '{summary.Name}' not found in project");

                        var newActive = dbFactory.Build(newDb, summary.PlcName);
                        if (newActive == null)
                            throw new OperationCanceledException(
                                "User declined to compile the inconsistent target DB.");

                        currentFocused = newActive;
                        Log.Information("DB switch: now editing {DbName}", newActive.Info.Name);
                        return newActive.Xml;
                    })
                    : null,
                additionalActiveDbs: additionalDbs,
                buildActiveDbForSummary: plcSoftware != null
                    ? new Func<DataBlockSummary, ActiveDb?>(summary =>
                    {
                        var sourcePlc = (project != null
                            ? discovery.FindPlcSoftwareByName(project, summary.PlcName)
                            : null) ?? plcSoftware;
                        var initial = discovery.ResolveDataBlock(sourcePlc, summary);
                        if (initial == null)
                        {
                            Log.Warning("buildActiveDbForSummary: '{Name}' not found in project", summary.Name);
                            return null;
                        }
                        return dbFactory.Build(initial, summary.PlcName);
                    })
                    : null,
                // #155 cross-open staleness valves: the dialog's explicit
                // refresh actions clear the session gates so a tag-table / UDT
                // edited in TIA mid-session is one click away (never silent).
                onInvalidateTagTableSession: plcSoftware != null
                    ? new Action(() => _tagTableExportGate.Invalidate(scope))
                    : null,
                onInvalidateUdtSession: plcSoftware != null
                    ? new Action(() => _udtValidationGate.Invalidate(scope))
                    : null,
                // #146: Apply-time progress splash. Lives on its own STA
                // dispatcher so it keeps painting while TIA's UI thread is
                // blocked in Openness `Blocks.Import`. The default in the VM
                // is a NoOp so headless tests / DevLauncher don't spin up
                // real windows; only the in-process Add-In wires the WPF impl.
                applyProgress: new BlockParam.Services.WpfApplyProgressService());

            licenseService.StartHeartbeat();

#if DIAGNOSTICS
            // Stage (e): TOTAL — handler entry to just before ShowDialog.
            totalSw.Stop();
            Log.Information(
                "OPEN-TIMING stage=total plc={Plc} dbCount={DbCount} buildMs={BuildMs} configMs={ConfigMs} totalMs={TotalMs}",
                plcName, allSelected.Count, buildMs, configMs, totalSw.ElapsedMilliseconds);
#endif

            var dialog = new BulkChangeDialog(vm);
            // Hand off splash → dialog only once the dialog has actually
            // painted, so there is no flash of empty desktop between them (#125).
            dialog.ContentRendered += (_, _) => splash?.Close();
            dialog.ShowDialog();
            licenseService.StopHeartbeat();
            licenseService.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in Bulk Change OnClick");
            prompt.ShowError(Res.Get("Rollback_Title"), ex.ToString());
        }
        finally
        {
            // Safety net for early-return / exception paths where the dialog
            // never opened. Idempotent with the handoff above.
            splash?.Close();
        }
    }

    /// <summary>
    /// Builds a tag-table-backed constant resolver if the cache directory has
    /// any cached XML, otherwise returns null. Symbolic array bounds
    /// (<c>Array[1..MAX_VALVES]</c>) need the resolver; without it the bound
    /// stays unresolved and the user can refresh tag tables from the dialog.
    /// </summary>
    private static IConstantResolver? TryBuildConstantResolver(string tagTableDir)
    {
        if (!Directory.Exists(tagTableDir)) return null;
        if (Directory.GetFiles(tagTableDir, "*.xml").Length == 0) return null;
        var cache = new TagTableCache(new XmlFileTagTableReader(tagTableDir));
        return new TagTableConstantResolver(cache);
    }

    private static (ConfigLoader configLoader,
                    IReadOnlyList<string> projectLanguages,
                    string? editingLanguage,
                    string? referenceLanguage)
        LoadProjectConfigAndLanguages(Project? project, string appDataDir)
    {
        var configPath = FindConfigFile();
        var configLoader = new ConfigLoader(configPath);

        IReadOnlyList<string> projectLanguages = Array.Empty<string>();
        string? editingLanguage = null;
        string? referenceLanguage = null;
        if (project != null)
        {
            configLoader.SetTiaProjectPath(project.Path.FullName);
            try
            {
                var langSettings = project.LanguageSettings;
                projectLanguages = langSettings.ActiveLanguages
                    .Select(l => l.Culture.Name)
                    .ToList();
                try { editingLanguage = langSettings.EditingLanguage?.Culture?.Name; }
                catch { /* not always exposed; falls back to null */ }
                try { referenceLanguage = langSettings.ReferenceLanguage?.Culture?.Name; }
                catch { /* not always exposed; falls back to null */ }
            }
            catch (Exception langEx)
            {
                Log.Warning(langEx, "Could not read TIA project languages");
            }
            Log.Information("TIA project text languages — active: [{Active}], editing: {Editing}, reference: {Reference}",
                string.Join(", ", projectLanguages),
                editingLanguage ?? "(unset)",
                referenceLanguage ?? "(unset)");
        }

        return (configLoader, projectLanguages, editingLanguage, referenceLanguage);
    }

    /// <summary>
    /// Update check (#61). Single shared service per dialog session; cache TTL
    /// gates the GitHub call so opening multiple DBs in a session does not
    /// multiply network traffic. Returns null if construction fails — the
    /// feature is purely informational so we don't block the dialog.
    /// </summary>
    private static IUpdateCheckService? TryBuildUpdateCheckService(string appDataDir, ConfigLoader configLoader)
    {
        try
        {
            var current = typeof(BulkChangeContextMenu).Assembly.GetName().Version
                ?? new Version(0, 0, 0);
            return new UpdateCheckService(
                fetcher: new GitHubReleaseFetcher(),
                currentVersion: VersionTag.FromSystemVersion(current),
                cachePath: Path.Combine(appDataDir, "update-check.json"),
                readSettings: () => configLoader.ReadUpdateCheckSettings());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UpdateCheck: cannot construct service — feature disabled this session");
            return null;
        }
    }

    private MenuStatus OnUpdateStatus(MenuSelectionProvider<IEngineeringObject> provider)
    {
        return provider.GetSelection<DataBlock>().Any()
            ? MenuStatus.Enabled
            : MenuStatus.Disabled;
    }

    private static string? FindConfigFile()
    {
        var candidates = new[]
        {
            AppDirectories.ConfigFile,
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
