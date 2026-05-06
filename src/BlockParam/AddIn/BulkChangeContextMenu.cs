using System.IO;
using System.Threading;
using Siemens.Engineering;
using Siemens.Engineering.AddIn.Menu;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
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
        try
        {
            // Multi-DB selection (#58). The user can right-click multiple DBs in
            // the project tree; we open one dialog that operates on all of them.
            // Index 0 is the focused DB (drives the dialog title / scope view);
            // additional selections become companions and participate in bulk
            // preview / Apply equally.
            var allSelected = provider.GetSelection<DataBlock>().ToList();
            if (allSelected.Count == 0) return;
            // Mutable: ImportBlock disposes the old DataBlock instance; we must refresh
            // this reference after every import so subsequent Apply clicks don't hit a
            // disposed GlobalDB.
            var selection = allSelected[0];

            EnsureTempCacheCleaned();
            ApplyConfiguredLanguage();

            Log.Information("Bulk Change v{Version} clicked on DB: {DbName}{Extra}",
                typeof(BulkChangeContextMenu).Assembly.GetName().Version, selection.Name,
                allSelected.Count > 1 ? $" (+{allSelected.Count - 1} companion DB(s))" : "");

            // Two language axes (#50). UI language: Thread.CurrentUICulture,
            // either the OS default or the user's config.json override
            // (applied above). Project text languages: read from
            // project.LanguageSettings further down for DB comment rendering.
            Log.Information("UI culture: {Culture} (ResourceManager picks Strings.<lang>.resx satellite)",
                System.Threading.Thread.CurrentThread.CurrentUICulture.Name);

            var adapter = new TiaPortalAdapter(_tiaPortal);

            // Per-project scope so parallel TIA instances / switched projects cannot share cache dirs (#14).
            // Layout: %TEMP%\BlockParam\<scope>\{TagTables,UdtTypes,DB export}
            var project = _tiaPortal.Projects.FirstOrDefault();
            var scope = ProjectScope.ForPath(project?.Path?.FullName);
            var tempDir = Path.Combine(Path.GetTempPath(), "BlockParam", scope);

            // Export DB to XML - handle inconsistent blocks
            string xmlPath = null!;
            if (!TryExportWithCompilePrompt(selection, adapter, () => xmlPath = adapter.ExportBlock(selection, tempDir)))
                return;

            var xml = File.ReadAllText(xmlPath);

            var plcSoftware = FindPlcSoftware(selection);

            // Build an optional constant resolver from any previously exported
            // tag tables so symbolic array bounds (Array[1..MAX_VALVES]) expand
            // correctly. If no tables are cached yet the array stays collapsed
            // with UnresolvedBound set; the user can then export tag tables
            // from the dialog and refresh. Uses the per-project scoped dir
            // (#14) to avoid cross-project bleed and match where `onRefreshTagTables` writes.
            var prestartTagTableDir = Path.Combine(tempDir, "TagTables");
            IConstantResolver? constantResolver = null;
            if (Directory.Exists(prestartTagTableDir) &&
                Directory.GetFiles(prestartTagTableDir, "*.xml").Length > 0)
            {
                var prestartCache = new TagTableCache(new XmlFileTagTableReader(prestartTagTableDir));
                constantResolver = new TagTableConstantResolver(prestartCache);
            }

            // Validate UDT cache against TIA's per-type ModifiedDate and re-export stale entries.
            var udtDir = Path.Combine(tempDir, "UdtTypes");
            if (plcSoftware != null)
            {
                var refreshed = RefreshStaleUdtCache(plcSoftware, udtDir);
                Log.Information("UDT cache validation: {Refreshed} stale file(s) re-exported", refreshed);
            }
            var udtResolver = new UdtSetPointResolver();
            udtResolver.LoadFromDirectory(udtDir);
            var commentResolver = new UdtCommentResolver();
            commentResolver.LoadFromDirectory(udtDir);
            Log.Information("UDT cache loaded: {TypeCount} types from {Dir}", udtResolver.TypeCount, udtDir);

            // Parse structure (constant resolver expands symbolic array bounds;
            // UDT resolvers fill in SetPoint / Comment for nested UDT leaves whose
            // DB XML carries no per-instance override).
            var parser = new SimaticMLParser(constantResolver, udtResolver, commentResolver);
            var dbInfo = parser.Parse(xml);
            if (dbInfo.UnresolvedUdts.Count > 0)
            {
                Log.Information("DB {Name} references {Count} UDT(s) not in cache: {Types}",
                    dbInfo.Name, dbInfo.UnresolvedUdts.Count, string.Join(", ", dbInfo.UnresolvedUdts));
            }
            Log.Information("Parsed DB {Name}: {MemberCount} top-level members, {TotalCount} total",
                dbInfo.Name, dbInfo.Members.Count, dbInfo.AllMembers().Count());

            // Create services
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

            var logger = new ChangeLogger();
            var bulkService = new BulkChangeService(logger, configLoader);
            var analyzer = new HierarchyAnalyzer();
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlockParam");
            var usagePath = Path.Combine(appDataDir, "usage.dat");
            var freeTracker = new LocalUsageTracker(usagePath);

            // License service: heartbeat-based concurrent session validation.
            // #20: probe the machine-wide managed key file first so multi-seat
            // customers can roll out / rotate keys via deployment tooling
            // (batch / SCCM / Intune / GPO) without each engineer re-typing the
            // key. Falls back to the per-user cache when no managed file exists.
            var serverUrl = configLoader.ReadLicenseServerUrl() ?? OnlineLicenseService.DefaultServerUrl;
            var licenseService = new OnlineLicenseService(
                appDataDir,
                serverUrl,
                sharedLicenseFilePath: OnlineLicenseService.DefaultSharedLicenseFilePath);
            var usageTracker = new LicensedUsageTracker(licenseService, freeTracker);

            // Tag table export: lazy (only when needed by autocomplete). Scoped per project (#14).
            var tagTableDir = Path.Combine(tempDir, "TagTables");

            // Update check (#61). Single shared service per dialog session;
            // cache TTL gates the GitHub call so opening multiple DBs in a
            // session does not multiply network traffic.
            IUpdateCheckService? updateCheckService = null;
            try
            {
                var current = typeof(BulkChangeContextMenu).Assembly.GetName().Version
                    ?? new Version(0, 0, 0);
                updateCheckService = new UpdateCheckService(
                    fetcher: new GitHubReleaseFetcher(),
                    currentVersion: VersionTag.FromSystemVersion(current),
                    cachePath: Path.Combine(appDataDir, "update-check.json"),
                    readSettings: () => configLoader.ReadUpdateCheckSettings());
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "UpdateCheck: cannot construct service — feature disabled this session");
            }

            // PLC count drives whether the header shows a "{PLC} / " prefix
            // (#59 follow-up). Single-PLC projects — the ≈85% common case —
            // get the cleaner "DB only" header; multi-PLC projects always
            // get the prefix so users can tell which PLC they're operating on.
            // Single source of truth for the displayed PLC name: empty when
            // we want it suppressed, real name otherwise. Both enumeration
            // and the VM's currentPlcName use this value, so the stash key
            // (which includes PlcName) stays consistent across stash + restore.
            var plcCount = CountPlcSoftwaresInProject(project);
            var displayPlcName = plcSoftware != null && plcCount > 1
                ? SafeGetPlcName(plcSoftware)
                : "";

            // Companion DBs (#58). Index 0 of allSelected is the focused DB
            // (already exported / parsed above); 1+ each get the same export +
            // parse + per-DB Apply closure as the focused one.
            var companions = new List<ActiveDb>();
            for (int i = 1; i < allSelected.Count; i++)
            {
                var companion = BuildCompanionActiveDb(
                    allSelected[i], adapter, tempDir, constantResolver,
                    udtResolver, commentResolver, displayPlcName);
                if (companion != null)
                    companions.Add(companion);
            }
            if (companions.Count > 0)
                Log.Information("Multi-DB session: {N} companion DB(s) active alongside {Primary}",
                    companions.Count, dbInfo.Name);

            // Open dialog
            var vm = new BulkChangeViewModel(
                dbInfo, xml, analyzer, bulkService, usageTracker, configLoader,
                onApply: modifiedXml =>
                {
                    Log.Information("Apply: writing modified XML for {DbName}", dbInfo.Name);

                    // #19 follow-up: BackupBlock also calls Export; if TIA's ImportBlock left
                    // the previous round's block inconsistent, the export fails the same way
                    // it did in the initial path. Reuse the compile-prompt helper instead of
                    // crashing silently.
                    if (!TryExportWithCompilePrompt(selection, adapter, () => adapter.BackupBlock(selection, tempDir)))
                    {
                        Log.Information("Apply cancelled: user declined compile for {DbName}", dbInfo.Name);
                        throw new OperationCanceledException("User declined to compile the inconsistent block.");
                    }

                    var modifiedPath = Path.Combine(tempDir, $"{SafeFileName.Sanitize(dbInfo.Name)}_modified.xml");
                    File.WriteAllText(modifiedPath, modifiedXml);
                    var blockGroup = (PlcBlockGroup)adapter.GetBlockGroup(selection);
                    adapter.ImportBlock(blockGroup, modifiedPath);

                    // #19: TIA's ImportBlock(Override) disposes the old DataBlock instance.
                    // Re-resolve the fresh instance from the block group so a second Apply
                    // inside the same dialog session doesn't throw on the stale reference.
                    var fresh = blockGroup.Blocks.Find(dbInfo.Name) as DataBlock;
                    if (fresh != null)
                    {
                        selection = fresh;
                    }
                    else
                    {
                        Log.Warning("Could not re-resolve DataBlock '{DbName}' after import — next Apply may fail", dbInfo.Name);
                    }
                    Log.Information("Import completed for {DbName}", dbInfo.Name);
                },
                onRefreshTagTables: plcSoftware != null
                    ? () => ExportTagTables(plcSoftware, tagTableDir)
                    : null,
                tagTableDir: plcSoftware != null ? tagTableDir : null,
                projectLanguages: projectLanguages,
                licenseService: licenseService,
                onRefreshUdtTypes: plcSoftware != null
                    ? new Action(() => { RefreshStaleUdtCache(plcSoftware, udtDir); })
                    : null,
                udtDir: udtDir,
                udtResolver: udtResolver,
                commentResolver: commentResolver,
                editingLanguage: editingLanguage,
                referenceLanguage: referenceLanguage,
                updateCheckService: updateCheckService,
                // DB-switcher (#59). Wired only when a PlcSoftware was found —
                // otherwise enumeration has nowhere to walk and the dropdown
                // stays hidden.
                enumerateDataBlocks: project != null
                    ? new Func<IReadOnlyList<DataBlockSummary>>(() => EnumerateDataBlocks(project))
                    : null,
                currentPlcName: displayPlcName,
                switchToDataBlock: plcSoftware != null
                    ? new Func<DataBlockSummary, string>(summary =>
                    {
                        // Resolve against the summary's owning PLC so a switch
                        // to a DB on a different PLC works. Falls back to the
                        // launch PLC when the project lookup fails (e.g. PLC
                        // got renamed mid-session).
                        var sourcePlc = (project != null
                            ? FindPlcSoftwareByName(project, summary.PlcName)
                            : null) ?? plcSoftware;
                        var newSelection = ResolveDataBlock(sourcePlc, summary)
                            ?? throw new InvalidOperationException(
                                $"DB '{summary.Name}' not found in project");

                        // Re-export under the same compile-prompt guard the
                        // initial export used (#19/#27) so an inconsistent
                        // target DB surfaces the same UX, not a raw stack trace.
                        string newXmlPath = null!;
                        if (!TryExportWithCompilePrompt(newSelection, adapter,
                                () => newXmlPath = adapter.ExportBlock(newSelection, tempDir)))
                            throw new OperationCanceledException(
                                "User declined to compile the inconsistent target DB.");

                        var newXml = File.ReadAllText(newXmlPath);

                        // Re-parse with the same resolvers so UDT setpoints /
                        // comments and tag-table constants stay consistent
                        // across the switch.
                        IConstantResolver? newConstantResolver = null;
                        if (Directory.Exists(tagTableDir)
                            && Directory.GetFiles(tagTableDir, "*.xml").Length > 0)
                        {
                            newConstantResolver = new TagTableConstantResolver(
                                new TagTableCache(new XmlFileTagTableReader(tagTableDir)));
                        }
                        var newParser = new SimaticMLParser(
                            newConstantResolver, udtResolver, commentResolver);
                        var newDbInfo = newParser.Parse(newXml);

                        // Update the closure-captured locals so the next Apply
                        // talks to the right DataBlock instance and uses the
                        // new DB name in log messages / file paths.
                        selection = newSelection;
                        dbInfo = newDbInfo;

                        Log.Information("DB switch: now editing {DbName}", newDbInfo.Name);
                        return newXml;
                    })
                    : null,
                additionalActiveDbs: companions,
                // Multi-DB add via dropdown (#58): the VM calls back here
                // to build a fully-wired ActiveDb (with per-DB OnApply)
                // for any DB the user checks in the popup. Resolves the
                // summary back to a live DataBlock first, then routes
                // through the same BuildCompanionActiveDb helper used for
                // context-menu-pre-selected companions.
                buildActiveDbForSummary: plcSoftware != null
                    ? new Func<DataBlockSummary, ActiveDb?>(summary =>
                    {
                        // Cross-PLC: resolve against the summary's owning PLC,
                        // not the dialog's launch PLC. Each chip then carries
                        // the right PLC name for its group header.
                        var sourcePlc = (project != null
                            ? FindPlcSoftwareByName(project, summary.PlcName)
                            : null) ?? plcSoftware;
                        var initial = ResolveDataBlock(sourcePlc, summary);
                        if (initial == null)
                        {
                            Log.Warning(
                                "buildActiveDbForSummary: '{Name}' not found in project",
                                summary.Name);
                            return null;
                        }
                        return BuildCompanionActiveDb(
                            initial, adapter, tempDir,
                            constantResolver, udtResolver, commentResolver,
                            summary.PlcName);
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
            ShowMessageBox(
                ex.ToString(),
                Res.Get("Rollback_Title"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private static int ExportTagTables(PlcSoftware plcSoftware, string exportDir)
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

    /// <summary>
    /// Walks the tag-table group tree depth-first, yielding every table at any
    /// nesting depth. The previous implementation only handled root + one
    /// subgroup level, silently dropping anything deeper — real customer
    /// projects nest 4+ levels and the missing constants surfaced as ~200
    /// false "value out of range" inspector entries (#63).
    /// </summary>
    private static IEnumerable<PlcTagTable> EnumerateTagTablesRecursive(PlcTagTableGroup group)
    {
        foreach (var table in group.TagTables)
            yield return table;

        foreach (var sub in group.Groups)
            foreach (var table in EnumerateTagTablesRecursive(sub))
                yield return table;
    }

    /// <summary>
    /// Walks every PLC's Program blocks tree in the project and projects every
    /// Data Block to a <see cref="DataBlockSummary"/> for the in-dialog
    /// DB-switcher dropdown. Lazy + on-demand: only invoked when the user
    /// opens the dropdown, then cached for the dialog session by the VM.
    /// Each summary carries its own <see cref="DataBlockSummary.PlcName"/> so
    /// the picker can group rows per PLC and chips can show the right owner.
    /// </summary>
    private static IReadOnlyList<DataBlockSummary> EnumerateDataBlocks(Project? project)
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

    /// <summary>
    /// Yields every (PlcSoftware, displayName) pair in the project. Same walk
    /// pattern as <see cref="CountPlcSoftwaresInProject"/> (device tree →
    /// device items → SoftwareContainer); per-item failures are swallowed so
    /// one mis-configured device cannot hide the rest of the project.
    /// </summary>
    private static IEnumerable<(PlcSoftware plc, string name)> EnumerateAllPlcSoftwares(Project project)
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

    private static PlcSoftware? FindPlcSoftwareByName(Project project, string plcName)
    {
        foreach (var (plc, name) in EnumerateAllPlcSoftwares(project))
        {
            if (string.Equals(name, plcName, StringComparison.Ordinal)) return plc;
        }
        return null;
    }

    /// <summary>
    /// Builds an <see cref="ActiveDb"/> for a non-focused DB selected in the
    /// project tree (#58). Mirrors the focused-DB setup: export + parse + an
    /// OnApply closure that re-imports the modified XML and refreshes the
    /// stale post-import handle. Returns null if export fails (e.g. user
    /// declines the compile prompt for an inconsistent DB) — the dialog
    /// opens without that companion.
    /// </summary>
    private ActiveDb? BuildCompanionActiveDb(
        DataBlock initialSelection,
        TiaPortalAdapter adapter,
        string tempDir,
        IConstantResolver? constantResolver,
        UdtSetPointResolver udtResolver,
        UdtCommentResolver commentResolver,
        string plcName)
    {
        // TIA's ImportBlock disposes the previous DataBlock reference on every
        // Apply, so we re-resolve after each import (line below). The captured
        // local is shared across both lambdas via the compiler-generated
        // closure, so the re-assignment is visible on the next Apply.
        DataBlock liveDb = initialSelection;

        string xmlPath = null!;
        if (!TryExportWithCompilePrompt(liveDb, adapter,
                () => xmlPath = adapter.ExportBlock(liveDb, tempDir)))
        {
            Log.Information("Companion DB skipped (user declined compile): {DbName}",
                initialSelection.Name);
            return null;
        }
        var xml = File.ReadAllText(xmlPath);

        var parser = new SimaticMLParser(constantResolver, udtResolver, commentResolver);
        var info = parser.Parse(xml);

        Log.Information("Companion DB parsed: {Name} ({Members} top-level members)",
            info.Name, info.Members.Count);

        Action<string> onApply = modifiedXml =>
        {
            Log.Information("Apply: writing modified XML for companion DB {DbName}", info.Name);

            if (!TryExportWithCompilePrompt(liveDb, adapter,
                    () => adapter.BackupBlock(liveDb, tempDir)))
            {
                Log.Information("Apply cancelled: user declined compile for companion {DbName}",
                    info.Name);
                throw new OperationCanceledException(
                    "User declined to compile the inconsistent companion block.");
            }

            var modifiedPath = Path.Combine(tempDir,
                $"{SafeFileName.Sanitize(info.Name)}_modified.xml");
            File.WriteAllText(modifiedPath, modifiedXml);
            var blockGroup = (PlcBlockGroup)adapter.GetBlockGroup(liveDb);
            adapter.ImportBlock(blockGroup, modifiedPath);

            // Re-resolve to the post-import live instance so the next Apply
            // does not hit a disposed reference (#19).
            var fresh = blockGroup.Blocks.Find(info.Name) as DataBlock;
            if (fresh != null)
            {
                liveDb = fresh;
            }
            else
            {
                Log.Warning(
                    "Could not re-resolve companion DataBlock '{DbName}' after import — next Apply may fail",
                    info.Name);
            }
            Log.Information("Companion import completed for {DbName}", info.Name);
        };

        return new ActiveDb(info, xml, onApply, plcName: plcName);
    }

    private static string SafeGetPlcName(PlcSoftware plc)
    {
        try { return plc.Name ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Walks <paramref name="project"/>'s device tree and counts the
    /// <see cref="PlcSoftware"/> instances. Used by the DB-switcher header
    /// (#59 follow-up): in single-PLC projects (≈85% of users) the PLC name
    /// would be redundant chrome, so we suppress the prefix unless the
    /// project actually has more than one PLC.
    /// </summary>
    private static int CountPlcSoftwaresInProject(Project? project)
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

    private static IEnumerable<DeviceItem> EnumerateDeviceItemsRecursive(DeviceItemComposition items)
    {
        foreach (DeviceItem item in items)
        {
            yield return item;
            foreach (var child in EnumerateDeviceItemsRecursive(item.DeviceItems))
                yield return child;
        }
    }

    /// <summary>
    /// Resolves a <see cref="DataBlockSummary"/> back to a live
    /// <see cref="DataBlock"/> by walking the same tree the dropdown was
    /// populated from. Folder path is part of the match so two DBs with the
    /// same name in different folders don't collide.
    /// </summary>
    private static DataBlock? ResolveDataBlock(PlcSoftware plcSoftware, DataBlockSummary summary)
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

    private static IEnumerable<(PlcType type, string? groupPath)> EnumerateTypesRecursive(
        PlcTypeGroup group, string? parentPath)
    {
        foreach (var type in group.Types)
            yield return (type, parentPath);

        foreach (var sub in group.Groups)
        {
            var subPath = parentPath == null ? sub.Name : $"{parentPath}/{sub.Name}";
            foreach (var entry in EnumerateTypesRecursive(sub, subPath))
                yield return entry;
        }
    }

    private static string FileNameFor(PlcType type, string? groupPath)
    {
        var raw = groupPath == null ? type.Name : $"{groupPath.Replace('/', '_')}_{type.Name}";
        return SafeFileName.Sanitize(raw);
    }

    /// <summary>
    /// Re-exports any UDT whose TIA <c>ModifiedDate</c> (or <c>InterfaceModifiedDate</c>)
    /// is newer than the cached XML file, or whose cache file is missing. Precise
    /// replacement for a time-based TTL — only stale entries are re-exported.
    /// If TIA flags any UDT as inconsistent during export, collects them and offers
    /// a single prompt to compile those UDTs individually and retry (#27).
    /// Returns the number of files written (initial pass + successful retries).
    ///
    /// Note: <see cref="PlcType"/> instances are captured only in local closures on
    /// the stack — never stored in fields or properties, which the TIA V20 Add-In
    /// Publisher rejects for engineering-object types (lifecycle safety check).
    /// </summary>
    private static int RefreshStaleUdtCache(PlcSoftware plcSoftware, string exportDir)
    {
        Directory.CreateDirectory(exportDir);
        int refreshed = 0;
        // Each entry captures its PlcType via closures; nothing of the TIA object
        // survives outside this method's stack frame.
        var inconsistent = new List<(string displayName, Func<bool> compile, Func<bool> reExport)>();

        var typeGroup = plcSoftware.TypeGroup;
        foreach (var (type, groupPath) in EnumerateTypesRecursive(typeGroup, parentPath: null))
            refreshed += ExportIfStale(type, exportDir, groupPath, inconsistent);

        if (inconsistent.Count > 0)
        {
            refreshed += InconsistentUdtRetry.RetryAfterCompile(
                inconsistent,
                nameOf: i => i.displayName,
                tryCompile: i => i.compile(),
                tryReExport: i => i.reExport(),
                askUser: AskUserToCompileInconsistentUdts);
        }

        return refreshed;
    }

    private static int ExportIfStale(
        PlcType type, string exportDir, string? groupPath,
        List<(string displayName, Func<bool> compile, Func<bool> reExport)> inconsistent)
    {
        var filePath = Path.Combine(exportDir, $"{FileNameFor(type, groupPath)}.xml");
        var displayName = groupPath == null ? type.Name : $"{groupPath}/{type.Name}";

        try
        {
            // Latest point in time where the type's layout or metadata could have changed.
            var tiaModified = type.ModifiedDate;
            try
            {
                var interfaceModified = type.InterfaceModifiedDate;
                if (interfaceModified > tiaModified) tiaModified = interfaceModified;
            }
            catch { /* some types may not expose this — fall back to ModifiedDate only */ }

            if (File.Exists(filePath))
            {
                var fileMtime = File.GetLastWriteTime(filePath);
                // Cache is fresh if the file is at least as new as TIA's modification stamp.
                if (fileMtime >= tiaModified) return 0;
            }

            File.Delete(filePath);
            type.Export(new FileInfo(filePath), ExportOptions.WithDefaults);
            return 1;
        }
        catch (Exception ex) when (IsInconsistencyError(ex))
        {
            // TIA flags this UDT as inconsistent — collect for a single post-pass
            // compile prompt instead of silently hiding the comment fallback (#27).
            Log.Warning("UDT '{Name}' cannot be exported: inconsistent — will offer compile", displayName);
            var capturedType = type;
            inconsistent.Add((
                displayName,
                compile: () => TryCompileUdt(capturedType, displayName),
                reExport: () => TryReExportUdt(capturedType, filePath, displayName)));
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to refresh UDT cache for {Name}", displayName);
            return 0;
        }
    }

    private static bool TryCompileUdt(PlcType type, string displayName)
    {
        try
        {
            var compilable = type.GetService<ICompilable>();
            if (compilable == null)
            {
                Log.Warning("No ICompilable service found for UDT {Name}", displayName);
                return false;
            }
            var result = compilable.Compile();
            Log.Information("Compiled UDT {Name}: {State}", displayName, result.State);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to compile UDT {Name}", displayName);
            return false;
        }
    }

    private static bool TryReExportUdt(PlcType type, string filePath, string displayName)
    {
        try
        {
            if (File.Exists(filePath)) File.Delete(filePath);
            type.Export(new FileInfo(filePath), ExportOptions.WithDefaults);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to re-export UDT {Name} after compile", displayName);
            return false;
        }
    }

    private static bool AskUserToCompileInconsistentUdts(IReadOnlyList<string> udtNames)
    {
        var message = Res.Format("Udt_InconsistentPrompt", udtNames.Count, string.Join(", ", udtNames));
        var answer = ShowMessageBox(
            message,
            Res.Get("Udt_InconsistentPromptTitle"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            Log.Information("User declined compile for {Count} inconsistent UDT(s)", udtNames.Count);
            return false;
        }
        return true;
    }

    private static PlcSoftware? FindPlcSoftware(DataBlock block)
    {
        // Walk up the parent chain until we find PlcSoftware
        IEngineeringObject? current = block;
        while (current != null)
        {
            if (current is PlcSoftware plc) return plc;
            current = current.Parent;
        }
        return null;
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

    /// <summary>
    /// Runs <paramref name="exportAction"/> and, if it fails with TIA's "Inconsistent block"
    /// error, prompts the user to compile the block and retries. Returns false if the user
    /// declined the compile (caller should abort); returns true if the export succeeded
    /// (possibly after a retry). Any non-inconsistency error propagates unchanged.
    /// </summary>
    private static bool TryExportWithCompilePrompt(DataBlock block, TiaPortalAdapter adapter, Action exportAction)
    {
        try
        {
            exportAction();
            return true;
        }
        catch (Exception ex) when (IsInconsistencyError(ex))
        {
            Log.Warning("DB {Name} is inconsistent, asking user to compile", block.Name);
            var answer = ShowMessageBox(
                Res.Format("Db_InconsistentPrompt", block.Name),
                Res.Get("Udt_InconsistentPromptTitle"),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                Log.Information("User cancelled compilation for {Name}", block.Name);
                return false;
            }

            adapter.CompileBlock(block);
            exportAction();
            return true;
        }
    }

    private static bool IsInconsistencyError(Exception ex) =>
        InconsistencyDetector.Matches(ex);

    private static System.Windows.MessageBoxResult ShowMessageBox(
        string message, string title,
        System.Windows.MessageBoxButton buttons,
        System.Windows.MessageBoxImage icon)
    {
        // Create a hidden topmost window as owner so the dialog appears in foreground
        var owner = new System.Windows.Window
        {
            Width = 0, Height = 0,
            WindowStyle = System.Windows.WindowStyle.None,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = true
        };
        owner.Show();

        try
        {
            return System.Windows.MessageBox.Show(owner, message, title, buttons, icon);
        }
        finally
        {
            owner.Close();
        }
    }
}
