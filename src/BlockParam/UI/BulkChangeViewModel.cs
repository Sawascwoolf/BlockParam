using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
/// Main ViewModel for the BulkChange dialog. Uses a flat list (via
/// <see cref="FlatTreeManager"/>) for proper column alignment in
/// ListView/GridView.
///
/// <para>
/// <b>Active-set state model (#78).</b> The dialog's active DBs +
/// per-DB pending-edit stashes + anchor PLC name are bundled into a
/// single immutable <see cref="ActiveSetState"/> snapshot exposed via
/// <see cref="State"/>. The setter is the one place that runs
/// <c>RebuildAfterActiveSetChanged</c>, <c>SyncStashedDbsCollection</c>
/// and the anchor-PLC display refresh — every mutation gesture funnels
/// through it, so forgetting to refresh after a change is structurally
/// impossible.
/// </para>
///
/// <para>
/// Mutators compute a new snapshot in locals and assign once.
/// Single-step gestures (chip ×, dropdown toggle) call helpers like
/// <see cref="TryComputeRemove"/> and assign the returned snapshot;
/// compound gestures (Solo, Reactivate stash, legacy
/// <see cref="SwitchToDataBlock"/>) compose the new snapshot across
/// multiple steps and assign once at the end → exactly one cascade per
/// user gesture, regardless of how many DBs were swapped in or out.
/// Cancellation = don't assign; the dialog stays on the previous
/// snapshot.
/// </para>
///
/// <para>
/// The legacy backing fields (<c>_activeDbs</c>, <c>_stashedDbs</c>,
/// <c>_currentPlcName</c>) remain as the storage indexed by the ~50
/// reader sites in this file; the State setter overwrites them from
/// the new snapshot to keep the two views in lock-step. <b>Lint
/// invariant:</b> any direct write to those fields outside the State
/// setter or the constructor seed is a bug — the snapshot will drift
/// out of sync with what the cascade can see.
/// </para>
/// </summary>
public class BulkChangeViewModel : ViewModelBase, IDisposable
{
    private bool _disposed;

    private readonly HierarchyAnalyzer _analyzer;
    private readonly BulkChangeService _bulkChangeService;
    private readonly ConfigLoader _configLoader;
    private readonly Func<string>? _onBackup;   // callback to create backup, returns backup path
    private readonly Action<string>? _onRestore; // callback to restore from backup path
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

    // Active DB set. All DBs in this list are peers — index 0 is just the
    // first one in storage order, used as the anchor when the UI needs a
    // single representative (title, default scope, "current" name display).
    // Per-DB state (Info, Xml, OnApply) lives on each ActiveDb instance, so
    // bulk preview / Apply iterate the whole list without privileging any
    // entry. Mutations go through Add / RemoveActiveDb so anchor display
    // updates stay consistent.
    private readonly List<ActiveDb> _activeDbs = new();
    // _active is kept as a derived alias over _activeDbs[0] so the ~50
    // call sites that expected "the anchor DB" don't all need rewriting.
    // It carries no privilege — removing _activeDbs[0] just shifts the next
    // one into position.
    private ActiveDb _active => _activeDbs[0];
    private string _title = "";
    // DB-switcher state (#59). Lazy-loaded on first dropdown open and cached
    // for the dialog session; the ↻ button re-enumerates on demand.
    private readonly Func<IReadOnlyList<DataBlockSummary>>? _enumerateDataBlocks;
    private readonly Func<DataBlockSummary, string>? _switchToDataBlock;
    // Multi-DB add (#58): host-supplied factory that builds a fully-wired
    // ActiveDb for an arbitrary DB picked in the dropdown — including a
    // per-DB OnApply that re-imports the modified xml back into TIA. Null
    // when the host couldn't supply it (DevLauncher, tests); the VM falls
    // back to a read-only ActiveDb in that case (see AddActiveDbFromSummary).
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

    // Tree-shape state (RootMembers, flat list, model lookups, synthetic-
    // group routing) lives on the MemberTreeViewModel slice (#80 slice 7a).
    // The host accesses it via the Tree property — see the constructor.

    // Session-scoped store for pending inline-edit values, keyed by MemberNode
    // reference. Survives BuildRootMembersFromActiveDbs rebuilds — fresh VMs
    // are seeded from here after every tree rebuild so active-set transitions
    // (solo, chip-×, reactivate) no longer orphan pending state.
    // Cleared on RefreshTree (Apply mints new MemberNode instances, making old
    // keys dead) and on explicit DiscardAll.
    private readonly PendingEditStore _pendingEditStore = new();

    // #78 snapshot: single immutable view of the active set + stashes +
    // anchor PLC. The State setter is the one place that runs the cascade
    // (RebuildAfterActiveSetChanged / SyncStashedDbsCollection / anchor
    // PLC display) so every mutation gesture goes through one funnel.
    // The legacy backing fields (_activeDbs / _stashedDbs / _currentPlcName)
    // are kept as the storage of last resort — many call sites still index
    // _activeDbs[i] etc. — and the setter overwrites them from the new
    // snapshot to keep the two views in lock-step.
    private ActiveSetState _state =
        new ActiveSetState(
            new List<ActiveDb>(),
            new Dictionary<string, StashedDbState>(),
            "");
    // Selection.SelectedFlatMember, Selection.SelectedScope, _manualSelectedPaths moved to
    // SelectionScopeViewModel slice (#80 slice 7b). Host accesses via
    // Selection.SelectedFlatMember / SelectedScope / ManualSelectedPaths.
    private string _newValue = "";
    private readonly HashSet<string> _bulkErrorPaths = new(StringComparer.Ordinal);
    // True once the user has typed in the NewValue textbox. Prefills from the
    // current selection are skipped while this is true, so user-entered input
    // isn't clobbered by selection changes.
    private bool _newValueTouched;
    private string _statusText = "";
    private string _validationError = "";
    private string _constraintInfo = "";
    private Timer? _valueDebounceTimer;
    private bool _showConstants;
    private bool _constantsForced;
    private string _tagTableAge = "";
    // _isRefreshing moved to MemberTreeViewModel as Tree.IsRefreshing (slice 7a).
    private bool _lastApplySucceeded;
    private bool _hasPendingChanges;
    private readonly IReadOnlyList<string> _projectLanguages;
    private readonly CommentLanguagePolicy _commentLanguagePolicy;
    private readonly Dispatcher _dispatcher;

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
        _activeDbs.Add(new ActiveDb(dataBlockInfo, currentXml, onApply));
        if (additionalActiveDbs != null)
            _activeDbs.AddRange(additionalActiveDbs);
        _analyzer = analyzer;
        _bulkChangeService = bulkChangeService;
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
        Subscription = new SubscriptionViewModel(
            usageTracker, licenseService, updateCheckService,
            configLoader, _messageBox, _dispatcher);
        // ApplyTooltip composes license + remaining-quota state, so re-raise
        // it whenever Subscription publishes a tier / quota change.
        Subscription.StateChanged += () => OnPropertyChanged(nameof(ApplyTooltip));
        _enumerateDataBlocks = enumerateDataBlocks;
        _switchToDataBlock = switchToDataBlock;
        _buildActiveDbForSummary = buildActiveDbForSummary;
        _currentPlcName = currentPlcName ?? "";
        // Seed the snapshot from the populated backing fields. Direct
        // assignment (not via the setter) — RootMembers / StashedDbs
        // collections aren't constructed yet, so the cascade isn't
        // safe to fire here. The constructor's explicit
        // BuildRootMembersFromActiveDbs / RebuildPlcPills calls below
        // take the place of the cascade for the initial build.
        _state = new ActiveSetState(
            _activeDbs.ToList(),
            new Dictionary<string, StashedDbState>(),
            _currentPlcName);
        _autocompleteProvider = tagTableCache != null
            ? new AutocompleteProvider(configLoader, tagTableCache)
            : null;
        // Autocomplete suggestion slice (#80 slice 3).
        Autocomplete = new AutocompleteViewModel();
        // Search + tree-filter slice (#80 slice 6). Slice owns SearchQuery /
        // SetPoint toggle / banner state; the filter pass itself (ApplyAllFilters
        // + RefreshFlatList) stays here because it walks the multi-DB tree
        // and is too entangled with host state to move in this PR.
        Filter = new SearchFilterViewModel(
            _dispatcher,
            getAnchorInfo: () => _active.Info,
            hasUdtRefresh: _onRefreshUdtTypes != null,
            onFiltersChanged: () =>
            {
                ApplyAllFilters();
                RefreshFlatList();
            },
            onSetpointsTurnedOn: _onRefreshUdtTypes != null ? TryRefreshUdtCache : (Action?)null);

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
        _title = BuildTitle(version, _currentPlcName, dataBlockInfo.Name, _activeDbs.Count);

        InlineRuleExtractor.ApplyTo(configLoader.GetConfig(), dataBlockInfo);

        // Bulk-preview collection slice (#80 slice 5). NewValue is host-owned;
        // pass it via callback so the slice's Summary stays in sync without
        // pulling NewValue into the slice's surface.
        BulkPreview = new BulkPreviewViewModel(() => _newValue);
        // Pending-edits + existing-issues collections slice (#80 slice 4).
        // The underlying PendingEditStore stays on the host VM — too many
        // call sites mutate it directly to make moving it worth the churn
        // in this PR. The slice owns only the visible collections + badges.
        Pending = new PendingEditsViewModel(() => _pendingEditStore.Count);
        StashedDbs = new ObservableCollection<StashedDbState>();

        // Tree-shape + flat-list + expand/collapse slice (#80 slice 7a).
        // Multi-DB workflow (#58): when more than one DB is active, every
        // active DB becomes a synthetic top-level "DB" group whose children
        // are that DB's actual members. The user picked this shape over a
        // flat union so each match carries a visible DB-of-origin label,
        // and scope walks naturally extend one level deeper.
        Tree = new MemberTreeViewModel(
            getActiveDbs: () => _activeDbs.AsReadOnly(),
            getCurrentPlcName: () => _currentPlcName,
            commentLanguagePolicy: _commentLanguagePolicy,
            subscribeToVm: SubscribeStartValueEdited);
        Tree.RootsRebuilt += OnTreeRootsRebuilt;

        // Selection / scope / manual-selection slice (#80 slice 7b). Must be
        // constructed BEFORE Tree.BuildRootMembersFromActiveDbs because
        // SubscribeStartValueEdited (called per minted VM during the build)
        // wires `MemberNodeViewModel.SelectedChanged += Selection.OnNodeSelected`.
        // The slice owns the four state items + derived properties; the host
        // keeps the OnMemberSelected / UpdateManualSelection orchestrators
        // and runs them from the slice's events.
        Selection = new SelectionScopeViewModel(Tree);
        Selection.MemberChanged += OnMemberSelected;
        Selection.ScopeChanged += OnSelectionScopeChanged;
        Selection.ManualSelectionChanged += OnSelectionManualChanged;

        Tree.BuildRootMembersFromActiveDbs();
        RefreshRuleHints();
        RebuildPlcPills();

        SetPendingCommand = new RelayCommand(ExecuteSetPending, CanExecuteSetPending);
        ApplyCommand = new RelayCommand(ExecuteApply, CanExecuteApply);
        ApplyAndCloseCommand = new RelayCommand(ExecuteApplyAndClose, CanExecuteApply);
        UpdateCommentsCommand = new RelayCommand(ExecuteUpdateComments, CanExecuteUpdateComments);
        DiscardPendingCommand = new RelayCommand(ExecuteDiscardPending, () => PendingInlineEditCount > 0);

