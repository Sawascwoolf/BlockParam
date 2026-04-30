using System.IO;
using System.Threading;
using Siemens.Engineering;
using Siemens.Engineering.AddIn.Menu;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using BlockParam.Config;
using BlockParam.Diagnostics;
using BlockParam.Licensing;
using BlockParam.Localization;
using BlockParam.Services;
using BlockParam.SimaticML;
using BlockParam.UI;

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
            // Mutable: ImportBlock disposes the old DataBlock instance; we must refresh
            // this reference after every import so subsequent Apply clicks don't hit a
            // disposed GlobalDB.
            var selection = provider.GetSelection<DataBlock>().FirstOrDefault();
            if (selection == null) return;

            EnsureTempCacheCleaned();
            ApplyConfiguredLanguage();

            Log.Information("Bulk Change v{Version} clicked on DB: {DbName}",
                typeof(BulkChangeContextMenu).Assembly.GetName().Version, selection.Name);

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
            var freeTracker = new LocalUsageTracker(usagePath, dailyLimit: 3);

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

                    var modifiedPath = Path.Combine(tempDir, $"{dbInfo.Name}_modified.xml");
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
                referenceLanguage: referenceLanguage);

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

        var tagGroup = plcSoftware.TagTableGroup;
        int count = 0;

        foreach (var table in tagGroup.TagTables)
        {
            var filePath = Path.Combine(exportDir, $"{table.Name}.xml");
            if (File.Exists(filePath)) File.Delete(filePath);
            table.Export(new FileInfo(filePath), ExportOptions.WithDefaults);
            count++;
        }

        foreach (var subGroup in tagGroup.Groups)
        {
            foreach (var table in subGroup.TagTables)
            {
                var filePath = Path.Combine(exportDir, $"{subGroup.Name}_{table.Name}.xml");
                if (File.Exists(filePath)) File.Delete(filePath);
                table.Export(new FileInfo(filePath), ExportOptions.WithDefaults);
                count++;
            }
        }

        return count;
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
        => groupPath == null ? type.Name : $"{groupPath.Replace('/', '_')}_{type.Name}";

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
