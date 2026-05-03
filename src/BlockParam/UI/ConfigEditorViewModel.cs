using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using BlockParam.Diagnostics;
using BlockParam.Config;

namespace BlockParam.UI;

/// <summary>
/// ViewModel for the file-based Configuration Editor dialog.
///
/// Two-level model: <see cref="RuleFiles"/> are physical .json files on disk,
/// each containing 1..N <see cref="RuleViewModel"/> entries (issue #70 fix —
/// previously rules 2..N were silently dropped on save).
/// </summary>
public class ConfigEditorViewModel : ViewModelBase
{
    private readonly ConfigLoader _configLoader;
    private string _sharedRulesDirectory = "";
    private string _validationMessage = "";
    private string _filterText = "";
    private RuleViewModel? _selectedRule;
    private RuleFileViewModel? _selectedFile;

    public ConfigEditorViewModel(
        ConfigLoader configLoader,
        IEnumerable<string>? tagTableNames = null)
    {
        _configLoader = configLoader;

        RuleFiles = new ObservableCollection<RuleFileViewModel>();
        TagTableNames = new ObservableCollection<string>();
        FilteredRuleFiles = CollectionViewSource.GetDefaultView(RuleFiles);
        FilteredRuleFiles.Filter = FilterFile;

        NewRuleCommand = new RelayCommand(ExecuteNewRule);
        NewFileCommand = new RelayCommand(ExecuteNewFile);
        DuplicateSelectedCommand = new RelayCommand(ExecuteDuplicateSelected, () => SelectedRule != null);
        DeleteSelectedCommand = new RelayCommand(ExecuteDeleteSelected, () => SelectedRule != null || SelectedFile != null);
        DeleteFileCommand = new RelayCommand(p => ExecuteDeleteFile(p as RuleFileViewModel), p => p is RuleFileViewModel);
        ResetToBaseCommand = new RelayCommand(ExecuteResetToBase, () => SelectedFile?.HasOverrides == true);
        SaveCommand = new RelayCommand(ExecuteSave);
        SaveAndCloseCommand = new RelayCommand(ExecuteSaveAndClose);
        ImportFilesCommand = new RelayCommand(ExecuteImportFiles);
        ExportFileCommand = new RelayCommand(p => ExecuteExportFile(p as RuleFileViewModel), p => p is RuleFileViewModel);
        ExportSelectedCommand = new RelayCommand(ExecuteExportSelected, () => SelectedFile != null);

        LoadAllFiles();

        if (tagTableNames != null)
            foreach (var name in tagTableNames)
                TagTableNames.Add(name);
    }

    public ObservableCollection<RuleFileViewModel> RuleFiles { get; }
    public ICollectionView FilteredRuleFiles { get; }
    public ObservableCollection<string> TagTableNames { get; }

    public event Action? RequestClose;

    /// <summary>The rule currently bound to the detail panel.</summary>
    public RuleViewModel? SelectedRule
    {
        get => _selectedRule;
        set
        {
            var previous = _selectedRule;
            if (SetProperty(ref _selectedRule, value))
            {
                if (previous != null) previous.IsSelected = false;
                if (value != null)
                {
                    value.IsSelected = true;
                    if (value.ParentFile != null)
                        SelectedFile = value.ParentFile;
                }
            }
        }
    }

    /// <summary>
    /// The file currently in focus. Set automatically when a rule is selected,
    /// or explicitly when the user clicks a file header (no rule selected).
    /// </summary>
    public RuleFileViewModel? SelectedFile
    {
        get => _selectedFile;
        set => SetProperty(ref _selectedFile, value);
    }

