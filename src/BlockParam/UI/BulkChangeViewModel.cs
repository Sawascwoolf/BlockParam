using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using BlockParam.Updates;

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

    // The focused DB (selection / scope / status text are computed against
    // this one). Per-DB state lives in ActiveDb. For multi-DB workflows
    // (#58) the focus stays on _active and additional DBs hang off
    // _companions; bulk preview / Apply iterate _active + _companions.
    private ActiveDb _active;
    private readonly List<ActiveDb> _companions = new();
    private string _title = "";
    // DB-switcher state (#59). Lazy-loaded on first dropdown open and cached
    // for the dialog session; the ↻ button re-enumerates on demand.
    private readonly Func<IReadOnlyList<DataBlockSummary>>? _enumerateDataBlocks;
    private readonly Func<DataBlockSummary, string>? _switchToDataBlock;
    // Multi-DB add (#58): host-supplied factory that builds a fully-wired
    // ActiveDb for an arbitrary DB picked in the dropdown — including a
    // per-DB OnApply that re-imports the modified xml back into TIA. Null
    // when the host couldn't supply it (DevLauncher, tests); the VM falls
    // back to a read-only companion in that case (see AddCompanionFromSummary).
    private readonly Func<DataBlockSummary, ActiveDb?>? _buildActiveDbForSummary;
    private IReadOnlyList<DataBlockSummary>? _availableDataBlocks;
    private IReadOnlyList<DataBlockSummary> _filteredDataBlocks = Array.Empty<DataBlockSummary>();
    private string _dataBlockSearchText = "";
    private bool _isDataBlocksDropdownOpen;
    private bool _isLoadingDataBlocks;
    private string _currentPlcName = "";
    // In-memory stash of pending edits keyed by DB identity (#59). Lets the
    // user switch DBs without committing or losing work — when they come back
    // to a stashed DB later in the same session, the edits restore.
    private readonly Dictionary<string, StashedDbState> _stashedDbs =
        new(StringComparer.Ordinal);

    // Model-to-VM and model-to-DB lookups for multi-DB routing (#58). Same
    // path string can occur in multiple DBs, so reference equality on
    // MemberNode is the unambiguous way to map a scope match back to its
    // tree VM (where pending values live) and to its owning ActiveDb (where
    // the xml lives). Rebuilt on every BuildRootMembersFromActiveDbs.
    private readonly Dictionary<MemberNode, MemberNodeViewModel> _modelToVm = new();
    private readonly Dictionary<MemberNode, ActiveDb> _modelToDb = new();
    // Multi-DB only (#58): the synthetic group VM that wraps each ActiveDb's
    // members. Used to route per-DB scans (ClearPendingValuesForDb,
    // CountPendingEditsForDb, ...) by reference instead of by Info.Name +
    // PlcName comparisons that would still collide across PLC boundaries.
    private readonly Dictionary<ActiveDb, MemberNodeViewModel> _dbToSynthetic = new();
    private MemberNodeViewModel? _selectedFlatMember;
    private ScopeLevel? _selectedScope;
    private string _newValue = "";
    // Multi-DB safe (#58): keyed by MemberNodeViewModel reference, not path
    // string. Two leaves in different DBs that happen to share a Path would
    // alias under string keying — Ctrl+click selection on companion DB
    // members would silently target the focused DB's same-path leaf.
    private readonly HashSet<MemberNodeViewModel> _manualSelectedPaths = new();
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
    private bool _limitWarningShown;
    private bool _isInspectorCollapsed;
    private bool _isBulkEditExpanded = true;
    private bool _isBulkPreviewExpanded = true;
    private bool _isPendingExpanded = true;
    private bool _isIssuesExpanded = true;
    private readonly IReadOnlyList<string> _projectLanguages;
    private readonly CommentLanguagePolicy _commentLanguagePolicy;
    private readonly Dispatcher _dispatcher;
    private readonly ILicenseService? _licenseService;
    private readonly IUpdateCheckService? _updateCheckService;
    private UpdateInfo? _availableUpdate;

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
        IUpdateCheckService? updateCheckService = null,
        Func<IReadOnlyList<DataBlockSummary>>? enumerateDataBlocks = null,
        Func<DataBlockSummary, string>? switchToDataBlock = null,
        string? currentPlcName = null,
        IReadOnlyList<ActiveDb>? additionalActiveDbs = null,
        Func<DataBlockSummary, ActiveDb?>? buildActiveDbForSummary = null)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _projectLanguages = projectLanguages is { Count: > 0 } ? projectLanguages : new[] { "en-GB" };
        _commentLanguagePolicy = new CommentLanguagePolicy(editingLanguage, referenceLanguage, _projectLanguages);
        _active = new ActiveDb(dataBlockInfo, currentXml, onApply);
        if (additionalActiveDbs != null)
            _companions.AddRange(additionalActiveDbs);
        _analyzer = analyzer;
        _bulkChangeService = bulkChangeService;
        _usageTracker = usageTracker;
        _configLoader = configLoader;
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
        _updateCheckService = updateCheckService;
        _enumerateDataBlocks = enumerateDataBlocks;
        _switchToDataBlock = switchToDataBlock;
        _buildActiveDbForSummary = buildActiveDbForSummary;
        _currentPlcName = currentPlcName ?? "";
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
        _title = BuildTitle(version, _currentPlcName, dataBlockInfo.Name);

        InlineRuleExtractor.ApplyTo(configLoader.GetConfig(), dataBlockInfo);

        BulkPreview = new ObservableCollection<BulkPreviewEntry>();
        PendingEdits = new ObservableCollection<PendingEditEntry>();
        ExistingIssues = new ObservableCollection<ExistingIssueEntry>();
        StashedDbs = new ObservableCollection<StashedDbState>();

        // Build tree view models. Multi-DB workflow (#58): when companions
        // exist, every DB (focused + each companion) becomes a synthetic
        // top-level "DB" group whose children are that DB's actual members.
        // The user picked this shape over a flat union so each match in the
        // tree carries a visible DB-of-origin label, and scope walks
        // naturally extend one level deeper.
        RootMembers = new ObservableCollection<MemberNodeViewModel>();
        BuildRootMembersFromActiveDbs();
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
        ShowUpdateDetailsCommand = new RelayCommand(ExecuteShowUpdateDetails, () => HasUpdateAvailable);
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
        GoToFirstChangeCommand = new RelayCommand(ExecuteGoToFirstChange,
            () => HasAnyChanges);

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
        InitializeUpdateCheck();

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
        // internal set so BulkChangeViewModelMultiDbTests can drive
        // CommitChanges in isolation — Apply normally toggles this flag
        // mid-flow and consumes it before returning, so without test-only
        // access there's no way to verify CommitChanges' multi-DB iteration
        // without running the whole Apply pipeline.
        internal set => SetProperty(ref _hasPendingChanges, value);
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
            // Multi-DB-safe (#58): invoke OnApply on every active DB, not
            // just the focused one. Single-DB sessions iterate exactly the
            // focused DB so behavior is unchanged. A null OnApply
            // (dropdown-added companion before per-DB host wiring) is a
            // skip — Apply for that DB is read-only.
            foreach (var db in AllActiveDbs)
            {
                db.OnApply?.Invoke(db.Xml);
            }
            HasPendingChanges = false;
            // Pair line for the matching Log.Error path below — without
            // this, a TIA-side import failure has the error logged but
            // the success path is silent, so support cannot tell which
            // happened from the log file alone.
            Log.Information(
                "CommitChanges: import succeeded for {DbCount} DB(s) (focus={Focused})",
                AllActiveDbs.Count, _active.Info.Name);
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
            Log.Error(ex, "CommitChanges: TIA import failed for {Db}", _active.Info.Name);
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
                OnPropertyChanged(nameof(SetButtonTooltip));
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
                        OnPropertyChanged(nameof(SetButtonText));
                        OnPropertyChanged(nameof(SetButtonTooltip));
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
        => _active.Info.UnresolvedUdts.Count == 0 || _onRefreshUdtTypes != null;

    /// <summary>Tooltip shown on the checkbox.</summary>
    public string ShowSetpointsOnlyTooltip
    {
        get
        {
            if (_active.Info.UnresolvedUdts.Count == 0)
                return "Only show members marked as SetPoint (Einstellwert) in the UDT type definition.";

            if (_onRefreshUdtTypes != null)
                return "Only show members marked as SetPoint. UDT types will be re-exported from TIA Portal when enabled.";

            var missing = string.Join(", ", _active.Info.UnresolvedUdts);
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
                return Res.Format("Dialog_SetManualCount", CountWouldChangeMembers());
            return _selectedScope != null
                ? $"Set {CountWouldChangeMembers()} in '{_selectedScope.AncestorName}'"
                : "Set";
        }
    }

    /// <summary>
    /// Two-line tooltip for the Set button: action description + count breakdown.
    /// Surfaces the total scope size so users still see "40 valves selected" even
    /// when the button label drops to "Set 35" because 5 already match (#65).
    /// </summary>
    public string SetButtonTooltip
    {
        get
        {
            var action = Res.Get("Dialog_SetTooltip");
            if (!CanEdit) return action;

            int total = TotalCandidateMembers();
            int willChange = CountWouldChangeMembers();
            int alreadyMatch = total - willChange;

            string breakdown;
            if (string.IsNullOrEmpty(_newValue))
            {
                breakdown = IsManualMode
                    ? Res.Format("Dialog_SetTooltip_ManualIdle", total)
                    : Res.Format("Dialog_SetTooltip_ScopeIdle", total, _selectedScope?.AncestorName ?? "");
            }
            else
            {
                breakdown = IsManualMode
                    ? Res.Format("Dialog_SetTooltip_ManualBreakdown", willChange, total, alreadyMatch)
                    : Res.Format("Dialog_SetTooltip_ScopeBreakdown",
                        willChange, total, _selectedScope?.AncestorName ?? "", alreadyMatch);
            }
            return action + "\n" + breakdown;
        }
    }

    /// <summary>True when 2+ leaf members are manually selected (Ctrl+Click).</summary>
    public bool IsManualMode => _manualSelectedPaths.Count >= 2;

    /// <summary>Number of manually selected leaf members (includes ones hidden by filter).</summary>
    public int ManualSelectionCount => _manualSelectedPaths.Count;

    /// <summary>
    /// Read-only view of manually selected member VMs (for code-behind
    /// rehydration). Multi-DB safe (#58): keyed by reference, so the
    /// dialog's ListView rehydration test (Contains(m)) picks the right
    /// VM in whichever DB it lives.
    /// </summary>
    public IReadOnlyCollection<MemberNodeViewModel> ManualSelectedPaths => _manualSelectedPaths;

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
    public ICommand ShowUpdateDetailsCommand { get; }
    public ICommand ExpandAllCommand { get; }
    public ICommand CollapseAllCommand { get; }
    public ICommand ClearManualSelectionCommand { get; }
    public ICommand ToggleInspectorCommand { get; }

    // --- DB-switcher dropdown (#59) ---

    public ICommand OpenDataBlocksDropdownCommand { get; }
    public ICommand CloseDataBlocksDropdownCommand { get; }
    public ICommand RefreshDataBlocksCommand { get; }
    public ICommand SwitchToStashedDbCommand { get; }
    public ICommand GoToFirstChangeCommand { get; }

    /// <summary>
    /// True when there's any work to jump to — either an active-DB pending
    /// edit or a stashed DB. Drives the visibility of the "jump to changes"
    /// header button so it disappears when nothing's queued.
    /// </summary>
    public bool HasAnyChanges => PendingInlineEditCount > 0 || HasStashedDbs;

    /// <summary>
    /// Raised when <see cref="GoToFirstChangeCommand"/> needs the view to
    /// scroll + select a member in the live tree. The view subscribes and
    /// drives <c>ListView.ScrollIntoView</c> + selection — the VM doesn't
    /// know about the visual list (#59 follow-up).
    /// </summary>
    public event Action<MemberNodeViewModel>? RequestJumpToMember;

    /// <summary>
    /// True when the host wired up DB enumeration + switching callbacks.
    /// The dropdown chevron / popup are hidden in tests + DevLauncher runs
    /// where no project is available.
    /// </summary>
    public bool HasDataBlockSwitcher =>
        _enumerateDataBlocks != null && _switchToDataBlock != null;

    /// <summary>Header label — the DB name part of the title combo.</summary>
    public string CurrentDataBlockName => _active.Info.Name;

    /// <summary>
    /// Populates <see cref="RootMembers"/> from the focused DB plus any
    /// companions (#58). Single-DB sessions get a flat list of the DB's
    /// top-level members (unchanged behavior); multi-DB sessions get one
    /// synthetic <see cref="MemberNode"/> per DB at the top level, whose
    /// children are that DB's actual members. Synthetic roots are tagged
    /// <c>Datatype="DB"</c> so the tree template can render them with a
    /// distinct chrome.
    /// </summary>
    private void BuildRootMembersFromActiveDbs()
    {
        // Always rebuild the routing dictionaries so every model node is
        // mapped to its current VM + owning DB. Stale entries from a prior
        // tree would route writes to disposed VMs.
        _modelToVm.Clear();
        _modelToDb.Clear();
        _dbToSynthetic.Clear();

        if (_companions.Count == 0)
        {
            // Single-DB: flat list of top-level members, identical to legacy.
            foreach (var member in _active.Info.Members)
            {
                var vm = new MemberNodeViewModel(member, null, _commentLanguagePolicy);
                SubscribeStartValueEdited(vm);
                IndexSubtree(vm, _active);
                RootMembers.Add(vm);
            }
            return;
        }

        // Multi-DB: one synthetic group node per DB. Children are the DB's
        // real top-level members, reused by reference — Path strings stay
        // unchanged, so existing rule patterns / scope-detection on member
        // paths still match across DBs.
        AddDbGroupRoot(_active);
        foreach (var companion in _companions)
            AddDbGroupRoot(companion);
    }

    private void AddDbGroupRoot(ActiveDb db)
    {
        var info = db.Info;
        var synthetic = new MemberNode(
            name: info.Name,
            datatype: "DB",
            startValue: null,
            path: info.Name,
            parent: null,
            children: info.Members);
        var groupVm = new MemberNodeViewModel(synthetic, null, _commentLanguagePolicy);
        // Subscribe edited-value events on every leaf descendant so inline
        // edits inside a companion DB still bubble up to the VM the same
        // way single-DB edits do.
        foreach (var descendant in groupVm.AllDescendants())
            SubscribeStartValueEdited(descendant);
        groupVm.IsExpanded = true;
        _dbToSynthetic[db] = groupVm;
        // Map every real (non-synthetic) descendant to its VM + owning DB so
        // multi-DB scope hits route writes to the right tree / xml.
        foreach (var descendant in groupVm.AllDescendants())
        {
            // Skip the synthetic group node itself — its Model has Datatype="DB"
            // and isn't a real member. Identifying it by reference equality
            // against the synthetic instance avoids relying on Datatype string
            // matching, which is fragile.
            if (ReferenceEquals(descendant.Model, synthetic)) continue;
            _modelToVm[descendant.Model] = descendant;
            _modelToDb[descendant.Model] = db;
        }
        RootMembers.Add(groupVm);
    }

    /// <summary>
    /// Indexes every node in <paramref name="rootVm"/>'s subtree (root
    /// included) into <see cref="_modelToVm"/> + <see cref="_modelToDb"/>.
    /// Used by the single-DB code path; multi-DB indexes inside
    /// <see cref="AddDbGroupRoot"/> with the synthetic-skip rule.
    /// </summary>
    private void IndexSubtree(MemberNodeViewModel rootVm, ActiveDb db)
    {
        _modelToVm[rootVm.Model] = rootVm;
        _modelToDb[rootVm.Model] = db;
        foreach (var descendant in rootVm.AllDescendants())
        {
            _modelToVm[descendant.Model] = descendant;
            _modelToDb[descendant.Model] = db;
        }
    }

    /// <summary>
    /// Resolves a <see cref="MemberNode"/> (model) to its tree VM. Preferred
    /// over <see cref="FindNodeByPath"/> when the caller already has the
    /// model — it's O(1), and unambiguous in multi-DB sessions where the
    /// same Path string can exist in multiple DBs.
    /// </summary>
    private MemberNodeViewModel? FindVmByModel(MemberNode model) =>
        _modelToVm.TryGetValue(model, out var vm) ? vm : null;

    /// <summary>
    /// Returns the active DB that owns <paramref name="model"/>, or null
    /// when the node is not part of the current tree (e.g. a stale model
    /// from before the last RefreshTree).
    /// </summary>
    private ActiveDb? FindActiveDbForModel(MemberNode model) =>
        _modelToDb.TryGetValue(model, out var db) ? db : null;

    /// <summary>
    /// All DBs currently being edited in this dialog session (#58). Always
    /// non-empty — index 0 is the focused DB; indices 1+ are companions
    /// added via multi-select. Bulk preview / Apply iterate the whole list.
    /// </summary>
    public IReadOnlyList<ActiveDb> AllActiveDbs
    {
        get
        {
            var list = new List<ActiveDb>(1 + _companions.Count) { _active };
            list.AddRange(_companions);
            return list;
        }
    }

    /// <summary>True when more than one DB is active in this session (#58).</summary>
    public bool HasMultipleActiveDbs => _companions.Count > 0;

    /// <summary>
    /// Owning PLC name for the active DB, surfaced as a dim prefix in the
    /// combo button and the window title so multi-PLC projects don't leave
    /// the user guessing which PLC the dialog is operating on. Empty when
    /// the host couldn't supply it (DevLauncher, single-PLC stand-ins).
    /// </summary>
    public string CurrentPlcName
    {
        get => _currentPlcName;
        private set
        {
            if (SetProperty(ref _currentPlcName, value))
                OnPropertyChanged(nameof(HasCurrentPlcName));
        }
    }

    /// <summary>True when <see cref="CurrentPlcName"/> is non-empty.</summary>
    public bool HasCurrentPlcName => !string.IsNullOrEmpty(_currentPlcName);

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
            {
                OnPropertyChanged(nameof(ShowEmptyDataBlocksMessage));
                RebuildFilteredDataBlockItems();
            }
        }
    }

    private IReadOnlyList<DataBlockListItem> _filteredDataBlockItems = Array.Empty<DataBlockListItem>();

    /// <summary>
    /// Multi-select dropdown rows (#58). Same membership and order as
    /// <see cref="FilteredDataBlocks"/>; each row wraps the summary with a
    /// transient IsActive flag that two-way binds to its checkbox.
    /// </summary>
    public IReadOnlyList<DataBlockListItem> FilteredDataBlockItems
    {
        get => _filteredDataBlockItems;
        private set => SetProperty(ref _filteredDataBlockItems, value);
    }

    private void RebuildFilteredDataBlockItems()
    {
        var items = new List<DataBlockListItem>(_filteredDataBlocks.Count);
        foreach (var summary in _filteredDataBlocks)
        {
            var (isActive, isFocused) = GetActiveStatusFor(summary);
            var item = new DataBlockListItem(summary, isActive, isFocused);
            item.ToggleRequested += OnDataBlockListItemToggled;
            items.Add(item);
        }
        FilteredDataBlockItems = items;
    }

    /// <summary>
    /// True/false pair for a row: (isActive: in the dialog's active set,
    /// isFocused: index 0 of the active set). Companions are isActive but
    /// not isFocused.
    /// </summary>
    private (bool isActive, bool isFocused) GetActiveStatusFor(DataBlockSummary summary)
    {
        // Match on (Name, PlcName) — multi-PLC projects can host two DBs
        // with the same name on different PLCs (#58 review must-fix #4).
        // The focused DB's PLC is _currentPlcName; companions carry their
        // own ActiveDb.PlcName.
        if (string.Equals(_active.Info.Name, summary.Name, StringComparison.Ordinal)
            && string.Equals(_currentPlcName, summary.PlcName, StringComparison.Ordinal))
            return (true, true);
        foreach (var companion in _companions)
            if (string.Equals(companion.Info.Name, summary.Name, StringComparison.Ordinal)
                && string.Equals(companion.PlcName, summary.PlcName, StringComparison.Ordinal))
                return (true, false);
        return (false, false);
    }

    /// <summary>
    /// Pushes the current active-set state back into every existing row so
    /// checkboxes stay accurate after the user toggles one (which mutates
    /// the active set but doesn't rebuild the list).
    /// </summary>
    private void RefreshFilteredDataBlockItemsActiveState()
    {
        foreach (var item in _filteredDataBlockItems)
        {
            var (isActive, isFocused) = GetActiveStatusFor(item.Summary);
            item.SyncFrom(isActive, isFocused);
        }
    }

    private void OnDataBlockListItemToggled(DataBlockListItem item)
    {
        // The IsActive setter has already flipped to the new value. Reflect
        // that into the active set: checked → add as companion; unchecked →
        // remove (with a guard preventing the focused DB from being
        // unchecked while it still owns pending edits).
        var (wasActive, wasFocused) = GetActiveStatusFor(item.Summary);
        bool wantActive = item.IsActive;

        try
        {
            if (wantActive && !wasActive)
            {
                AddCompanionFromSummary(item.Summary);
            }
            else if (!wantActive && wasActive)
            {
                if (wasFocused)
                {
                    // Unchecking the focused DB is intentionally blocked in
                    // this commit — it would require promoting a companion
                    // to focus and re-driving the title / scope state, which
                    // is a follow-up. Snap the checkbox back so the user
                    // sees the rejection without an error dialog.
                    Log.Information(
                        "Refusing to remove focused DB {Name} via dropdown — uncheck a companion instead",
                        item.Name);
                }
                else
                {
                    RemoveCompanion(item.Summary);
                }
            }
        }
        finally
        {
            // Always re-sync the row checkbox states from the authoritative
            // active set so a refused toggle snaps back visually.
            RefreshFilteredDataBlockItemsActiveState();
            // The tree depends on the active set: rebuild it so the new DB
            // appears as a synthetic-rooted subtree (or vanishes on remove).
            RootMembers.Clear();
            BuildRootMembersFromActiveDbs();
            OnPropertyChanged(nameof(HasMultipleActiveDbs));
        }
    }

    /// <summary>
    /// Loads + parses a DB picked from the dropdown and appends it to
    /// <see cref="_companions"/> (#58). Reuses the host's
    /// <see cref="_switchToDataBlock"/> callback for export — it already
    /// handles the compile-prompt for inconsistent DBs and re-parses with
    /// the same resolvers as the focused DB.
    /// </summary>
    private void AddCompanionFromSummary(DataBlockSummary summary)
    {
        try
        {
            // Preferred path (#58): host supplies a fully-wired ActiveDb,
            // including a per-DB OnApply that re-imports the modified xml
            // back into TIA. This is symmetric with the context-menu's
            // BuildCompanionActiveDb so dropdown-added companions are
            // first-class for Apply, not read-only.
            if (_buildActiveDbForSummary != null)
            {
                var built = _buildActiveDbForSummary(summary);
                if (built == null)
                {
                    Log.Information("Companion build returned null for {Name}", summary.Name);
                    return;
                }
                _companions.Add(built);
                Log.Information("Companion DB enabled via dropdown (writable): {Name}",
                    built.Info.Name);
                return;
            }

            // Fallback (DevLauncher / tests): no host factory wired. Use
            // the older _switchToDataBlock(summary) → xml callback to
            // build a READ-ONLY companion. Apply on this companion will
            // be a no-op (OnApply is null); the multi-DB Apply path skips
            // null callbacks rather than throwing, and the
            // remove-with-stash-prompt's 'Apply, then remove' branch
            // refuses to charge a quota unit for a write that never
            // reaches TIA.
            if (_switchToDataBlock == null)
            {
                Log.Information(
                    "DB enable ignored: neither buildActiveDbForSummary nor switchToDataBlock wired");
                return;
            }
            var xml = _switchToDataBlock(summary);
            var constantResolver = _tagTableCache != null
                ? new TagTableConstantResolver(_tagTableCache)
                : (IConstantResolver?)null;
            var parser = new SimaticMLParser(constantResolver, _udtResolver, _commentResolver);
            var info = parser.Parse(xml);
            // PlcName from _currentPlcName: the dropdown only enumerates DBs
            // from the focused PLC, so any read-only-fallback companion is
            // implicitly on the same PLC as the focused DB.
            _companions.Add(new ActiveDb(info, xml, onApply: null, plcName: _currentPlcName));
            Log.Information("Companion DB enabled via dropdown (read-only fallback): {Name}",
                info.Name);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to enable companion DB {Name}", summary.Name);
            StatusText = Res.Format("Status_DbSwitchFailed", summary.Name, ex.Message);
        }
    }

    /// <summary>
    /// Removes a companion identified by <see cref="DataBlockSummary"/>
    /// (#58). When the companion has pending edits in the live tree, asks
    /// the user what to do via the same 3-way Apply / Stash / Cancel
    /// prompt the #59 single-switch uses, so unchecking a row doesn't
    /// silently drop edits.
    ///
    /// Identifies the companion by (Name, PlcName) so multi-PLC projects
    /// where two PLCs share a DB name don't accidentally drop the wrong
    /// companion (#58 review must-fix #4).
    /// </summary>
    /// <returns>true if the companion was removed; false if the user
    /// cancelled (caller should re-check the dropdown row).</returns>
    private bool RemoveCompanion(DataBlockSummary summary)
    {
        for (int i = 0; i < _companions.Count; i++)
        {
            var companion = _companions[i];
            // Match by (Name, PlcName). PlcName comparison is empty-safe:
            // single-PLC DataBlockListItems carry "" for PlcName, which
            // matches the VM's _currentPlcName fallback.
            if (!string.Equals(companion.Info.Name, summary.Name, StringComparison.Ordinal))
                continue;
            if (!string.Equals(_currentPlcName, summary.PlcName, StringComparison.Ordinal))
                continue;

            // Count pending edits within this companion's synthetic subtree.
            var pendingCount = CountPendingEditsForDb(companion);
            if (pendingCount > 0)
            {
                var result = _messageBox.AskYesNoCancel(
                    Res.Format("Dialog_SwitchDb_KeepConfirm_Text",
                        pendingCount, companion.Info.Name),
                    Res.Get("Dialog_SwitchDb_KeepConfirm_Title"));

                if (result == YesNoCancelResult.Cancel)
                {
                    Log.Information("Companion remove cancelled by user: {Name}", summary.Name);
                    return false;
                }
                if (result == YesNoCancelResult.Yes)
                {
                    // Apply this companion's edits before removing it.
                    // Re-uses the multi-DB Apply path so the unified counter
                    // gets charged correctly. Aborts on Apply failure (cap
                    // hit, validation error) — leaves the companion present.
                    var applyOk = TryApplyCompanionInPlace(companion);
                    if (!applyOk)
                    {
                        Log.Information(
                            "Companion remove aborted: pending Apply did not succeed for {Name}",
                            summary.Name);
                        return false;
                    }
                }
                else
                {
                    // No: stash the pending edits keyed by DB identity so
                    // re-checking the row in the same dialog session
                    // restores them. Mirrors the #59 stash semantics for
                    // the dropdown-driven multi-DB path.
                    StashPendingEditsForDb(companion);
                }
            }

            _companions.RemoveAt(i);
            Log.Information("Companion DB disabled via dropdown: {Name}", summary.Name);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Clears pending inline edits inside a specific DB's synthetic subtree
    /// (#58). Used by multi-DB Apply's partial-commit branch: when DB#1
    /// committed but DB#2 cancelled, DB#1's tree must drop its pending
    /// flags (the values are now in TIA) while DB#2's pending values stay
    /// for retry.
    /// </summary>
    private void ClearPendingValuesForDb(ActiveDb db)
    {
        // Multi-PLC safe (#58 review must-fix #4): _dbToSynthetic is keyed
        // by ActiveDb reference, so two PLCs' DB_Common map to two separate
        // synthetic VMs and never alias.
        if (!_dbToSynthetic.TryGetValue(db, out var root)) return;
        foreach (var node in root.AllDescendants())
        {
            if (node.IsPendingInlineEdit) node.ClearPending();
        }
    }

    /// <summary>
    /// Counts pending inline edits inside a companion's synthetic subtree.
    /// Used by the remove-with-stash-prompt flow (#58).
    /// </summary>
    private int CountPendingEditsForDb(ActiveDb db)
    {
        return _dbToSynthetic.TryGetValue(db, out var root)
            ? CountPendingInlineEdits(root.Children)
            : 0;
    }

    /// <summary>
    /// Stashes a companion's pending edits in <see cref="_stashedDbs"/> so
    /// re-adding the DB in the same session restores them. The stash is
    /// in-memory only — closing the dialog drops it.
    /// </summary>
    private void StashPendingEditsForDb(ActiveDb db)
    {
        var entries = new List<StashedEditEntry>();
        if (_dbToSynthetic.TryGetValue(db, out var root))
        {
            foreach (var node in root.AllDescendants())
            {
                if (node.IsPendingInlineEdit && node.PendingValue != null)
                {
                    entries.Add(new StashedEditEntry(
                        node.Path,
                        node.StartValue ?? "",
                        node.PendingValue));
                }
            }
        }
        if (entries.Count == 0) return;

        var summary = new DataBlockSummary(
            db.Info.Name,
            "",
            blockType: db.Info.BlockType,
            isInstanceDb: string.Equals(db.Info.BlockType, "InstanceDB", StringComparison.Ordinal),
            plcName: _currentPlcName);
        var key = $"{summary.PlcName}|{summary.FolderPath}|{summary.Name}";
        _stashedDbs[key] = new StashedDbState(summary, entries);
        SyncStashedDbsCollection();
    }

    /// <summary>
    /// Best-effort "apply this companion's edits before we remove it"
    /// (#58 remove-prompt → Yes branch). Iterates the companion's pending
    /// edits, writes them into its xml, calls its OnApply once, then
    /// charges the counter. Returns false if the daily cap or a null
    /// OnApply blocks the write — caller leaves the companion in place.
    /// </summary>
    private bool TryApplyCompanionInPlace(ActiveDb db)
    {
        if (db.OnApply == null)
        {
            // Read-only companion (dropdown-added with no host callback).
            // The user picked "Apply, then remove" but there's nothing to
            // apply against, so refuse rather than silently drop.
            StatusText = Res.Format("Status_DbSwitch_ApplyBlocked");
            return false;
        }

        // Multi-PLC safe (#58 review must-fix #4): index by ActiveDb
        // reference rather than DB name, so two PLCs hosting DB_Common
        // are not aliased.
        var pendingEdits = _dbToSynthetic.TryGetValue(db, out var root)
            ? CollectPendingInlineEdits(root.Children)
            : new List<(MemberNode Member, string Value)>();
        if (pendingEdits.Count == 0) return true;

        var status = _usageTracker.GetStatus();
        if (pendingEdits.Count > status.RemainingToday)
        {
            StatusText = Res.Format("Status_WouldExceedLimit",
                pendingEdits.Count, status.RemainingToday);
            return false;
        }

        try
        {
            int totalChanged = 0;
            foreach (var (member, value) in pendingEdits)
            {
                var writeResult = _writer.ModifyStartValues(db.Xml, new[] { member }, value);
                if (writeResult.IsSuccess)
                {
                    db.Xml = writeResult.ModifiedXml;
                    totalChanged++;
                }
            }
            db.OnApply.Invoke(db.Xml);
            if (totalChanged > 0) _usageTracker.RecordUsage(totalChanged);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TryApplyCompanionInPlace failed for {Name}", db.Info.Name);
            return false;
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
        if (string.Equals(summary.Name, _active.Info.Name, StringComparison.Ordinal)
            && string.Equals(summary.FolderPath, GetCurrentFolderPath(), StringComparison.Ordinal)
            && string.Equals(summary.PlcName, _currentPlcName, StringComparison.Ordinal))
        {
            // Already on the target DB — just close the dropdown. PLC is part
            // of identity because two PLCs can share a DB name (a project with
            // multiple PLCs can have two DB_Unit_A's at the root).
            IsDataBlocksDropdownOpen = false;
            return false;
        }

        // Snapshot any active pending edits so we can either commit them, stash
        // them, or (on Cancel) leave them untouched on the live tree.
        var pendingSnapshot = SnapshotPendingEdits();

        if (pendingSnapshot.Count > 0)
        {
            var message = Res.Format("Dialog_SwitchDb_KeepConfirm_Text",
                pendingSnapshot.Count, _active.Info.Name, summary.Name);
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
            _active.Xml = newXml;
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

            // Pull the new PLC name off the summary the host gave us — it
            // already carries the right value from enumeration.
            CurrentPlcName = summary.PlcName ?? "";
            var version = typeof(BulkChangeViewModel).Assembly.GetName().Version;
            Title = BuildTitle(version, _currentPlcName, _active.Info.Name);
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
                    ? Res.Format("Status_DbSwitched_StashRestored", _active.Info.Name, restored)
                    : Res.Format("Status_DbSwitched_StashPartial",
                        _active.Info.Name, restored, dropped);
            }
            else
            {
                StatusText = Res.Format("Status_DbSwitched", _active.Info.Name);
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
            .FirstOrDefault(b => string.Equals(b.Name, _active.Info.Name, StringComparison.Ordinal))
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
        $"{summary.PlcName}\u0001{summary.FolderPath}\u0001{summary.Name}";

    /// <summary>
    /// Jumps to the first DB with pending work (#59 follow-up). Active-DB
    /// pending edits win — scroll + select the first one. If the active DB
    /// is clean but stashes exist, switch to the first stashed DB and let
    /// the restore path surface its edits.
    /// </summary>
    private void ExecuteGoToFirstChange()
    {
        if (PendingInlineEditCount > 0)
        {
            var first = PendingEdits.FirstOrDefault();
            if (first != null)
            {
                first.Node.EnsureVisible();
                SelectedFlatMember = first.Node;
                RequestJumpToMember?.Invoke(first.Node);
            }
            return;
        }

        var firstStash = StashedDbs.FirstOrDefault();
        if (firstStash != null)
            SwitchToDataBlock(firstStash.Summary);
    }

    private void StashCurrentDb(IReadOnlyList<StashedEditEntry> edits)
    {
        var summary = new DataBlockSummary(
            _active.Info.Name,
            GetCurrentFolderPath(),
            blockType: _active.Info.BlockType,
            isInstanceDb: string.Equals(_active.Info.BlockType, "InstanceDB", StringComparison.Ordinal),
            plcName: _currentPlcName);
        var key = StashKey(summary);
        var state = new StashedDbState(summary, edits);
        _stashedDbs[key] = state;
        SyncStashedDbsCollection();
        Log.Information("Stashed {Count} pending edit(s) for DB {Db} (PLC {Plc})",
            edits.Count, summary.Name,
            string.IsNullOrEmpty(summary.PlcName) ? "<unset>" : summary.PlcName);
    }

    /// <summary>
    /// Builds the dialog window title — adds a "<c>{PLC} / </c>" prefix to the
    /// DB name when a PLC name is known so multi-PLC projects don't leave
    /// the user guessing which software unit they're operating on. Single-PLC
    /// hosts (DevLauncher) pass an empty PLC name and the prefix is dropped.
    /// </summary>
    private static string BuildTitle(System.Version? version, string plcName, string dbName)
    {
        var location = string.IsNullOrEmpty(plcName) ? dbName : $"{plcName} / {dbName}";
        return $"BlockParam v{version}: {location}";
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
        var validator = BuildValidator();
        foreach (var edit in state.Edits)
        {
            var node = FindNodeByPath(edit.Path);
            if (node is { IsLeaf: true })
            {
                // Restore-without-quota: setting EditableStartValue would route
                // through OnSingleValueEdited and consume one inline-edit slot
                // per restored row, which would silently drop edits mid-restore
                // for free-tier users near the daily cap. Set PendingValue
                // directly + run the same validator OnSingleValueEdited uses
                // so HasInlineError / InlineErrorMessage stay accurate.
                node.PendingValue = edit.PendingValue;
                var error = validator.Validate(node.Model, edit.PendingValue);
                node.HasInlineError = error != null;
                node.InlineErrorMessage = error;
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
        // OnSingleValueEdited normally drives these refreshes; we bypassed it
        // for the no-quota path so the aggregated views (PendingEdits list,
        // BulkPreview, sidebar badges) need an explicit nudge.
        if (restored > 0)
        {
            RefreshPendingAndPreview();
            OnPropertyChanged(nameof(HasInlineErrors));
            RaiseInvalidPendingChanged();
        }
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
        OnPropertyChanged(nameof(HasAnyChanges));
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
    public bool IsLimitReached => _usageTracker.GetStatus().IsLimitReached;

    /// <summary>
    /// Picked so a few back-to-back bulk applies on a normal day stay quiet, but
    /// the user gets a nudge once they're close enough that the next batch could
    /// push them over. Tied to the 200/day free-tier cap; revisit if that changes.
    /// </summary>
    private const int TightHeadroomThreshold = 50;

    /// <summary>
    /// Tooltip for the Apply button(s). Pro tier and the unsurprising free-tier
    /// case (single change, plenty of headroom) get the plain advisory; otherwise
    /// the cost line is appended so users see "this Apply uses N of M" before
    /// they click.
    /// </summary>
    public string ApplyTooltip
    {
        get
        {
            var baseText = Res.Get("Dialog_ApplyTooltip");
            if (_licenseService?.IsProActive == true) return baseText;

            var cost = PendingInlineEditCount;
            if (cost == 0) return baseText;

            var remaining = _usageTracker.GetStatus().RemainingToday;
            if (cost <= 1 && remaining >= TightHeadroomThreshold) return baseText;

            return baseText + Environment.NewLine + Environment.NewLine +
                Res.Format("Dialog_ApplyTooltip_CostLine", cost, remaining);
        }
    }

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

        // Multi-DB scope generation (#58): when companions exist, every
        // within-DB scope gains a cross-DB sibling matching the same paths
        // across all active DBs, plus an "All selected DBs" mega-scope.
        // The selected member's owning DB drives the within-DB analysis;
        // companions contribute only to the cross-DB lifts.
        AnalysisResult result;
        if (HasMultipleActiveDbs)
        {
            var owningDb = FindActiveDbForModel(memberVm.Model)?.Info ?? _active.Info;
            var allInfos = AllActiveDbs.Select(a => a.Info).ToList();
            result = _analyzer.AnalyzeMulti(allInfos, owningDb, memberVm.Model);
        }
        else
        {
            result = _analyzer.Analyze(_active.Info, memberVm.Model);
        }

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
            // Multi-DB safe (#58 review must-fix #2): resolve scope members
            // to their owning DB's tree VMs by reference, not by path string.
            // The previous HashSet<string>+full-tree-walk would mark same-named
            // paths in companion DBs as Affected even when the user picked a
            // within-DB scope on a single DB.
            var affectedVms = ResolveScopeVms(_selectedScope.MatchingMembers);

            foreach (var vm in affectedVms)
                MarkAffected(vm, _newValue);

            // AffectedBadge bubbles up through ancestors; refresh every
            // node's badge property so parent UDTs re-render their counts.
            // Doing this once per tree (not per affected vm) is O(N), same
            // as the legacy walk's PropertyChanged raises but without the
            // cross-DB false-mark side effect.
            foreach (var root in RootMembers)
                RaiseAffectedBadgeRecursive(root);
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
                foreach (var node in _manualSelectedPaths)
                {
                    if (!node.IsLeaf) continue;
                    TryAddPreviewEntry(node);
                }
            }
            else if (_selectedScope != null)
            {
                foreach (var m in _selectedScope.MatchingMembers)
                {
                    // Multi-DB safe: route by model reference, not path string.
                    // Same path can exist in multiple DBs; FindVmByModel is
                    // O(1) and always picks the right DB's tree VM.
                    var node = FindVmByModel(m);
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
        OnPropertyChanged(nameof(ApplyTooltip));
        OnPropertyChanged(nameof(HasAnyChanges));
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
        // Multi-DB safe lookup: a model is owned by exactly one ActiveDb,
        // so reference equality picks the right tree VM regardless of which
        // DBs share the path string.
        var vm = FindVmByModel(model);
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
            _active.Info, _selectedScope.MatchingMembers.ToList(),
            valueResolver: ResolvePendingValue);

        foreach (var (target, comment) in previews)
        {
            // Comment previews target scope members → route by model
            // reference for cross-DB safety (#58).
            var vm = FindVmByModel(target);
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
                var comment = templateGen!.Generate(_active.Info, node.Model, rule.CommentTemplate, lang, ResolvePendingValue);
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

    /// <summary>
    /// Resolves a scope's <see cref="MemberNode"/> match list to the
    /// owning DB's tree VMs via <see cref="FindVmByModel"/>. Multi-DB
    /// safe — same path string in a different DB is an entirely
    /// different model instance and won't end up in the result set.
    /// </summary>
    private HashSet<MemberNodeViewModel> ResolveScopeVms(IEnumerable<MemberNode> models)
    {
        var result = new HashSet<MemberNodeViewModel>();
        foreach (var m in models)
        {
            var vm = FindVmByModel(m);
            if (vm != null) result.Add(vm);
        }
        return result;
    }

    private static void MarkAffected(MemberNodeViewModel vm, string newValue)
    {
        var effectiveValue = vm.IsPendingInlineEdit
            ? (vm.EditableStartValue ?? vm.StartValue ?? "")
            : (vm.StartValue ?? "");
        var alreadyHasValue = !string.IsNullOrEmpty(newValue)
            && string.Equals(effectiveValue, newValue, StringComparison.OrdinalIgnoreCase);

        if (alreadyHasValue)
            vm.IsAlreadyMatching = true;
        else
            vm.IsAffected = true;

        vm.EnsureVisible();
    }

    private static void RaiseAffectedBadgeRecursive(MemberNodeViewModel node)
    {
        node.RaisePropertyChanged(nameof(node.AffectedBadge));
        foreach (var child in node.Children)
            RaiseAffectedBadgeRecursive(child);
    }

    private void ApplyAllFilters()
    {
        // Multi-DB safe (#58): build per-DB search/exclude sets and route
        // them to each synthetic root via _dbToSynthetic. Path strings are
        // unique within a DB but identical across DBs that share the
        // structure, so a single shared HashSet<string> would mis-mark same-
        // path leaves in companion DBs as search hits.
        var perDbSearchPaths = new Dictionary<ActiveDb, HashSet<string>>();
        int totalSearchHits = 0;
        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            foreach (var db in AllActiveDbs)
            {
                var result = _searchService.Search(db.Info, _searchQuery);
                perDbSearchPaths[db] =
                    new HashSet<string>(result.Matches.Select(m => m.Path));
                totalSearchHits += result.HitCount;
            }
        }
        SearchHitCount = totalSearchHits;

        var config = _configLoader.GetConfig();

        // Per-DB exclude sets so HiddenByRuleCount + ApplyFilter use the
        // owning DB's path set, not just the focused DB's.
        var perDbExcludeSet = new Dictionary<ActiveDb, HashSet<string>>();
        int totalHidden = 0;
        foreach (var db in AllActiveDbs)
        {
            var excl = BuildExcludeSetFor(db.Info, config);
            if (excl != null)
            {
                perDbExcludeSet[db] = excl;
                totalHidden += db.Info.AllMembers().Count(m => m.IsLeaf && excl.Contains(m.Path));
            }
        }
        HiddenByRuleCount = totalHidden;

        if (HasMultipleActiveDbs)
        {
            // Route per synthetic root so each DB's filter sets only affect
            // its own subtree.
            foreach (var (db, syntheticRoot) in _dbToSynthetic)
            {
                perDbSearchPaths.TryGetValue(db, out var sp);
                perDbExcludeSet.TryGetValue(db, out var ex);
                syntheticRoot.ApplyFilter(
                    ruleFilterActive: true,
                    searchMatchPaths: sp,
                    excludedByRules: ex,
                    showSetpointsOnly: _showSetpointsOnly);
                if (sp != null) SmartExpandSearchMatches(syntheticRoot, sp);
            }
        }
        else
        {
            perDbSearchPaths.TryGetValue(_active, out var searchPaths);
            perDbExcludeSet.TryGetValue(_active, out var excludeSet);
            foreach (var root in RootMembers)
                root.ApplyFilter(ruleFilterActive: true, searchPaths, excludeSet, _showSetpointsOnly);
            if (searchPaths != null)
            {
                foreach (var root in RootMembers)
                    SmartExpandSearchMatches(root, searchPaths);
            }
        }
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
        if (string.IsNullOrWhiteSpace(_searchQuery)) return;

        if (HasMultipleActiveDbs)
        {
            // Per-DB search so a path that's a hit in one DB doesn't smart-
            // expand the same path in companion DBs that don't have a hit.
            foreach (var (db, syntheticRoot) in _dbToSynthetic)
            {
                var result = _searchService.Search(db.Info, _searchQuery);
                var searchPaths = new HashSet<string>(result.Matches.Select(m => m.Path));
                SmartExpandSearchMatches(syntheticRoot, searchPaths);
            }
        }
        else
        {
            var result = _searchService.Search(_active.Info, _searchQuery);
            var searchPaths = new HashSet<string>(result.Matches.Select(m => m.Path));
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
            foreach (var node in _manualSelectedPaths)
            {
                if (!node.IsLeaf) continue;
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

            RefreshTree(_active.Xml, resolver, commentResolver);

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
        if (string.IsNullOrWhiteSpace(_newValue) || HasValidationError)
            return false;

        if (IsManualMode)
        {
            if (!IsSelectionTypeHomogeneous) return false;
            return CountWouldChangeMembers() > 0;
        }

        if (!HasSelection || !HasScope) return false;

        return CountWouldChangeMembers() > 0;
    }

    /// <summary>
    /// Count of members that will actually be staged when Set Pending runs —
    /// i.e. those whose effective start value differs from <c>NewValue</c>.
    /// Shared by <see cref="CanExecuteSetPending"/> and <see cref="SetButtonText"/>
    /// so the button's enable state and advertised count cannot drift apart (#65).
    /// </summary>
    private int CountWouldChangeMembers()
    {
        if (IsManualMode)
        {
            return _manualSelectedPaths.Count(node => node.IsLeaf && WouldChange(node));
        }

        if (_selectedScope == null) return 0;

        return _selectedScope.MatchingMembers.Count(m =>
        {
            var node = FindNodeByPath(m.Path);
            // Unresolved paths can't be staged by SetPendingOnNodes, so don't
            // count them — that's exactly the inflation the old label had.
            return node != null && WouldChange(node);
        });
    }

    private bool WouldChange(MemberNodeViewModel node)
    {
        var effective = node.IsPendingInlineEdit
            ? (node.EditableStartValue ?? node.StartValue ?? "")
            : (node.StartValue ?? "");
        return !string.Equals(effective, _newValue ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Total members in the active scope or manual selection — the denominator
    /// for the tooltip's "X of Y will be staged" breakdown. Counts only paths
    /// that resolve to a leaf so it matches what SetPendingOnNodes can act on.
    /// </summary>
    private int TotalCandidateMembers()
    {
        if (IsManualMode)
        {
            return _manualSelectedPaths.Count(node => node.IsLeaf);
        }
        return _selectedScope?.MatchCount ?? 0;
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

        MaybeWarnLimitReachedOnce();

        // Multi-DB safe (#58 review must-fix #2): resolve scope members to
        // their owning DB's tree VMs by reference. Path-string staging used
        // to bleed pending values into companion DBs that happened to have
        // the same path; this routes each scope member to exactly its own
        // tree node.
        var affectedVms = ResolveScopeVms(_selectedScope.MatchingMembers);
        int count = 0;

        foreach (var vm in affectedVms)
            count += SetPendingOnSingleVm(vm, _newValue);

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
        MaybeWarnLimitReachedOnce();

        int count = 0;
        foreach (var node in _manualSelectedPaths)
        {
            if (!node.IsLeaf) continue;
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

    /// <summary>
    /// Stages a pending value on a single resolved leaf VM (#58 review
    /// must-fix #2). Same semantics as the legacy recursive
    /// <see cref="SetPendingOnNodes"/> walk's leaf branch — overrides
    /// existing pending, or clears stale pending when the bulk target
    /// equals StartValue. Non-leaf VMs are skipped: the bulk operation
    /// only makes sense on primitive leaves, and the analyzer's
    /// MatchingMembers can include non-leaf array-of-UDT entries that
    /// are best ignored here. (The legacy walk's recursion-into-children
    /// was a no-op anyway: children's paths weren't in the affected set.)
    /// </summary>
    private static int SetPendingOnSingleVm(MemberNodeViewModel vm, string newValue)
    {
        if (!vm.IsLeaf) return 0;
        var startsEqualsNew = string.Equals(vm.StartValue, newValue, StringComparison.OrdinalIgnoreCase);
        if (!startsEqualsNew)
        {
            if (!string.Equals(vm.PendingValue, newValue, StringComparison.OrdinalIgnoreCase))
            {
                vm.PendingValue = newValue;
                return 1;
            }
            return 0;
        }
        if (vm.IsPendingInlineEdit)
        {
            vm.ClearPending();
            return 1;
        }
        return 0;
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
        if (HasInlineErrors) return false;
        if (PendingInlineEditCount == 0 && !HasPendingChanges) return false;

        // Free-tier cap: block Apply when the pending batch would push past
        // the daily quota. The user has to drop some edits or upgrade.
        var status = _usageTracker.GetStatus();
        if (PendingInlineEditCount > status.RemainingToday) return false;

        return true;
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
        // Multi-DB Apply (#58): when companions exist, iterate every active
        // DB, write each one's pending edits into its own xml, charge the
        // total against the daily quota once, and call each DB's OnApply
        // inside the same dialog tick. Host wires all OnApply invocations
        // into a single ExclusiveAccess block so multi-DB Apply is one TIA
        // undo step (matches issue #58 decision).
        if (_companions.Count > 0)
        {
            ExecuteApplyMultiDb();
            return;
        }

        _lastApplySucceeded = false;
        var pendingEdits = CollectPendingInlineEdits(RootMembers);

        if (pendingEdits.Count == 0 && !_hasPendingChanges)
            return;

        // Pre-check the daily cap: each pending edit is charged as one unit
        // against the free-tier quota on Apply. Block the entire batch if it
        // would push past the limit — partial Apply leaves the user in a
        // confusing half-applied state. Pro tier always passes (DailyLimit
        // is int.MaxValue via LicensedUsageTracker).
        var status = _usageTracker.GetStatus();
        if (pendingEdits.Count > status.RemainingToday)
        {
            StatusText = Res.Format("Status_WouldExceedLimit",
                pendingEdits.Count, status.RemainingToday);
            UpdateUsageStatus();
            return;
        }

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
                    _active.Xml, new[] { member }, value);

                if (writeResult.IsSuccess)
                {
                    _active.Xml = writeResult.ModifiedXml;
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
            _active.Xml = ApplyCommentPreviews(_active.Xml);

            StatusText = Res.Format("Status_Changed", totalChanged, _active.Info.Name);
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

            // Charge the daily quota — one unit per value actually written.
            // We pre-checked above, but the post-write call can still reject
            // if a parallel writer (other Add-In instance, same machine)
            // consumed quota between pre-check and write. The TIA mutation is
            // already committed at this point, so we can't roll back — but we
            // CAN consume whatever quota remains so the next Apply is blocked
            // by CanExecuteApply, and warn the user that they're over-cap.
            if (totalChanged > 0 && !_usageTracker.RecordUsage(totalChanged))
            {
                var remaining = _usageTracker.GetStatus().RemainingToday;
                if (remaining > 0)
                    _usageTracker.RecordUsage(remaining);
                StatusText = Res.Format("Status_AppliedOverCap",
                    totalChanged, _active.Info.Name);
                Log.Warning(
                    "ExecuteApply: quota race — wrote {N} past cap; counter pinned to limit",
                    totalChanged);
            }

            // Re-export from TIA to get the canonical XML after import
            RefreshTree(_active.Xml);

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
    /// Multi-DB Apply (#58). Walks each top-level synthetic root in
    /// <see cref="RootMembers"/>, resolves its owning <see cref="ActiveDb"/>
    /// by name, collects pending edits from that subtree, writes them into
    /// that DB's XML, and finally calls every DB's OnApply in one pass.
    /// Quota is charged once on the sum across all DBs — a single
    /// freemium counter increment (#58 decision: no separate multi-DB cap).
    /// </summary>
    private void ExecuteApplyMultiDb()
    {
        _lastApplySucceeded = false;

        // Pair every synthetic root with its ActiveDb so each pending edit
        // is routed to the correct XML buffer + OnApply callback. Multi-PLC
        // safe (#58 review must-fix #4): _dbToSynthetic is keyed by
        // ActiveDb reference, never by name — two PLCs hosting DBs with
        // identical names map to two distinct synthetic roots.
        var perDb = new List<(ActiveDb db, List<(MemberNode Member, string Value)> edits)>();
        int totalChanges = 0;
        // Order by AllActiveDbs so Phase-2 commits the focused DB first
        // (matches the legacy ExecuteApply ordering).
        foreach (var db in AllActiveDbs)
        {
            if (!_dbToSynthetic.TryGetValue(db, out var synthetic)) continue;
            var edits = CollectPendingInlineEdits(synthetic.Children);
            totalChanges += edits.Count;
            perDb.Add((db, edits));
        }

        if (totalChanges == 0 && !_hasPendingChanges) return;

        // Pre-check the daily cap against the SUM across all DBs (#58:
        // unified counter, no per-DB quota).
        var status = _usageTracker.GetStatus();
        if (totalChanges > status.RemainingToday)
        {
            StatusText = Res.Format("Status_WouldExceedLimit",
                totalChanges, status.RemainingToday);
            UpdateUsageStatus();
            return;
        }

        Log.Information("ExecuteApplyMultiDb: {Total} pending changes across {DbCount} DBs",
            totalChanges, perDb.Count);

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

            // Phase 1: write every DB's modified XML in memory. We don't
            // hand any of them to the host yet — if a write fails partway
            // through, no TIA mutation has happened so the abort is clean.
            foreach (var (db, edits) in perDb)
            {
                foreach (var (member, value) in edits)
                {
                    var writeResult = _writer.ModifyStartValues(
                        db.Xml, new[] { member }, value);
                    if (writeResult.IsSuccess)
                    {
                        db.Xml = writeResult.ModifiedXml;
                        totalChanged++;
                    }
                    else
                    {
                        Log.Warning("Failed for {Db}/{Path}: {Errors}",
                            db.Info.Name, member.Path,
                            string.Join("; ", writeResult.Errors));
                    }
                }
            }

            _lastApplySucceeded = true;
            HasPendingChanges = true;

            // Phase 2: hand each DB's modified XML to its host callback.
            // The host wires these into a single ExclusiveAccess block per
            // #58 (decision: one undo step across the whole multi-DB
            // Apply). User-cancel inside any callback aborts the rest.
            //
            // Partial-commit accounting: if DB#1 succeeds and DB#2 cancels,
            // DB#1's xml is already in TIA — we cannot roll it back from
            // here. The honest path is to charge quota for the writes that
            // DID succeed, surface a partial-commit status, and clear
            // pending values on committed DBs (so they don't show up as
            // pending forever). The cancelled DB's pending values stay in
            // its tree so the user can retry it after compiling / fixing.
            int committedChanges = 0;
            int committedDbs = 0;
            string? cancelledOnDb = null;
            foreach (var (db, edits) in perDb)
            {
                try
                {
                    db.OnApply?.Invoke(db.Xml);
                    committedChanges += edits.Count;
                    committedDbs++;
                }
                catch (OperationCanceledException)
                {
                    cancelledOnDb = db.Info.Name;
                    break;
                }
            }

            if (cancelledOnDb != null)
            {
                // Charge the partial committed sum so the user can't
                // accidentally double-spend on the next click. Counter
                // race handling mirrors the all-success path.
                if (committedChanges > 0 && !_usageTracker.RecordUsage(committedChanges))
                {
                    var remaining = _usageTracker.GetStatus().RemainingToday;
                    if (remaining > 0) _usageTracker.RecordUsage(remaining);
                }

                // Surface what actually happened. Without this, UI just
                // shows the empty status text and the user sees pending
                // edits stuck on the cancelled DB with no explanation.
                StatusText = Res.Format("Status_Changed",
                    committedChanges,
                    $"{committedDbs}/{perDb.Count} DB(s); '{cancelledOnDb}' cancelled");
                Log.Warning(
                    "ExecuteApplyMultiDb partial commit: {Committed}/{Total} DBs applied, cancelled on {Cancelled}",
                    committedDbs, perDb.Count, cancelledOnDb);

                // Clear pending state on the DBs that DID commit so they
                // don't keep showing as pending after the partial-Apply.
                for (int i = 0; i < committedDbs; i++)
                {
                    var (db, _) = perDb[i];
                    ClearPendingValuesForDb(db);
                }
                _lastApplySucceeded = false;
                HasPendingChanges = false;
                UpdateUsageStatus();
                return;
            }

            HasPendingChanges = false;

            // Phase 3: charge the unified daily counter once for the sum
            // (#58 decision: no separate multi-DB counter). Race handling
            // mirrors the single-DB path.
            if (totalChanged > 0 && !_usageTracker.RecordUsage(totalChanged))
            {
                var remaining = _usageTracker.GetStatus().RemainingToday;
                if (remaining > 0) _usageTracker.RecordUsage(remaining);
                Log.Warning(
                    "ExecuteApplyMultiDb: quota race — wrote {N} past cap; counter pinned to limit",
                    totalChanged);
            }

            StatusText = Res.Format("Status_Changed", totalChanged,
                $"{perDb.Count} DBs");

            // Phase 4: re-parse each DB from its post-import XML and rebuild
            // the synthetic-rooted tree so subsequent edits target the
            // canonical structure. The simplest correct approach is to
            // RefreshTree using the focused DB's xml — companions get
            // re-parsed inside RefreshTree's BuildRootMembersFromActiveDbs.
            for (int i = 0; i < perDb.Count; i++)
            {
                var (db, _) = perDb[i];
                if (ReferenceEquals(db, _active)) continue;
                var parser = new SimaticMLParser(
                    _tagTableCache != null
                        ? new TagTableConstantResolver(_tagTableCache)
                        : null,
                    _udtResolver, _commentResolver);
                db.Info = parser.Parse(db.Xml);
            }
            RefreshTree(_active.Xml);
            RefreshPendingAndPreview();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Multi-DB Apply threw");
            HandleErrorWithRollback(ex, backupPath);
        }

        UpdateUsageStatus();
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

        try
        {
            EnsureTagTableCache();
            var templateGen = new TemplateCommentGenerator(config, _tagTableCache);

            // Multi-DB safe (#58): group scope members by their owning DB
            // and write each DB's xml separately. A cross-DB scope's
            // MatchingMembers spans multiple DBs naturally; this routes
            // each match to its own ActiveDb.Xml. Single-DB sessions
            // collapse to a one-DB group, behaviour unchanged.
            var byDb = new Dictionary<ActiveDb, List<MemberNode>>();
            foreach (var member in _selectedScope.MatchingMembers)
            {
                var owningDb = FindActiveDbForModel(member);
                if (owningDb == null) continue;
                if (!byDb.TryGetValue(owningDb, out var list))
                {
                    list = new List<MemberNode>();
                    byDb[owningDb] = list;
                }
                list.Add(member);
            }

            int totalAffected = 0;
            foreach (var (db, members) in byDb)
            {
                var modifiedXml = db.Xml;
                int dbAffected = 0;
                foreach (var lang in _projectLanguages)
                {
                    var targets = templateGen.GenerateForScope(db.Info, members, lang, ResolvePendingValue);
                    if (dbAffected == 0) dbAffected = targets.Count;
                    foreach (var (target, comment) in targets)
                    {
                        modifiedXml = _writer.ModifyComment(modifiedXml, target, comment, lang);
                    }
                }
                db.Xml = modifiedXml;
                totalAffected += dbAffected;
            }

            var label = byDb.Count == 1
                ? byDb.Keys.First().Info.Name
                : $"{byDb.Count} DBs";
            StatusText = Res.Format("Comments_Updated", totalAffected, label);
            HasPendingChanges = true;
            // RefreshTree re-parses every active DB's xml in
            // BuildRootMembersFromActiveDbs, so the focused DB's xml is the
            // only argument the helper needs.
            RefreshTree(_active.Xml);
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
        return HasScope && HasCommentConfig;
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

        // Single counter (issue #62): inline edits are free to stage. Quota is
        // charged per-change on successful Apply, not per keystroke. Warn once
        // per dialog open if the user starts editing while already at 0 left,
        // so they aren't blindsided when Apply is disabled.
        if (!memberVm.IsPendingInlineEdit)
            MaybeWarnLimitReachedOnce();

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
    private HashSet<string>? BuildExcludeSet(BulkChangeConfig? config) =>
        BuildExcludeSetFor(_active.Info, config);

    /// <summary>
    /// Builds the exclude-from-setpoints path set for a specific DB (#58).
    /// Multi-DB ApplyAllFilters calls this once per active DB to keep each
    /// DB's path set distinct — a single shared set would mis-mark same-
    /// path leaves in companion DBs as excluded.
    /// </summary>
    private HashSet<string>? BuildExcludeSetFor(DataBlockInfo info, BulkChangeConfig? config)
    {
        if (config == null) return null;
        var excludeRules = config.Rules.Where(r => r.ExcludeFromSetpoints && !string.IsNullOrEmpty(r.PathPattern)).ToList();
        if (excludeRules.Count == 0) return null;

        var excluded = new HashSet<string>();
        foreach (var member in info.AllMembers())
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
        _active.Info = parser.Parse(modifiedXml);
        InlineRuleExtractor.ApplyTo(_configLoader.GetConfig(), _active.Info);

        RootMembers.Clear();
        BuildRootMembersFromActiveDbs();
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
                // Multi-DB safe (#58 review must-fix #3): without this branch,
                // RefreshTree post-multi-DB-Apply would always rebuild scopes
                // from the within-DB analyzer only, dropping the cross-DB
                // siblings + mega-scope and forcing the user to re-pick after
                // every Apply.
                AnalysisResult result;
                if (HasMultipleActiveDbs)
                {
                    var owningDb = FindActiveDbForModel(restored.Model)?.Info ?? _active.Info;
                    var allInfos = AllActiveDbs.Select(a => a.Info).ToList();
                    result = _analyzer.AnalyzeMulti(allInfos, owningDb, restored.Model);
                }
                else
                {
                    result = _analyzer.Analyze(_active.Info, restored.Model);
                }
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
            UsageStatusText = Res.Format("Status_Remaining",
                status.RemainingToday, status.DailyLimit);
            LicenseTierText = Res.Get("License_Tier_Free");
        }

        OnPropertyChanged(nameof(UsageStatusText));
        OnPropertyChanged(nameof(LicenseTierText));
        OnPropertyChanged(nameof(ShowLicenseKeyButton));
        OnPropertyChanged(nameof(LicenseKeyButtonText));
        OnPropertyChanged(nameof(IsLimitReached));
        OnPropertyChanged(nameof(ApplyTooltip));
    }

    /// <summary>
    /// Shows the daily-cap-reached modal once per dialog open, when the user
    /// first attempts to stage or edit a change while at 0 remaining quota.
    /// Staging itself isn't blocked — Apply is the choke point — but a single
    /// proactive heads-up beats discovering it via a disabled Apply button.
    /// </summary>
    private void MaybeWarnLimitReachedOnce()
    {
        if (_limitWarningShown) return;
        if (!_usageTracker.GetStatus().IsLimitReached) return;

        _limitWarningShown = true;
        _messageBox.ShowInfo(
            Res.Get("LimitReached_Modal_Message"),
            Res.Get("LimitReached_Modal_Title"));
    }

    private void ExecuteEnterLicenseKey()
    {
        if (_licenseService == null) return;

        var dialog = new LicenseKeyDialog(_licenseService, _updateCheckService, _configLoader);
        dialog.Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        dialog.ShowDialog();
        UpdateUsageStatus();
        // The user may have toggled "Check for updates" — re-evaluate the badge.
        InitializeUpdateCheck(forceRefresh: false);
    }

    /// <summary>
    /// Update available (#61). Null when no newer version is on offer
    /// (offline / opted out / already on latest / skipped).
    /// </summary>
    public UpdateInfo? AvailableUpdate
    {
        get => _availableUpdate;
        private set
        {
            if (ReferenceEquals(_availableUpdate, value)) return;
            _availableUpdate = value;
            OnPropertyChanged(nameof(AvailableUpdate));
            OnPropertyChanged(nameof(HasUpdateAvailable));
            OnPropertyChanged(nameof(UpdateBadgeText));
            OnPropertyChanged(nameof(UpdateBadgeTooltip));
            (ShowUpdateDetailsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool HasUpdateAvailable => _availableUpdate != null;

    public string UpdateBadgeText
    {
        get
        {
            if (_availableUpdate == null) return "";
            var current = typeof(BulkChangeViewModel).Assembly.GetName().Version;
            var currentText = current != null
                ? $"v{current.Major}.{Math.Max(0, current.Minor)}.{Math.Max(0, current.Build)}"
                : "v?";
            var latest = _availableUpdate.TagName;
            if (latest.Length > 0 && latest[0] != 'v' && latest[0] != 'V') latest = "v" + latest;
            return Res.Format("Update_BadgeText", currentText, latest);
        }
    }

    public string UpdateBadgeTooltip => _availableUpdate == null
        ? ""
        : Res.Format("Update_BadgeTooltip",
            string.IsNullOrEmpty(_availableUpdate.Name)
                ? _availableUpdate.TagName
                : _availableUpdate.Name);

    private void InitializeUpdateCheck(bool forceRefresh = true)
    {
        if (_updateCheckService == null) return;

        // Synchronous cached read so the badge shows up the moment the
        // dialog opens — no flash where it appears half a second later.
        try { AvailableUpdate = _updateCheckService.GetCached(); }
        catch (Exception ex) { Log.Warning(ex, "UpdateCheck: GetCached threw"); }

        if (!forceRefresh) return;

        // Fire-and-forget refresh — never blocks the UI thread, never
        // surfaces an error. Cache TTL gates the actual network hit.
        _ = Task.Run(async () =>
        {
            try
            {
                var info = await _updateCheckService.CheckAsync().ConfigureAwait(false);
                _dispatcher.BeginInvoke(new Action(() => AvailableUpdate = info));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "UpdateCheck: background CheckAsync threw");
            }
        });
    }

    private void ExecuteShowUpdateDetails()
    {
        var info = _availableUpdate;
        if (info == null || _updateCheckService == null) return;

        var dialog = new UpdateAvailableDialog(info);
        dialog.Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        dialog.ShowDialog();
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
            if (_manualSelectedPaths.Add(m)) changed = true;
        }

        if (!isFilterRehydration)
        {
            foreach (var m in removedList)
            {
                if (_manualSelectedPaths.Remove(m)) changed = true;
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
        OnPropertyChanged(nameof(SetButtonTooltip));
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
        foreach (var node in _manualSelectedPaths)
        {
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
        OnPropertyChanged(nameof(SetButtonTooltip));
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
        foreach (var node in _manualSelectedPaths)
        {
            if (node.IsLeaf)
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
