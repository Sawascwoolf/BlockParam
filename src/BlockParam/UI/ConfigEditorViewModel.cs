using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using BlockParam.Diagnostics;
using BlockParam.Config;

namespace BlockParam.UI;

/// <summary>
/// ViewModel for the file-based Configuration Editor dialog.
/// Scans Project/Local/Shared directories and shows individual rule files.
/// </summary>
public class ConfigEditorViewModel : ViewModelBase
{
    private readonly ConfigLoader _configLoader;
    private string _sharedRulesDirectory = "";
    private string _validationMessage = "";
    private string _filterText = "";
    private RuleFileViewModel? _selectedFile;

    /// <param name="configLoader">Config loader (provides directory paths).</param>
    /// <param name="tagTableNames">Optional tag table names for dropdown.</param>
    public ConfigEditorViewModel(
        ConfigLoader configLoader,
        IEnumerable<string>? tagTableNames = null)
    {
        _configLoader = configLoader;

        RuleFiles = new ObservableCollection<RuleFileViewModel>();
        TagTableNames = new ObservableCollection<string>();
        FilteredRuleFiles = CollectionViewSource.GetDefaultView(RuleFiles);
        FilteredRuleFiles.Filter = FilterRule;

        NewRuleCommand = new RelayCommand(ExecuteNewRule);
        DuplicateSelectedCommand = new RelayCommand(ExecuteDuplicateSelected, () => SelectedFile != null);
        DeleteSelectedCommand = new RelayCommand(ExecuteDeleteSelected, () => SelectedFile != null);
        ResetToBaseCommand = new RelayCommand(ExecuteResetToBase, () => SelectedFile?.HasOverrides == true);
        SaveCommand = new RelayCommand(ExecuteSave);
        SaveAndCloseCommand = new RelayCommand(ExecuteSaveAndClose);

        LoadAllFiles();

        if (tagTableNames != null)
            foreach (var name in tagTableNames)
                TagTableNames.Add(name);
    }

    public ObservableCollection<RuleFileViewModel> RuleFiles { get; }
    public ICollectionView FilteredRuleFiles { get; }
    public ObservableCollection<string> TagTableNames { get; }