        EditConfigCommand = new RelayCommand(ExecuteEditConfig);
        RefreshConstantsCommand = new RelayCommand(ExecuteRefreshConstants);
        // Inspector-panel expand/collapse state lives on its own slice VM
        // (#80 slice 1). The dialog code-behind subscribes to
        // `Inspector.PropertyChanged` directly for the splitter-column
        // animation — no host-side relay needed.
        Inspector = new InspectorPanelsViewModel();
        ClearManualSelectionCommand = new RelayCommand(ExecuteClearManualSelection,
            () => Selection.ManualSelectionCount > 0);

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
            // Peer model: clicking a stashed-DB header re-activates that DB
            // by adding it back to the active set, then restoring its stash
            // edits. The previous anchor stays — the user can remove it
            // themselves if desired. Overwriting the anchor in place would
            // destroy the user's other active DBs, which the legacy
            // SwitchToDataBlock path did under the old single-DB model.
            if (parameter is StashedDbState stash) ReactivateStashedDb(stash);
        });

        // Apply initial filter and build flat list
        ApplyAllFilters();
        RefreshFlatList();
        Subscription.UpdateUsageStatus();
        Subscription.InitializeUpdateCheck();

        // #26: Surface pre-existing rule violations on dialog load. Runs after
        // RefreshRuleHints so RuleHint is available for the issue tooltip.
        RebuildExistingIssues();
    }

    // --- Properties ---

    /// <summary>
    /// Current snapshot of the active-DB set + interlocked state (#78).
    /// Every mutation goes through the setter, which is the single point
    /// that runs <see cref="RebuildAfterActiveSetChanged"/> /
    /// <see cref="SyncStashedDbsCollection"/> / anchor-display refresh.
    /// </summary>
    public ActiveSetState State
    {
        get => _state;
        private set
        {
            if (ReferenceEquals(_state, value)) return;
            var old = _state;
            _state = value;

            // Sync the legacy backing fields so the ~50 read sites that
            // index _activeDbs[i] / _stashedDbs[k] / _currentPlcName
            // continue to see the same authoritative values.
            _activeDbs.Clear();
            _activeDbs.AddRange(value.Dbs);
            _stashedDbs.Clear();
            foreach (var kv in value.Stashes) _stashedDbs[kv.Key] = kv.Value;
            bool anchorChanged = !string.Equals(_currentPlcName, value.AnchorPlcName, StringComparison.Ordinal);
            if (anchorChanged)
            {
                _currentPlcName = value.AnchorPlcName;
                OnPropertyChanged(nameof(CurrentPlcName));
                OnPropertyChanged(nameof(HasCurrentPlcName));
            }

            OnActiveSetChanged(old, value);
        }
    }

    /// <summary>
    /// Diff old vs new snapshot and run only the cascade slices that
    /// actually changed. Reference equality on Dbs / Stashes works because
    /// every mutator constructs fresh List / Dictionary instances —
    /// "same instance" implies "no change."
    /// </summary>
    private void OnActiveSetChanged(ActiveSetState old, ActiveSetState now)
    {
        bool dbsChanged = !ReferenceEquals(old.Dbs, now.Dbs);
        bool stashesChanged = !ReferenceEquals(old.Stashes, now.Stashes);

        if (dbsChanged) RebuildAfterActiveSetChanged();
        if (stashesChanged) SyncStashedDbsCollection();
        OnPropertyChanged(nameof(HasMultipleActiveDbs));

        // #91 — every cascade that changes the active set must refresh the
        // title and CurrentDataBlockName, not just the anchor-remove path.
        // Solo / reactivate / add all funnel through here, so single-DB
        // ↔ multi-DB title transitions stay in sync without each gesture
        // owning its own Title = BuildTitle(...) call.
        if (dbsChanged) RefreshAnchorDisplay();
    }

    private void RefreshAnchorDisplay()
    {
        var version = typeof(BulkChangeViewModel).Assembly.GetName().Version;
        var anchorName = State.Dbs.Count > 0 ? State.Dbs[0].Info.Name : "";
        Title = BuildTitle(version, _currentPlcName, anchorName, State.Dbs.Count);
        OnPropertyChanged(nameof(CurrentDataBlockName));
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }
    /// <summary>
    /// Selection / scope / manual-selection slice (#80 slice 7b). Owns
    /// <c>SelectedFlatMember</c>, <c>SelectedScope</c>,
    /// <c>AvailableScopes</c>, the manual-selection set, plus the
    /// single-focus invariant guard. XAML binds via
    /// <c>{Binding Selection.SelectedFlatMember}</c>,
    /// <c>{Binding Selection.AvailableScopes}</c>, etc.
    /// </summary>
    public SelectionScopeViewModel Selection { get; }

    /// <summary>
    /// Tree-shape + flat-list + expand/collapse slice (#80 slice 7a). Owns
    /// the <c>RootMembers</c> tree, the <c>FlatMembers</c> projection, the
    /// three <c>MemberNode</c>/<c>ActiveDb</c> lookup dictionaries, and the
    /// expand-all / collapse-all commands. XAML binds via
    /// <c>{Binding Tree.RootMembers}</c>, <c>{Binding Tree.FlatMembers}</c>,
    /// <c>{Binding Tree.ExpandAllCommand}</c> etc.
    /// </summary>
    public MemberTreeViewModel Tree { get; }

    /// <summary>Convenience alias for <c>Tree.RootMembers</c>. Selection /
    /// scope / Apply code on the host walks this many times; the alias
    /// keeps those call sites short.</summary>
    private ObservableCollection<MemberNodeViewModel> RootMembers => Tree.RootMembers;

    /// <summary>
    /// Bulk-preview collection slice (#80 slice 5). Owns the live preview
    /// rows plus the section-header summary / conflict-overlap readouts.
    /// XAML binds via <c>{Binding BulkPreview.Entries}</c> etc.
    /// </summary>
    public BulkPreviewViewModel BulkPreview { get; }

    /// <summary>
    /// Pending-edits + existing-issues slice (#80 slice 4). Owns the
    /// PendingEdits / ExistingIssues collections + badge properties.
    /// XAML binds via <c>{Binding Pending.PendingEdits}</c> etc.
    /// </summary>
    public PendingEditsViewModel Pending { get; }

    /// <summary>
    /// Search + tree-filter slice (#80 slice 6). Owns the search box,
    /// search-hit / hidden-by-rule counters, and the SetPoint-only toggle.
    /// XAML binds via <c>{Binding Filter.SearchQuery}</c> etc.
    /// </summary>
    public SearchFilterViewModel Filter { get; }

    /// <summary>
    /// One entry per DB the user has switched away from with un-applied
    /// pending edits (#59). Each entry renders as its own inspector section
    /// so the staged work stays visible across switches; clicking the section
    /// header switches back to that DB (running the same prompt again).
    /// </summary>
    public ObservableCollection<StashedDbState> StashedDbs { get; }

    public bool HasStashedDbs => StashedDbs.Count > 0;

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
            // just the first one. Single-DB sessions iterate exactly that
            // DB so behavior is unchanged. A null OnApply (dropdown-added
            // ActiveDb before per-DB host wiring) is a skip — Apply for
            // that DB is read-only.
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

    // SelectedFlatMember / SelectedScope moved to SelectionScopeViewModel
    // (slice 7b). The host subscribes to Selection.MemberChanged and
    // Selection.ScopeChanged from the constructor and runs OnMemberSelected
    // / UpdateHighlighting in the handler. SetButtonText / SetButtonTooltip
    // are composed properties on the host; the host re-raises them when
    // Selection raises HasScope / IsManualMode.

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

    // HasSelection / HasScope moved to SelectionScopeViewModel (slice 7b).
    public bool HasValidationError => !string.IsNullOrEmpty(_validationError);
    /// <summary>
    /// Set-button label. Composes <c>Selection.SelectedScope</c> /
    /// <c>Selection.IsManualMode</c> with host-side member-count
    /// readouts, so it stays on the host. The host's
    /// <c>Selection.ScopeChanged</c> / <c>Selection.ManualSelectionChanged</c>
    /// subscriptions re-raise this when the slice's state moves.
    /// </summary>
    public string SetButtonText
    {
        get
        {
            if (Selection.IsManualMode)
                return Res.Format("Dialog_SetManualCount", CountWouldChangeMembers());
            return Selection.SelectedScope != null
                ? $"Set {CountWouldChangeMembers()} in '{Selection.SelectedScope.AncestorName}'"
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
            if (!Selection.CanEdit) return action;

            int total = TotalCandidateMembers();
            int willChange = CountWouldChangeMembers();
            int alreadyMatch = total - willChange;

            string breakdown;
            if (string.IsNullOrEmpty(_newValue))
            {
                breakdown = Selection.IsManualMode
                    ? Res.Format("Dialog_SetTooltip_ManualIdle", total)
                    : Res.Format("Dialog_SetTooltip_ScopeIdle", total, Selection.SelectedScope?.AncestorName ?? "");
            }
            else
            {
                breakdown = Selection.IsManualMode
                    ? Res.Format("Dialog_SetTooltip_ManualBreakdown", willChange, total, alreadyMatch)
                    : Res.Format("Dialog_SetTooltip_ScopeBreakdown",
                        willChange, total, Selection.SelectedScope?.AncestorName ?? "", alreadyMatch);
            }
            return action + "\n" + breakdown;
        }
    }

    // IsManualMode / ManualSelectionCount / ManualSelectedPaths / CanEdit /
    // ManualSelectionSummary / IsSelectionTypeHomogeneous / SelectedMemberDisplay
    // moved to SelectionScopeViewModel (slice 7b).

    public ICommand SetPendingCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand ApplyAndCloseCommand { get; }
    public ICommand UpdateCommentsCommand { get; }
    public ICommand DiscardPendingCommand { get; }

    public ICommand EditConfigCommand { get; }
    public ICommand ClearManualSelectionCommand { get; }

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
    public string CurrentDataBlockName => _active.Info.Name;

    /// <summary>
    /// Populates <see cref="RootMembers"/> from every active DB (#58).
    /// Single-DB sessions get a flat list of the DB's
    /// top-level members (unchanged behavior); multi-DB sessions get one
    /// synthetic <see cref="MemberNode"/> per DB at the top level, whose
    /// children are that DB's actual members. Synthetic roots are tagged
    /// <c>Datatype="DB"</c> so the tree template can render them with a
    /// distinct chrome.
    /// </summary>
    /// <summary>
    /// Post-rebuild hook fired by <see cref="MemberTreeViewModel.RootsRebuilt"/>.
    /// The tree slice owns the fresh <c>MemberNodeViewModel</c> instances; the
    /// host owns the pending-edit store + the validator, so the seeding pass
    /// stays here. (Slice 7a kept the host-side pending-store coupling so the
    /// slice doesn't acquire a store reference.)
    /// </summary>
    private void OnTreeRootsRebuilt() => SeedVmsFromStore();

    /// <summary>
    /// Restores pending-edit state from <see cref="_pendingEditStore"/> onto
    /// freshly-constructed <see cref="MemberNodeViewModel"/> instances after a
    /// tree rebuild. Called via <see cref="MemberTreeViewModel.RootsRebuilt"/>
    /// after every <c>BuildRootMembersFromActiveDbs</c>, so active-set
    /// transitions (solo, chip-×, reactivate) automatically preserve
    /// in-progress edits without callers needing to stash/restore manually.
    /// </summary>
    private void SeedVmsFromStore()
    {
        var validator = BuildValidator();
        foreach (var (node, pendingValue) in _pendingEditStore.GetAll())
        {
            if (!Tree.ModelToVm.TryGetValue(node, out var vm))
                continue;
            // Set the field directly to avoid firing OnSingleValueEdited,
            // which would write back to the store (creating a loop) and
            // charge a spurious "new edit" warn for the free-tier cap.
            vm.SetPendingFromStore(pendingValue);
            // Re-run validation so HasInlineError / InlineErrorMessage are
            // accurate on the new VM (the value hasn't changed but the VM
            // instance is fresh and its error state is unset).
            var error = validator.Validate(node, pendingValue);
            vm.HasInlineError = error != null;
            vm.InlineErrorMessage = error;
        }
    }

    /// <summary>
    /// All DBs currently being edited in this dialog session (#58). Always
    /// non-empty. The DBs are peers — bulk preview / Apply iterate the
    /// whole list. The DB at index 0 holds the "anchor" display role
    /// (default title / scope label, see <see cref="DataBlockListItem.IsAnchor"/>);
    /// it has no privilege over removability or Apply ordering.
    /// </summary>
    public IReadOnlyList<ActiveDb> AllActiveDbs => _activeDbs.AsReadOnly();

    /// <summary>True when more than one DB is active in this session (#58).</summary>
    public bool HasMultipleActiveDbs => _activeDbs.Count > 1;

    // ── Pill-row (#pill-refactor) ─────────────────────────────────────────────

    /// <summary>
    /// One pill per PLC that has at least one active DB. Replaces the old
    /// chip row + DbSwitcherButton / DbSwitcherPopup in the dialog toolbar.
    /// Rebuilt by <see cref="RebuildPlcPills"/> whenever the active set changes.
    /// </summary>
    public ObservableCollection<PlcPillViewModel> PlcPills { get; }
        = new ObservableCollection<PlcPillViewModel>();

    /// <summary>
    /// PLCs the user added via "+ PLC" before they had any active DB.
    /// These produce empty pills until the user opens them and toggles a
    /// DB. Once a DB is active for a PLC the entry is redundant (the
    /// active-DB path also makes the PLC appear), but we keep it so the
    /// pill survives the user removing all DBs again.
    /// </summary>
    private readonly HashSet<string> _extraPillPlcs = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds <paramref name="plcName"/> to the row as an empty pill. The
    /// new pill loads its DB list lazily on first popup open, same as the
    /// active-set-derived pills. No-op if the PLC isn't a current candidate
    /// (already in row, or not present in the project's DB list at all) —
    /// this keeps <see cref="_extraPillPlcs"/> from accumulating stale
    /// names if the click stream races a project mutation.
    /// </summary>
    public void AddPlcToRow(string plcName)
    {
        if (string.IsNullOrEmpty(plcName)) return;
        if (!InactiveProjectPlcs.Contains(plcName, StringComparer.Ordinal)) return;
        _extraPillPlcs.Add(plcName);
        RebuildPlcPills();
        OnPropertyChanged(nameof(InactiveProjectPlcs));
    }

    /// <summary>
    /// PLC names present in the project but not yet represented in the
    /// pill row. Drives the "+ PLC" popup's item list and the
    /// <see cref="CanAddPlc"/> visibility gate.
    /// </summary>
    public IReadOnlyList<string> InactiveProjectPlcs
    {
        get
        {
            if (!HasDataBlockSwitcher) return Array.Empty<string>();
            LoadAvailableDataBlocks(force: false);
            var projectDbs = _availableDataBlocks;
            if (projectDbs == null || projectDbs.Count == 0)
                return Array.Empty<string>();

            var rowPlcs = new HashSet<string>(
                PlcPills.Select(p => p.PlcName ?? ""), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var db in projectDbs)
            {
                var plc = db.PlcName ?? "";
                if (rowPlcs.Contains(plc)) continue;
                if (!seen.Add(plc)) continue;
                result.Add(plc);
            }
            return result;
        }
    }

    /// <summary>
    /// True when at least one project PLC is not yet in the row. Bound
    /// to the "+ PLC" button's Visibility.
    /// </summary>
    public bool CanAddPlc => InactiveProjectPlcs.Count > 0;

    // Re-entrancy guard: when the cascade rewrites SelectedDbs on each pill,
    // the pill fires SelectionChanged → OnPillSelectionChanged. Without the
    // guard we'd enter AddActiveDbToSet / RemoveActiveDb for every item in
    // the selection sync, spiraling into multiple cascades.
    private bool _syncingPillSelection;

    // "Add DB" trailing affordance: project-wide pill to pick a DB from a PLC
    // that has no active DB yet.
    private bool _isAddDbPopupOpen;

    public bool IsAddDbPopupOpen
    {
        get => _isAddDbPopupOpen;
        set => SetProperty(ref _isAddDbPopupOpen, value);
    }

    private void RebuildPlcPills()
    {
        // Unsubscribe old pills before clearing.
        foreach (var pill in PlcPills)
            pill.SelectionChanged -= OnPillSelectionChanged;
        PlcPills.Clear();

        // Prune _extraPillPlcs of names that no longer correspond to any
        // project PLC. Without this, a manually-added pill survives even
        // after its PLC disappears from the project (e.g., after a
        // refresh), and the orphan stays in the row indefinitely.
        if (_extraPillPlcs.Count > 0 && _availableDataBlocks != null)
        {
            var projectPlcs = new HashSet<string>(
                _availableDataBlocks.Select(d => d.PlcName ?? ""),
                StringComparer.Ordinal);
            _extraPillPlcs.RemoveWhere(p => !projectPlcs.Contains(p));
        }

        var newPills = PlcPillGroupsService.Build(
            _activeDbs,
            _currentPlcName,
            loadDbsForPlc: LoadDbsForPlcAsync,
            extraPlcs: _extraPillPlcs);

        foreach (var pill in newPills)
        {
            pill.SelectionChanged += OnPillSelectionChanged;
            PlcPills.Add(pill);
        }

        // Pill row just changed — re-evaluate the inactive-PLCs list so
        // the "+ PLC" popup and its button visibility stay in sync.
        OnPropertyChanged(nameof(InactiveProjectPlcs));
        OnPropertyChanged(nameof(CanAddPlc));
    }

    private void OnPillSelectionChanged(object? sender, PillSelectionChangedEventArgs e)
    {
        if (_syncingPillSelection) return;
        _syncingPillSelection = true;
        try
        {
            foreach (var summary in e.Added)
            {
                if (!GetActiveStatusFor(summary).isActive)
                    AddActiveDbFromSummary(summary);
            }
            foreach (var summary in e.Removed)
            {
                if (State.Dbs.Count <= 1)
                {
                    Log.Information(
                        "Refusing pill remove on {Name} — at least one DB must stay active",
                        summary.Name);
                    // Snap pill selection back so the UI doesn't show a deselected last DB.
                    if (sender is PlcPillViewModel pill)
                    {
                        var activeItems = GetActiveItemsForPlc(pill);
                        pill.SyncSelectedDbs(activeItems);
                    }
                    return;
                }
                var match = FindActiveDb(summary);
                if (match != null) RemoveActiveDb(match);
            }
        }
        finally
        {
            _syncingPillSelection = false;
        }
    }

    private IReadOnlyList<DataBlockListItem> GetActiveItemsForPlc(PlcPillViewModel pill)
    {
        var result = new List<DataBlockListItem>();
        for (int i = 0; i < _activeDbs.Count; i++)
        {
            var db = _activeDbs[i];
            var plc = (i == 0 ? _currentPlcName : db.PlcName) ?? "";
            if (!string.Equals(plc, pill.PlcName, StringComparison.Ordinal)) continue;
            result.Add(new DataBlockListItem(
                new DataBlockSummary(db.Info.Name, "", plcName: plc, number: db.Info.Number),
                isActive: true,
                isAnchor: i == 0));
        }
        return result;
    }

    /// <summary>
    /// Lazy-loads the available DB list for a specific PLC. Called by each
    /// <see cref="PlcPillViewModel"/> on first popup open.
    ///
    /// Reuses the same <see cref="_availableDataBlocks"/> cache that
    /// <see cref="RefreshDataBlocksCommand"/> populates, then filters to the
    /// requested PLC so each pill only shows its own DBs.
    ///
    /// Returns on the calling (UI) thread since <see cref="LoadAvailableDataBlocks"/>
    /// already runs synchronously on the UI thread.
    /// </summary>
    internal Task<IReadOnlyList<DataBlockListItem>> LoadDbsForPlcAsync(string plcName)
    {
        if (_enumerateDataBlocks == null)
            return Task.FromResult<IReadOnlyList<DataBlockListItem>>(Array.Empty<DataBlockListItem>());

        LoadAvailableDataBlocks(force: false);

        var source = _availableDataBlocks ?? Array.Empty<DataBlockSummary>();
        var filtered = string.IsNullOrEmpty(plcName)
            ? source
            : source.Where(s => string.Equals(s.PlcName, plcName, StringComparison.Ordinal)).ToList();

        var items = filtered
            .Select(s =>
            {
                var (isActive, isAnchor) = GetActiveStatusFor(s);
                var item = new DataBlockListItem(s, isActive, isAnchor);
                item.ToggleRequested += OnDataBlockListItemToggled;
                return item;
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<DataBlockListItem>>(items);
    }

    // ── End pill-row ──────────────────────────────────────────────────────────

    /// <summary>
    /// Refuses the last-DB removal (matches the pill last-uncheck rule)
    /// and routes everything else through the existing
    /// <see cref="RemoveActiveDb"/> path so the stash / Apply prompt fires
    /// for DBs with pending edits. Internal so tests can drive gestures
    /// without going through chip / pill UI objects.
    /// </summary>
    internal void RequestRemoveActiveDb(ActiveDb db)
    {
        Log.Information(
            "[gesture] PillRemove on {Name} | {State}", db.Info.Name, SnapshotState());
        if (State.Dbs.Count <= 1)
        {
            Log.Information(
                "Refusing pill removal on {Name} — at least one DB must stay active",
                db.Info.Name);
            return;
        }
        RemoveActiveDb(db);
    }

    /// <summary>
    /// One-line dump of the gesture-relevant VM state for log breadcrumbs.
    /// Reads the bound collections so the log matches what the user sees.
    /// </summary>
    private string SnapshotState() =>
        $"active=[{string.Join(",", _activeDbs.Select(d => d.Info.Name))}] " +
        $"pending={Pending.PendingEdits.Count} stashed={StashedDbs.Count} " +
        $"treeShape={(_activeDbs.Count == 1 ? "single" : "multi")}";

    /// <summary>
    /// Owning PLC name for the active DB, surfaced as a dim prefix in the
    /// combo button and the window title so multi-PLC projects don't leave
    /// the user guessing which PLC the dialog is operating on. Empty when
    /// the host couldn't supply it (DevLauncher, single-PLC stand-ins).
    /// </summary>
    /// <summary>
    /// Anchor PLC display, derived from <see cref="State"/>'s
    /// <c>AnchorPlcName</c>. Get-only — the State setter is the single
    /// path that updates the underlying field and raises the
    /// <see cref="System.ComponentModel.INotifyPropertyChanged"/> events
    /// for both this property and <see cref="HasCurrentPlcName"/>.
    /// </summary>
    public string CurrentPlcName => _currentPlcName;

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
            var (isActive, isAnchor) = GetActiveStatusFor(summary);
            var item = new DataBlockListItem(summary, isActive, isAnchor);
            item.ToggleRequested += OnDataBlockListItemToggled;
            items.Add(item);
        }
        FilteredDataBlockItems = items;
    }

    /// <summary>
    /// True/false pair for a row: (isActive: in the dialog's active set,
    /// isAnchor: index 0 of the active set, used as the UI's display
    /// anchor). isAnchor carries no privilege over removability — it just
    /// tells the row template whether to render the anchor decoration.
    /// </summary>
    private (bool isActive, bool isAnchor) GetActiveStatusFor(DataBlockSummary summary)
    {
        // Match on (Name, PlcName) — multi-PLC projects can host two DBs
        // with the same name on different PLCs (#58 review must-fix #4).
        // For index 0 the PLC name comes from _currentPlcName (display state);
        // for the rest we read each ActiveDb.PlcName directly.
        for (int i = 0; i < _activeDbs.Count; i++)
        {
            var db = _activeDbs[i];
            var plc = i == 0 ? _currentPlcName : db.PlcName;
            if (string.Equals(db.Info.Name, summary.Name, StringComparison.Ordinal)
                && string.Equals(plc, summary.PlcName, StringComparison.Ordinal))
                return (true, i == 0);
        }
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
            var (isActive, isAnchor) = GetActiveStatusFor(item.Summary);
            item.SyncFrom(isActive, isAnchor);
        }
    }

    private void OnDataBlockListItemToggled(DataBlockListItem item)
    {
        // The IsActive setter has already flipped to the new value. Peer
        // model: checked → add to active set; unchecked → remove from active
        // set, with the same Apply / Stash / Cancel prompt regardless of
        // whether the row is the current anchor. The active set must keep
        // at least one DB — refuse the last uncheck. Both branches funnel
        // through State, so the cascade fires exactly once per toggle (#78).
        var (wasActive, _) = GetActiveStatusFor(item.Summary);
        bool wantActive = item.IsActive;
        Log.Information(
            "[gesture] Dropdown row {Name} {Direction} (was={WasActive} now={WantActive}) | {State}",
            item.Name,
            wantActive ? "CHECKED" : "UNCHECKED",
            wasActive, wantActive, SnapshotState());

        if (wantActive && !wasActive)
        {
            AddActiveDbToSet(item);
        }
        else if (!wantActive && wasActive)
        {
            if (State.Dbs.Count <= 1)
            {
                Log.Information(
                    "Refusing to remove {Name} — at least one DB must stay active",
                    item.Name);
                // Snap the row checkbox back into sync with the unchanged
                // active set — the cascade only fires when State actually
                // changes, so we have to nudge the dropdown rows here.
                RefreshFilteredDataBlockItemsActiveState();
            }
            else
            {
                var match = FindActiveDb(item.Summary);
                if (match != null) RemoveActiveDb(match);
                else RefreshFilteredDataBlockItemsActiveState();
            }
        }
    }

    /// <summary>
    /// Pure-add path: append the dropdown-checked DB to the active set.
    /// PendingEditStore (9814a6e) seeds pending values onto fresh VMs
    /// after the rebuild, so the previously-required Apply/Stash/Cancel
    /// rescue prompt on every other active DB is unnecessary — adding
    /// a peer never destroys an in-progress edit (#93).
    /// </summary>
    private void AddActiveDbToSet(DataBlockListItem item)
    {
        var built = BuildActiveDbFromSummary(item.Summary);
        if (built == null)
        {
            RefreshFilteredDataBlockItemsActiveState();
            return;
        }
        State = State.With(dbs: State.Dbs.Concat(new[] { built }).ToList());
        Log.Information("DB enabled via dropdown: {Name}", built.Info.Name);
    }

    /// <summary>
    /// Solo gesture (#58 peer-mode): replace the active set with just the
    /// target DB. Each dropped DB runs through the same Apply / Stash /
    /// Cancel prompt RemoveActiveDb uses.
    ///
    /// Cancel semantics:
    ///   • Target was already in the active set → partial-set behaviour
    ///     (current matches user choices: silently-dropped DBs stay dropped,
    ///     cancelled-on DBs stay, target stays).
    ///   • Target was newly built (soloed from the dropdown without first
    ///     checking it) → any cancel reverts the entire gesture so the
    ///     freshly-built target is NOT silently added alongside the DB the
    ///     user explicitly refused to drop. Without this, Solo+Cancel from a
    ///     single-DB session quietly mutates the active set into multi-DB
    ///     shape, triggering a tree rebuild that orphans pending edits on
    ///     the original DB.
    ///
    /// Compound op (#78): builds the next snapshot in a local and commits
    /// once at the end → exactly one cascade per gesture.
    /// </summary>
    public void SoloActiveDb(DataBlockSummary target)
    {
        var original = State;
        var current = original;
        var targetDb = FindActiveDb(target);
        bool targetWasNewlyBuilt = false;
        if (targetDb == null)
        {
            // The user soloed a row that wasn't already checked — append it
            // to the snapshot before pruning so the "leave only this one"
            // invariant always holds.
            var built = BuildActiveDbFromSummary(target);
            if (built == null)
            {
                IsDataBlocksDropdownOpen = false;
                return;
            }
            current = current.With(dbs: current.Dbs.Concat(new[] { built }).ToList());
            targetDb = built;
            targetWasNewlyBuilt = true;
        }

        var next = ComposeRemoveOthers(current, targetDb);

        // If we just appended a fresh target and the prune loop bailed
        // (next still carries DBs other than target), drop the whole
        // composition: the user's Cancel applies to the entire gesture.
        if (targetWasNewlyBuilt && next.Dbs.Count > 1)
        {
            Log.Information(
                "Solo+Cancel reverted: target {Name} was newly built and a " +
                "prune cancelled — restoring original active set",
                target.Name);
            IsDataBlocksDropdownOpen = false;
            return;
        }

        State = next;
        IsDataBlocksDropdownOpen = false;
    }

    /// <summary>
    /// Reference-based variant of <see cref="SoloActiveDb"/>. Originally
    /// called from the chip body click; surviving as a public seam used by
    /// tests and (in future) any "solo this DB" pill affordance. Same
    /// prompts on every dropped DB with pending edits; if the user cancels
    /// mid-solo, the remaining drops are skipped and the active set is
    /// whatever survived.
    /// </summary>
    public void SoloActiveDbByReference(ActiveDb target)
    {
        Log.Information(
            "[gesture] SoloByReference → {Name} | {State}",
            target.Info.Name, SnapshotState());
        if (State.Dbs.Count <= 1)
        {
            // Already the only active DB — there's nothing to solo away.
            return;
        }

        State = ComposeRemoveOthers(State, target);
    }

    /// <summary>
    /// Composes the snapshot that results from removing every DB in
    /// <paramref name="current"/> except <paramref name="target"/>. Walks
    /// the others in storage order, calling <see cref="TryComputeRemove"/>
    /// for each (which prompts on pending edits). If the user cancels mid-
    /// loop, returns the partial composition so the active set ends up as
    /// whatever survived. Caller assigns the result to <see cref="State"/>.
    /// </summary>
    private ActiveSetState ComposeRemoveOthers(ActiveSetState current, ActiveDb target)
    {
        var next = current;
        foreach (var db in current.Dbs.Where(d => !ReferenceEquals(d, target)).ToList())
        {
            var step = TryComputeRemove(next, db);
            if (step == null)
            {
                Log.Information(
                    "Solo cancelled at {Name} — leaving partial set", db.Info.Name);
                break;
            }
            next = step;
        }
        return next;
    }

    private bool IsSameSummary(ActiveDb db, DataBlockSummary summary)
    {
        // Mirror FindActiveDb's anchor PLC handling: index 0 reads
        // _currentPlcName; the rest carry their own PlcName on ActiveDb.
        var idx = _activeDbs.IndexOf(db);
        var plc = idx == 0 ? _currentPlcName : db.PlcName;
        return string.Equals(db.Info.Name, summary.Name, StringComparison.Ordinal)
            && string.Equals(plc, summary.PlcName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Inspector path: re-add a previously-stashed DB back into the active
    /// set, drop the others (with the same Apply / Stash / Cancel prompt
    /// every remove uses), pop the stash entry, and replay its edits onto
    /// the live tree. One snapshot is composed across all of these and
    /// committed once → exactly one cascade. The pending-value replay
    /// runs after the State assignment so it lands on the freshly-built
    /// tree, not on VMs the cascade is about to discard.
    /// </summary>
    private void ReactivateStashedDb(StashedDbState stash)
    {
        Log.Information(
            "[gesture] Stash header click → reactivate {Name} | {State}",
            stash.DbName, SnapshotState());
        var summary = stash.Summary;
        var current = State;
        var target = FindActiveDb(summary);

        // #92 — When the user has ≥2 active DBs and clicks a stash header,
        // ask whether they want to *add* the stashed DB to the current
        // session (additive) or *replace* the active set with it (the
        // legacy single-DB behaviour). Single-DB sessions skip the prompt:
        // there's nothing to "replace away" so the gesture is unambiguous
        // and goes through the additive branch implicitly via the existing
        // append-then-prune flow below (which becomes a no-op prune when
        // current has only one DB == target).
        if (current.Dbs.Count >= 2)
        {
            var decision = _messageBox.AskAddOrReplace(
                Res.Format("Reactivate_AdditiveOrReplace_Text", summary.Name),
                Res.Get("Reactivate_AdditiveOrReplace_Title"));
            if (decision == AddOrReplaceResult.Cancel)
            {
                Log.Information("Reactivate cancelled by user for {Name}", summary.Name);
                return;
            }
            if (decision == AddOrReplaceResult.Add)
            {
                ReactivateStashedDbAdditive(stash);
                return;
            }
            // Replace falls through to the Replace path below.
        }

        if (target == null)
        {
            var built = BuildActiveDbFromSummary(summary);
            if (built == null) return;
            current = current.With(dbs: current.Dbs.Concat(new[] { built }).ToList());
            target = built;
        }

        var next = ComposeRemoveOthers(current, target);

        // Pop the stash entry inside the same snapshot so the cascade
        // sees stashes-changed once. Other DBs that got stashed during
        // ComposeRemoveOthers (peer with pending edits, user picked Stash)
        // stay in next.Stashes — only the reactivated DB's entry is popped.
        var stashKey = StashKey(summary);
        if (next.Stashes.ContainsKey(stashKey))
        {
            var stashesNew = next.Stashes.ToDictionary(kv => kv.Key, kv => kv.Value);
            stashesNew.Remove(stashKey);
            next = next.With(stashes: stashesNew);
        }

        State = next;

        // Replay the stash edits onto the now-final live tree. Runs after
        // State assignment so the cascade has already rebuilt RootMembers
        // — otherwise the new PendingValue assignments would land on VMs
        // the rebuild is about to discard.
        var (restored, dropped) = RestoreStashOntoLive(stash);
        if (restored > 0 || dropped > 0)
        {
            StatusText = dropped == 0
                ? Res.Format("Status_DbSwitched_StashRestored", summary.Name, restored)
                : Res.Format("Status_DbSwitched_StashPartial",
                    summary.Name, restored, dropped);
        }
    }

    /// <summary>
    /// Additive branch of <see cref="ReactivateStashedDb"/>: append the
    /// stashed DB to the active set without dropping any other active DBs,
    /// pop its stash entry, replay its edits. One snapshot, one cascade.
    /// </summary>
    private void ReactivateStashedDbAdditive(StashedDbState stash)
    {
        var summary = stash.Summary;
        var built = BuildActiveDbFromSummary(summary);
        if (built == null) return;

        var stashesNew = State.Stashes.ToDictionary(kv => kv.Key, kv => kv.Value);
        stashesNew.Remove(StashKey(summary));
        State = State.With(
            dbs: State.Dbs.Concat(new[] { built }).ToList(),
            stashes: stashesNew);

        // Multi-DB shape after additive add — scope the path lookup to the
        // just-added DB's subtree so peer DBs with colliding member paths
        // don't accidentally absorb the stashed edits (#82 path-identity).
        var (restored, dropped) = RestoreStashOntoLive(stash, scopedTo: built);
        if (restored > 0 || dropped > 0)
        {
            StatusText = dropped == 0
                ? Res.Format("Status_DbSwitched_StashRestored", summary.Name, restored)
                : Res.Format("Status_DbSwitched_StashPartial",
                    summary.Name, restored, dropped);
        }
    }

    /// <summary>
    /// Re-runs the full UI rebuild after _activeDbs is mutated (add / remove /
    /// reactivate). Refreshes dropdown checkbox states, clears stale selection
    /// /scope/manual-selection (held by VM references that the rebuild
    /// invalidates), rebuilds the tree from the new active set, then re-applies
    /// filters + the flat list so the visible ListView (bound to FlatMembers,
    /// not RootMembers) doesn't need a stray click to refresh.
    /// </summary>
    private void RebuildAfterActiveSetChanged()
    {
        Log.Information(
            "[cascade] RebuildAfterActiveSetChanged → rebuilding tree | {State}",
            SnapshotState());
        // BuildRootMembersFromActiveDbs creates fresh MemberNodeViewModel
        // instances on every rebuild, so any selection / scope / manual-
        // selection state held by reference points at orphaned VMs from
        // the previous tree. Clear them before the rebuild — same pattern
        // SwitchToDataBlock uses — so the count machinery doesn't accumulate
        // phantom entries from a deactivated DB.
        Selection.SetSelectedFlatMemberSilent(null);
        Selection.SetSelectedScopeSilent(null);
        Selection.ClearManualPaths();
        // Tree.BuildRootMembersFromActiveDbs clears RootMembers + the lookup
        // dicts internally — no separate RootMembers.Clear() needed.
        Tree.BuildRootMembersFromActiveDbs();
        ApplyAllFilters();
        RefreshFlatList();
        RebuildPlcPills();
        // PendingEdits / BulkPreview hold MemberNodeViewModel references —
        // BuildRootMembersFromActiveDbs just minted fresh ones, so any
        // surviving entries point at orphans from the prior tree. Without
        // this refresh the inspector's "pending changes" list keeps showing
        // edits whose DB just left the active set, and the chip-close /
        // dropdown-add prompt machinery (which counts pending state on the
        // *new* tree) silently disagrees with what the user sees. Same
        // pattern as the _manualSelectedPaths.Clear() above — the cascade
        // owns the cleanup of every collection that holds VM references.
        RebuildPendingEdits();
        BulkPreview.Clear();
        // Clear() raises the underlying CollectionChanged event, but the
        // slice's derived properties (Count, HasEntries, Summary, Conflict*)
        // are separate INotifyPropertyChanged signals — without this, the
        // inspector header badge/summary stays stale until the next
        // ComputeBulkPreview cycle.
        BulkPreview.RaiseDerivedChanged();
        // CanShowSetpointsOnly / ShowSetpointsOnlyTooltip derive from the
        // anchor's UnresolvedUdts via the Filter slice's getAnchorInfo
        // closure. When the active-set change swaps the anchor to a DB with
        // a different UDT-resolution outcome, the checkbox's enabled state
        // and tooltip have to refresh — the slice has no way to observe the
        // anchor change on its own.
        Filter.RaiseSetpointsCapabilityChanged();
        // SelectedFlatMember / SelectedScope / manual-set were cleared via
        // Selection.SetXxxSilent above; the slice raised its own
        // PropertyChanged for SelectedFlatMember / HasSelection / SelectedScope /
        // HasScope / CanEdit / SelectedMemberDisplay. ClearManualPaths is
        // silent by design — bundle the manual-side notifications here.
        // Selection.RaiseManualSelectionChanged fires the slice's events AND
        // triggers Selection.ManualSelectionChanged → OnSelectionManualChanged,
        // which re-raises SetButtonText / SetButtonTooltip on the host channel.
        // No need to raise those a second time directly.
        Selection.RaiseManualSelectionChanged();
        OnPropertyChanged(nameof(HasMultipleActiveDbs));
    }

    /// <summary>
    /// Pure builder (#78): produces a fully-wired <see cref="ActiveDb"/>
    /// for a dropdown-picked DB without touching <see cref="State"/>.
    /// Compound mutations (Solo, Reactivate) call this directly and
    /// fold the result into their composed snapshot; the simple
    /// "checkbox checked" path goes through
    /// <see cref="AddActiveDbFromSummary"/>, which builds + assigns.
    /// </summary>
    private ActiveDb? BuildActiveDbFromSummary(DataBlockSummary summary)
    {
        try
        {
            // Preferred path (#58): host supplies a fully-wired ActiveDb,
            // including a per-DB OnApply that re-imports the modified xml
            // back into TIA. This is symmetric with the context-menu's
            // ActiveDbFactory so dropdown-added DBs are first-class for
            // Apply, not read-only.
            if (_buildActiveDbForSummary != null)
            {
                var built = _buildActiveDbForSummary(summary);
                if (built == null)
                    Log.Information("ActiveDb build returned null for {Name}", summary.Name);
                return built;
            }

            // Fallback (DevLauncher / tests): no host factory wired. Use
            // the older _switchToDataBlock(summary) → xml callback to
            // build a READ-ONLY ActiveDb. Apply on this DB will be a
            // no-op (OnApply is null); the multi-DB Apply path skips
            // null callbacks rather than throwing, and the
            // remove-with-stash-prompt's 'Apply, then remove' branch
            // refuses to charge a quota unit for a write that never
            // reaches TIA.
            if (_switchToDataBlock == null)
            {
                Log.Information(
                    "DB enable ignored: neither buildActiveDbForSummary nor switchToDataBlock wired");
                return null;
            }
            var xml = _switchToDataBlock(summary);
            var constantResolver = _tagTableCache != null
                ? new TagTableConstantResolver(_tagTableCache)
                : (IConstantResolver?)null;
            var parser = new SimaticMLParser(constantResolver, _udtResolver, _commentResolver);
            var info = parser.Parse(xml);
            // PlcName from _currentPlcName: the dropdown only enumerates DBs
            // from the anchor's PLC, so any read-only-fallback addition is
            // implicitly on the same PLC as the anchor.
            return new ActiveDb(info, xml, onApply: null, plcName: _currentPlcName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to build ActiveDb {Name}", summary.Name);
            StatusText = Res.Format("Status_DbSwitchFailed", summary.Name, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Single-step add (dropdown checkbox-on). Builds the ActiveDb and
    /// commits to <see cref="State"/> in one shot — the cascade fires
    /// once on the resulting snapshot. Compound paths build directly via
    /// <see cref="BuildActiveDbFromSummary"/> and skip this wrapper.
    /// </summary>
    private void AddActiveDbFromSummary(DataBlockSummary summary)
    {
        var built = BuildActiveDbFromSummary(summary);
        if (built == null) return;
        State = State.With(dbs: State.Dbs.Concat(new[] { built }).ToList());
        Log.Information("DB enabled via dropdown: {Name}", built.Info.Name);
    }

    /// <summary>
    /// Looks up an active DB by (Name, PlcName) so dropdown rows resolve to
    /// the right ActiveDb instance even in multi-PLC projects where two
    /// PLCs share a DB name (#58 review must-fix #4).
    /// </summary>
    private ActiveDb? FindActiveDb(DataBlockSummary summary)
    {
        for (int i = 0; i < _activeDbs.Count; i++)
        {
            var db = _activeDbs[i];
            // Index 0 reads its display PLC from _currentPlcName; the rest
            // read it from each ActiveDb directly.
            var plc = i == 0 ? _currentPlcName : db.PlcName;
            if (string.Equals(db.Info.Name, summary.Name, StringComparison.Ordinal)
                && string.Equals(plc, summary.PlcName, StringComparison.Ordinal))
                return db;
        }
        return null;
    }

    /// <summary>
    /// User's choice in response to the "this DB has pending edits" prompt
    /// raised when removing a DB from the active set (#78). Centralised so
    /// every remove path maps the <see cref="ApplyStashCancelResult"/>
    /// the same way: ApplyAndSwitch→Apply, StashAndSwitch→Stash, Cancel→Cancel.
    /// <c>NoEdits</c> is the no-prompt fast path.
    /// </summary>
    private enum PendingDecision { NoEdits, Apply, Stash, Cancel }

    /// <summary>
    /// Single source of truth for the 3-way prompt that fires whenever a
    /// DB with pending inline edits is about to be dropped from the active
    /// set (#78). Returns <see cref="PendingDecision.NoEdits"/> immediately
    /// if there's nothing pending — caller proceeds with the removal
    /// without showing a dialog.
    /// </summary>
    private PendingDecision PromptForPendingEditsOnRemove(ActiveDb db)
    {
        var pendingCount = CountPendingEditsForDb(db);
        if (pendingCount == 0)
        {
            Log.Information(
                "[prompt] no-prompt fast path on {Name} (CountPendingEditsForDb=0) | {State}",
                db.Info.Name, SnapshotState());
            return PendingDecision.NoEdits;
        }
        Log.Information(
            "[prompt] Showing 3-way for {Name} (pending={Pending}) | {State}",
            db.Info.Name, pendingCount, SnapshotState());
        var result = _messageBox.AskApplyStashCancel(
            Res.Format("Dialog_SwitchDb_KeepConfirm_Text",
                pendingCount, db.Info.Name),
            Res.Get("Dialog_SwitchDb_KeepConfirm_Title"));
        Log.Information(
            "[prompt] User picked {Result} for {Name}", result, db.Info.Name);
        return result switch
        {
            ApplyStashCancelResult.ApplyAndSwitch => PendingDecision.Apply,
            ApplyStashCancelResult.StashAndSwitch => PendingDecision.Stash,
            _ => PendingDecision.Cancel,
        };
    }

    /// <summary>
    /// Close-confirm prompt fired when both the active DB and stashed DBs
    /// hold pending edits. Wrapped here so the dialog code-behind doesn't
    /// need to reach into the VM's message-box service.
    /// </summary>
    internal CloseWithStashResult PromptForCloseWithStash()
    {
        var active = PendingInlineEditCount;
        var stashedCount = StashedDbs.Sum(s => s.Count);
        var stashedDbList = string.Join(", ", StashedDbs.Select(s => s.DbName));
        return _messageBox.AskCloseWithStash(
            Res.Format("Dialog_UnsavedChanges_Prompt_WithStash",
                active, stashedCount, stashedDbList),
            Res.Get("Dialog_UnsavedChanges_Title"));
    }

    /// <summary>
    /// Snapshots pending inline edits for <paramref name="db"/> into an
    /// inert <see cref="StashedDbState"/> ready to be added to a new
    /// <see cref="ActiveSetState"/>. Returns null when the DB has no
    /// pending edits to capture. Reads from the store — unambiguous in
    /// multi-DB sessions where the same Path can exist in multiple DBs,
    /// and correct even if the tree hasn't rebuilt yet. Pure read —
    /// does NOT mutate <c>_stashedDbs</c> (the snapshot setter does).
    /// </summary>
    private StashedDbState? CaptureStashForDb(ActiveDb db)
    {
        var entries = new List<StashedEditEntry>();
        foreach (var (node, pendingValue) in _pendingEditStore.GetForDb(db, Tree.ModelToDb))
        {
            // Resolve to the VM to read StartValue (which is model-derived
            // and stable). If the VM can't be found the model is still valid.
            var vm = Tree.FindVmByModel(node);
            entries.Add(new StashedEditEntry(
                node.Path,
                vm?.StartValue ?? node.StartValue ?? "",
                pendingValue));
        }
        if (entries.Count == 0) return null;
        var summary = new DataBlockSummary(
            db.Info.Name,
            "",
            blockType: db.Info.BlockType,
            isInstanceDb: string.Equals(db.Info.BlockType, "InstanceDB", StringComparison.Ordinal),
            plcName: _currentPlcName);
        return new StashedDbState(summary, entries);
    }

    /// <summary>
    /// Computes the next snapshot after removing <paramref name="db"/>
    /// from <paramref name="current"/>. Side-effects (the user-visible
    /// prompt + Apply branch's per-DB OnApply + usage-counter charge)
    /// run here because they MUST occur against the live (pre-cascade)
    /// tree. Returns null if the user cancelled or the Apply branch
    /// failed (cap hit / read-only OnApply); caller treats null as
    /// "abort this remove."
    /// </summary>
    private ActiveSetState? TryComputeRemove(ActiveSetState current, ActiveDb db)
    {
        var decision = PromptForPendingEditsOnRemove(db);
        if (decision == PendingDecision.Cancel)
        {
            Log.Information("DB remove cancelled by user: {Name}", db.Info.Name);
            return null;
        }
        if (decision == PendingDecision.Apply)
        {
            if (!TryApplyActiveDbInPlace(db))
            {
                Log.Information(
                    "DB remove aborted: pending Apply did not succeed for {Name}",
                    db.Info.Name);
                return null;
            }
        }

        IReadOnlyDictionary<string, StashedDbState> nextStashes = current.Stashes;
        if (decision == PendingDecision.Stash)
        {
            var captured = CaptureStashForDb(db);
            if (captured != null)
            {
                var dict = current.Stashes.ToDictionary(kv => kv.Key, kv => kv.Value);
                dict[StashKey(captured.Summary)] = captured;
                nextStashes = dict;
            }
        }

        var nextDbs = current.Dbs.Where(d => !ReferenceEquals(d, db)).ToList();
        // Anchor handoff: only when the removed DB was the anchor; the
        // new anchor is whatever moved into nextDbs[0]. Same rule as the
        // legacy RemoveActiveDb.
        var nextAnchor = current.AnchorPlcName;
        if (current.Dbs.Count > 0 && ReferenceEquals(db, current.Dbs[0]))
        {
            nextAnchor = nextDbs.Count > 0 ? (nextDbs[0].PlcName ?? "") : "";
        }

        Log.Information(
            ReferenceEquals(db, current.Dbs[0])
                ? "Anchor removed: now {NewName} (was: {OldName})"
                : "DB disabled: {OldName}",
            nextDbs.Count > 0 ? nextDbs[0].Info.Name : "<empty>",
            db.Info.Name);

        return new ActiveSetState(nextDbs, nextStashes, nextAnchor);
    }

    /// <summary>
    /// Single-step remove (chip × / dropdown uncheck). Compound paths
    /// (solo, reactivate) call <see cref="TryComputeRemove"/> directly
    /// to compose multiple removes into one snapshot before assigning.
    /// </summary>
    /// <returns>true if removed; false if the user cancelled.</returns>
    private bool RemoveActiveDb(ActiveDb db)
    {
        var next = TryComputeRemove(State, db);
        if (next == null) return false;
        State = next;
        return true;
    }

    /// <summary>
    /// Clears pending inline edits inside a specific DB's subtree (#58).
    /// Used by multi-DB Apply's partial-commit branch: when DB#1 committed
    /// but DB#2 cancelled, DB#1's tree must drop its pending flags (the
    /// values are now in TIA) while DB#2's pending values stay for retry.
    /// </summary>
    private void ClearPendingValuesForDb(ActiveDb db)
    {
        // Walk Tree.ModelToDb to find VMs owned by this DB and clear their
        // pending state. Keyed by MemberNode reference — safe in multi-DB
        // sessions where the same path can appear in multiple DBs.
        foreach (var kvp in Tree.ModelToDb)
        {
            if (!ReferenceEquals(kvp.Value, db)) continue;
            if (Tree.ModelToVm.TryGetValue(kvp.Key, out var vm) && vm.IsPendingInlineEdit)
                vm.ClearPending();
        }
        // Evict from the store so a later tree rebuild doesn't re-populate
        // values that were just committed.
        _pendingEditStore.ClearForDb(db, Tree.ModelToDb);
    }

    /// <summary>
    /// Counts pending inline edits inside a DB's subtree. Used by the
    /// remove-with-stash-prompt flow (#58, #78). Reads from the store
    /// rather than walking the VM tree — O(store.Count) instead of
    /// O(subtree size), and correct even when the tree hasn't rebuilt yet.
    /// </summary>
    private int CountPendingEditsForDb(ActiveDb db)
        => _pendingEditStore.CountForDb(db, Tree.ModelToDb);

    /// <summary>
    /// Best-effort "apply this DB's edits before we remove it" (#58
    /// remove-prompt → Yes branch). Iterates the DB's pending edits,
    /// writes them into its xml, calls its OnApply once, then charges
    /// the counter. Returns false if the daily cap or a null OnApply
    /// blocks the write — caller leaves the DB in place.
    /// </summary>
    private bool TryApplyActiveDbInPlace(ActiveDb db)
    {
        if (db.OnApply == null)
        {
            // Read-only ActiveDb (dropdown-added with no host callback).
            // The user picked "Apply, then remove" but there's nothing to
            // apply against, so refuse rather than silently drop.
            StatusText = Res.Format("Status_DbSwitch_ApplyBlocked");
            return false;
        }

        // Store-based: unambiguous in multi-DB sessions (MemberNode identity),
        // correct even when the tree hasn't rebuilt yet (store survives rebuilds).
        var pendingEdits = _pendingEditStore.GetForDb(db, Tree.ModelToDb).ToList();
        if (pendingEdits.Count == 0) return true;

        var status = Subscription.GetUsageStatus();
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
            if (totalChanged > 0) Subscription.RecordUsage(totalChanged);
            // The edits are now committed to TIA — evict the DB's store entries
            // so a subsequent tree rebuild doesn't re-populate committed values.
            _pendingEditStore.ClearForDb(db, Tree.ModelToDb);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TryApplyActiveDbInPlace failed for {Name}", db.Info.Name);
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

    private static string StashKey(DataBlockSummary summary) =>
        $"{summary.PlcName}\u0001{summary.FolderPath}\u0001{summary.Name}";


    /// <summary>
    /// Builds the dialog window title. Single-DB sessions render
    /// <c>"BlockParam v{version}: {PLC} / {DB}"</c>. Multi-DB sessions
    /// drop the DB/PLC suffix entirely (#91): the chip strip is the
    /// single source of truth for which DBs are in scope, so surfacing
    /// one specific DB's name in the title contradicts the peer-DB
    /// model. Single-PLC hosts (DevLauncher) pass an empty PLC name and
    /// the prefix is dropped.
    /// </summary>
    private static string BuildTitle(System.Version? version, string plcName, string dbName, int activeDbCount)
    {
        if (activeDbCount > 1) return $"BlockParam v{version}";
        var location = string.IsNullOrEmpty(plcName) ? dbName : $"{plcName} / {dbName}";
        return $"BlockParam v{version}: {location}";
    }

    /// <summary>
    /// Replays a stash's edits onto the live tree (#78). Pure write — does
    /// NOT pop the stash entry from <see cref="_stashedDbs"/>. Callers
    /// that need the entry popped do that separately, ideally as part of
    /// composing the next <see cref="ActiveSetState"/> so the cascade
    /// fires once.
    /// </summary>
    private (int restored, int dropped) RestoreStashOntoLive(StashedDbState state, ActiveDb? scopedTo = null)
    {
        int restored = 0;
        int dropped = 0;
        var validator = BuildValidator();
        foreach (var edit in state.Edits)
        {
            var node = scopedTo != null
                ? FindNodeByPathInDb(edit.Path, scopedTo)
                : Tree.FindNodeByPath(edit.Path);
            if (node is { IsLeaf: true })
            {
                // Restore-without-quota: setting EditableStartValue would route
                // through OnSingleValueEdited and consume one inline-edit slot
                // per restored row, which would silently drop edits mid-restore
                // for free-tier users near the daily cap. Set PendingValue
                // directly + run the same validator OnSingleValueEdited uses
                // so HasInlineError / InlineErrorMessage stay accurate.
                node.PendingValue = edit.PendingValue;
                // Mirror the restored value to the store so subsequent tree
                // rebuilds (e.g. a later active-set change) can re-seed it.
                _pendingEditStore.Set(node.Model, edit.PendingValue);
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
        // OnSingleValueEdited normally drives these refreshes; we bypassed it
        // for the no-quota path so the aggregated views (PendingEdits list,
        // BulkPreview, sidebar badges) need an explicit nudge.
        if (restored > 0)
        {
            RefreshPendingAndPreview();
            OnPropertyChanged(nameof(HasInlineErrors));
            Pending.RaiseInvalidPendingChanged();
        }
        Log.Information("Restored {Restored} stashed edit(s) for {Db} ({Dropped} dropped)",
            restored, state.Summary.Name, dropped);
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
    /// Inspector-panel expand/collapse state (#80 slice 1).
    /// XAML binds via <c>{Binding Inspector.IsBulkEditExpanded}</c> etc.
    /// </summary>
    public InspectorPanelsViewModel Inspector { get; }

    /// <summary>
    /// Raised after the flat list has been refreshed so the view can rehydrate
    /// the ListView's multi-selection from <see cref="ManualSelectedPaths"/>.
    /// </summary>
    public event Action? FlatListRefreshed;

    /// <summary>
    /// Subscription / usage / update slice (#80 slice 2). Owns license-tier
    /// display, usage status, license-key dialog opener, upgrade prompt,
    /// update-available badge. The host VM keeps <see cref="ApplyTooltip"/>
    /// because it composes Apply-pipeline state with subscription state.
    /// </summary>
    public SubscriptionViewModel Subscription { get; }

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
            if (Subscription.IsProActive) return baseText;

            var cost = PendingInlineEditCount;
            if (cost == 0) return baseText;

            var remaining = Subscription.GetUsageStatus().RemainingToday;
            if (cost <= 1 && remaining >= TightHeadroomThreshold) return baseText;

            return baseText + Environment.NewLine + Environment.NewLine +
                Res.Format("Dialog_ApplyTooltip_CostLine", cost, remaining);
        }
    }

    /// <summary>Number of individual inline edits waiting to be applied.</summary>
    public int PendingInlineEditCount => Pending.PendingInlineEditCount;

    /// <summary>
    /// Autocomplete suggestion slice (#80 slice 3). Owns the candidate
    /// pool, the filtered subset, and the glob provider. XAML binds via
    /// <c>{Binding Autocomplete.FilteredSuggestions}</c> etc.
    /// </summary>
    public AutocompleteViewModel Autocomplete { get; }

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

        return AutocompleteViewModel.Match(all, filter);
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
        Autocomplete.ClearFiltered();
        UpdateHighlighting();
    }

    /// <summary>Show suggestions filtered by current text (always opens).</summary>
    public void ShowAllSuggestions() => Autocomplete.ShowAll(_newValue?.Trim() ?? "");

    /// <summary>Toggle: show filtered suggestions or hide them.</summary>
    public void ToggleAllSuggestions() => Autocomplete.Toggle(_newValue?.Trim() ?? "");

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

    // ToggleExpand / IsRefreshing moved to MemberTreeViewModel (slice 7a).
    // Code-behind that needs them now reads `vm.Tree.ToggleExpand` /
    // `vm.Tree.IsRefreshing`.

    // --- Private methods ---

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
    /// cancels the 200ms debounce on <see cref="SearchFilterViewModel.SearchQuery"/>
    /// and runs the filter + flat-list refresh synchronously. Delegates to
    /// <see cref="SearchFilterViewModel.FlushPendingSearch"/> on the
    /// <see cref="Filter"/> slice; kept as a host-side shim so existing test /
    /// capture-script call sites don't have to chase the new path.
    /// </summary>
    internal void FlushPendingSearch() => Filter.FlushPendingSearch();

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
        // Pre-PR shape (one combined try/finally on the host) is preserved by
        // routing the selection-restore + multi-select rehydration through
        // Tree.RebuildFlatList's insideRefreshScope callback — both still run
        // while Tree.IsRefreshing is true, so SelectedFlatMember setter +
        // code-behind SelectionChanged stay suppressed during the rebuild.
        var selectedPath = Selection.SelectedFlatMember?.Path;
        Tree.RebuildFlatList(insideRefreshScope: () =>
        {
            if (selectedPath != null)
            {
                var restored = Tree.FlatMembers.FirstOrDefault(m => m.Path == selectedPath);
                // Silent setter so we don't re-trigger OnMemberSelected during
                // the rebuild — the slice raises SelectedFlatMember /
                // HasSelection / SelectedMemberDisplay PropertyChanged for us.
                Selection.SetSelectedFlatMemberSilent(restored);
            }
            FlatListRefreshed?.Invoke();
        });
    }

    /// <summary>
    /// Host-side handler for <see cref="SelectionScopeViewModel.ScopeChanged"/>.
    /// The slice raises this once per <c>SelectedScope</c> setter call; the
    /// host runs highlighting + re-raises the composed
    /// <see cref="SetButtonText"/> / <see cref="SetButtonTooltip"/>
    /// notifications that compose with scope state.
    ///
    /// <para>
    /// Set Pending / Clear Manual <c>CanExecute</c> picks up changes via
    /// WPF's <c>CommandManager.RequerySuggested</c> on the next render
    /// cycle — pre-slice code did not call <c>RaiseCanExecuteChanged</c>
    /// from this seam either.
    /// </para>
    /// </summary>
    private void OnSelectionScopeChanged()
    {
        UpdateHighlighting();
        OnPropertyChanged(nameof(SetButtonText));
        OnPropertyChanged(nameof(SetButtonTooltip));
    }

    /// <summary>
    /// Host-side handler for <see cref="SelectionScopeViewModel.ManualSelectionChanged"/>.
    /// Re-raises the host-composed properties that depend on the manual-set
    /// count. The slice already re-raised its own properties
    /// (IsManualMode, ManualSelectionCount, SelectedMemberDisplay, …);
    /// host only re-raises what it owns.
    /// </summary>
    private void OnSelectionManualChanged()
    {
        OnPropertyChanged(nameof(SetButtonText));
        OnPropertyChanged(nameof(SetButtonTooltip));
    }

    private void OnMemberSelected(MemberNodeViewModel? memberVm)
    {
        Selection.AvailableScopes.Clear();
        // Silent setter — the same method re-populates and re-selects below;
        // routing through the public setter would cascade UpdateHighlighting
        // twice (once here, once when the auto-selected scope assigns).
        Selection.SetSelectedScopeSilent(null);
        ValidationError = "";
        ConstraintInfo = "";
        Autocomplete.ClearCandidates();

        // In manual multi-select mode, scope analysis does not apply.
        if (Selection.IsManualMode)
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

        // Multi-DB scope generation (#58): when more than one DB is
        // active, every within-DB scope gains a cross-DB sibling matching
        // the same paths across all active DBs, plus an "All selected
        // DBs" mega-scope. The selected member's owning DB drives the
        // within-DB analysis; the other active DBs contribute only to
        // the cross-DB lifts.
        AnalysisResult result;
        if (HasMultipleActiveDbs)
        {
            var owningDb = Tree.FindActiveDbForModel(memberVm.Model)?.Info ?? _active.Info;
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
            Selection.AvailableScopes.Add(scope);

        if (Selection.AvailableScopes.Count > 0)
            Selection.SelectedScope = Selection.AvailableScopes[0];

        // Proactive hint (#7) — single source of truth with the inline-edit tooltip.
        ConstraintInfo = memberVm.RuleHint ?? "";

        StatusText = "";

        // Re-sync selection after flat list rebuilds during click processing
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (memberVm != null && Selection.SelectedFlatMember != memberVm)
                Selection.SetSelectedFlatMemberSilent(memberVm);
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
        Selection.SelectedFlatMember?.EnsureVisible();

        if (Selection.SelectedScope == null && !Selection.IsManualMode)
        {
            ComputeBulkPreview();
            RebuildPendingEdits();
            RefreshFlatList();
            return;
        }

        if (Selection.SelectedScope != null)
        {
            // Multi-DB safe (#58 review must-fix #2): resolve scope members
            // to their owning DB's tree VMs by reference, not by path string.
            // The previous HashSet<string>+full-tree-walk would mark same-named
            // paths in other active DBs as Affected even when the user picked
            // a within-DB scope on a single DB.
            var affectedVms = ResolveScopeVms(Selection.SelectedScope.MatchingMembers);

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
                        && (Selection.IsManualMode || Selection.SelectedScope != null);
        if (hasInput)
        {
            if (Selection.IsManualMode)
            {
                foreach (var node in Selection.ManualSelectedPaths)
                {
                    if (!node.IsLeaf) continue;
                    TryAddPreviewEntry(node);
                }
            }
            else if (Selection.SelectedScope != null)
            {
                foreach (var m in Selection.SelectedScope.MatchingMembers)
                {
                    // Multi-DB safe: route by model reference, not path string.
                    // Same path can exist in multiple DBs; FindVmByModel is
                    // O(1) and always picks the right DB's tree VM.
                    var node = Tree.FindVmByModel(m);
                    if (node == null || !node.IsLeaf) continue;
                    TryAddPreviewEntry(node);
                }
            }
        }

        // Only raise bindings when the result might actually have changed.
        if (wasNonEmpty || BulkPreview.Count > 0)
            BulkPreview.RaiseDerivedChanged();
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
    /// Call order matters: preview first so the rebuild can read the
    /// just-built BulkPreview paths.
    /// </summary>
    private void RefreshPendingAndPreview()
    {
        Pending.RaisePendingCountChanged();
        OnPropertyChanged(nameof(PendingInlineEditCount));
        OnPropertyChanged(nameof(ApplyTooltip));
        ComputeBulkPreview();
        RebuildPendingEdits();
    }

    /// <summary>
    /// Rebuilds the pending-edits side panel from the current tree state.
    /// </summary>
    private void RebuildPendingEdits()
    {
        var bulkPaths = BulkPreview.Count > 0
            ? new HashSet<string>(BulkPreview.Entries.Select(e => e.Path), StringComparer.Ordinal)
            : null;
        Pending.Rebuild(RootMembers, bulkPaths);
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
        var vm = Tree.FindVmByModel(model);
        if (vm != null && vm.IsPendingInlineEdit)
            return vm.EditableStartValue;
        return model.StartValue;
    }

    private void UpdateCommentPreviews()
    {
        var config = _configLoader.GetConfig();
        if (config == null || Selection.SelectedScope == null) return;
        if (!config.Rules.Any(r => !string.IsNullOrEmpty(r.CommentTemplate))) return;

        EnsureTagTableCache();
        var generator = new TemplateCommentGenerator(config, _tagTableCache);
        var previews = generator.GenerateForScope(
            _active.Info, Selection.SelectedScope.MatchingMembers.ToList(),
            valueResolver: ResolvePendingValue);

        foreach (var (target, comment) in previews)
        {
            // Comment previews target scope members → route by model
            // reference for cross-DB safety (#58).
            var vm = Tree.FindVmByModel(target);
            if (vm != null)
            {
                vm.PreviewComment = comment;
            }
        }
    }

    internal string ApplyCommentPreviews(string xml)
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

    /// <summary>
    /// Diagnostic wrapper around <see cref="MemberTreeViewModel.FindNodeByPathInDb"/>:
    /// the slice returns null when the active set isn't in multi-DB shape
    /// yet (its <c>_dbToSynthetic</c> doesn't have an entry for
    /// <paramref name="owner"/>), the host adds the warn-log so a future
    /// ordering bug surfaces as "stashed edit dropped" in the log instead
    /// of silently aliasing onto another DB. The slice intentionally has
    /// no <c>Serilog</c> dependency.
    /// </summary>
    private MemberNodeViewModel? FindNodeByPathInDb(string path, ActiveDb owner)
    {
        if (!Tree.DbToSynthetic.ContainsKey(owner))
        {
            Log.Warning(
                "FindNodeByPathInDb({Path}) called for {Db} but no synthetic " +
                "root is registered — active set may not be in multi-DB shape " +
                "yet; returning null to avoid cross-DB path aliasing",
                path, owner.Info.Name);
            return null;
        }
        return Tree.FindNodeByPathInDb(path, owner);
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
            var vm = Tree.FindVmByModel(m);
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
        // path leaves in other active DBs as search hits.
        var perDbSearchPaths = new Dictionary<ActiveDb, HashSet<string>>();
        int totalSearchHits = 0;
        var searchQuery = Filter.SearchQuery;
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            foreach (var db in AllActiveDbs)
            {
                var result = _searchService.Search(db.Info, searchQuery);
                perDbSearchPaths[db] =
                    new HashSet<string>(result.Matches.Select(m => m.Path));
                totalSearchHits += result.HitCount;
            }
        }
        Filter.SearchHitCount = totalSearchHits;

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
        Filter.HiddenByRuleCount = totalHidden;

        if (HasMultipleActiveDbs)
        {
            // Route per synthetic root so each DB's filter sets only affect
            // its own subtree.
            foreach (var kvp in Tree.DbToSynthetic)
            {
                var db = kvp.Key;
                var syntheticRoot = kvp.Value;
                perDbSearchPaths.TryGetValue(db, out var sp);
                perDbExcludeSet.TryGetValue(db, out var ex);
                syntheticRoot.ApplyFilter(
                    ruleFilterActive: true,
                    searchMatchPaths: sp,
                    excludedByRules: ex,
                    showSetpointsOnly: Filter.ShowSetpointsOnly);
                if (sp != null) SmartExpandSearchMatches(syntheticRoot, sp);
            }
        }
        else
        {
            perDbSearchPaths.TryGetValue(_active, out var searchPaths);
            perDbExcludeSet.TryGetValue(_active, out var excludeSet);
            foreach (var root in RootMembers)
                root.ApplyFilter(ruleFilterActive: true, searchPaths, excludeSet, Filter.ShowSetpointsOnly);
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
        var searchQuery = Filter.SearchQuery;
        if (string.IsNullOrWhiteSpace(searchQuery)) return;

        if (HasMultipleActiveDbs)
        {
            // Per-DB search so a path that's a hit in one DB doesn't smart-
            // expand the same path in other active DBs that don't have a hit.
            foreach (var kvp in Tree.DbToSynthetic)
            {
                var db = kvp.Key;
                var syntheticRoot = kvp.Value;
                var result = _searchService.Search(db.Info, searchQuery);
                var searchPaths = new HashSet<string>(result.Matches.Select(m => m.Path));
                SmartExpandSearchMatches(syntheticRoot, searchPaths);
            }
        }
        else
        {
            var result = _searchService.Search(_active.Info, searchQuery);
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
        if (Selection.IsManualMode)
        {
            if (!Selection.IsSelectionTypeHomogeneous)
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
            foreach (var node in Selection.ManualSelectedPaths)
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
                        _bulkErrorPaths.Add(node.Path);
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

        if (string.IsNullOrEmpty(_newValue) || Selection.SelectedFlatMember == null)
        {
            ValidationError = "";
            return;
        }

        ValidationError = ValidateValueForMember(Selection.SelectedFlatMember, _newValue) ?? "";
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
            var n = Tree.FindNodeByPath(path);
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
    private void RebuildExistingIssues() =>
        Pending.RebuildExistingIssues(RootMembers, BuildValidator());

    private void UpdateFilteredSuggestions() =>
        Autocomplete.ApplyFilter(_newValue?.Trim() ?? "");

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
        Autocomplete.ClearCandidates();

        if (Selection.SelectedFlatMember == null || !Selection.SelectedFlatMember.IsLeaf) return;
        if (!_showConstants) return;

        EnsureTagTableCache();
        if (_tagTableCache == null) return;

        var config = _configLoader.GetConfig();
        var rule = config?.GetRule(Selection.SelectedFlatMember.Model);

        IReadOnlyList<AutocompleteSuggestion> suggestions;
        if (rule?.TagTableReference != null)
        {
            // Rule-based: only matching tables
            suggestions = _autocompleteProvider?.GetSuggestions(Selection.SelectedFlatMember.Model, "")
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
            Autocomplete.SetCandidates(suggestions, new GlobSuggestionProvider(suggestions));
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

            Filter.RaiseSetpointsCapabilityChanged();
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

    // ExpandAll / CollapseAll commands + ExpandAllChildren / CollapseAllChildren
    // methods moved to MemberTreeViewModel (slice 7a). Code-behind callers
    // route through `vm.Tree.ExpandAllChildren(node)` etc.

    /// <summary>Can stage bulk scope or manual-selection values as pending.</summary>
    private bool CanExecuteSetPending()
    {
        if (string.IsNullOrWhiteSpace(_newValue) || HasValidationError)
            return false;

        if (Selection.IsManualMode)
        {
            if (!Selection.IsSelectionTypeHomogeneous) return false;
            return CountWouldChangeMembers() > 0;
        }

        if (!Selection.HasSelection || !Selection.HasScope) return false;

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
        if (Selection.IsManualMode)
        {
            return Selection.ManualSelectedPaths.Count(node => node.IsLeaf && WouldChange(node));
        }

        if (Selection.SelectedScope == null) return 0;

        return Selection.SelectedScope.MatchingMembers.Count(m =>
        {
            var node = Tree.FindNodeByPath(m.Path);
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
        if (Selection.IsManualMode)
        {
            return Selection.ManualSelectedPaths.Count(node => node.IsLeaf);
        }
        return Selection.SelectedScope?.MatchCount ?? 0;
    }

    /// <summary>
    /// Stages bulk scope or manual-selection values as pending on each affected node.
    /// Does NOT modify XML — values turn yellow until Apply is clicked.
    /// </summary>
    private void ExecuteSetPending()
    {
        if (Selection.IsManualMode)
        {
            ExecuteSetPendingManual();
            return;
        }

        if (Selection.SelectedScope == null) return;

        Subscription.MaybeWarnLimitReachedOnce();

        // Multi-DB safe (#58 review must-fix #2): resolve scope members to
        // their owning DB's tree VMs by reference. Path-string staging used
        // to bleed pending values into other active DBs that happened to
        // have the same path; this routes each scope member to exactly its
        // own tree node.
        var affectedVms = ResolveScopeVms(Selection.SelectedScope.MatchingMembers);
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
        Subscription.MaybeWarnLimitReachedOnce();

        int count = 0;
        foreach (var node in Selection.ManualSelectedPaths)
        {
            if (!node.IsLeaf) continue;
            var startsEqualsNew = string.Equals(node.StartValue, _newValue, StringComparison.OrdinalIgnoreCase);
            if (!startsEqualsNew)
            {
                if (!string.Equals(node.PendingValue, _newValue, StringComparison.OrdinalIgnoreCase))
                {
                    node.PendingValue = _newValue;
                    _pendingEditStore.Set(node.Model, _newValue);
                    count++;
                }
            }
            else if (node.IsPendingInlineEdit)
            {
                node.ClearPending();
                _pendingEditStore.Clear(node.Model);
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
    private int SetPendingOnSingleVm(MemberNodeViewModel vm, string newValue)
    {
        if (!vm.IsLeaf) return 0;
        var startsEqualsNew = string.Equals(vm.StartValue, newValue, StringComparison.OrdinalIgnoreCase);
        if (!startsEqualsNew)
        {
            if (!string.Equals(vm.PendingValue, newValue, StringComparison.OrdinalIgnoreCase))
            {
                vm.PendingValue = newValue;
                _pendingEditStore.Set(vm.Model, newValue);
                return 1;
            }
            return 0;
        }
        if (vm.IsPendingInlineEdit)
        {
            vm.ClearPending();
            _pendingEditStore.Clear(vm.Model);
            return 1;
        }
        return 0;
    }

    private int SetPendingOnNodes(MemberNodeViewModel node,
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
                    _pendingEditStore.Set(node.Model, newValue);
                    count++;
                }
            }
            else if (node.IsPendingInlineEdit)
            {
                // Bulk targets the original value and there's a stale pending on this
                // node — clear it so the node reverts to StartValue.
                node.ClearPending();
                _pendingEditStore.Clear(node.Model);
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
        var status = Subscription.GetUsageStatus();
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
    /// Applies ALL pending changes (bulk-staged + inline edits) to XML.
    /// </summary>
    private void ExecuteApply()
    {
        // Multi-DB Apply (#58): when more than one DB is active, iterate
        // every active DB, write each one's pending edits into its own xml,
        // charge the total against the daily quota once, and call each DB's
        // OnApply inside the same dialog tick. Host wires all OnApply
        // invocations into a single ExclusiveAccess block so multi-DB Apply
        // is one TIA undo step (matches issue #58 decision).
        if (_activeDbs.Count > 1)
        {
            ExecuteApplyMultiDb();
            return;
        }

        _lastApplySucceeded = false;
        // Store-based: single-DB mode has exactly one active DB, so GetAll()
        // yields its pending entries without needing a per-DB filter.
        var pendingEdits = _pendingEditStore.GetAll().ToList();

        if (pendingEdits.Count == 0 && !_hasPendingChanges)
            return;

        // Pre-check the daily cap: each pending edit is charged as one unit
        // against the free-tier quota on Apply. Block the entire batch if it
        // would push past the limit — partial Apply leaves the user in a
        // confusing half-applied state. Pro tier always passes (DailyLimit
        // is int.MaxValue via LicensedUsageTracker).
        var status = Subscription.GetUsageStatus();
        if (pendingEdits.Count > status.RemainingToday)
        {
            StatusText = Res.Format("Status_WouldExceedLimit",
                pendingEdits.Count, status.RemainingToday);
            Subscription.UpdateUsageStatus();
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
                Subscription.UpdateUsageStatus();
                return;
            }

            // Charge the daily quota — one unit per value actually written.
            // We pre-checked above, but the post-write call can still reject
            // if a parallel writer (other Add-In instance, same machine)
            // consumed quota between pre-check and write. The TIA mutation is
            // already committed at this point, so we can't roll back — but we
            // CAN consume whatever quota remains so the next Apply is blocked
            // by CanExecuteApply, and warn the user that they're over-cap.
            if (totalChanged > 0 && !Subscription.RecordUsage(totalChanged))
            {
                var remaining = Subscription.GetUsageStatus().RemainingToday;
                if (remaining > 0 && !Subscription.RecordUsage(remaining))
                {
                    Log.Warning(
                        "ExecuteApply: second RecordUsage({Remaining}) also failed — " +
                        "counter may have diverged from quota state",
                        remaining);
                }
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

        Subscription.UpdateUsageStatus();
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
            // Store-based: unambiguous in multi-PLC sessions; correct even if
            // the tree hasn't rebuilt after a recent active-set transition.
            var edits = _pendingEditStore.GetForDb(db, Tree.ModelToDb).ToList();
            if (edits.Count == 0) continue;
            totalChanges += edits.Count;
            perDb.Add((db, edits));
        }

        if (totalChanges == 0 && !_hasPendingChanges) return;

        // Pre-check the daily cap against the SUM across all DBs (#58:
        // unified counter, no per-DB quota).
        var status = Subscription.GetUsageStatus();
        if (totalChanges > status.RemainingToday)
        {
            StatusText = Res.Format("Status_WouldExceedLimit",
                totalChanges, status.RemainingToday);
            Subscription.UpdateUsageStatus();
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
                if (committedChanges > 0 && !Subscription.RecordUsage(committedChanges))
                {
                    var remaining = Subscription.GetUsageStatus().RemainingToday;
                    if (remaining > 0 && !Subscription.RecordUsage(remaining))
                    {
                        Log.Warning(
                            "ExecuteApplyMultiDb partial-commit: second RecordUsage({Remaining}) " +
                            "also failed — counter may have diverged from quota state",
                            remaining);
                    }
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
                // Pending list + count + ApplyTooltip would otherwise stay
                // showing the pre-clear total — both happy paths below call
                // RefreshPendingAndPreview, this early-return branch must too.
                RefreshPendingAndPreview();
                Subscription.UpdateUsageStatus();
                return;
            }

            HasPendingChanges = false;

            // Phase 3: charge the unified daily counter once for the sum
            // (#58 decision: no separate multi-DB counter). Race handling
            // mirrors the single-DB path.
            if (totalChanged > 0 && !Subscription.RecordUsage(totalChanged))
            {
                var remaining = Subscription.GetUsageStatus().RemainingToday;
                if (remaining > 0 && !Subscription.RecordUsage(remaining))
                {
                    Log.Warning(
                        "ExecuteApplyMultiDb full-commit: second RecordUsage({Remaining}) " +
                        "also failed — counter may have diverged from quota state",
                        remaining);
                }
                Log.Warning(
                    "ExecuteApplyMultiDb: quota race — wrote {N} past cap; counter pinned to limit",
                    totalChanged);
            }

            StatusText = Res.Format("Status_Changed", totalChanged,
                $"{perDb.Count} DBs");

            // Phase 4: re-parse each DB from its post-import XML and rebuild
            // the synthetic-rooted tree so subsequent edits target the
            // canonical structure. The simplest correct approach is to
            // RefreshTree using the anchor DB's xml — every other active
            // DB gets re-parsed inside RefreshTree's
            // BuildRootMembersFromActiveDbs.
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

        Subscription.UpdateUsageStatus();
    }


    /// <summary>
    /// F-031: Bulk-update all comments in the current scope using the configured
    /// comment generation rules.
    /// </summary>
    private void ExecuteUpdateComments()
    {
        var config = _configLoader.GetConfig();
        if (config == null || Selection.SelectedScope == null) return;
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
            foreach (var member in Selection.SelectedScope.MatchingMembers)
            {
                var owningDb = Tree.FindActiveDbForModel(member);
                if (owningDb == null) continue;
                if (!byDb.TryGetValue(owningDb, out var list))
                {
                    list = new List<MemberNode>();
                    byDb[owningDb] = list;
                }
                list.Add(member);
            }

            int totalAffected = 0;
            foreach (var kvp in byDb)
            {
                var db = kvp.Key;
                var members = kvp.Value;
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

        Subscription.UpdateUsageStatus();
    }

    private bool CanExecuteUpdateComments()
    {
        return Selection.HasScope && HasCommentConfig;
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
        node.SelectedChanged += Selection.OnNodeSelected;
        foreach (var child in node.Children)
            SubscribeStartValueEdited(child);
    }

    // _inSelectionCascade + OnNodeSelected moved to SelectionScopeViewModel
    // (slice 7b). Host's SubscribeStartValueEdited routes `SelectedChanged`
    // through Selection.OnNodeSelected directly.

    /// <summary>
    /// Handles direct inline editing of a single start value in the table.
    /// Stores the value as pending (does NOT modify XML until Apply).
    /// Validates constraints and updates the pending status display.
    /// </summary>
    private void OnSingleValueEdited(MemberNodeViewModel memberVm, string newValue)
    {
        Log.Information(
            "[gesture] Inline edit on {Path}: '{Old}' → '{New}' | {State}",
            memberVm.Path, memberVm.StartValue ?? "", newValue, SnapshotState());
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
            // Evict cleared edit from the store so tree rebuilds don't
            // re-populate a value the user just reverted.
            _pendingEditStore.Clear(memberVm.Model);
            RefreshPendingAndPreview();
            return;
        }

        // Single counter (issue #62): inline edits are free to stage. Quota is
        // charged per-change on successful Apply, not per keystroke. Warn once
        // per dialog open if the user starts editing while already at 0 left,
        // so they aren't blindsided when Apply is disabled.
        if (!memberVm.IsPendingInlineEdit)
            Subscription.MaybeWarnLimitReachedOnce();

        // Shared validator → same rule language as the bulk inspector (#7).
        var error = BuildValidator().Validate(memberVm.Model, newValue);

        memberVm.HasInlineError = error != null;
        memberVm.InlineErrorMessage = error;
        if (error != null)
            StatusText = $"{memberVm.Name}: {error}";
        else if (StatusText.StartsWith(memberVm.Name + ":"))
            StatusText = "";

        // Mirror the staged value to the store so tree rebuilds (active-set
        // transitions) can seed fresh VMs without losing this edit.
        _pendingEditStore.Set(memberVm.Model, newValue);

        // Pending edits no longer smart-expand ancestors (#10): they surface in the
        // sidebar, which is where the user looks for them. The tree's expansion
        // state stays under the user's control.

        RefreshPendingAndPreview();
        OnPropertyChanged(nameof(HasInlineErrors));
        Pending.RaiseInvalidPendingChanged();
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
    /// path leaves in other active DBs as excluded.
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
        // Clear the store first so SeedVmsFromStore can't revive discarded
        // edits if a tree rebuild happens before the next user gesture.
        _pendingEditStore.ClearAll();
        ClearAllPendingInlineEdits(RootMembers);
        RefreshPendingAndPreview();
        StatusText = Res.Get("Status_Ready");
        RefreshFlatList();
    }

    private void ClearAllPendingInlineEdits(IEnumerable<MemberNodeViewModel> nodes)
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
        var selectedPath = Selection.SelectedFlatMember?.Path;
        var selectedScopeAncestorPath = Selection.SelectedScope?.AncestorPath;
        var selectedScopeDepth = Selection.SelectedScope?.Depth;

        // Keep the most recent UDT resolvers so later RefreshTree calls (from Apply) don't drop them.
        if (udtResolver != null) _udtResolver = udtResolver;
        if (commentResolver != null) _commentResolver = commentResolver;
        var constantResolver = _tagTableCache != null
            ? new TagTableConstantResolver(_tagTableCache)
            : (IConstantResolver?)null;
        var parser = new SimaticMLParser(constantResolver, _udtResolver, _commentResolver);
        _active.Info = parser.Parse(modifiedXml);
        InlineRuleExtractor.ApplyTo(_configLoader.GetConfig(), _active.Info);

        // Parse produces new MemberNode instances for the active DB — the old
        // keys in the store are now dead. Clear the store before rebuilding so
        // SeedVmsFromStore doesn't populate fresh VMs with stale values from
        // the committed (and now applied) edit set.
        _pendingEditStore.ClearAll();

        // Tree.BuildRootMembersFromActiveDbs clears RootMembers + the lookup
        // dicts internally.
        Tree.BuildRootMembersFromActiveDbs();
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
            var restored = Tree.FlatMembers.FirstOrDefault(m => m.Path == selectedPath);
            if (restored != null)
            {
                // Silent setter — RefreshTree re-populates scopes manually
                // below (the slice would otherwise re-fire OnMemberSelected
                // which already ran once for this leaf during the restore
                // flow). The slice raises SelectedFlatMember / HasSelection /
                // SelectedMemberDisplay PropertyChanged itself.
                Selection.SetSelectedFlatMemberSilent(restored);

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
                    var owningDb = Tree.FindActiveDbForModel(restored.Model)?.Info ?? _active.Info;
                    var allInfos = AllActiveDbs.Select(a => a.Info).ToList();
                    result = _analyzer.AnalyzeMulti(allInfos, owningDb, restored.Model);
                }
                else
                {
                    result = _analyzer.Analyze(_active.Info, restored.Model);
                }
                Selection.AvailableScopes.Clear();
                foreach (var scope in result.Scopes.Reverse())
                    Selection.AvailableScopes.Add(scope);

                // #8: Match the previously selected scope by ancestor path, not
                // by index. Keeps the dropdown sticky through Apply even when
                // the scope count/order drifts.
                var restoredScope = Selection.AvailableScopes.FirstOrDefault(s =>
                    string.Equals(s.AncestorPath, selectedScopeAncestorPath, StringComparison.Ordinal)
                    && s.Depth == selectedScopeDepth);
                if (restoredScope != null)
                    Selection.SelectedScope = restoredScope;

                // Value is reset after Apply: the just-committed value would
                // misrepresent the current state (#8). Clear touched so the
                // next selection can prefill cleanly.
                Autocomplete.SuppressSuggestions = true;
                _newValueTouched = false;
                _newValue = "";
                OnPropertyChanged(nameof(NewValue));
                Autocomplete.SuppressSuggestions = false;
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
        bool wasManual = Selection.IsManualMode;
        bool changed = false;

        foreach (var m in addedList)
        {
            if (!m.IsLeaf) continue;
            if (Selection.AddManualPath(m)) changed = true;
        }

        if (!isFilterRehydration)
        {
            foreach (var m in removedList)
            {
                if (Selection.RemoveManualPath(m)) changed = true;
            }
        }

        if (!changed) return;

        bool isNowManual = Selection.IsManualMode;

        // The slice raises IsManualMode / ManualSelectionCount /
        // ManualSelectionSummary / IsSelectionTypeHomogeneous / HasScope /
        // CanEdit / SelectedMemberDisplay PropertyChanged; the
        // OnSelectionManualChanged handler bundles the host-composed
        // SetButtonText / SetButtonTooltip + RaiseCanExecuteChanged.
        Selection.RaiseManualSelectionChanged();

        // Entering manual mode: the scope-based highlighting from the single
        // selection no longer applies. Clear the scope state and wipe affected
        // rows + smart-expanded parents. (OnMemberSelected may have already
        // run before SelectionChanged fired, so we handle this here too.)
        if (!wasManual && isNowManual)
        {
            Selection.AvailableScopes.Clear();
            // Silent setter — UpdateHighlighting runs below; routing through
            // the public setter would fire ScopeChanged → UpdateHighlighting
            // twice.
            Selection.SetSelectedScopeSilent(null);
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
            OnMemberSelected(Selection.SelectedFlatMember);
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
        foreach (var node in Selection.ManualSelectedPaths)
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
        if (Selection.ManualSelectionCount == 0) return;
        Selection.ClearManualPaths();
        ClearBulkRowHighlights();
        _newValueTouched = false;
        _newValue = "";
        OnPropertyChanged(nameof(NewValue));

        // Slice raises the manual-set + scope-mode-related notifications;
        // OnSelectionManualChanged bundles SetButtonText / SetButtonTooltip /
        // RaiseCanExecuteChanged.
        Selection.RaiseManualSelectionChanged();

        ValidateValue();
        FlatListRefreshed?.Invoke();
    }

    // GetSelectedDatatypes() moved to SelectionScopeViewModel (slice 7b).

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _valueDebounceTimer?.Dispose();
        _valueDebounceTimer = null;

        // Slices own their own disposable state.
        Filter.Dispose();
        Subscription.Dispose();
    }
}