    public string SharedRulesDirectory
    {
        get => _sharedRulesDirectory;
        set => SetProperty(ref _sharedRulesDirectory, value);
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                FilteredRuleFiles.Refresh();
                AutoExpandMatches();
            }
        }
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        set => SetProperty(ref _validationMessage, value);
    }

    public ICommand NewRuleCommand { get; }
    public ICommand NewFileCommand { get; }
    public ICommand DuplicateSelectedCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand DeleteFileCommand { get; }
    public ICommand ResetToBaseCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAndCloseCommand { get; }

    /// <summary>
    /// UI-only stub for now (#70 follow-up). Wires the toolbar button so the
    /// import flow can be added without touching the ViewModel surface again.
    /// </summary>
    public ICommand ImportFilesCommand { get; }

    /// <summary>UI-only stub: export currently selected file. Logic out of scope.</summary>
    public ICommand ExportSelectedCommand { get; }

    /// <summary>UI-only stub: export a specific file (used by the file-header overflow).</summary>
    public ICommand ExportFileCommand { get; }

    /// <summary>True when the import/export flow is implemented. Always false until logic lands.</summary>
    public bool IsImportExportImplemented => false;

    private bool FilterFile(object obj)
    {
        if (string.IsNullOrWhiteSpace(_filterText)) return true;
        if (obj is not RuleFileViewModel file) return false;

        if (file.FileName.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        return file.Rules.Any(r =>
            r.PathPattern.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0
            || r.TagTableName.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0
            || r.CommentTemplate.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private void AutoExpandMatches()
    {
        if (string.IsNullOrWhiteSpace(_filterText)) return;
        foreach (var file in RuleFiles)
        {
            var anyMatch = file.Rules.Any(r =>
                r.PathPattern.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0
                || r.TagTableName.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0
                || r.CommentTemplate.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0);
            if (anyMatch) file.IsExpanded = true;
        }
    }

    /// <summary>
    /// Scans Project / Local / Shared directories. Files with the same name
    /// across multiple sources are grouped — the highest-priority version wins
    /// and the others become <see cref="RuleFileViewModel.OverriddenVersions"/>.
    /// </summary>
    private void LoadAllFiles()
    {
        RuleFiles.Clear();

        var config = _configLoader.GetConfig();
        SharedRulesDirectory = config?.RulesDirectory ?? "";

        var allFiles = new List<RuleFileViewModel>();

        if (!string.IsNullOrWhiteSpace(SharedRulesDirectory))
        {
            var sharedDir = ResolveSharedDirectory(SharedRulesDirectory);
            LoadFilesFromDirectory(sharedDir, RuleSource.Shared, allFiles);
        }

        var localDir = _configLoader.GetLocalRulesDirectory();
        LoadFilesFromDirectory(localDir, RuleSource.Local, allFiles);

        var projectDir = _configLoader.GetTiaProjectRulesDirectory();
        if (projectDir != null && Directory.Exists(projectDir))
            LoadFilesFromDirectory(projectDir, RuleSource.TiaProject, allFiles);

        // Group by filename (override unit is the file). Same physical path
        // across sources (shared==local) gets deduped first.
        var groups = allFiles.GroupBy(f => f.FileName, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var unique = group
                .GroupBy(f => Path.GetFullPath(f.FilePath), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(f => f.Source).First())
                .OrderByDescending(f => f.Source)
                .ToList();

            var effective = unique[0];

            for (int i = 1; i < unique.Count; i++)
                effective.OverriddenVersions.Add(unique[i]);

            if (effective.OverriddenVersions.Count > 0)
                effective.IsOverride = true;

            effective.NotifyOverrideChanged();
            RuleFiles.Add(effective);
        }
    }

    private void LoadFilesFromDirectory(string directoryPath, RuleSource source,
        List<RuleFileViewModel> target)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return;

        string[] files;
        try
        {
            files = Directory.GetFiles(directoryPath, "*.json");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Cannot access rules directory: {Path}", directoryPath);
            return;
        }

        foreach (var file in files)
        {
            var vm = RuleFileViewModel.FromFile(file, source);
            if (vm != null)
                target.Add(vm);
        }
    }

    private string ResolveSharedDirectory(string rulesDir)
    {
        if (Path.IsPathRooted(rulesDir))
            return rulesDir;

        var localDir = _configLoader.GetLocalRulesDirectory();
        var configDir = Path.GetDirectoryName(localDir);
        if (configDir != null)
            return Path.Combine(configDir, rulesDir);

        return rulesDir;
    }

    private string? GetDirectoryForSource(RuleSource source)
    {
        return source switch
        {
            RuleSource.TiaProject => _configLoader.GetTiaProjectRulesDirectory(),
            RuleSource.Local => _configLoader.GetLocalRulesDirectory(),
            RuleSource.Shared => string.IsNullOrWhiteSpace(SharedRulesDirectory)
                ? null : ResolveSharedDirectory(SharedRulesDirectory),
            _ => null
        };
    }

    /// <summary>
    /// Adds a new rule to the currently selected file. If no file is selected
    /// (e.g. first-run, empty editor) we create a new file with the rule inside.
    /// </summary>
    private void ExecuteNewRule()
    {
        try
        {
            var file = SelectedFile ?? SelectedRule?.ParentFile;
            if (file == null)
            {
                ExecuteNewFile();
                return;
            }

            var rule = new RuleViewModel { IsNew = true };
            file.Rules.Add(rule);
            file.IsExpanded = true;
            SelectedRule = rule;
            ValidationMessage = "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ExecuteNewRule failed");
            ValidationMessage = $"Could not create new rule: {ex.Message}";
        }
    }

    /// <summary>Creates a new file with one starter rule and selects it.</summary>
    private void ExecuteNewFile()
    {
        try
        {
            var defaultSource = GetDefaultNewFileSource();
            var dir = GetDirectoryForSource(defaultSource);
            if (dir == null) { ValidationMessage = "No valid save directory available."; return; }

            var fileName = GenerateUniqueNewFileName(dir);
            var file = new RuleFileViewModel
            {
                FileName = fileName,
                FilePath = Path.Combine(dir, fileName),
                Source = defaultSource,
                SaveDestination = defaultSource,
                FileType = "Rule",
                IsNew = true,
                IsExpanded = true
            };
            var rule = new RuleViewModel { IsNew = true };
            file.Rules.Add(rule);

            RuleFiles.Add(file);
            SelectedRule = rule;
            SelectedFile = file;
            ValidationMessage = "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ExecuteNewFile failed");
            ValidationMessage = $"Could not create new file: {ex.Message}";
        }
    }

    private void ExecuteDuplicateSelected()
    {
        if (SelectedRule == null) return;
        try
        {
            var src = SelectedRule;
            var file = src.ParentFile;
            if (file == null) return;

            var copy = new RuleViewModel
            {
                PathPattern = src.PathPattern,
                Datatype = src.Datatype,
                TagTableName = src.TagTableName,
                RequireTagTableValue = src.RequireTagTableValue,
                Min = src.Min,
                Max = src.Max,
                AllowedValues = src.AllowedValues,
                CommentTemplate = src.CommentTemplate,
                ExcludeFromSetpoints = src.ExcludeFromSetpoints,
                IsNew = true
            };
            var idx = file.Rules.IndexOf(src);
            file.Rules.Insert(idx + 1, copy);
            file.IsExpanded = true;
            SelectedRule = copy;
            ValidationMessage = "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ExecuteDuplicateSelected failed");
            ValidationMessage = $"Could not duplicate rule: {ex.Message}";
        }
    }

    /// <summary>
    /// Tracks unique filenames against staged + on-disk files so successive
    /// "+ File" clicks don't both produce "new-rule.json".
    /// </summary>
    private string GenerateUniqueNewFileName(string dir)
    {
        var stagedNames = new HashSet<string>(
            RuleFiles.Select(r => r.FileName),
            StringComparer.OrdinalIgnoreCase);

        const string baseName = "new-rule";
        for (int i = 1; i < 1000; i++)
        {
            var candidate = i == 1 ? $"{baseName}.json" : $"{baseName}-{i}.json";
            var fullPath = Path.Combine(dir, candidate);
            if (!stagedNames.Contains(candidate) && !File.Exists(fullPath))
                return candidate;
        }
        return $"{baseName}-{Guid.NewGuid():N}.json";
    }

    private RuleSource GetDefaultNewFileSource()
    {
        var projectDir = _configLoader.GetTiaProjectRulesDirectory();
        return projectDir != null ? RuleSource.TiaProject : RuleSource.Local;
    }

    /// <summary>
    /// Context-aware delete: removes the selected rule. If it's the last rule
    /// in the file, the file is removed from disk too.
    /// </summary>
    private void ExecuteDeleteSelected()
    {
        if (SelectedRule != null)
        {
            var file = SelectedRule.ParentFile;
            if (file == null) return;

            file.Rules.Remove(SelectedRule);
            SelectedRule = null;

            if (file.Rules.Count == 0)
            {
                DeleteFileFromDisk(file);
                RuleFiles.Remove(file);
                if (SelectedFile == file) SelectedFile = null;
            }

            _configLoader.Invalidate();
            return;
        }

        if (SelectedFile != null)
        {
            ExecuteDeleteFile(SelectedFile);
        }
    }

    private void ExecuteDeleteFile(RuleFileViewModel? file)
    {
        if (file == null) return;
        DeleteFileFromDisk(file);
        RuleFiles.Remove(file);
        if (SelectedFile == file) SelectedFile = null;
        if (SelectedRule?.ParentFile == file) SelectedRule = null;
        _configLoader.Invalidate();
    }

    private void DeleteFileFromDisk(RuleFileViewModel file)
    {
        if (!File.Exists(file.FilePath)) return;
        try
        {
            File.Delete(file.FilePath);
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Cannot delete file: {ex.Message}";
        }
    }

    /// <summary>
    /// Deletes the current override file and reveals the next-priority version.
    /// Operates at the file level — all rules in the file are reset together.
    /// </summary>
    private void ExecuteResetToBase()
    {
        if (SelectedFile == null || SelectedFile.OverriddenVersions.Count == 0) return;

        var current = SelectedFile;
        var baseFile = current.OverriddenVersions[0];

        var currentPath = Path.GetFullPath(current.FilePath);
        var basePath = Path.GetFullPath(baseFile.FilePath);
        if (File.Exists(currentPath)
            && !string.Equals(currentPath, basePath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Delete(currentPath);
            }
            catch (Exception ex)
            {
                ValidationMessage = $"Cannot delete override: {ex.Message}";
                return;
            }
        }

        var nameToSelect = baseFile.FileName;
        _configLoader.Invalidate();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            SelectedRule = null;
            SelectedFile = null;
            LoadAllFiles();
            FilteredRuleFiles.Refresh();
            SelectedFile = RuleFiles.FirstOrDefault(f =>
                string.Equals(f.FileName, nameToSelect, StringComparison.OrdinalIgnoreCase));
        }));
    }

    private void ExecuteSave()
    {
        try
        {
            if (SaveAll())
                ReloadAfterSave();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ExecuteSave crashed");
            ValidationMessage = $"Save error: {ex.Message}";
        }
    }

    private void ExecuteSaveAndClose()
    {
        if (SaveAll())
            RequestClose?.Invoke();
    }

    /// <summary>UI-only stub. Logic intentionally out of scope.</summary>
    private void ExecuteImportFiles()
    {
        ValidationMessage = "Import is not yet implemented.";
    }

    /// <summary>UI-only stub. Logic intentionally out of scope.</summary>
    private void ExecuteExportSelected()
    {
        ExecuteExportFile(SelectedFile);
    }

    /// <summary>UI-only stub. Logic intentionally out of scope.</summary>
    private void ExecuteExportFile(RuleFileViewModel? file)
    {
        ValidationMessage = file != null
            ? $"Export of '{file.FileName}' is not yet implemented."
            : "Export is not yet implemented.";
    }

    private void ReloadAfterSave()
    {
        var selectedFileName = SelectedFile?.FileName;
        var selectedRulePattern = SelectedRule?.PathPattern;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                SelectedRule = null;
                SelectedFile = null;
                LoadAllFiles();
                FilteredRuleFiles.Refresh();
                if (selectedFileName != null)
                {
                    var file = RuleFiles.FirstOrDefault(f =>
                        string.Equals(f.FileName, selectedFileName, StringComparison.OrdinalIgnoreCase));
                    SelectedFile = file;
                    if (file != null && selectedRulePattern != null)
                        SelectedRule = file.Rules.FirstOrDefault(r =>
                            string.Equals(r.PathPattern, selectedRulePattern, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ReloadAfterSave crashed in dispatcher callback");
            }
        }));
    }

    private bool SaveAll()
    {
        // Auto-derive filename for new files whose first rule's pattern was edited
        foreach (var file in RuleFiles)
        {
            if (file.IsNew)
            {
                var derived = file.DeriveFileNameFromFirstRule();
                if (!string.Equals(file.FileName, derived, StringComparison.OrdinalIgnoreCase)
                    && (file.FileName == "" || file.FileName.StartsWith("new-rule", StringComparison.OrdinalIgnoreCase)))
                {
                    file.FileName = derived;
                    var dir = Path.GetDirectoryName(file.FilePath);
                    if (!string.IsNullOrEmpty(dir))
                        file.FilePath = Path.Combine(dir, derived);
                }
            }

            var error = file.Validate();
            if (error != null)
            {
                ValidationMessage = error;
                SelectedFile = file;
                if (file.Rules.Count > 0)
                    SelectedRule = file.Rules.FirstOrDefault(r => r.Validate(file.FileName) != null) ?? file.Rules[0];
                return false;
            }
        }

        SaveSharedDirectorySetting();

        foreach (var file in RuleFiles)
        {
            try
            {
                var targetDir = GetDirectoryForSource(file.SaveDestination);
                if (targetDir == null)
                {
                    ValidationMessage = $"No valid directory for '{file.FileName}' (destination: {file.SaveDestination}).";
                    return false;
                }

                var targetPath = Path.Combine(targetDir, file.FileName);
                var config = file.ToBulkChangeConfig();
                _configLoader.SaveRuleFile(targetPath, config);

                file.FilePath = targetPath;
                file.Source = file.SaveDestination;
                file.MarkClean();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SaveAll: failed for '{FileName}'", file.FileName);
                ValidationMessage = $"Save failed for '{file.FileName}': {ex.Message}";
                return false;
            }
        }

        ValidationMessage = "";
        _configLoader.Invalidate();
        return true;
    }

    private void SaveSharedDirectorySetting()
    {
        _configLoader.SaveSharedRulesDirectory(
            string.IsNullOrWhiteSpace(SharedRulesDirectory) ? null : SharedRulesDirectory);
    }
}