    public event Action? RequestClose;

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
                FilteredRuleFiles.Refresh();
        }
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        set => SetProperty(ref _validationMessage, value);
    }

    public ICommand NewRuleCommand { get; }
    public ICommand DuplicateSelectedCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand ResetToBaseCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAndCloseCommand { get; }

    private bool FilterRule(object obj)
    {
        if (string.IsNullOrWhiteSpace(_filterText)) return true;
        if (obj is not RuleFileViewModel rule) return false;
        return rule.PathPattern.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Scans all 3 directories, groups by filename, and shows only the
    /// highest-priority version per rule (TiaProject > Local > Shared).
    /// Lower-priority versions are stored in OverriddenVersions for reset.
    /// </summary>
    private void LoadAllFiles()
    {
        RuleFiles.Clear();

        // Read shared directory from existing config
        var config = _configLoader.GetConfig();
        SharedRulesDirectory = config?.RulesDirectory ?? "";

        // Load all files into a flat list first
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

        // Group by path pattern, keep highest priority, attach overridden versions.
        // Skip duplicates that point to the same physical file (e.g. shared==local dir).
        var groups = allFiles
            .GroupBy(f => f.PathPattern, StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Returns the default save directory based on destination.
    /// </summary>
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

    private void ExecuteNewRule()
    {
        try
        {
            var defaultSource = GetDefaultNewFileSource();
            var dir = GetDirectoryForSource(defaultSource);
            if (dir == null) { ValidationMessage = "No valid save directory available."; return; }

            var fileName = GenerateUniqueNewRuleFileName(dir);
            var vm = new RuleFileViewModel
            {
                FileName = fileName,
                FilePath = Path.Combine(dir, fileName),
                Source = defaultSource,
                SaveDestination = defaultSource,
                FileType = "Rule",
                IsNew = true
            };
            RuleFiles.Add(vm);
            SelectedFile = vm;
            ValidationMessage = "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ExecuteNewRule failed");
            ValidationMessage = $"Could not create new rule: {ex.Message}";
        }
    }

    private void ExecuteDuplicateSelected()
    {
        if (SelectedFile == null) return;
        try
        {
            var source = SelectedFile;
            var destination = source.SaveDestination;
            var dir = GetDirectoryForSource(destination) ?? GetDirectoryForSource(GetDefaultNewFileSource());
            if (dir == null) { ValidationMessage = "No valid save directory available."; return; }

            // Order matters: set the fields first (with IsNew=false so the pattern setter
            // does not auto-rename the file to the source's filename), then assign the
            // unique filename, then flip IsNew=true so subsequent pattern edits auto-sync.
            var vm = new RuleFileViewModel
            {
                FileType = source.FileType,
                Source = destination,
                SaveDestination = destination,
                PathPattern = source.PathPattern,
                Datatype = source.Datatype,
                TagTableName = source.TagTableName,
                RequireTagTableValue = source.RequireTagTableValue,
                Min = source.Min,
                Max = source.Max,
                AllowedValues = source.AllowedValues,
                CommentTemplate = source.CommentTemplate,
                ExcludeFromSetpoints = source.ExcludeFromSetpoints
            };
            var fileName = GenerateUniqueNewRuleFileName(dir);
            vm.FileName = fileName;
            vm.FilePath = Path.Combine(dir, fileName);
            vm.IsNew = true;
            RuleFiles.Add(vm);
            SelectedFile = vm;
            ValidationMessage = "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ExecuteDuplicateSelected failed");
            ValidationMessage = $"Could not duplicate rule: {ex.Message}";
        }
    }

    /// <summary>
    /// Returns a filename that does not collide with an existing file on disk
    /// or an unsaved rule already staged in memory. Two successive "+"-clicks
    /// previously produced two rules pointing at the same "new-rule.json",
    /// which caused the second Save to overwrite — and in some environments
    /// crashed the DataGrid when the new row was committed over a pending edit.
    /// </summary>
    private string GenerateUniqueNewRuleFileName(string dir)
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
        // Prefer Project if available, otherwise Local
        var projectDir = _configLoader.GetTiaProjectRulesDirectory();
        return projectDir != null ? RuleSource.TiaProject : RuleSource.Local;
    }

    private void ExecuteDeleteSelected()
    {
        if (SelectedFile == null) return;

        // Delete the file from disk if it exists
        if (File.Exists(SelectedFile.FilePath))
        {
            try
            {
                File.Delete(SelectedFile.FilePath);
            }
            catch (Exception ex)
            {
                ValidationMessage = $"Cannot delete file: {ex.Message}";
                return;
            }
        }

        RuleFiles.Remove(SelectedFile);
        SelectedFile = null;
        _configLoader.Invalidate();
    }

    /// <summary>
    /// Deletes the current override and reveals the next-priority version.
    /// </summary>
    private void ExecuteResetToBase()
    {
        if (SelectedFile == null || SelectedFile.OverriddenVersions.Count == 0) return;

        var current = SelectedFile;
        var baseRule = current.OverriddenVersions[0];

        // Delete the override file (only if physically different from the base)
        var currentPath = Path.GetFullPath(current.FilePath);
        var basePath = Path.GetFullPath(baseRule.FilePath);
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

        // Reload deferred to avoid collection modification during active bindings
        var patternToSelect = baseRule.PathPattern;
        _configLoader.Invalidate();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            SelectedFile = null;
            LoadAllFiles();
            FilteredRuleFiles.Refresh();
            SelectedFile = RuleFiles.FirstOrDefault(f =>
                string.Equals(f.PathPattern, patternToSelect, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// Rebuilds the rule list after save to update override grouping.
    /// Deferred via Dispatcher to avoid modifying the collection during active bindings.
    /// </summary>
    private void ReloadAfterSave()
    {
        var selectedPattern = SelectedFile?.PathPattern;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                SelectedFile = null;
                LoadAllFiles();
                FilteredRuleFiles.Refresh();
                if (selectedPattern != null)
                    SelectedFile = RuleFiles.FirstOrDefault(f =>
                        string.Equals(f.PathPattern, selectedPattern, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ReloadAfterSave crashed in dispatcher callback");
            }
        }));
    }

    /// <summary>
    /// Validates and saves all modified files. Returns true on success.
    /// </summary>
    private bool SaveAll()
    {
        // Validate all files
        foreach (var file in RuleFiles)
        {
            var error = file.Validate();
            if (error != null)
            {
                ValidationMessage = error;
                SelectedFile = file;
                return false;
            }
        }

        // Save shared directory setting to config.json
        SaveSharedDirectorySetting();

        // Save each file to its destination
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

                // Update file's path and source to new location
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
