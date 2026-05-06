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
            var stateFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlockParam", "cache-cleanup.txt");
            if (!CacheCleanupSchedule.IsDue(stateFile)) return;

            var root = Path.Combine(Path.GetTempPath(), "BlockParam");
            var (files, dirs, nextRun) = TempCacheCleanup.Run(root);
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
        try
        {
            // Multi-DB selection (#58). Index 0 is the focused DB; 1+ are companions.
            var allSelected = provider.GetSelection<DataBlock>().ToList();
            if (allSelected.Count == 0) return;

            EnsureTempCacheCleaned();
            ApplyConfiguredLanguage();

            Log.Information("Bulk Change v{Version} clicked on DB: {DbName}{Extra}",
                typeof(BulkChangeContextMenu).Assembly.GetName().Version, allSelected[0].Name,
                allSelected.Count > 1 ? $" (+{allSelected.Count - 1} companion DB(s))" : "");
            Log.Information("UI culture: {Culture} (ResourceManager picks Strings.<lang>.resx satellite)",
                System.Threading.Thread.CurrentThread.CurrentUICulture.Name);

            // Per-project scope so parallel TIA instances / switched projects cannot share cache dirs (#14).
            var project = _tiaPortal.Projects.FirstOrDefault();
            var scope = ProjectScope.ForPath(project?.Path?.FullName);
            var tempDir = Path.Combine(Path.GetTempPath(), "BlockParam", scope);
            var tagTableDir = Path.Combine(tempDir, "TagTables");
            var udtDir = Path.Combine(tempDir, "UdtTypes");
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlockParam");

            // Service composition. Each service is a single-responsibility seam (#81):
            // adapter (TIA Openness wrapping), discovery (tree walks), exporter (compile-prompt
            // UX), tag/UDT cache services (per-project XML on disk), factory (DB → ActiveDb).
            var adapter = new TiaPortalAdapter(_tiaPortal);
            var discovery = new ProjectDiscovery();
            var blockExporter = new BlockExporter(adapter, prompt);
            var tagTableExporter = new TagTableExporter();
            var udtCacheRefresher = new UdtCacheRefresher(prompt);

            var plcSoftware = discovery.FindPlcSoftware(allSelected[0]);

            // Refresh UDT cache before parsing — out-of-date UDT XML produces wrong
            // setpoint / comment defaults at the leaf level.
            if (plcSoftware != null)
            {
                var refreshed = udtCacheRefresher.Refresh(plcSoftware, udtDir);
                Log.Information("UDT cache validation: {Refreshed} stale file(s) re-exported", refreshed);
            }
            var udtResolver = new UdtSetPointResolver();
            udtResolver.LoadFromDirectory(udtDir);
            var commentResolver = new UdtCommentResolver();
            commentResolver.LoadFromDirectory(udtDir);
            Log.Information("UDT cache loaded: {TypeCount} types from {Dir}", udtResolver.TypeCount, udtDir);

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

            // Active DB factory. Encapsulates export + parse + OnApply closure for
            // every DB (focused + companions) so OnClick no longer carries that logic.
            var dbFactory = new ActiveDbFactory(
                blockExporter, adapter, tempDir,
                constantResolver, udtResolver, commentResolver);

            // Focused DB. Wrapped in a single-element holder so the in-dialog
            // DB switcher (#59) can swap which ActiveDb the VM's onApply targets
            // without re-entering the VM construction path.
            var focused = dbFactory.Build(allSelected[0], displayPlcName);
            if (focused == null) return;
            var currentFocused = focused;

            // Companion DBs (#58). Skipped if export fails (declined compile, etc.).
            var companions = new List<ActiveDb>();
            for (int i = 1; i < allSelected.Count; i++)
            {
                var c = dbFactory.Build(allSelected[i], displayPlcName);
                if (c != null) companions.Add(c);
            }
            if (companions.Count > 0)
                Log.Information("Multi-DB session: {N} companion DB(s) active alongside {Primary}",
                    companions.Count, focused.Info.Name);

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

            var vm = new BulkChangeViewModel(
                focused.Info, focused.Xml, analyzer, bulkService, usageTracker, configLoader,
                // Thunk: the focused ActiveDb may be swapped by switchToDataBlock,
                // so we route Apply through the latest reference, not a captured one.
                onApply: xml => currentFocused.OnApply!(xml),
                onRefreshTagTables: plcSoftware != null
                    ? () => tagTableExporter.Export(plcSoftware, tagTableDir)
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
                enumerateDataBlocks: project != null
                    ? new Func<IReadOnlyList<DataBlockSummary>>(() => discovery.EnumerateDataBlocks(project))
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
                additionalActiveDbs: companions,
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
                    : null);

            licenseService.StartHeartbeat();
            var dialog = new BulkChangeDialog(vm);
            dialog.ShowDialog();
            licenseService.StopHeartbeat();
            licenseService.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in Bulk Change OnClick");
            prompt.ShowError(Res.Get("Rollback_Title"), ex.ToString());
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
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
                "BlockParam", "config.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
