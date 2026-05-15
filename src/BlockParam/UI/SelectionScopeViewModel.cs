using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BlockParam.Localization;
using BlockParam.Services;

namespace BlockParam.UI;

/// <summary>
/// Selection + scope + manual-selection slice (#80 slice 7b).
///
/// <para>
/// Owns the four pieces of user-selection state that drive the bulk-
/// edit dialog: the currently focused flat-member (<see cref="SelectedFlatMember"/>),
/// the active hierarchy scope picked from the dropdown
/// (<see cref="SelectedScope"/>), the list of available scopes the
/// analyzer produced (<see cref="AvailableScopes"/>), and the set of
/// leaves the user has manually multi-selected via Ctrl+Click
/// (<see cref="ManualSelectedPaths"/>).
/// </para>
///
/// <para>
/// <b>What stays on the host (orchestration).</b> The setter on
/// <see cref="SelectedFlatMember"/> raises the <see cref="MemberChanged"/>
/// event; the host subscribes and runs the (large) scope-analysis +
/// autocomplete-reload + new-value-prefill pipeline that used to live
/// in <c>OnMemberSelected</c>. Same shape for <see cref="SelectedScope"/>:
/// the slice raises <see cref="ScopeChanged"/>, the host runs
/// <c>UpdateHighlighting</c>. The host's <c>UpdateManualSelection</c> +
/// <c>ExecuteClearManualSelection</c> orchestrators read/write slice
/// state through the silent-write helpers (<see cref="SetSelectedFlatMemberSilent"/>,
/// <see cref="SetSelectedScopeSilent"/>, <see cref="AddManualPath"/>,
/// <see cref="RemoveManualPath"/>, <see cref="ClearManualPaths"/>) so
/// the cascade order matches pre-slice byte-for-byte.
/// </para>
///
/// <para>
/// <b>Why the orchestration stayed.</b> <c>OnMemberSelected</c> and
/// <c>UpdateManualSelection</c> each touch host-owned state on every
/// branch — <c>ValidationError</c>, <c>ConstraintInfo</c>, the
/// autocomplete provider, <c>_newValue</c>, the highlighting pass, the
/// flat-list refresh. Moving the orchestration into the slice would
/// require injecting eight or more callbacks, and the slice would be
/// 60% callback plumbing. The pragmatic cut keeps the slice as a
/// state container with focused event signals and lets the host walk
/// its existing side-effect graph from the event handler.
/// </para>
///
/// <para>
/// <b>Tree dependency.</b> The slice reads
/// <c>Tree.IsRefreshing</c> from the setter re-entry guard (the WPF
/// flat-list rebuild flips it to <c>true</c> mid-rebuild, and
/// <see cref="SelectedFlatMember"/>'s setter must short-circuit then).
/// It also calls <c>Tree.FindActiveDbForModel</c> indirectly through
/// the host's <see cref="MemberChanged"/> handler. <see cref="OnNodeSelected"/>
/// walks <c>Tree.RootMembers</c> to enforce the global single-focus
/// invariant (#95).
/// </para>
/// </summary>
public class SelectionScopeViewModel : ViewModelBase
{
    private readonly MemberTreeViewModel _tree;
    private MemberNodeViewModel? _selectedFlatMember;
    private ScopeLevel? _selectedScope;
    private readonly HashSet<MemberNodeViewModel> _manualSelectedPaths = new();
    private bool _inSelectionCascade;

    public SelectionScopeViewModel(MemberTreeViewModel tree)
    {
        _tree = tree;
        AvailableScopes = new ObservableCollection<ScopeLevel>();
    }

