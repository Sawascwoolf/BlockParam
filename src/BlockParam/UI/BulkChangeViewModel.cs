using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BlockParam.Config;
using BlockParam.Diagnostics;
using BlockParam.Licensing;
using BlockParam.Localization;
using BlockParam.Models;
using BlockParam.Services;
using BlockParam.SimaticML;

namespace BlockParam.UI;

/// <summary>
/// Main ViewModel for the BulkChange dialog.
/// Uses a flat list (via FlatTreeManager) for proper column alignment in ListView/GridView.
/// </summary>
public class BulkChangeViewModel : ViewModelBase, IDisposable
{
    private EventHandler? _licenseStateChangedHandler;
    private bool _disposed;

    private readonly HierarchyAnalyzer _analyzer;
    private readonly BulkChangeService _bulkChangeService;
    private readonly IUsageTracker _usageTracker;
    private readonly ConfigLoader _configLoader;
    private readonly Action<string>? _onApply;
    private readonly Func<string>? _onBackup;   // callback to create backup, returns backup path
    private readonly Action<string>? _onRestore; // callback to restore from backup path
    private readonly FlatTreeManager _flatTreeManager = new();
    private readonly SimaticMLWriter _writer = new();
    private readonly MemberSearchService _searchService = new();
    private readonly IMessageBoxService _messageBox;
    private readonly Action? _onRefreshTagTables;
    private readonly string? _tagTableDir;
    private readonly Action? _onRefreshUdtTypes;
    private readonly string? _udtDir;
    private UdtSetPointResolver? _udtResolver;
    private UdtCommentResolver? _commentResolver;
    private AutocompleteProvider? _autocompleteProvider;
    private TagTableCache? _tagTableCache;

    private DataBlockInfo _dataBlockInfo;
    private string _title = "";
    // DB-switcher state (#59). Lazy-loaded on first dropdown open and cached
    // for the dialog session; the ↻ button re-enumerates on demand.
    private readonly Func<IReadOnlyList<DataBlockSummary>>? _enumerateDataBlocks;
    private readonly Func<DataBlockSummary, string>? _switchToDataBlock;
    private IReadOnlyList<DataBlockSummary>? _availableDataBlocks;
    private IReadOnlyList<DataBlockSummary> _filteredDataBlocks = Array.Empty<DataBlockSummary>();
    private string _dataBlockSearchText = "";
    private bool _isDataBlocksDropdownOpen;
    private bool _isLoadingDataBlocks;
    // In-memory stash of pending edits keyed by DB identity (#59). Lets the
    // user switch DBs without committing or losing work — when they come back
    // to a stashed DB later in the same session, the edits restore.
    private readonly Dictionary<string, StashedDbState> _stashedDbs =
        new(StringComparer.Ordinal);
    private MemberNodeViewModel? _selectedFlatMember;
    private ScopeLevel? _selectedScope;
    private string _newValue = "";
    private readonly HashSet<string> _manualSelectedPaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _bulkErrorPaths = new(StringComparer.Ordinal);
    // True once the user has typed in the NewValue textbox. Prefills from the
    // current selection are skipped while this is true, so user-entered input
    // isn't clobbered by selection changes.
    private bool _newValueTouched;
    private GlobSuggestionProvider? _suggestionProvider;
    private IReadOnlyList<AutocompleteSuggestion> _suggestions = Array.Empty<AutocompleteSuggestion>();
    private IReadOnlyList<AutocompleteSuggestion> _filteredSuggestions = Array.Empty<AutocompleteSuggestion>();
    private string _statusText = "";
    private string _validationError = "";
    private string _constraintInfo = "";
    private string _searchQuery = "";
    private int _searchHitCount;
    private Timer? _searchDebounceTimer;
    private Timer? _valueDebounceTimer;
    private int _hiddenByRuleCount;
    private bool _showSetpointsOnly;
    private bool _showConstants;
    private bool _constantsForced;
    private string _tagTableAge = "";
    private bool _isRefreshing;
    private bool _suppressSuggestions;
    private bool _lastApplySucceeded;
    private bool _hasPendingChanges;
    private bool _isInspectorCollapsed;
    private bool _isBulkEditExpanded = true;
    private bool _isBulkPreviewExpanded = true;
    private bool _isPendingExpanded = true;
    private bool _isIssuesExpanded = true;
    private string _currentXml;
    private readonly IReadOnlyList<string> _projectLanguages;
    private readonly CommentLanguagePolicy _commentLanguagePolicy;
    private readonly Dispatcher _dispatcher;
    private readonly ILicenseService? _licenseService;

    public BulkChangeViewModel(
        DataBlockInfo dataBlockInfo,
        string currentXml,
        HierarchyAnalyzer analyzer,
        BulkChangeService bulkChangeService,
        IUsageTracker usageTracker,
        ConfigLoader configLoader,
        Action<string>? onApply = null,
        Func<string>? onBackup = null,
        Action<string>? onRestore = null,
        IMessageBoxService? messageBox = null,
        TagTableCache? tagTableCache = null,
        Action? onRefreshTagTables = null,
        string? tagTableDir = null,
        IReadOnlyList<string>? projectLanguages = null,
        ILicenseService? licenseService = null,
        Action? onRefreshUdtTypes = null,
        string? udtDir = null,
        UdtSetPointResolver? udtResolver = null,
        UdtCommentResolver? commentResolver = null,
        string? editingLanguage = null,
        string? referenceLanguage = null,
        Func<IReadOnlyList<DataBlockSummary>>? enumerateDataBlocks = null,
        Func<DataBlockSummary, string>? switchToDataBlock = null)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _projectLanguages = projectLanguages is { Count: > 0 } ? projectLanguages : new[] { "en-GB" };
        _commentLanguagePolicy = new CommentLanguagePolicy(editingLanguage, referenceLanguage, _projectLanguages);
        _dataBlockInfo = dataBlockInfo;
        _currentXml = currentXml;
        _analyzer = analyzer;
        _bulkChangeService = bulkChangeService;
        _usageTracker = usageTracker;
        _configLoader = configLoader;
        _onApply = onApply;
        _onBackup = onBackup;
        _onRestore = onRestore;
        _messageBox = messageBox ?? new WpfMessageBoxService();
        _tagTableCache = tagTableCache;
        _onRefreshTagTables = onRefreshTagTables;
        _tagTableDir = tagTableDir;
        _onRefreshUdtTypes = onRefreshUdtTypes;
        _udtDir = udtDir;
        _udtResolver = udtResolver;
        _commentResolver = commentResolver;
        _licenseService = licenseService;
        _enumerateDataBlocks = enumerateDataBlocks;
        _switchToDataBlock = switchToDataBlock;
        _autocompleteProvider = tagTableCache != null
            ? new AutocompleteProvider(configLoader, tagTableCache)
            : null;

        UpdateTagTableAge();

        {
            var config = configLoader.GetConfig();
            if (config != null)
            {
                Log.Information("Config loaded: {RuleCount} rules, rulesDirectory={RulesDir}",
                    config.Rules.Count, config.RulesDirectory ?? "(none)");
            }
        }

        var version = typeof(BulkChangeViewModel).Assembly.GetName().Version;
        _title = $"BlockParam v{version}: {dataBlockInfo.Name}";

        InlineRuleExtractor.ApplyTo(configLoader.GetConfig(), dataBlockInfo);

        BulkPreview = new ObservableCollection<BulkPreviewEntry>();
        PendingEdits = new ObservableCollection<PendingEditEntry>();
        ExistingIssues = new ObservableCollection<ExistingIssueEntry>();
        StashedDbs = new ObservableCollection<StashedDbState>();

        // Build tree view models
        RootMembers = new ObservableCollection<MemberNodeViewModel>();
        foreach (var member in dataBlockInfo.Members)
        {
            var vm = new MemberNodeViewModel(member, null, _commentLanguagePolicy);
            SubscribeStartValueEdited(vm);
            RootMembers.Add(vm);
        }
        RefreshRuleHints();

        AvailableScopes = new ObservableCollection<ScopeLevel>();

        SetPendingCommand = new RelayCommand(ExecuteSetPending, CanExecuteSetPending);
        ApplyCommand = new RelayCommand(ExecuteApply, CanExecuteApply);
        ApplyAndCloseCommand = new RelayCommand(ExecuteApplyAndClose, CanExecuteApply);
        UpdateCommentsCommand = new RelayCommand(ExecuteUpdateComments, CanExecuteUpdateComments);
        DiscardPendingCommand = new RelayCommand(ExecuteDiscardPending, () => PendingInlineEditCount > 0);

        EditConfigCommand = new RelayCommand(ExecuteEditConfig);
        RefreshConstantsCommand = new RelayCommand(ExecuteRefreshConstants);
        EnterLicenseKeyCommand = new RelayCommand(ExecuteEnterLicenseKey);
        UpgradeToProCommand = new RelayCommand(ExecuteUpgradeToPro);
        ExpandAllCommand = new RelayCommand(ExecuteExpandAll);
        CollapseAllCommand = new RelayCommand(ExecuteCollapseAll);
        ToggleInspectorCommand = new RelayCommand(() => IsInspectorCollapsed = !IsInspectorCollapsed);
        ToggleBulkEditCommand = new RelayCommand(() => IsBulkEditExpanded = !IsBulkEditExpanded);
        ToggleBulkPreviewCommand = new RelayCommand(() => IsBulkPreviewExpanded = !IsBulkPreviewExpanded);
        TogglePendingCommand = new RelayCommand(() => IsPendingExpanded = !IsPendingExpanded);
        ToggleIssuesCommand = new RelayCommand(() => IsIssuesExpanded = !IsIssuesExpanded);
        ClearManualSelectionCommand = new RelayCommand(ExecuteClearManualSelection,
            () => _manualSelectedPaths.Count > 0);

        OpenDataBlocksDropdownCommand = new RelayCommand(ExecuteOpenDataBlocksDropdown,
            () => _enumerateDataBlocks != null && _switchToDataBlock != null);
        CloseDataBlocksDropdownCommand = new RelayCommand(() =>
        {
            IsDataBlocksDropdownOpen = false;
        });
        RefreshDataBlocksCommand = new RelayCommand(ExecuteRefreshDataBlocks,
            () => _enumerateDataBlocks != null && !_isLoadingDataBlocks);
        SwitchToStashedDbCommand = new RelayCommand(parameter =>
        {
            if (parameter is StashedDbState stash) SwitchToDataBlock(stash.Summary);
        });

        if (_licenseService != null)
        {
            _licenseStateChangedHandler = (_, __) =>
                _dispatcher.BeginInvoke(new Action(() => UpdateUsageStatus()));
            _licenseService.LicenseStateChanged += _licenseStateChangedHandler;
        }

        // Apply initial filter and build flat list
        ApplyAllFilters();
        RefreshFlatList();
        UpdateUsageStatus();