    /// <summary>
    /// Focused leaf in the flat ListView. Setter raises
    /// <see cref="MemberChanged"/>; host runs the scope-analysis +
    /// autocomplete-reload + new-value-prefill pipeline in the handler.
    /// Short-circuits while <c>Tree.IsRefreshing</c> to suppress ghost
    /// "removed items" events WPF raises mid-rebuild.
    /// </summary>
    public MemberNodeViewModel? SelectedFlatMember
    {
        get => _selectedFlatMember;
        set
        {
            if (_tree.IsRefreshing) return;
            if (SetProperty(ref _selectedFlatMember, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedMemberDisplay));
                MemberChanged?.Invoke(value);
            }
        }
    }

    /// <summary>
    /// Scope picked from the dropdown (or auto-selected by the
    /// host after scope analysis). Setter raises
    /// <see cref="ScopeChanged"/>; host runs <c>UpdateHighlighting</c>.
    /// </summary>
    public ScopeLevel? SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (SetProperty(ref _selectedScope, value))
            {
                OnPropertyChanged(nameof(HasScope));
                OnPropertyChanged(nameof(CanEdit));
                ScopeChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// Scope rows shown in the dropdown. Populated by the host's
    /// scope-analysis pipeline after the user picks a leaf.
    /// </summary>
    public ObservableCollection<ScopeLevel> AvailableScopes { get; }

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

    /// <summary>True iff a leaf is currently focused.</summary>
    public bool HasSelection => _selectedFlatMember is { IsLeaf: true };

    /// <summary>
    /// True when a scope is picked AND we're not in manual mode — manual
    /// mode supersedes scope state, so <see cref="SelectedScope"/> can
    /// linger from a prior selection without affecting Apply.
    /// </summary>
    public bool HasScope => _selectedScope != null && !IsManualMode;

    /// <summary>
    /// Bulk panel is visible when the user can edit — either scope mode
    /// (single selection) or manual mode (2+ selected).
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

    /// <summary>
    /// Status-line display string: manual-mode summary, or member name +
    /// datatype when a single leaf is focused, or the localized
    /// "click to select" placeholder otherwise.
    /// </summary>
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

    /// <summary>
    /// Raised after <see cref="SelectedFlatMember"/> changes. The host
    /// runs its <c>OnMemberSelected</c> orchestrator from this event
    /// (scope analysis, prefill, autocomplete reload, etc.).
    /// </summary>
    public event Action<MemberNodeViewModel?>? MemberChanged;

    /// <summary>
    /// Raised after <see cref="SelectedScope"/> changes. The host runs
    /// <c>UpdateHighlighting</c> from this event.
    /// </summary>
    public event Action? ScopeChanged;

    /// <summary>
    /// Raised after <see cref="_manualSelectedPaths"/> mutates (Add /
    /// Remove / Clear). The host re-raises composed properties
    /// (<c>CanEdit</c>, <c>SetButtonText</c>, etc.) from this event.
    /// </summary>
    public event Action? ManualSelectionChanged;

    /// <summary>
    /// Silent set of <see cref="SelectedFlatMember"/>: writes the
    /// backing field and raises the property-changed notifications, but
    /// does <i>not</i> fire <see cref="MemberChanged"/>. Used by the
    /// host's selection-restore code paths (<c>RefreshTree</c>,
    /// <c>RefreshFlatList</c>, <c>OnMemberSelected</c>'s dispatcher
    /// re-sync) that already know the side effects have been applied.
    /// </summary>
    public void SetSelectedFlatMemberSilent(MemberNodeViewModel? value)
    {
        if (SetProperty(ref _selectedFlatMember, value, nameof(SelectedFlatMember)))
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedMemberDisplay));
        }
    }

    /// <summary>
    /// Silent set of <see cref="SelectedScope"/>: writes backing field +
    /// raises PropertyChanged but suppresses the <see cref="ScopeChanged"/>
    /// event. Used by <c>OnMemberSelected</c> when it clears scope state
    /// (the same method then re-populates and selects, so a stray cascade
    /// from the clear would double-run highlighting).
    /// </summary>
    public void SetSelectedScopeSilent(ScopeLevel? value)
    {
        if (SetProperty(ref _selectedScope, value, nameof(SelectedScope)))
        {
            OnPropertyChanged(nameof(HasScope));
            OnPropertyChanged(nameof(CanEdit));
        }
    }

    /// <summary>
    /// Adds a manually-selected leaf. Returns <c>true</c> if the path was
    /// new (i.e. the set actually changed). Caller is responsible for
    /// raising <see cref="ManualSelectionChanged"/> once per batch of
    /// add/remove ops via <see cref="RaiseManualSelectionChanged"/>.
    /// </summary>
    public bool AddManualPath(MemberNodeViewModel node) => _manualSelectedPaths.Add(node);

    /// <summary>
    /// Removes a manually-selected leaf. Returns <c>true</c> if the path
    /// was present (i.e. the set actually changed).
    /// </summary>
    public bool RemoveManualPath(MemberNodeViewModel node) => _manualSelectedPaths.Remove(node);

    /// <summary>
    /// Clears the manual-selection set. No-op if already empty. Always
    /// silent; caller raises <see cref="ManualSelectionChanged"/> once if
    /// the wider clear gesture had other side effects to bundle.
    /// </summary>
    public void ClearManualPaths() => _manualSelectedPaths.Clear();

    /// <summary>
    /// Raises <see cref="ManualSelectionChanged"/> plus the derived
    /// property notifications that hang off the manual-paths count.
    /// </summary>
    public void RaiseManualSelectionChanged()
    {
        OnPropertyChanged(nameof(IsManualMode));
        OnPropertyChanged(nameof(ManualSelectionCount));
        OnPropertyChanged(nameof(ManualSelectionSummary));
        OnPropertyChanged(nameof(IsSelectionTypeHomogeneous));
        OnPropertyChanged(nameof(HasScope));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(SelectedMemberDisplay));
        ManualSelectionChanged?.Invoke();
    }

    /// <summary>
    /// Global single-focus invariant (#95): when one node's
    /// <c>MemberNodeViewModel.IsSelected</c> becomes true, every other
    /// node in <c>Tree.RootMembers</c> drops its selection — so the
    /// dialog never sees a leaf selected in DB A *and* a leaf selected in
    /// DB B at the same time. Wired up by the host through
    /// <c>SubscribeStartValueEdited</c>'s <c>SelectedChanged += OnNodeSelected</c>.
    /// </summary>
    public void OnNodeSelected(MemberNodeViewModel justSelected)
    {
        // Guard: synthetic DB-group roots (Datatype == "DB") are tree
        // structural nodes, not real members. If a future feature ever sets
        // IsSelected = true on one (e.g. "select-all-in-DB" gesture or a
        // keyboard shortcut on the DB header), the cascade below would clear
        // IsSelected on every real leaf across every DB — the exact opposite
        // of what the user would expect. Bail early so synthetic roots are
        // inert from the single-focus perspective. (#123)
        if (justSelected.Datatype == "DB") return;

        if (_inSelectionCascade) return;
        _inSelectionCascade = true;
        try
        {
            foreach (var root in _tree.RootMembers)
            {
                if (!ReferenceEquals(root, justSelected) && root.IsSelected)
                    root.IsSelected = false;
                foreach (var descendant in root.AllDescendants())
                {
                    if (!ReferenceEquals(descendant, justSelected) && descendant.IsSelected)
                        descendant.IsSelected = false;
                }
            }
        }
        finally
        {
            _inSelectionCascade = false;
        }
    }

    /// <summary>
    /// Set of distinct <c>Datatype</c> strings across the currently
    /// manually-selected leaves. Empty when there are no manual
    /// selections. Used by <see cref="ManualSelectionSummary"/> and
    /// <see cref="IsSelectionTypeHomogeneous"/>, and exposed for the
    /// host's manual-mode validation pipeline.
    /// </summary>
    public IReadOnlyCollection<string> GetSelectedDatatypes()
    {
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in _manualSelectedPaths)
        {
            if (node.IsLeaf)
                types.Add(node.Datatype);
        }
        return types;
    }
}