        // #26: Surface pre-existing rule violations on dialog load. Runs after
        // RefreshRuleHints so RuleHint is available for the issue tooltip.
        RebuildExistingIssues();
    }

    // --- Properties ---

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }
    public ObservableCollection<MemberNodeViewModel> RootMembers { get; }
    public ObservableCollection<ScopeLevel> AvailableScopes { get; }

    /// <summary>
    /// Live preview of the rows that would be staged if the user clicked "Set".
    /// Rebuilt reactively whenever target / scope / value changes — it does
    /// NOT itself mutate any node. On Set, entries are transferred to
    /// pending and the collection is cleared.
    /// </summary>
    public ObservableCollection<BulkPreviewEntry> BulkPreview { get; }

    /// <summary>
    /// Aggregated view of every node that currently has a pending inline edit.
    /// Rebuilt whenever <c>PendingInlineEditCount</c> changes.
    /// </summary>
    public ObservableCollection<PendingEditEntry> PendingEdits { get; }

    /// <summary>
    /// Findings produced by running the validator over the *existing* StartValues
    /// when the dialog opens (and after every tree refresh / inline edit). Read-only —
    /// these are pre-existing rule violations the user can fix manually, not pending
    /// edits. They never block Apply (#26).
    /// </summary>
    public ObservableCollection<ExistingIssueEntry> ExistingIssues { get; }

    /// <summary>
    /// One entry per DB the user has switched away from with un-applied
    /// pending edits (#59). Each entry renders as its own inspector section
    /// so the staged work stays visible across switches; clicking the section
    /// header switches back to that DB (running the same prompt again).
    /// </summary>
    public ObservableCollection<StashedDbState> StashedDbs { get; }

    public bool HasStashedDbs => StashedDbs.Count > 0;

    public bool HasBulkPreview => BulkPreview.Count > 0;
    public int BulkPreviewCount => BulkPreview.Count;
    public bool HasPendingEdits => PendingEdits.Count > 0;
    public bool HasExistingIssues => ExistingIssues.Count > 0;
    public int ExistingIssuesCount => ExistingIssues.Count;

    /// <summary>Preview rows whose node already has a pending edit — they'd overwrite it on Set.</summary>
    public int BulkPreviewConflictCount => BulkPreview.Count(e => e.HasPendingConflict);

    public bool HasBulkPreviewConflict => BulkPreviewConflictCount > 0;

    public string BulkPreviewConflictWarning
    {
        get
        {
            int n = BulkPreviewConflictCount;
            if (n == 0) return "";
            return n == 1
                ? "\u26A0 1 overlap with pending edits \u2014 will be overwritten."
                : $"\u26A0 {n} overlap with pending edits \u2014 will be overwritten.";
        }
    }

    /// <summary>
    /// Summary shown in the section header, e.g. "90 ⇢ 85" when all rows share
    /// the same original value, or "{count} targets" otherwise.
    /// </summary>
    public string BulkPreviewSummary
    {
        get
        {
            if (BulkPreview.Count == 0) return "";
            var firstOrig = BulkPreview[0].OriginalValue;
            bool homogeneous = BulkPreview.All(e =>
                string.Equals(e.OriginalValue, firstOrig, StringComparison.Ordinal));
            if (homogeneous && !string.IsNullOrEmpty(firstOrig))
                return $"{firstOrig} \u21E2 {_newValue}";
            return $"{BulkPreview.Count} targets";
        }
    }

    /// <summary>Flat list for the ListView (proper column alignment).</summary>
    public ObservableCollection<MemberNodeViewModel> FlatMembers => _flatTreeManager.FlatList;

    public event Action? RequestClose;

    /// <summary>True if Apply was used but changes not yet committed to TIA.</summary>
    public bool HasPendingChanges
    {
        get => _hasPendingChanges;
        private set => SetProperty(ref _hasPendingChanges, value);
    }

    /// <summary>
    /// Commits all pending changes to TIA Portal (import + compile).
    /// Returns true on success, false if the user cancelled (e.g. declined the
    /// compile prompt on an inconsistent block). Throws nothing for user-cancel;
    /// genuine failures are shown via <see cref="IMessageBoxService"/> and return false.
    /// </summary>
    public bool CommitChanges()
    {
        if (!_hasPendingChanges) return true;
        try
        {
            _onApply?.Invoke(_currentXml);
            HasPendingChanges = false;
            // Pair line for the matching Log.Error path below — without
            // this, a TIA-side import failure has the error logged but
            // the success path is silent, so support cannot tell which
            // happened from the log file alone.
            Log.Information("CommitChanges: import succeeded for {Db}", _dataBlockInfo.Name);
            return true;
        }
        catch (OperationCanceledException)
        {
            // User declined the compile prompt on an inconsistent block. Keep pending
            // state so they can Apply again after compiling in TIA, or Discard to drop.
            StatusText = Res.Get("Status_Ready");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CommitChanges: TIA import failed for {Db}", _dataBlockInfo.Name);
            HasPendingChanges = false;
            _messageBox.ShowError(
                $"TIA Portal import failed:\n\n{ex.InnerException?.Message ?? ex.Message}",
                "Import Error");
            return false;
        }
    }

    /// <summary>Selected item in the flat ListView.</summary>
    public MemberNodeViewModel? SelectedFlatMember
    {
        get => _selectedFlatMember;
        set
        {
            if (_isRefreshing) return; // Don't re-trigger during flat list rebuild
            if (SetProperty(ref _selectedFlatMember, value))
            {
                OnMemberSelected(value);
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedMemberDisplay));
            }
        }
    }

    public ScopeLevel? SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (SetProperty(ref _selectedScope, value))
            {
                UpdateHighlighting();
                OnPropertyChanged(nameof(HasScope));
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(SetButtonText));
            }
        }
    }

    public string NewValue
    {
        get => _newValue;
        set
        {
            if (SetProperty(ref _newValue, value))
            {
                // WPF two-way binding routes user keystrokes through this setter,
                // so mark the textbox as touched. Programmatic prefills bypass the
                // setter (write _newValue directly) to avoid triggering this.
                _newValueTouched = true;
                // Debounce expensive operations (filter 2000+ suggestions, highlight tree)
                _valueDebounceTimer?.Dispose();
                _valueDebounceTimer = new Timer(_ =>
                {
                    _dispatcher.BeginInvoke(new Action(() =>
                    {
                        ValidateValue();
                        UpdateFilteredSuggestions();
                        UpdateHighlighting();
                    }));
                }, null, 150, Timeout.Infinite);
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                OnPropertyChanged(nameof(HasSearchQuery));
                // Debounce search: WPF Binding.Delay uses DispatcherTimer which
                // doesn't work reliably when hosted inside TIA Portal (WinForms host
                // without Application.Current). Use Threading.Timer + Dispatcher instead.
                _searchDebounceTimer?.Dispose();
                _searchDebounceTimer = new Timer(_ =>
                {
                    _dispatcher.BeginInvoke(new Action(() =>
                    {
                        ApplyAllFilters();
                        RefreshFlatList();
                    }));
                }, null, 200, Timeout.Infinite);
            }
        }
    }

    public int SearchHitCount
    {
        get => _searchHitCount;
        private set => SetProperty(ref _searchHitCount, value);
    }

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(_searchQuery);

    /// <summary>
    /// Number of leaf members currently hidden by <c>excludeFromSetpoints</c> rules.
    /// Rules are always applied — the banner surfaces their effect so users understand
    /// why entries are missing and can open the Config Editor to review them (#23).
    /// </summary>
    public int HiddenByRuleCount
    {
        get => _hiddenByRuleCount;
        private set
        {
            if (SetProperty(ref _hiddenByRuleCount, value))
            {
                OnPropertyChanged(nameof(ShowRuleFilterBanner));
                OnPropertyChanged(nameof(RuleFilterBannerText));
            }
        }
    }

    /// <summary>True when at least one rule is currently hiding a leaf member.</summary>
    public bool ShowRuleFilterBanner => _hiddenByRuleCount > 0;

    /// <summary>Localized banner text: "Rule filter is hiding N member(s) — review rules in Config Editor".</summary>
    public string RuleFilterBannerText => Res.Format("Dialog_RuleFilterBanner", _hiddenByRuleCount);

    /// <summary>
    /// When on, hide every leaf that is not a UDT-resolved SetPoint. AND-combined
    /// with the always-on rule filter. If UDT types are missing when toggled on,
    /// the VM transparently exports them from TIA Portal and re-parses the DB.
    /// </summary>
    public bool ShowSetpointsOnly
    {
        get => _showSetpointsOnly;
        set
        {
            // On OFF→ON we revalidate the UDT cache against TIA's ModifiedDate stamps.
            // The check is cheap when nothing changed; stale entries get re-exported.
            if (value && !_showSetpointsOnly && _onRefreshUdtTypes != null)
                TryRefreshUdtCache();

            if (SetProperty(ref _showSetpointsOnly, value))
            {
                ApplyAllFilters();
                RefreshFlatList();
            }
        }
    }

    /// <summary>
    /// The checkbox is enabled unless we are certain the filter cannot work —
    /// i.e. the DB references UDTs that are not cached AND no refresh path is wired.
    /// When a refresh callback exists, clicking triggers a fresh export automatically.
    /// </summary>
    public bool CanShowSetpointsOnly
        => _dataBlockInfo.UnresolvedUdts.Count == 0 || _onRefreshUdtTypes != null;

    /// <summary>Tooltip shown on the checkbox.</summary>
    public string ShowSetpointsOnlyTooltip
    {
        get
        {
            if (_dataBlockInfo.UnresolvedUdts.Count == 0)
                return "Only show members marked as SetPoint (Einstellwert) in the UDT type definition.";

            if (_onRefreshUdtTypes != null)
                return "Only show members marked as SetPoint. UDT types will be re-exported from TIA Portal when enabled.";

            var missing = string.Join(", ", _dataBlockInfo.UnresolvedUdts);
            return $"Disabled: UDT type definitions are missing ({missing}) and no PLC connection is available to export them.";
        }
    }


    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string ValidationError
    {
        get => _validationError;
        set
        {
            SetProperty(ref _validationError, value);
            OnPropertyChanged(nameof(HasValidationError));
        }
    }

    public string ConstraintInfo
    {
        get => _constraintInfo;
        set => SetProperty(ref _constraintInfo, value);
    }

    public string UsageStatusText { get; private set; } = "";

    public bool HasSelection => _selectedFlatMember is { IsLeaf: true };
    public bool HasScope => _selectedScope != null && !IsManualMode;
    public bool HasValidationError => !string.IsNullOrEmpty(_validationError);
    public string SetButtonText
    {
        get
        {
            if (IsManualMode)
                return Res.Format("Dialog_SetManualCount", _manualSelectedPaths.Count);
            return _selectedScope != null
                ? $"Set {_selectedScope.MatchCount} in '{_selectedScope.AncestorName}'"
                : "Set";
        }
    }

    /// <summary>True when 2+ leaf members are manually selected (Ctrl+Click).</summary>
    public bool IsManualMode => _manualSelectedPaths.Count >= 2;

    /// <summary>Number of manually selected leaf members (includes ones hidden by filter).</summary>
    public int ManualSelectionCount => _manualSelectedPaths.Count;

    /// <summary>Read-only view of manually selected member paths (for code-behind rehydration).</summary>
    public IReadOnlyCollection<string> ManualSelectedPaths => _manualSelectedPaths;

    /// <summary>
    /// Bulk panel is visible when the user can edit — either scope mode (single selection)
    /// or manual mode (2+ selected).
    /// </summary>
    public bool CanEdit => HasScope || IsManualMode;

    /// <summary>Summary text shown instead of the scope dropdown when in manual mode.</summary>
    public string ManualSelectionSummary
    {
        get
        {
            if (!IsManualMode) return "";
            var types = GetSelectedDatatypes();
            if (types.Count == 1)
                return Res.Format("Dialog_ManualSelectionSummary", _manualSelectedPaths.Count, types.First());
            return Res.Format("Dialog_ManualSelectionMixed", _manualSelectedPaths.Count, types.Count);
        }
    }

    /// <summary>True when all manually selected members share the same datatype.</summary>
    public bool IsSelectionTypeHomogeneous
    {
        get
        {
            if (!IsManualMode) return true;
            return GetSelectedDatatypes().Count == 1;
        }
    }

    public string SelectedMemberDisplay
    {
        get
        {
            if (IsManualMode) return ManualSelectionSummary;
            return _selectedFlatMember is { IsLeaf: true }
                ? Res.Format("Selection_MemberDisplay", _selectedFlatMember.Name, _selectedFlatMember.Datatype)
                : Res.Get("Selection_ClickToSelect");
        }
    }

    public ICommand SetPendingCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand ApplyAndCloseCommand { get; }
    public ICommand UpdateCommentsCommand { get; }
    public ICommand DiscardPendingCommand { get; }

    public ICommand EditConfigCommand { get; }
    public ICommand EnterLicenseKeyCommand { get; }
    public ICommand UpgradeToProCommand { get; }
    public ICommand ExpandAllCommand { get; }
    public ICommand CollapseAllCommand { get; }
    public ICommand ClearManualSelectionCommand { get; }
    public ICommand ToggleInspectorCommand { get; }

    // --- DB-switcher dropdown (#59) ---

    public ICommand OpenDataBlocksDropdownCommand { get; }
    public ICommand CloseDataBlocksDropdownCommand { get; }
    public ICommand RefreshDataBlocksCommand { get; }
    public ICommand SwitchToStashedDbCommand { get; }

    /// <summary>
    /// True when the host wired up DB enumeration + switching callbacks.
    /// The dropdown chevron / popup are hidden in tests + DevLauncher runs
    /// where no project is available.
    /// </summary>
    public bool HasDataBlockSwitcher =>
        _enumerateDataBlocks != null && _switchToDataBlock != null;

    /// <summary>Header label — the DB name part of the title combo.</summary>
    public string CurrentDataBlockName => _dataBlockInfo.Name;

    public bool IsDataBlocksDropdownOpen
    {
        get => _isDataBlocksDropdownOpen;
        set => SetProperty(ref _isDataBlocksDropdownOpen, value);
    }

    public bool IsLoadingDataBlocks
    {
        get => _isLoadingDataBlocks;
        private set
        {
            if (SetProperty(ref _isLoadingDataBlocks, value))
                OnPropertyChanged(nameof(ShowEmptyDataBlocksMessage));
        }
    }

    /// <summary>Filtered, alphabetised list shown inside the dropdown.</summary>
    public IReadOnlyList<DataBlockSummary> FilteredDataBlocks
    {
        get => _filteredDataBlocks;
        private set
        {
            if (SetProperty(ref _filteredDataBlocks, value))
                OnPropertyChanged(nameof(ShowEmptyDataBlocksMessage));
        }
    }

    /// <summary>True when enumeration is settled but the filter matches nothing.</summary>
    public bool ShowEmptyDataBlocksMessage =>
        !_isLoadingDataBlocks
        && _availableDataBlocks != null
        && _filteredDataBlocks.Count == 0;

    /// <summary>Type-to-filter text for the dropdown's search box.</summary>
    public string DataBlockSearchText
    {
        get => _dataBlockSearchText;
        set
        {
            if (SetProperty(ref _dataBlockSearchText, value))
                ApplyDataBlockFilter();
        }
    }

    /// <summary>
    /// Lazy-load + cache enumeration result. Subsequent opens are O(filter)
    /// — no re-enumeration unless the user clicks ↻ (#59).
    /// </summary>
    private void ExecuteOpenDataBlocksDropdown()
    {
        if (_availableDataBlocks == null)
            LoadAvailableDataBlocks(force: false);

        ApplyDataBlockFilter();
        IsDataBlocksDropdownOpen = true;
    }

    private void ExecuteRefreshDataBlocks()
    {
        LoadAvailableDataBlocks(force: true);
        ApplyDataBlockFilter();
    }

    private void LoadAvailableDataBlocks(bool force)
    {
        if (_enumerateDataBlocks == null) return;
        if (!force && _availableDataBlocks != null) return;

        IsLoadingDataBlocks = true;
        try
        {
            // Enumeration runs on the UI thread today: TIA Openness calls
            // must originate from the same thread that owns the project.
            // The visible spinner + the dialog's scoped scope keep this OK
            // for typical project sizes; if it ever bites we'd move the
            // enumeration onto a background task with a marshalled call.
            _availableDataBlocks = DataBlockListFilter.Sort(_enumerateDataBlocks());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DB enumeration failed");
            _availableDataBlocks = Array.Empty<DataBlockSummary>();
            StatusText = Res.Format("Status_DbEnumFailed", ex.Message);
        }
        finally
        {
            IsLoadingDataBlocks = false;
        }
    }

    private void ApplyDataBlockFilter()
    {
        var source = _availableDataBlocks ?? (IReadOnlyList<DataBlockSummary>)Array.Empty<DataBlockSummary>();
        FilteredDataBlocks = DataBlockListFilter.Filter(source, _dataBlockSearchText);
    }

    /// <summary>
    /// Switch the dialog over to a different DB (#59). When the current DB
    /// has staged inline edits, prompts a 3-way choice:
    /// <list type="bullet">
    ///   <item><b>Yes</b> — apply staged edits to TIA, then switch.</item>
    ///   <item><b>No</b> — keep staged edits in an in-memory stash so the user
    ///     can come back to this DB later in the same session, then switch.</item>
    ///   <item><b>Cancel</b> — stay on the current DB.</item>
    /// </list>
    /// On switch in, looks up <see cref="_stashedDbs"/> for any prior stash and
    /// re-applies it to the freshly loaded tree (orphan paths drop silently
    /// with a status note when the DB structure has changed since stashing).
    /// Returns true on a successful switch, false on cancel / no-op.
    /// </summary>
    public bool SwitchToDataBlock(DataBlockSummary summary)
    {
        if (_switchToDataBlock == null) return false;
        if (string.Equals(summary.Name, _dataBlockInfo.Name, StringComparison.Ordinal)
            && string.Equals(summary.FolderPath, GetCurrentFolderPath(), StringComparison.Ordinal))
        {
            // Already on the target DB — just close the dropdown.
            IsDataBlocksDropdownOpen = false;
            return false;
        }

        // Snapshot any active pending edits so we can either commit them, stash
        // them, or (on Cancel) leave them untouched on the live tree.
        var pendingSnapshot = SnapshotPendingEdits();

        if (pendingSnapshot.Count > 0)
        {
            var message = Res.Format("Dialog_SwitchDb_KeepConfirm_Text",
                pendingSnapshot.Count, _dataBlockInfo.Name, summary.Name);
            var choice = _messageBox.AskYesNoCancel(
                message, Res.Get("Dialog_SwitchDb_KeepConfirm_Title"));

            switch (choice)
            {
                case YesNoCancelResult.Cancel:
                    return false;
                case YesNoCancelResult.Yes:
                    // Apply path: commit staged edits to TIA before leaving the
                    // current DB. CommitChanges returns false if the user
                    // cancels the inconsistent-block compile prompt — abort
                    // the switch so their work isn't silently dropped.
                    if (!ExecuteApplyForSwitch())
                        return false;
                    // After Apply succeeds the tree's PendingValue fields are
                    // cleared by RefreshTree, so nothing to stash.
                    break;
                case YesNoCancelResult.No:
                    // Stash path: capture the snapshot under the current DB's
                    // identity. Replaces any prior stash for this DB so the
                    // newest edits win.
                    StashCurrentDb(pendingSnapshot);
                    DiscardPendingSilent();
                    break;
            }
        }

        IsDataBlocksDropdownOpen = false;

        try
        {
            var newXml = _switchToDataBlock(summary);
            _currentXml = newXml;
            // Reset selection / scope before RefreshTree — the path / scope
            // we'd try to restore belong to a different DB and would log a
            // warning every switch.
            _selectedFlatMember = null;
            _selectedScope = null;
            _manualSelectedPaths.Clear();
            _newValue = "";
            _newValueTouched = false;
            _suppressSuggestions = true;

            RefreshTree(newXml, _udtResolver, _commentResolver);

            _suppressSuggestions = false;
            AvailableScopes.Clear();

            var version = typeof(BulkChangeViewModel).Assembly.GetName().Version;
            Title = $"BlockParam v{version}: {_dataBlockInfo.Name}";
            OnPropertyChanged(nameof(CurrentDataBlockName));
            OnPropertyChanged(nameof(SelectedFlatMember));
            OnPropertyChanged(nameof(SelectedScope));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasScope));
            OnPropertyChanged(nameof(NewValue));
            OnPropertyChanged(nameof(SelectedMemberDisplay));

            // Restore any prior stash for this DB. Removes the stash entry on
            // success — once edits are back on the live tree they're tracked
            // there, not in the stash.
            var (restored, dropped) = RestoreStashFor(summary);

            if (restored > 0 || dropped > 0)
            {
                StatusText = dropped == 0
                    ? Res.Format("Status_DbSwitched_StashRestored", _dataBlockInfo.Name, restored)
                    : Res.Format("Status_DbSwitched_StashPartial",
                        _dataBlockInfo.Name, restored, dropped);
            }
            else
            {
                StatusText = Res.Format("Status_DbSwitched", _dataBlockInfo.Name);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DB switch to {Name} failed", summary.Name);
            StatusText = Res.Format("Status_DbSwitchFailed", summary.Name, ex.Message);
            _messageBox.ShowError(
                Res.Format("Dialog_SwitchDb_LoadFailed", summary.Name, ex.Message),
                Res.Get("Rollback_Title"));
            return false;
        }
    }

    private string GetCurrentFolderPath()
    {
        // Best-effort: the cached list is the only place we can look up the
        // current DB's folder path. Empty fallback keeps dedupe permissive.
        if (_availableDataBlocks == null) return "";
        return _availableDataBlocks
            .FirstOrDefault(b => string.Equals(b.Name, _dataBlockInfo.Name, StringComparison.Ordinal))
            ?.FolderPath ?? "";
    }

    /// <summary>
    /// Walks the live tree and snapshots every node with a <c>PendingValue</c>
    /// into inert <see cref="StashedEditEntry"/> rows. Used both before a
    /// switch (to populate the stash) and to render the badge count.
    /// </summary>
    private IReadOnlyList<StashedEditEntry> SnapshotPendingEdits()
    {
        var list = new List<StashedEditEntry>();
        SnapshotRecursive(RootMembers, list);
        return list;
    }

    private static void SnapshotRecursive(
        IEnumerable<MemberNodeViewModel> nodes, List<StashedEditEntry> output)
    {
        foreach (var node in nodes)
        {
            if (node.PendingValue != null)
            {
                output.Add(new StashedEditEntry(
                    node.Path,
                    node.StartValue ?? "",
                    node.PendingValue));
            }
            SnapshotRecursive(node.Children, output);
        }
    }

    private static string StashKey(DataBlockSummary summary) =>
        summary.FolderPath + "" + summary.Name;

    private void StashCurrentDb(IReadOnlyList<StashedEditEntry> edits)
    {
        var summary = new DataBlockSummary(
            _dataBlockInfo.Name,
            GetCurrentFolderPath(),
            blockType: _dataBlockInfo.BlockType,
            isInstanceDb: string.Equals(_dataBlockInfo.BlockType, "InstanceDB", StringComparison.Ordinal));
        var key = StashKey(summary);
        var state = new StashedDbState(summary, edits);
        _stashedDbs[key] = state;
        SyncStashedDbsCollection();
        Log.Information("Stashed {Count} pending edit(s) for DB {Db}",
            edits.Count, summary.Name);
    }

    /// <summary>
    /// Re-applies a stash to the freshly loaded tree. Returns (restored, dropped)
    /// so the caller can surface a status line when paths no longer resolve
    /// (DB was edited externally between stash and restore).
    /// </summary>
    private (int restored, int dropped) RestoreStashFor(DataBlockSummary summary)
    {
        var key = StashKey(summary);
        if (!_stashedDbs.TryGetValue(key, out var state)) return (0, 0);

        int restored = 0;
        int dropped = 0;
        foreach (var edit in state.Edits)
        {
            var node = FindNodeByPath(edit.Path);
            if (node is { IsLeaf: true })
            {
                // EditableStartValue setter routes through the same path as
                // user typing, so validation + pending-list aggregation fires.
                node.EditableStartValue = edit.PendingValue;
                restored++;
            }
            else
            {
                dropped++;
                Log.Information("Stashed edit dropped (path no longer exists): {Path}", edit.Path);
            }
        }

        _stashedDbs.Remove(key);
        SyncStashedDbsCollection();
        Log.Information("Restored {Restored} stashed edit(s) for {Db} ({Dropped} dropped)",
            restored, summary.Name, dropped);
        return (restored, dropped);
    }

    /// <summary>Mirrors the dictionary into the bound <see cref="StashedDbs"/> collection.</summary>
    private void SyncStashedDbsCollection()
    {
        StashedDbs.Clear();
        foreach (var state in _stashedDbs.Values
            .OrderBy(s => s.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.DbName, StringComparer.OrdinalIgnoreCase))
        {
            StashedDbs.Add(state);
        }
        OnPropertyChanged(nameof(HasStashedDbs));
    }

    /// <summary>
    /// Yes-on-prompt path: commits staged edits to TIA via the existing Apply
    /// path. Used by the DB-switcher 3-way prompt (#59) so the user's "Apply
    /// before switching" choice goes through the same code path as the Apply
    /// button and surfaces the same compile-prompt UX on inconsistent blocks.
    /// </summary>
    private bool ExecuteApplyForSwitch()
    {
        if (!ApplyCommand.CanExecute(null))
        {
            // Mirror the Apply button's gating — invalid pending edits, license
            // limit, etc. shouldn't be silently bypassed by the switch flow.
            StatusText = Res.Get("Status_DbSwitch_ApplyBlocked");
            return false;
        }

        ExecuteApply();
        // ExecuteApply leaves _hasPendingChanges=true and StatusText set on
        // success; HasInlineErrors stays false. Treat success as "no errors
        // on the tree after the call" — failed Apply paths set status and
        // leave pending edits in place.
        return PendingInlineEditCount == 0;
    }


    public bool IsInspectorCollapsed
    {
        get => _isInspectorCollapsed;
        set
        {
            if (_isInspectorCollapsed == value) return;
            _isInspectorCollapsed = value;
            OnPropertyChanged(nameof(IsInspectorCollapsed));
            OnPropertyChanged(nameof(IsInspectorExpanded));
        }
    }

    public bool IsInspectorExpanded => !_isInspectorCollapsed;

    public bool IsBulkEditExpanded
    {
        get => _isBulkEditExpanded;
        set { if (_isBulkEditExpanded != value) { _isBulkEditExpanded = value; OnPropertyChanged(nameof(IsBulkEditExpanded)); } }
    }

    public bool IsBulkPreviewExpanded
    {
        get => _isBulkPreviewExpanded;
        set { if (_isBulkPreviewExpanded != value) { _isBulkPreviewExpanded = value; OnPropertyChanged(nameof(IsBulkPreviewExpanded)); } }
    }

    public bool IsPendingExpanded
    {
        get => _isPendingExpanded;
        set { if (_isPendingExpanded != value) { _isPendingExpanded = value; OnPropertyChanged(nameof(IsPendingExpanded)); } }
    }

    public bool IsIssuesExpanded
    {
        get => _isIssuesExpanded;
        set { if (_isIssuesExpanded != value) { _isIssuesExpanded = value; OnPropertyChanged(nameof(IsIssuesExpanded)); } }
    }

    public ICommand ToggleBulkEditCommand { get; }
    public ICommand ToggleBulkPreviewCommand { get; }
    public ICommand TogglePendingCommand { get; }
    public ICommand ToggleIssuesCommand { get; }

    /// <summary>
    /// Raised after the flat list has been refreshed so the view can rehydrate
    /// the ListView's multi-selection from <see cref="ManualSelectedPaths"/>.
    /// </summary>
    public event Action? FlatListRefreshed;

    public string LicenseTierText { get; private set; } = "";

    /// <summary>
    /// Show the license dialog opener whenever a license service is available — users need
    /// access even when Pro (to view status, remove / re-activate on a new machine, etc.).
    /// </summary>
    public bool ShowLicenseKeyButton => _licenseService != null;

    /// <summary>Label on the license dialog opener — adapts to tier.</summary>
    public string LicenseKeyButtonText =>
        _licenseService?.IsProActive == true
            ? Res.Get("License_ManageKey")
            : Res.Get("License_EnterKey");
    public bool IsLimitReached => _usageTracker.GetStatus().IsLimitReached
                               || _usageTracker.GetInlineStatus().IsLimitReached;

    /// <summary>Number of individual inline edits waiting to be applied.</summary>
    public int PendingInlineEditCount => CountPendingInlineEdits(RootMembers);

    /// <summary>Status text showing pending inline edits count.</summary>
    public string? PendingStatusText
    {
        get
        {
            var count = PendingInlineEditCount;
            return count > 0 ? $"{count} pending inline edit{(count == 1 ? "" : "s")}" : null;
        }
    }

    /// <summary>Suggestion provider for autocomplete (null = no suggestions).</summary>
    public GlobSuggestionProvider? SuggestionProvider
    {
        get => _suggestionProvider;
        private set => SetProperty(ref _suggestionProvider, value);
    }

    /// <summary>All autocomplete suggestions for the current member.</summary>
    public IReadOnlyList<AutocompleteSuggestion> Suggestions
    {
        get => _suggestions;
        private set => SetProperty(ref _suggestions, value);
    }

    /// <summary>Filtered suggestions based on current text input.</summary>
    public IReadOnlyList<AutocompleteSuggestion> FilteredSuggestions
    {
        get => _filteredSuggestions;
        private set
        {
            if (SetProperty(ref _filteredSuggestions, value))
                OnPropertyChanged(nameof(HasFilteredSuggestions));
        }
    }

    public bool HasFilteredSuggestions => _filteredSuggestions.Count > 0;

    /// <summary>Returns filtered suggestions for a specific member (used by inline autocomplete).</summary>
    public IReadOnlyList<AutocompleteSuggestion> GetSuggestionsForMember(MemberNodeViewModel memberVm, string filter)
    {
        if (!memberVm.IsLeaf) return Array.Empty<AutocompleteSuggestion>();

        EnsureTagTableCache();
        if (_tagTableCache == null) return Array.Empty<AutocompleteSuggestion>();

        var config = _configLoader.GetConfig();
        var rule = config?.GetRule(memberVm.Model);

        IReadOnlyList<AutocompleteSuggestion> all;
        if (rule?.TagTableReference != null && _autocompleteProvider != null)
            all = _autocompleteProvider.GetSuggestions(memberVm.Model, "");
        else if (_showConstants)
            all = _tagTableCache.GetTableNames()
                .SelectMany(name => _tagTableCache.GetEntries(name))
                .Select(e => new AutocompleteSuggestion(e.Value, e.Name, e.Comment))
                .ToList();
        else
            return Array.Empty<AutocompleteSuggestion>();

        if (string.IsNullOrWhiteSpace(filter))
            return all;

        var terms = filter.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return all.Where(s => terms.All(term =>
            s.DisplayName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
            s.Value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
            (s.Comment != null && s.Comment.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)))
            .ToList();
    }

    /// <summary>
    /// True when a keystroke on <see cref="NewValue"/> has scheduled a
    /// debounce timer that hasn't fired yet. Test-only seam for verifying
    /// that <see cref="AcceptSuggestion"/> cancels the trailing timer.
    /// </summary>
    internal bool HasPendingValueDebounce => _valueDebounceTimer != null;

    /// <summary>Accept a suggestion: set value and close the list.</summary>
    public void AcceptSuggestion(string value)
    {
        // Cancel any pending debounce from prior keystrokes — otherwise the
        // timer fires after we clear FilteredSuggestions and repopulates it
        // from the accepted value, re-opening the overlay.
        _valueDebounceTimer?.Dispose();
        _valueDebounceTimer = null;

        _newValue = value; // Set backing field to avoid re-triggering filter
        OnPropertyChanged(nameof(NewValue));
        ValidateValue();
        FilteredSuggestions = Array.Empty<AutocompleteSuggestion>();
        UpdateHighlighting();
    }

    /// <summary>Show suggestions filtered by current text (always opens).</summary>
    public void ShowAllSuggestions()
    {
        if (_suggestions.Count == 0) return;

        var filter = _newValue?.Trim() ?? "";
        if (string.IsNullOrEmpty(filter))
        {
            FilteredSuggestions = _suggestions.ToList();
            return;
        }

        var terms = filter.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        FilteredSuggestions = _suggestions
            .Where(s => terms.All(term =>
                s.DisplayName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.Value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (s.Comment != null && s.Comment.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)))
            .ToList();
    }

    /// <summary>Toggle: show filtered suggestions or hide them.</summary>
    public void ToggleAllSuggestions()
    {
        if (_filteredSuggestions.Count > 0)
        {
            FilteredSuggestions = Array.Empty<AutocompleteSuggestion>();
        }
        else
        {
            // Apply current text as filter (empty = show all)
            var filter = _newValue?.Trim() ?? "";
            if (string.IsNullOrEmpty(filter))
            {
                FilteredSuggestions = _suggestions.ToList();
                return;
            }

            var terms = filter.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            FilteredSuggestions = _suggestions
                .Where(s => terms.All(term =>
                    s.DisplayName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.Value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (s.Comment != null && s.Comment.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)))
                .ToList();
        }
    }

    /// <summary>Whether to show constant suggestions. Forced on when a rule with tagTableReference matches.</summary>
    public bool ShowConstants
    {
        get => _showConstants;
        set
        {
            if (!_constantsForced || value) // Can't uncheck when forced
            {
                if (SetProperty(ref _showConstants, value))
                    ReloadSuggestions();
            }
        }
    }

    /// <summary>True when a config rule forces constants on (checkbox disabled).</summary>
    public bool ConstantsForced
    {
        get => _constantsForced;
        private set => SetProperty(ref _constantsForced, value);
    }

    /// <summary>Display string showing how old the tag table data is.</summary>
    public string TagTableAge
    {
        get => _tagTableAge;
        private set => SetProperty(ref _tagTableAge, value);
    }

    public ICommand RefreshConstantsCommand { get; }

    public bool HasCommentConfig =>
        _configLoader.GetConfig()?.Rules.Any(r => !string.IsNullOrEmpty(r.CommentTemplate)) == true;

    // --- Public methods (called from code-behind) ---

    /// <summary>
    /// Toggles expand/collapse for a node and refreshes the flat list.
    /// </summary>
    public void ToggleExpand(MemberNodeViewModel node)
    {
        _flatTreeManager.ToggleExpand(node, RootMembers);
    }

    // --- Private methods ---

    /// <summary>
    /// True while <see cref="RefreshFlatList"/> is rebuilding <see cref="FlatMembers"/>.
    /// The view checks this in its SelectionChanged handler to ignore the ghost
    /// "removed items" events WPF raises when the ItemsSource is mutated.
    /// </summary>
    public bool IsRefreshing => _isRefreshing;

    /// <summary>
    /// For scripted scenarios (DevLauncher screenshot capture, future UI tests):
    /// cancels the 150ms debounce on <see cref="NewValue"/> and runs highlighting
    /// synchronously so the next render frame reflects the staged state.
    /// Not used by the interactive UI.
    /// </summary>
    internal void FlushPendingHighlighting()
    {
        _valueDebounceTimer?.Dispose();
        _valueDebounceTimer = null;
        ValidateValue();
        UpdateFilteredSuggestions();
        UpdateHighlighting();
    }

    /// <summary>
    /// Scripted-only companion to <see cref="FlushPendingHighlighting"/>:
    /// cancels the 200ms debounce on <see cref="SearchQuery"/> and runs the
    /// filter + flat-list refresh synchronously.
    /// </summary>
    internal void FlushPendingSearch()
    {
        _searchDebounceTimer?.Dispose();
        _searchDebounceTimer = null;
        ApplyAllFilters();
        RefreshFlatList();
    }

    /// <summary>
    /// Scripted-only: clears <see cref="NewValue"/> and the internal
    /// user-touched flag so a subsequent member selection can prefill the
    /// field via the normal interactive path. The public setter would set
    /// the touched flag to true, which suppresses prefill.
    /// </summary>
    internal void ResetNewValueSilent()
    {
        _newValue = "";
        _newValueTouched = false;
        OnPropertyChanged(nameof(NewValue));
    }

    public void RefreshFlatList()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var selectedPath = _selectedFlatMember?.Path;
            _flatTreeManager.Refresh(RootMembers);

            // Restore selection in the new flat list (same node by path)
            if (selectedPath != null)
            {
                var restored = _flatTreeManager.FlatList
                    .FirstOrDefault(m => m.Path == selectedPath);
                // Set backing field directly to avoid re-triggering OnMemberSelected
                _selectedFlatMember = restored;
                OnPropertyChanged(nameof(SelectedFlatMember));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedMemberDisplay));
            }

            // Rehydrate multi-selection while _isRefreshing is still true so that
            // cascaded SelectedItem changes (e.g. from SelectedItems.Clear()) do
            // NOT trigger OnMemberSelected → UpdateHighlighting → recursion.
            FlatListRefreshed?.Invoke();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void OnMemberSelected(MemberNodeViewModel? memberVm)
    {
        AvailableScopes.Clear();
        _selectedScope = null; // Set backing field to avoid triggering UpdateHighlighting twice
        OnPropertyChanged(nameof(SelectedScope));
        OnPropertyChanged(nameof(HasScope));
        OnPropertyChanged(nameof(CanEdit));
        ValidationError = "";
        ConstraintInfo = "";
        SuggestionProvider = null;
        Suggestions = Array.Empty<AutocompleteSuggestion>();
        FilteredSuggestions = Array.Empty<AutocompleteSuggestion>();

        // In manual multi-select mode, scope analysis does not apply.
        if (IsManualMode)
        {
            // Scope highlighting is already cleared by UpdateManualSelection when
            // we entered manual mode. Do NOT call UpdateHighlighting/RefreshFlatList
            // here: WPF may be mid-way through processing a Ctrl+Click, and mutating
            // the flat list + re-rehydrating SelectedItems now causes WPF to
            // reconcile against stale state and drop the wrong rows.
            PrefillNewValueFromFeaturedMember(memberVm);
            ValidateValue();
            return;
        }

        if (memberVm == null || !memberVm.IsLeaf)
        {
            UpdateHighlighting();
            // No selection: reset touched so the next click prefills cleanly.
            _newValueTouched = false;
            _newValue = "";
            OnPropertyChanged(nameof(NewValue));
            return;
        }

        // Pre-fill with current (or pending) value unless the user has typed.
        PrefillNewValueFromFeaturedMember(memberVm);

        // Determine if constants should be shown (forced by rule or user choice)
        var acConfig = _configLoader.GetConfig();
        var rule = acConfig?.GetRule(memberVm.Model);
        var hasRuleWithTagTable = rule?.TagTableReference != null;

        if (hasRuleWithTagTable)
        {
            ConstantsForced = true;
            _showConstants = true; // Set backing field to avoid triggering ReloadSuggestions twice
            OnPropertyChanged(nameof(ShowConstants));
        }
        else
        {
            ConstantsForced = false;
            // Keep user's previous ShowConstants choice
        }

        ReloadSuggestions();

        var result = _analyzer.Analyze(_dataBlockInfo, memberVm.Model);

        if (!result.HasBulkOptions)
        {
            StatusText = Res.Get("Status_SingleOccurrence");
            return;
        }

        foreach (var scope in result.Scopes.Reverse())
            AvailableScopes.Add(scope);

        if (AvailableScopes.Count > 0)
            SelectedScope = AvailableScopes[0];

        // Proactive hint (#7) — single source of truth with the inline-edit tooltip.
        ConstraintInfo = memberVm.RuleHint ?? "";

        StatusText = "";

        // Re-sync selection after flat list rebuilds during click processing
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (memberVm != null && _selectedFlatMember != memberVm)
            {
                _selectedFlatMember = memberVm;
                OnPropertyChanged(nameof(SelectedFlatMember));
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void UpdateHighlighting()
    {
        foreach (var root in RootMembers)
            root.ClearAffected();

        // Clear all comment previews
        foreach (var root in RootMembers)
            ClearCommentPreviews(root);

        // Re-expand search matches and pending edits after ClearAffected collapsed them
        ReExpandNonAffected();

        // Keep the selected member visible after ClearAffected collapsed smart-expands
        _selectedFlatMember?.EnsureVisible();

        if (_selectedScope == null && !IsManualMode)
        {
            ComputeBulkPreview();
            RebuildPendingEdits();
            RefreshFlatList();
            return;
        }

        if (_selectedScope != null)
        {
            var affectedPaths = new HashSet<string>(
                _selectedScope.MatchingMembers.Select(m => m.Path));

            foreach (var root in RootMembers)
                HighlightAffected(root, affectedPaths, _newValue);
        }

        // Generate comment previews if template is configured
        UpdateCommentPreviews();

        ComputeBulkPreview();
        // Pending rows' conflict flag depends on BulkPreview — refresh after compute.
        RebuildPendingEdits();
        RefreshFlatList();
    }

    /// <summary>
    /// Rebuilds <see cref="BulkPreview"/> from the current (scope or manual
    /// selection) × NewValue state. Rows whose effective value already equals
    /// NewValue are skipped. Rows whose effective value differs get an entry
    /// with <c>HasPendingConflict = true</c> if they currently carry a pending
    /// inline edit (that edit would be overwritten on Set).
    /// Does NOT touch <see cref="PendingEdits"/> — callers that also need
    /// pending refreshed should use <see cref="RefreshPendingAndPreview"/> or
    /// call <see cref="RebuildPendingEdits"/> explicitly after this.
    /// </summary>
    private void ComputeBulkPreview()
    {
        bool wasNonEmpty = BulkPreview.Count > 0;
        BulkPreview.Clear();

        // No input → nothing to compute. Avoid walking scope members on every
        // inline keystroke when the inspector isn't being driven at all.
        bool hasInput = !string.IsNullOrEmpty(_newValue)
                        && (IsManualMode || _selectedScope != null);
        if (hasInput)
        {
            if (IsManualMode)
            {
                foreach (var path in _manualSelectedPaths)
                {
                    var node = FindNodeByPath(path);
                    if (node == null || !node.IsLeaf) continue;
                    TryAddPreviewEntry(node);
                }
            }
            else if (_selectedScope != null)
            {
                foreach (var m in _selectedScope.MatchingMembers)
                {
                    var node = FindNodeByPath(m.Path);
                    if (node == null || !node.IsLeaf) continue;
                    TryAddPreviewEntry(node);
                }
            }
        }

        // Only raise bindings when the result might actually have changed.
        if (wasNonEmpty || BulkPreview.Count > 0)
        {
            OnPropertyChanged(nameof(HasBulkPreview));
            OnPropertyChanged(nameof(BulkPreviewCount));
            OnPropertyChanged(nameof(BulkPreviewSummary));
            OnPropertyChanged(nameof(BulkPreviewConflictCount));
            OnPropertyChanged(nameof(HasBulkPreviewConflict));
            OnPropertyChanged(nameof(BulkPreviewConflictWarning));
        }
    }

    private void TryAddPreviewEntry(MemberNodeViewModel node)
    {
        var effective = node.IsPendingInlineEdit
            ? (node.EditableStartValue ?? node.StartValue ?? "")
            : (node.StartValue ?? "");
        if (string.Equals(effective, _newValue, StringComparison.OrdinalIgnoreCase))
            return;
        BulkPreview.Add(new BulkPreviewEntry(
            node,
            node.StartValue ?? "",
            _newValue,
            node.IsPendingInlineEdit));
    }

    /// <summary>
    /// Refreshes BOTH the bulk preview and the pending edits queue plus the
    /// associated pending-count / status notifications. Use after any change
    /// that could shift which nodes are pending OR which rows the preview
    /// would overwrite — the two sides are coupled (preview conflict flags
    /// depend on pending state, pending overwrite flags depend on preview).
    /// Call order matters: preview first so <c>RebuildPendingEdits</c> can
    /// read the just-built BulkPreview paths.
    /// </summary>
    private void RefreshPendingAndPreview()
    {
        OnPropertyChanged(nameof(PendingInlineEditCount));
        OnPropertyChanged(nameof(PendingStatusText));
        ComputeBulkPreview();
        RebuildPendingEdits();
    }

    /// <summary>
    /// Rebuilds <see cref="PendingEdits"/> from the current tree state.
    /// Call whenever <c>PendingInlineEditCount</c> is expected to have changed.
    /// </summary>
    private void RebuildPendingEdits()
    {
        PendingEdits.Clear();
        var bulkPaths = BulkPreview.Count > 0
            ? new HashSet<string>(BulkPreview.Select(e => e.Path), StringComparer.Ordinal)
            : null;
        CollectPendingEntries(RootMembers, bulkPaths);
        OnPropertyChanged(nameof(HasPendingEdits));
        RaiseInvalidPendingChanged();
    }

    /// <summary>
    /// Nudges bindings attached to <see cref="InvalidPendingCount"/> and friends.
    /// Call after any mutation that could flip a pending entry's error state.
    /// </summary>
    private void RaiseInvalidPendingChanged()
    {
        OnPropertyChanged(nameof(InvalidPendingCount));
        OnPropertyChanged(nameof(HasInvalidPending));
        OnPropertyChanged(nameof(InvalidPendingBadge));
    }

    private void CollectPendingEntries(IEnumerable<MemberNodeViewModel> nodes,
        HashSet<string>? bulkPaths)
    {
        foreach (var node in nodes)
        {
            if (node.IsPendingInlineEdit)
            {
                bool overwritten = bulkPaths != null && bulkPaths.Contains(node.Path);
                PendingEdits.Add(new PendingEditEntry(
                    node,
                    node.StartValue ?? "",
                    node.PendingValue ?? "",
                    willBeOverwrittenByBulk: overwritten));
            }
            CollectPendingEntries(node.Children, bulkPaths);
        }
    }

    /// <summary>
    /// Undoes a single pending edit (per-row ↶ in the pending queue).
    /// </summary>
    public void UndoPendingEdit(PendingEditEntry entry)
    {
        if (entry?.Node == null) return;
        entry.Node.ClearPending();
        RefreshPendingAndPreview();
        RefreshFlatList();
    }

    private static void ClearCommentPreviews(MemberNodeViewModel node)
    {
        node.PreviewComment = null;
        foreach (var child in node.Children)
            ClearCommentPreviews(child);
    }

    /// <summary>
    /// Resolves the effective value for a MemberNode: pending value if edited, otherwise StartValue.
    /// </summary>
    private string? ResolvePendingValue(MemberNode model)
    {
        var vm = FindNodeByPath(model.Path);
        if (vm != null && vm.IsPendingInlineEdit)
            return vm.EditableStartValue;
        return model.StartValue;
    }

    private void UpdateCommentPreviews()
    {
        var config = _configLoader.GetConfig();
        if (config == null || _selectedScope == null) return;
        if (!config.Rules.Any(r => !string.IsNullOrEmpty(r.CommentTemplate))) return;

        EnsureTagTableCache();
        var generator = new TemplateCommentGenerator(config, _tagTableCache);
        var previews = generator.GenerateForScope(
            _dataBlockInfo, _selectedScope.MatchingMembers.ToList(),
            valueResolver: ResolvePendingValue);

        foreach (var (target, comment) in previews)
        {
            var vm = FindNodeByPath(target.Path);
            if (vm != null)
            {
                vm.PreviewComment = comment;
            }
        }
    }

    private string ApplyCommentPreviews(string xml)
    {
        var commentTargets = new List<MemberNodeViewModel>();
        CollectPendingCommentNodes(RootMembers, commentTargets);

        if (commentTargets.Count == 0) return xml;

        var config = _configLoader.GetConfig();
        EnsureTagTableCache();
        var templateGen = config != null ? new TemplateCommentGenerator(config, _tagTableCache) : null;

        foreach (var node in commentTargets)
        {
            var rule = config?.GetCommentRule(node.Model);
            if (rule?.CommentTemplate == null) continue;

            foreach (var lang in _projectLanguages)
            {
                var comment = templateGen!.Generate(_dataBlockInfo, node.Model, rule.CommentTemplate, lang, ResolvePendingValue);
                xml = _writer.ModifyComment(xml, node.Model, comment, lang);
                Log.Information("Comment updated: {Path} → {Comment} ({Lang})", node.Model.Path, comment, lang);
            }
        }

        return xml;
    }

    private static void CollectPendingCommentNodes(
        IEnumerable<MemberNodeViewModel> nodes,
        List<MemberNodeViewModel> result)
    {
        foreach (var node in nodes)
        {
            if (node.PreviewComment != null && node.CommentState != "unchanged")
                result.Add(node);
            CollectPendingCommentNodes(node.Children, result);
        }
    }

    private MemberNodeViewModel? FindNodeByPath(string path)
    {
        foreach (var root in RootMembers)
        {
            var found = FindNodeByPathRecursive(root, path);
            if (found != null) return found;
        }
        return null;
    }

    private static MemberNodeViewModel? FindNodeByPathRecursive(MemberNodeViewModel node, string path)
    {
        if (node.Path == path) return node;
        foreach (var child in node.Children)
        {
            var found = FindNodeByPathRecursive(child, path);
            if (found != null) return found;
        }
        return null;
    }

    private void HighlightAffected(MemberNodeViewModel node, HashSet<string> affectedPaths, string newValue)
    {
        if (affectedPaths.Contains(node.Path))
        {
            var effectiveValue = node.IsPendingInlineEdit
                ? (node.EditableStartValue ?? node.StartValue ?? "")
                : (node.StartValue ?? "");
            var alreadyHasValue = !string.IsNullOrEmpty(newValue)
                && string.Equals(effectiveValue, newValue, StringComparison.OrdinalIgnoreCase);

            if (alreadyHasValue)
                node.IsAlreadyMatching = true;
            else
                node.IsAffected = true;

            node.EnsureVisible();
        }

        foreach (var child in node.Children)
            HighlightAffected(child, affectedPaths, newValue);

        node.RaisePropertyChanged(nameof(node.AffectedBadge));
    }

    private void ApplyAllFilters()
    {
        HashSet<string>? searchPaths = null;
        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            var searchResult = _searchService.Search(_dataBlockInfo, _searchQuery);
            searchPaths = new HashSet<string>(searchResult.Matches.Select(m => m.Path));
            SearchHitCount = searchResult.HitCount;
        }
        else
        {
            SearchHitCount = 0;
        }

        var config = _configLoader.GetConfig();
        var excludeSet = BuildExcludeSet(config);

        HiddenByRuleCount = excludeSet == null
            ? 0
            : _dataBlockInfo.AllMembers().Count(m => m.IsLeaf && excludeSet.Contains(m.Path));

        foreach (var root in RootMembers)
            root.ApplyFilter(ruleFilterActive: true, searchPaths, excludeSet, _showSetpointsOnly);

        // Smart-expand parents of search matches
        if (searchPaths != null)
        {
            foreach (var root in RootMembers)
                SmartExpandSearchMatches(root, searchPaths);
        }

        // Pending inline edits no longer smart-expand (#10) — they're surfaced
        // in the sidebar, so forcing the tree open was redundant and disruptive.
    }

    private void SmartExpandSearchMatches(MemberNodeViewModel node, HashSet<string> searchPaths)
    {
        if (searchPaths.Contains(node.Path))
            node.EnsureVisible();

        foreach (var child in node.Children)
            SmartExpandSearchMatches(child, searchPaths);
    }

    /// <summary>
    /// Re-expands search matches after <see cref="MemberNodeViewModel.ClearAffected"/>
    /// collapsed smart-expanded nodes. Pending inline edits are intentionally not
    /// re-expanded (#10) — they live in the sidebar.
    /// </summary>
    private void ReExpandNonAffected()
    {
        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            var searchResult = _searchService.Search(_dataBlockInfo, _searchQuery);
            var searchPaths = new HashSet<string>(searchResult.Matches.Select(m => m.Path));
            foreach (var root in RootMembers)
                SmartExpandSearchMatches(root, searchPaths);
        }
    }

    private void ValidateValue()
    {
        // Reset row highlights from any previous manual-mode validation before
        // we re-evaluate. Pending inline edits keep their own error state.
        ClearBulkRowHighlights();

        // Manual mode: block on mixed types, otherwise validate each selected
        // member against its own rule (different members may reference different
        // tag tables / constraints).
        if (IsManualMode)
        {
            if (!IsSelectionTypeHomogeneous)
            {
                ValidationError = Res.Get("Validation_MixedDatatypes");
                return;
            }

            if (string.IsNullOrEmpty(_newValue))
            {
                ValidationError = "";
                return;
            }

            string? firstError = null;
            string? firstErrorName = null;
            foreach (var path in _manualSelectedPaths)
            {
                var node = FindNodeByPath(path);
                if (node == null || !node.IsLeaf) continue;
                var memberError = ValidateValueForMember(node, _newValue);
                if (memberError != null)
                {
                    // Paint the offending row red (unless it already has a
                    // pending inline edit, which owns HasInlineError).
                    if (!node.IsPendingInlineEdit)
                    {
                        node.HasInlineError = true;
                        node.InlineErrorMessage = memberError;
                        _bulkErrorPaths.Add(path);
                    }
                    if (firstError == null)
                    {
                        firstError = memberError;
                        firstErrorName = node.Name;
                    }
                }
            }

            ValidationError = firstError != null ? $"{firstErrorName}: {firstError}" : "";
            return;
        }

        if (string.IsNullOrEmpty(_newValue) || _selectedFlatMember == null)
        {
            ValidationError = "";
            return;
        }

        ValidationError = ValidateValueForMember(_selectedFlatMember, _newValue) ?? "";
    }

    /// <summary>
    /// Clears row-level error highlights set by manual-mode bulk validation,
    /// without touching rows that have their own pending inline edit error.
    /// </summary>
    private void ClearBulkRowHighlights()
    {
        if (_bulkErrorPaths.Count == 0) return;
        foreach (var path in _bulkErrorPaths)
        {
            var n = FindNodeByPath(path);
            if (n != null && !n.IsPendingInlineEdit)
            {
                n.HasInlineError = false;
                n.InlineErrorMessage = null;
            }
        }
        _bulkErrorPaths.Clear();
    }

    /// <summary>
    /// Runs the full validation pipeline for a single member via the shared
    /// <see cref="MemberValidator"/>. Returns null on success.
    /// </summary>
    private string? ValidateValueForMember(MemberNodeViewModel memberVm, string value) =>
        BuildValidator().Validate(memberVm.Model, value);

    /// <summary>
    /// Builds a validator pinned to the current config + tag-table cache. Fresh
    /// on each call so rule/cache invalidation is picked up without bookkeeping.
    /// </summary>
    private MemberValidator BuildValidator()
    {
        EnsureTagTableCache();
        return new MemberValidator(_configLoader.GetConfig(), _tagTableCache);
    }

    /// <summary>
    /// Refreshes <see cref="MemberNodeViewModel.RuleHint"/> on every node so
    /// rule-constrained cells surface the hint proactively (tooltip + inspector).
    /// Hints read only rule metadata — no tag-table export is forced here; that
    /// stays lazy and runs on the validation path.
    /// </summary>
    private void RefreshRuleHints()
    {
        var config = _configLoader.GetConfig();
        foreach (var root in RootMembers)
            ApplyRuleHint(root, config);
    }

    private static void ApplyRuleHint(MemberNodeViewModel node, BulkChangeConfig? config)
    {
        node.RuleHint = RuleHintFormatter.Format(config?.GetRule(node.Model), node.Model.Datatype);
        foreach (var child in node.Children)
            ApplyRuleHint(child, config);
    }

    /// <summary>
    /// Runs the shared <see cref="MemberValidator"/> over every leaf member's
    /// *current* StartValue (independent of any pending edit) and rebuilds
    /// <see cref="ExistingIssues"/> with one entry per violation. Surfaces
    /// rule drift and edits made directly in TIA without forcing the user to
    /// touch the value first (#26).
    /// </summary>
    /// <remarks>
    /// Intentionally does not depend on tag tables having been exported — the
    /// validator already short-circuits cleanly when the cache is missing
    /// (no false positives for tag-table rules with no cache yet).
    /// </remarks>
    private void RebuildExistingIssues()
    {
        ExistingIssues.Clear();
        var validator = BuildValidator();

        foreach (var root in RootMembers)
            ScanExistingViolations(root, validator);

        OnPropertyChanged(nameof(HasExistingIssues));
        OnPropertyChanged(nameof(ExistingIssuesCount));
    }

    private void ScanExistingViolations(MemberNodeViewModel node, MemberValidator validator)
    {
        if (node.IsLeaf && !string.IsNullOrEmpty(node.StartValue))
        {
            var error = validator.Validate(node.Model, node.StartValue);
            if (error != null)
            {
                node.HasExistingViolation = true;
                node.ExistingViolationMessage = error;
                ExistingIssues.Add(new ExistingIssueEntry(
                    node, node.StartValue ?? "", error, node.RuleHint));
            }
            else if (node.HasExistingViolation)
            {
                node.HasExistingViolation = false;
                node.ExistingViolationMessage = null;
            }
        }
        foreach (var child in node.Children)
            ScanExistingViolations(child, validator);
    }

    private void UpdateFilteredSuggestions()
    {
        if (_suggestions.Count == 0 || _suppressSuggestions)
        {
            FilteredSuggestions = Array.Empty<AutocompleteSuggestion>();
            return;
        }

        var filter = _newValue?.Trim() ?? "";
        if (string.IsNullOrEmpty(filter))
        {
            // Don't show list when input is empty
            FilteredSuggestions = Array.Empty<AutocompleteSuggestion>();
            return;
        }

        // Split by whitespace → AND: all terms must match somewhere
        var terms = filter.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        FilteredSuggestions = _suggestions
            .Where(s => terms.All(term =>
                s.DisplayName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.Value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (s.Comment != null && s.Comment.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)))
            .ToList();
    }

    /// <summary>
    /// Ensures tag tables are exported and cached. Called lazily on first need.
    /// </summary>
    private void EnsureTagTableCache()
    {
        if (_tagTableCache != null) return;
        if (_tagTableDir == null) return;

        _onRefreshTagTables?.Invoke();
        if (System.IO.Directory.Exists(_tagTableDir))
        {
            var tagReader = new XmlFileTagTableReader(_tagTableDir);
            _tagTableCache = new TagTableCache(tagReader);
            _autocompleteProvider = new AutocompleteProvider(_configLoader, _tagTableCache);
            // The "tag autocomplete is empty" support case is impossible to
            // diagnose without knowing what the cache resolved to.
            Log.Information("TagTableCache loaded: {Tables}",
                string.Join(", ", _tagTableCache.GetTableNames()));
        }
        UpdateTagTableAge();
    }

    /// <summary>
    /// Reloads suggestions for the currently selected member based on ShowConstants state.
    /// </summary>
    private void ReloadSuggestions()
    {
        Suggestions = Array.Empty<AutocompleteSuggestion>();
        FilteredSuggestions = Array.Empty<AutocompleteSuggestion>();
        SuggestionProvider = null;

        if (_selectedFlatMember == null || !_selectedFlatMember.IsLeaf) return;
        if (!_showConstants) return;

        EnsureTagTableCache();
        if (_tagTableCache == null) return;

        var config = _configLoader.GetConfig();
        var rule = config?.GetRule(_selectedFlatMember.Model);

        IReadOnlyList<AutocompleteSuggestion> suggestions;
        if (rule?.TagTableReference != null)
        {
            // Rule-based: only matching tables
            suggestions = _autocompleteProvider?.GetSuggestions(_selectedFlatMember.Model, "")
                ?? Array.Empty<AutocompleteSuggestion>();
        }
        else
        {
            // Generic: ALL constants from ALL tag tables
            var allEntries = _tagTableCache.GetTableNames()
                .SelectMany(name => _tagTableCache.GetEntries(name))
                .Select(e => new AutocompleteSuggestion(e.Value, e.Name, e.Comment))
                .ToList();
            suggestions = allEntries;
        }

        if (suggestions.Count > 0)
        {
            SuggestionProvider = new GlobSuggestionProvider(suggestions);
            Suggestions = suggestions;
        }
    }

    /// <summary>
    /// Re-export UDT type definitions from TIA Portal and re-parse the DB so the
    /// SetPoint filter reflects the current project state. Called transparently
    /// when the user toggles "Show setpoints only" on.
    /// </summary>
    private void TryRefreshUdtCache()
    {
        if (_onRefreshUdtTypes == null) return;
        try
        {
            _onRefreshUdtTypes();

            var resolver = new UdtSetPointResolver();
            var commentResolver = new UdtCommentResolver();
            if (_udtDir != null)
            {
                resolver.LoadFromDirectory(_udtDir);
                commentResolver.LoadFromDirectory(_udtDir);
            }

            RefreshTree(_currentXml, resolver, commentResolver);

            OnPropertyChanged(nameof(CanShowSetpointsOnly));
            OnPropertyChanged(nameof(ShowSetpointsOnlyTooltip));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UDT cache refresh failed");
            _messageBox.ShowError(
                $"Failed to refresh UDT cache:\n\n{ex.Message}",
                "SetPoint Filter");
        }
    }

    private void ExecuteRefreshConstants()
    {
        _onRefreshTagTables?.Invoke();

        if (_tagTableDir != null && System.IO.Directory.Exists(_tagTableDir))
        {
            var tagReader = new XmlFileTagTableReader(_tagTableDir);
            _tagTableCache = new TagTableCache(tagReader);
            _autocompleteProvider = new AutocompleteProvider(_configLoader, _tagTableCache);
            Log.Information("TagTableCache refreshed: {Tables}",
                string.Join(", ", _tagTableCache.GetTableNames()));
        }

        UpdateTagTableAge();
        ReloadSuggestions();
        // Tag-table rules may have flipped from "no cache → no validation" to
        // "cache loaded → validates" — rerun the existing-value scan (#26).
        RebuildExistingIssues();
    }

    private void UpdateTagTableAge()
    {
        if (_tagTableDir == null || !System.IO.Directory.Exists(_tagTableDir))
        {
            TagTableAge = "no data";
            return;
        }
        var files = System.IO.Directory.GetFiles(_tagTableDir, "*.xml");
        if (files.Length == 0)
        {
            TagTableAge = "no data";
            return;
        }
        var newest = files.Max(f => System.IO.File.GetLastWriteTime(f));
        var age = DateTime.Now - newest;
        TagTableAge = age.TotalMinutes < 1 ? "just now"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m ago"
            : age.TotalHours < 24 ? $"{(int)age.TotalHours}h ago"
            : $"{newest:yyyy-MM-dd HH:mm}";
    }

    private void ExecuteExpandAll()
    {
        FlatTreeManager.ExpandAll(RootMembers);
        RefreshFlatList();
    }

    private void ExecuteCollapseAll()
    {
        FlatTreeManager.CollapseAll(RootMembers);
        RefreshFlatList();
    }

    /// <summary>
    /// Expands a node and all its descendants, then refreshes the flat list.
    /// </summary>
    public void ExpandAllChildren(MemberNodeViewModel node)
    {
        FlatTreeManager.ExpandAllChildren(node);
        RefreshFlatList();
    }

    /// <summary>
    /// Collapses a node and all its descendants, then refreshes the flat list.
    /// </summary>
    public void CollapseAllChildren(MemberNodeViewModel node)
    {
        FlatTreeManager.CollapseAllChildren(node);
        RefreshFlatList();
    }

    /// <summary>Can stage bulk scope or manual-selection values as pending.</summary>
    private bool CanExecuteSetPending()
    {
        if (string.IsNullOrWhiteSpace(_newValue)
            || HasValidationError
            || _usageTracker.GetStatus().IsLimitReached)
            return false;

        if (IsManualMode)
        {
            // Blocked when selection mixes datatypes.
            if (!IsSelectionTypeHomogeneous) return false;

            // At least one selected member must actually change.
            return _manualSelectedPaths.Any(p =>
            {
                var node = FindNodeByPath(p);
                if (node == null || !node.IsLeaf) return false;
                var effective = node.IsPendingInlineEdit
                    ? (node.EditableStartValue ?? node.StartValue ?? "")
                    : (node.StartValue ?? "");
                return !string.Equals(effective, _newValue, StringComparison.OrdinalIgnoreCase);
            });
        }

        if (!HasSelection || !HasScope) return false;

        // Disabled when all affected members already have the target value
        if (_selectedScope != null)
        {
            var wouldChange = _selectedScope.MatchingMembers.Any(m =>
            {
                var node = FindNodeByPath(m.Path);
                if (node == null) return true;
                var effective = node.IsPendingInlineEdit
                    ? (node.EditableStartValue ?? node.StartValue ?? "")
                    : (node.StartValue ?? "");
                return !string.Equals(effective, _newValue, StringComparison.OrdinalIgnoreCase);
            });
            if (!wouldChange) return false;
        }

        return true;
    }

    /// <summary>
    /// Stages bulk scope or manual-selection values as pending on each affected node.
    /// Does NOT modify XML — values turn yellow until Apply is clicked.
    /// </summary>
    private void ExecuteSetPending()
    {
        if (IsManualMode)
        {
            ExecuteSetPendingManual();
            return;
        }

        if (_selectedScope == null) return;

        if (!_usageTracker.RecordUsage())
        {
            StatusText = Res.Get("Status_LimitReached");
            UpdateUsageStatus();
            return;
        }

        var affectedPaths = new HashSet<string>(
            _selectedScope.MatchingMembers.Select(m => m.Path));
        int count = 0;

        foreach (var root in RootMembers)
            count += SetPendingOnNodes(root, affectedPaths, _newValue);

        // Clear bulk highlighting (values are now pending/yellow)
        foreach (var root in RootMembers)
            root.ClearAffected();

        // Bulk committed — treat NewValue as consumed so the next selection
        // can prefill from its own member (otherwise the stale value stays).
        _newValueTouched = false;

        // RefreshPendingAndPreview re-runs ComputeBulkPreview which will clear
        // the preview (every staged node now has PendingValue == _newValue, so
        // TryAddPreviewEntry skips them) and raises all the right events.
        RefreshPendingAndPreview();
        StatusText = $"{count} values staged — click Apply to commit";
        RefreshFlatList();
    }

    /// <summary>
    /// Stages values as pending on each manually selected leaf member.
    /// </summary>
    private void ExecuteSetPendingManual()
    {
        if (!_usageTracker.RecordUsage())
        {
            StatusText = Res.Get("Status_LimitReached");
            UpdateUsageStatus();
            return;
        }

        int count = 0;
        foreach (var path in _manualSelectedPaths)
        {
            var node = FindNodeByPath(path);
            if (node == null || !node.IsLeaf) continue;
            var startsEqualsNew = string.Equals(node.StartValue, _newValue, StringComparison.OrdinalIgnoreCase);
            if (!startsEqualsNew)
            {
                if (!string.Equals(node.PendingValue, _newValue, StringComparison.OrdinalIgnoreCase))
                {
                    node.PendingValue = _newValue;
                    count++;
                }
            }
            else if (node.IsPendingInlineEdit)
            {
                node.ClearPending();
                count++;
            }
        }

        foreach (var root in RootMembers)
            root.ClearAffected();

        _newValueTouched = false;

        RefreshPendingAndPreview();
        StatusText = $"{count} values staged — click Apply to commit";
        RefreshFlatList();
    }

    private static int SetPendingOnNodes(MemberNodeViewModel node,
        HashSet<string> affectedPaths, string newValue)
    {
        int count = 0;
        if (affectedPaths.Contains(node.Path) && node.IsLeaf)
        {
            var startsEqualsNew = string.Equals(node.StartValue, newValue, StringComparison.OrdinalIgnoreCase);
            if (!startsEqualsNew)
            {
                // Target value differs from the original — stage it as pending.
                // Overrides any prior pending (inline or bulk) for this node.
                if (!string.Equals(node.PendingValue, newValue, StringComparison.OrdinalIgnoreCase))
                {
                    node.PendingValue = newValue;
                    count++;
                }
            }
            else if (node.IsPendingInlineEdit)
            {
                // Bulk targets the original value and there's a stale pending on this
                // node — clear it so the node reverts to StartValue.
                node.ClearPending();
                count++;
            }
        }
        foreach (var child in node.Children)
            count += SetPendingOnNodes(child, affectedPaths, newValue);
        return count;
    }

    /// <summary>Can apply when there are any pending changes (inline or bulk-staged).</summary>
    private bool CanExecuteApply()
    {
        return (PendingInlineEditCount > 0 || HasPendingChanges)
            && !HasInlineErrors;
    }

    public bool HasInlineErrors => HasInlineErrorsRecursive(RootMembers);

    private static bool HasInlineErrorsRecursive(IEnumerable<MemberNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.HasInlineError) return true;
            if (HasInlineErrorsRecursive(node.Children)) return true;
        }
        return false;
    }

    /// <summary>
    /// Count of pending entries whose staged value fails validation (#11).
    /// Derived from the tree so it stays in sync with inline-edit validation.
    /// </summary>
    public int InvalidPendingCount => PendingEdits.Count(e => e.Node.HasInlineError);

    public bool HasInvalidPending => InvalidPendingCount > 0;

    /// <summary>"N of M invalid" summary shown on the sidebar header badge.</summary>
    public string InvalidPendingBadge
    {
        get
        {
            var total = PendingEdits.Count;
            var invalid = InvalidPendingCount;
            return invalid == 0 ? "" : Res.Format("Pending_InvalidBadge", invalid, total);
        }
    }

    /// <summary>
    /// Applies ALL pending changes (bulk-staged + inline edits) to XML.
    /// </summary>
    private void ExecuteApply()
    {
        _lastApplySucceeded = false;
        var pendingEdits = CollectPendingInlineEdits(RootMembers);

        if (pendingEdits.Count == 0 && !_hasPendingChanges)
            return;

        Log.Information("ExecuteApply: {Count} pending changes", pendingEdits.Count);

        // Create backup before modification
        string? backupPath = null;
        try
        {
            backupPath = _onBackup?.Invoke();
            Log.Information("Backup created: {BackupPath}", backupPath);
        }
        catch (Exception backupEx)
        {
            Log.Warning(backupEx, "Backup failed, continuing without backup");
        }

        try
        {
            int totalChanged = 0;

            // Apply all pending edits to XML
            foreach (var (member, value) in pendingEdits)
            {
                var writeResult = _writer.ModifyStartValues(
                    _currentXml, new[] { member }, value);

                if (writeResult.IsSuccess)
                {
                    _currentXml = writeResult.ModifiedXml;
                    totalChanged++;
                    Log.Information("Applied: {Path} → {Value}", member.Path, value);
                }
                else
                {
                    Log.Warning("Failed for {Path}: {Errors}",
                        member.Path, string.Join("; ", writeResult.Errors));
                }
            }

            // Apply comment previews
            _currentXml = ApplyCommentPreviews(_currentXml);

            StatusText = Res.Format("Status_Changed", totalChanged, _dataBlockInfo.Name);
            _lastApplySucceeded = true;

            Log.Information("ExecuteApply: {Changed} values written to XML, importing to TIA...", totalChanged);

            // Import to TIA Portal immediately
            HasPendingChanges = true;
            var committed = CommitChanges();
            if (!committed)
            {
                // User cancelled (e.g. declined compile prompt on inconsistent block).
                // Preserve pending edits in the tree so they can retry — skip RefreshTree
                // because TIA still holds the pre-Apply state.
                _lastApplySucceeded = false;
                UpdateUsageStatus();
                return;
            }

            // Re-export from TIA to get the canonical XML after import
            RefreshTree(_currentXml);

            // RefreshTree rebuilds RootMembers (all PendingValue=null), but computed
            // properties only refresh their bindings when PropertyChanged is raised.
            RefreshPendingAndPreview();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Apply threw exception");
            HandleErrorWithRollback(ex, backupPath);
        }

        UpdateUsageStatus();
    }

    private void ExecuteApplyAndClose()
    {
        ExecuteApply();
        if (_lastApplySucceeded)
            RequestClose?.Invoke();
    }

    /// <summary>
    /// F-031: Bulk-update all comments in the current scope using the configured
    /// comment generation rules.
    /// </summary>
    private void ExecuteUpdateComments()
    {
        var config = _configLoader.GetConfig();
        if (config == null || _selectedScope == null) return;
        if (!config.Rules.Any(r => !string.IsNullOrEmpty(r.CommentTemplate))) return;

        if (!_usageTracker.RecordUsage())
        {
            StatusText = Res.Get("Status_LimitReached");
            UpdateUsageStatus();
            return;
        }

        try
        {
            EnsureTagTableCache();
            var templateGen = new TemplateCommentGenerator(config, _tagTableCache);
            var scopeMembers = _selectedScope.MatchingMembers.ToList();

            var modifiedXml = _currentXml;
            int affectedCount = 0;
            // Generate comments per language so {member.comment} resolves to the correct translation
            foreach (var lang in _projectLanguages)
            {
                var targets = templateGen.GenerateForScope(_dataBlockInfo, scopeMembers, lang, ResolvePendingValue);
                if (affectedCount == 0) affectedCount = targets.Count;
                foreach (var (target, comment) in targets)
                {
                    modifiedXml = _writer.ModifyComment(modifiedXml, target, comment, lang);
                }
            }

            _currentXml = modifiedXml;
            StatusText = Res.Format("Comments_Updated", affectedCount, _dataBlockInfo.Name);
            HasPendingChanges = true;
            RefreshTree(modifiedXml);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UpdateComments failed");
            StatusText = Res.Format("Status_ErrorComments", ex.Message);
        }

        UpdateUsageStatus();
    }

    private bool CanExecuteUpdateComments()
    {
        return HasScope && HasCommentConfig && !_usageTracker.GetStatus().IsLimitReached;
    }

    /// <summary>
    /// F-072: When an error occurs during bulk operation, ask the user
    /// Opens the Config Editor dialog. After save, reloads config and refreshes constraints.
    /// </summary>
    private void ExecuteEditConfig()
    {
        EnsureTagTableCache();
        var vm = new ConfigEditorViewModel(
            _configLoader,
            _tagTableCache?.GetTableNames());
        var dialog = new ConfigEditorDialog(vm);
        dialog.ShowDialog();

        // Reload config after editor closes (may have been saved)
        _configLoader.Invalidate();
        OnPropertyChanged(nameof(HasCommentConfig));
        // Re-evaluate rule hints + existing-value findings against the
        // (possibly updated) ruleset (#26).
        RefreshRuleHints();
        RebuildExistingIssues();
        StatusText = Res.Get("Status_Ready");
    }

    /// <summary>
    /// F-072: When an error occurs during bulk operation, ask the user
    /// whether to rollback (restore backup) or keep the partial result.
    /// </summary>
    private void HandleErrorWithRollback(Exception ex, string? backupPath)
    {
        if (backupPath != null && _onRestore != null)
        {
            var message = Res.Format("Rollback_Question", ex.Message);

            if (_messageBox.AskYesNo(message, Res.Get("Rollback_Title")))
            {
                try
                {
                    _onRestore(backupPath);
                    Log.Information("Rollback completed from {BackupPath}", backupPath);
                    StatusText = Res.Get("Rollback_Complete");
                }
                catch (Exception restoreEx)
                {
                    Log.Error(restoreEx, "Rollback failed from {BackupPath}", backupPath);
                    StatusText = Res.Format("Rollback_Failed", restoreEx.Message);
                }
            }
            else
            {
                Log.Warning("User chose to keep partial result after error: {Error}", ex.Message);
            StatusText = Res.Format("Status_ErrorPartialKept", ex.Message);
            }
        }
        else
        {
            Log.Error(ex, "Error with no backup available");
            StatusText = Res.Format("Status_ErrorNoBackup", ex.Message);
        }
    }

    /// <summary>
    /// Subscribes to StartValueEdited on a node and all its descendants.
    /// </summary>
    private void SubscribeStartValueEdited(MemberNodeViewModel node)
    {
        node.StartValueEdited += OnSingleValueEdited;
        foreach (var child in node.Children)
            SubscribeStartValueEdited(child);
    }

    /// <summary>
    /// Handles direct inline editing of a single start value in the table.
    /// Stores the value as pending (does NOT modify XML until Apply).
    /// Validates constraints and updates the pending status display.
    /// </summary>
    private void OnSingleValueEdited(MemberNodeViewModel memberVm, string newValue)
    {
        if (string.IsNullOrEmpty(newValue))
        {
            // Inline revert / cleared field: drop any stale validation error
            // from the prior value, then refresh the aggregated pending queue
            // / preview — otherwise the row lingers red and a stale message
            // stays in StatusText. StartsWith matches the exact producer
            // format ($"{memberVm.Name}: {error}") without accidentally
            // clearing messages about other members whose name contains this
            // one as a substring.
            memberVm.HasInlineError = false;
            memberVm.InlineErrorMessage = null;
            if (StatusText.StartsWith(memberVm.Name + ":"))
                StatusText = "";
            RefreshPendingAndPreview();
            return;
        }

        // Only count against inline limit if this is a new edit, not a correction of an existing pending value
        if (!memberVm.IsPendingInlineEdit)
        {
            if (_usageTracker.GetInlineStatus().IsLimitReached)
            {
                StatusText = Res.Get("Status_InlineLimitReached");
                UpdateUsageStatus();
                return;
            }

            if (!_usageTracker.RecordInlineEdit())
            {
                StatusText = Res.Get("Status_InlineLimitReached");
                UpdateUsageStatus();
                return;
            }
        }

        // Shared validator → same rule language as the bulk inspector (#7).
        var error = BuildValidator().Validate(memberVm.Model, newValue);

        memberVm.HasInlineError = error != null;
        memberVm.InlineErrorMessage = error;
        if (error != null)
            StatusText = $"{memberVm.Name}: {error}";
        else if (StatusText.StartsWith(memberVm.Name + ":"))
            StatusText = "";

        // Pending edits no longer smart-expand ancestors (#10): they surface in the
        // sidebar, which is where the user looks for them. The tree's expansion
        // state stays under the user's control.

        RefreshPendingAndPreview();
        OnPropertyChanged(nameof(HasInlineErrors));
        RaiseInvalidPendingChanged();
    }

    /// <summary>
    /// Builds a set of member paths excluded by rules with ExcludeFromSetpoints=true.
    /// </summary>
    private HashSet<string>? BuildExcludeSet(BulkChangeConfig? config)
    {
        if (config == null) return null;
        var excludeRules = config.Rules.Where(r => r.ExcludeFromSetpoints && !string.IsNullOrEmpty(r.PathPattern)).ToList();
        if (excludeRules.Count == 0) return null;

        var excluded = new HashSet<string>();
        foreach (var member in _dataBlockInfo.AllMembers())
        {
            foreach (var rule in excludeRules)
            {
                if (PathPatternMatcher.IsMatch(member, rule.PathPattern!))
                {
                    excluded.Add(member.Path);
                    break;
                }
            }
        }
        return excluded.Count > 0 ? excluded : null;
    }

    /// <summary>Collects all nodes with pending inline edits as (MemberNode, newValue) pairs.</summary>
    private static List<(MemberNode Member, string Value)> CollectPendingInlineEdits(
        IEnumerable<MemberNodeViewModel> nodes)
    {
        var result = new List<(MemberNode, string)>();
        foreach (var node in nodes)
        {
            if (node.PendingValue != null)
                result.Add((node.Model, node.PendingValue));
            result.AddRange(CollectPendingInlineEdits(node.Children));
        }
        return result;
    }

    /// <summary>Counts nodes with pending inline edits.</summary>
    private static int CountPendingInlineEdits(IEnumerable<MemberNodeViewModel> nodes)
    {
        int count = 0;
        foreach (var node in nodes)
        {
            if (node.PendingValue != null) count++;
            count += CountPendingInlineEdits(node.Children);
        }
        return count;
    }

    /// <summary>Clears all pending inline edits without applying them.</summary>
    private void ExecuteDiscardPending()
    {
        // #21: Confirm before nuking staged edits — a misclick used to silently wipe them all.
        var count = PendingInlineEditCount;
        if (count > 0)
        {
            var message = Res.Format("Dialog_DiscardConfirm_Text", count);
            if (!_messageBox.AskYesNo(message, Res.Get("Dialog_DiscardConfirm_Title")))
                return;
        }

        DiscardPendingSilent();
    }

    /// <summary>
    /// Clears all pending inline edits without prompting. Used by the close-confirm
    /// flow, which has already asked the user — a second confirm dialog is noise.
    /// </summary>
    public void DiscardPendingSilent()
    {
        ClearAllPendingInlineEdits(RootMembers);
        RefreshPendingAndPreview();
        StatusText = Res.Get("Status_Ready");
        RefreshFlatList();
    }

    private static void ClearAllPendingInlineEdits(IEnumerable<MemberNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            node.ClearPending();
            ClearAllPendingInlineEdits(node.Children);
        }
    }

    private void RefreshTree(
        string modifiedXml,
        UdtSetPointResolver? udtResolver = null,
        UdtCommentResolver? commentResolver = null)
    {
        // Save expand states from ALL nodes (recursively, not just flat list)
        var expandStates = new Dictionary<string, (bool Expanded, bool Smart)>();
        foreach (var root in RootMembers)
            CollectExpandStates(root, expandStates);

        // Preserve selection + scope by stable identity (#8). Index-based lookup
        // broke because OnMemberSelected adds scopes reversed while RefreshTree
        // used plain order — indices didn't line up after Apply.
        var selectedPath = _selectedFlatMember?.Path;
        var selectedScopeAncestorPath = _selectedScope?.AncestorPath;
        var selectedScopeDepth = _selectedScope?.Depth;

        // Keep the most recent UDT resolvers so later RefreshTree calls (from Apply) don't drop them.
        if (udtResolver != null) _udtResolver = udtResolver;
        if (commentResolver != null) _commentResolver = commentResolver;
        var constantResolver = _tagTableCache != null
            ? new TagTableConstantResolver(_tagTableCache)
            : (IConstantResolver?)null;
        var parser = new SimaticMLParser(constantResolver, _udtResolver, _commentResolver);
        _dataBlockInfo = parser.Parse(modifiedXml);
        InlineRuleExtractor.ApplyTo(_configLoader.GetConfig(), _dataBlockInfo);

        RootMembers.Clear();
        foreach (var member in _dataBlockInfo.Members)
        {
            var vm = new MemberNodeViewModel(member, null, _commentLanguagePolicy);
            SubscribeStartValueEdited(vm);
            RootMembers.Add(vm);
        }
        RefreshRuleHints();
        RebuildExistingIssues();

        // Restore expand states on ALL new nodes before building flat list
        foreach (var root in RootMembers)
            RestoreExpandStates(root, expandStates);

        ApplyAllFilters();
        RefreshFlatList();

        // Restore selection and re-trigger scope analysis
        if (selectedPath != null)
        {
            var restored = _flatTreeManager.FlatList.FirstOrDefault(m => m.Path == selectedPath);
            if (restored != null)
            {
                _selectedFlatMember = restored;
                OnPropertyChanged(nameof(SelectedFlatMember));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedMemberDisplay));

                // Re-populate scopes for the restored selection using the same
                // ordering as OnMemberSelected so index/position stay stable.
                var result = _analyzer.Analyze(_dataBlockInfo, restored.Model);
                AvailableScopes.Clear();
                foreach (var scope in result.Scopes.Reverse())
                    AvailableScopes.Add(scope);

                // #8: Match the previously selected scope by ancestor path, not
                // by index. Keeps the dropdown sticky through Apply even when
                // the scope count/order drifts.
                var restoredScope = AvailableScopes.FirstOrDefault(s =>
                    string.Equals(s.AncestorPath, selectedScopeAncestorPath, StringComparison.Ordinal)
                    && s.Depth == selectedScopeDepth);
                if (restoredScope != null)
                    SelectedScope = restoredScope;

                // Value is reset after Apply: the just-committed value would
                // misrepresent the current state (#8). Clear touched so the
                // next selection can prefill cleanly.
                _suppressSuggestions = true;
                _newValueTouched = false;
                _newValue = "";
                OnPropertyChanged(nameof(NewValue));
                _suppressSuggestions = false;
            }
        }
    }

    private static void CollectExpandStates(MemberNodeViewModel vm, Dictionary<string, (bool Expanded, bool Smart)> states)
    {
        if (vm.IsExpanded)
            states[vm.Path] = (true, vm.IsSmartExpanded);
        foreach (var child in vm.Children)
            CollectExpandStates(child, states);
    }

    private static void RestoreExpandStates(MemberNodeViewModel vm, Dictionary<string, (bool Expanded, bool Smart)> states)
    {
        if (states.TryGetValue(vm.Path, out var state))
        {
            vm.IsExpanded = state.Expanded;
            vm.IsSmartExpanded = state.Smart;
        }
        foreach (var child in vm.Children)
            RestoreExpandStates(child, states);
    }

    private void UpdateUsageStatus()
    {
        if (_licenseService != null && _licenseService.IsProActive)
        {
            UsageStatusText = Res.Get("Status_Pro");
            LicenseTierText = Res.Get("License_Tier_Pro");
        }
        else
        {
            var status = _usageTracker.GetStatus();
            var inlineStatus = _usageTracker.GetInlineStatus();
            UsageStatusText = Res.Format("Status_RemainingBoth",
                status.RemainingToday, status.DailyLimit,
                inlineStatus.RemainingToday, inlineStatus.DailyLimit);
            LicenseTierText = Res.Get("License_Tier_Free");
        }

        OnPropertyChanged(nameof(UsageStatusText));
        OnPropertyChanged(nameof(LicenseTierText));
        OnPropertyChanged(nameof(ShowLicenseKeyButton));
        OnPropertyChanged(nameof(LicenseKeyButtonText));
        OnPropertyChanged(nameof(IsLimitReached));
    }

    private void ExecuteEnterLicenseKey()
    {
        if (_licenseService == null) return;

        var dialog = new LicenseKeyDialog(_licenseService);
        dialog.Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        dialog.ShowDialog();
        UpdateUsageStatus();
    }

    /// <summary>
    /// Called by the view when the user's Ctrl+Click selection changes.
    /// Only leaf members are tracked. <paramref name="isFilterRehydration"/> is true
    /// when the view is programmatically restoring selection after a refresh —
    /// removed items in that case are hidden by the filter, not deselected.
    /// </summary>
    public void UpdateManualSelection(
        IEnumerable<MemberNodeViewModel> added,
        IEnumerable<MemberNodeViewModel> removed,
        bool isFilterRehydration)
    {
        var addedList = added.ToList();
        var removedList = removed.ToList();
        bool wasManual = IsManualMode;
        bool changed = false;

        foreach (var m in addedList)
        {
            if (!m.IsLeaf) continue;
            if (_manualSelectedPaths.Add(m.Path)) changed = true;
        }

        if (!isFilterRehydration)
        {
            foreach (var m in removedList)
            {
                if (_manualSelectedPaths.Remove(m.Path)) changed = true;
            }
        }

        if (!changed) return;

        bool isNowManual = IsManualMode;

        OnPropertyChanged(nameof(IsManualMode));
        OnPropertyChanged(nameof(ManualSelectionCount));
        OnPropertyChanged(nameof(ManualSelectionSummary));
        OnPropertyChanged(nameof(IsSelectionTypeHomogeneous));
        OnPropertyChanged(nameof(HasScope));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(SetButtonText));
        OnPropertyChanged(nameof(SelectedMemberDisplay));

        // Entering manual mode: the scope-based highlighting from the single
        // selection no longer applies. Clear the scope state and wipe affected
        // rows + smart-expanded parents. (OnMemberSelected may have already
        // run before SelectionChanged fired, so we handle this here too.)
        if (!wasManual && isNowManual)
        {
            AvailableScopes.Clear();
            _selectedScope = null;
            OnPropertyChanged(nameof(SelectedScope));
            OnPropertyChanged(nameof(HasScope));
            // Pin ancestors of manually selected leaves as user-expanded so that
            // UpdateHighlighting's ClearAffected doesn't collapse them and hide
            // the first-selected row from the flat list (which would deselect it).
            PinManuallySelectedVisibility();
            UpdateHighlighting();
        }
        else if (isNowManual)
        {
            // Already in manual mode: still pin newly-added selections so a later
            // filter/refresh keeps them visible and selectable.
            PinManuallySelectedVisibility();
            RefreshFlatList();
        }
        else if (wasManual)
        {
            // Transitioning back to scope mode. OnMemberSelected already ran
            // before this method (with IsManualMode still true) and skipped scope
            // setup. Re-run it now that the mode has flipped.
            OnMemberSelected(_selectedFlatMember);
        }

        ValidateValue();
    }

    /// <summary>
    /// Updates the NewValue textbox with the current featured member's value —
    /// but only if the user hasn't typed in the textbox yet. Once the user has
    /// edited the textbox, their input is preserved across selection changes
    /// until they clear the selection entirely.
    /// </summary>
    private void PrefillNewValueFromFeaturedMember(MemberNodeViewModel? memberVm)
    {
        if (_newValueTouched) return;
        if (memberVm is not { IsLeaf: true }) return;

        var value = memberVm.IsPendingInlineEdit
            ? (memberVm.EditableStartValue ?? memberVm.StartValue ?? "")
            : (memberVm.StartValue ?? "");
        if (value == _newValue) return;

        // Bypass the public setter so we don't flip _newValueTouched on this
        // programmatic update.
        _newValue = value;
        OnPropertyChanged(nameof(NewValue));
    }

    private void PinManuallySelectedVisibility()
    {
        foreach (var path in _manualSelectedPaths)
        {
            var node = FindNodeByPath(path);
            if (node == null) continue;
            var p = node.Parent;
            while (p != null)
            {
                if (!p.IsExpanded || p.IsSmartExpanded)
                {
                    p.IsExpanded = true;
                    p.IsSmartExpanded = false; // promote to user-expanded
                }
                p = p.Parent;
            }
        }
    }

    private void ExecuteClearManualSelection()
    {
        if (_manualSelectedPaths.Count == 0) return;
        _manualSelectedPaths.Clear();
        ClearBulkRowHighlights();
        _newValueTouched = false;
        _newValue = "";
        OnPropertyChanged(nameof(NewValue));

        OnPropertyChanged(nameof(IsManualMode));
        OnPropertyChanged(nameof(ManualSelectionCount));
        OnPropertyChanged(nameof(ManualSelectionSummary));
        OnPropertyChanged(nameof(IsSelectionTypeHomogeneous));
        OnPropertyChanged(nameof(HasScope));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(SetButtonText));
        OnPropertyChanged(nameof(SelectedMemberDisplay));

        ValidateValue();
        FlatListRefreshed?.Invoke();
    }

    /// <summary>
    /// Returns the distinct datatypes (case-insensitive) of currently selected members.
    /// Ignores paths that no longer resolve to a leaf node.
    /// </summary>
    private IReadOnlyCollection<string> GetSelectedDatatypes()
    {
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _manualSelectedPaths)
        {
            var node = FindNodeByPath(path);
            if (node is { IsLeaf: true })
                types.Add(node.Datatype);
        }
        return types;
    }

    private void ExecuteUpgradeToPro()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ShopUrls.CheckoutUrl) { UseShellExecute = true });
        }
        catch
        {
            // Fallback: silently ignore if browser cannot be opened
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _valueDebounceTimer?.Dispose();
        _valueDebounceTimer = null;
        _searchDebounceTimer?.Dispose();
        _searchDebounceTimer = null;

        // Unsubscribe so the lambda's `this` capture stops keeping the VM alive.
        // We do NOT dispose _licenseService — the caller that constructed it owns it.
        if (_licenseService != null && _licenseStateChangedHandler != null)
        {
            _licenseService.LicenseStateChanged -= _licenseStateChangedHandler;
            _licenseStateChangedHandler = null;
        }
    }
}
