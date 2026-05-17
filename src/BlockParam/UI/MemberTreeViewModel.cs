using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using BlockParam.Models;

namespace BlockParam.UI;

/// <summary>
/// Tree-shape + flat-list + expand/collapse slice (#80 slice 7a).
///
/// <para>
/// Owns the <see cref="RootMembers"/> collection that backs the dialog's
/// tree, the flat-list projection used by the WPF
/// <c>ListView</c>/<c>GridView</c>, the three <c>MemberNode</c>/
/// <c>ActiveDb</c> lookup dictionaries that route writes back to the
/// right tree VM, and the expand-all / collapse-all commands.
/// </para>
///
/// <para>
/// <b>What stays on the host (slice 7b territory):</b> selection state
/// (<c>SelectedFlatMember</c>, <c>SelectedScope</c>, <c>AvailableScopes</c>,
/// <c>_manualSelectedPaths</c>), scope analysis (<c>OnMemberSelected</c>),
/// manual-selection state machine (<c>UpdateManualSelection</c>), and the
/// selection-restore wrapper around <see cref="RebuildFlatList"/>. The
/// host's <c>RefreshFlatList</c> calls <see cref="RebuildFlatList"/> in
/// between save-selection and restore-selection steps.
/// </para>
///
/// <para>
/// <b>Dependency on slice 8 (active-set) state:</b> the slice doesn't own
/// the active DB set — it takes <c>getActiveDbs</c> / <c>getCurrentPlcName</c>
/// callbacks so multi-DB tree shape stays correct without the slice
/// holding its own copy. When slice 8 lands these callbacks reduce to
/// reading one composed VM.
/// </para>
///
/// <para>
/// <b>Why no pending-store dependency:</b> the pending-store seeding after
/// a tree rebuild is host-side (<c>SeedVmsFromStore</c> reads
/// <c>Tree.ModelToVm</c> + the host's <c>_pendingEditStore</c>). The slice
/// fires <see cref="RootsRebuilt"/> after <see cref="BuildRootMembersFromActiveDbs"/>
/// so the host can run its post-rebuild hooks (seeding + validation)
/// without the slice depending on the store.
/// </para>
/// </summary>
public class MemberTreeViewModel : ViewModelBase
{
    private readonly FlatTreeManager _flatTreeManager = new();
    private readonly Func<IReadOnlyList<ActiveDb>> _getActiveDbs;
    private readonly Func<string> _getCurrentPlcName;
    private readonly CommentLanguagePolicy _commentLanguagePolicy;
    /// <summary>
    /// Per-node subscription callback the host wires for inline-edit
    /// (<c>StartValueEdited</c>) and selection (<c>SelectedChanged</c>) events.
    ///
    /// <para>
    /// <b>Non-recursive by contract (#108).</b> The slice invokes this callback
    /// once per <see cref="MemberNodeViewModel"/> it mints — both for top-level
    /// roots and every descendant — and the host implementation must register
    /// handlers on the passed node only, never recurse into <c>node.Children</c>.
    /// A previous recursive host doubled (then N-tupled, by depth) the
    /// handler registrations on every non-leaf descendant in multi-DB mode
    /// because <see cref="BuildDbGroupRoot"/> already walks every descendant.
    /// </para>
    /// </summary>
    private readonly Action<MemberNodeViewModel> _subscribeToVm;

    // #79: the three model→VM / model→DB / DB→synthetic dictionaries are
    // owned by a single immutable snapshot. Every tree rebuild builds the
    // snapshot in locals and installs it in one atomic assignment via
    // <see cref="BuildTreeIndex"/>, so readers never observe a partial state
    // where roots have been minted but a descendant is still missing from
    // ModelToVm, or where DbToSynthetic still has keys from the prior tree.
    private TreeIndexState _treeIndex = TreeIndexState.Empty;

    private bool _isRefreshing;
    // #122: backing field for RootMembers so BuildRootMembersFromActiveDbs can
    // swap the entire collection in one assignment and raise a single
    // PropertyChanged — instead of Clear() + N×Add() firing N+1 CollectionChanged
    // events while _treeIndex is already on the new snapshot.
    private ObservableCollection<MemberNodeViewModel> _rootMembers;

    public MemberTreeViewModel(
        Func<IReadOnlyList<ActiveDb>> getActiveDbs,
        Func<string> getCurrentPlcName,
        CommentLanguagePolicy commentLanguagePolicy,
        Action<MemberNodeViewModel> subscribeToVm)
    {
        _getActiveDbs = getActiveDbs;
        _getCurrentPlcName = getCurrentPlcName;
        _commentLanguagePolicy = commentLanguagePolicy;
        _subscribeToVm = subscribeToVm;

        _rootMembers = new ObservableCollection<MemberNodeViewModel>();
        ExpandAllCommand = new RelayCommand(ExecuteExpandAll);
        CollapseAllCommand = new RelayCommand(ExecuteCollapseAll);
    }

    /// <summary>
    /// Top-level tree VMs. Single-DB session = one entry per top-level
    /// member of the anchor DB; multi-DB session = one synthetic
    /// <c>Datatype="DB"</c> group per active DB whose children are that
    /// DB's real members.
    ///
    /// <para>
    /// Backed by a writable field so <see cref="BuildRootMembersFromActiveDbs"/>
    /// can swap to a fresh <see cref="ObservableCollection{T}"/> in one
    /// assignment, raising a single <c>PropertyChanged</c> instead of
    /// N+1 <c>CollectionChanged</c> events while the new index is already
    /// installed (#122).
    /// </para>
    /// </summary>
    public ObservableCollection<MemberNodeViewModel> RootMembers
    {
        get => _rootMembers;
        private set => SetProperty(ref _rootMembers, value);
    }

    /// <summary>
    /// Flat-list projection of <see cref="RootMembers"/> respecting current
    /// expand/collapse + visibility state. Bound by the <c>ListView</c>.
    /// </summary>
    public ObservableCollection<MemberNodeViewModel> FlatMembers => _flatTreeManager.FlatList;

    /// <summary>
    /// True while <see cref="RebuildFlatList"/> is in flight. Read by the
    /// dialog's <c>SelectionChanged</c> handler in code-behind to ignore
    /// ghost "removed items" events WPF raises when the <c>ItemsSource</c>
    /// is mutated.
    /// </summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    /// <summary>
    /// Immutable snapshot of the three model→VM / model→DB / DB→synthetic
    /// lookup dictionaries (#79). Assigned exactly once per tree rebuild;
    /// callers can capture the reference and read it concurrently with a
    /// subsequent rebuild without observing a partial state.
    /// </summary>
    public TreeIndexState TreeIndex => _treeIndex;

    /// <summary>
    /// <c>MemberNode</c> → owning tree VM. O(1) replacement for any
    /// path-string walk; unambiguous in multi-DB sessions where the same
    /// path string can appear in multiple DBs (#82). Backed by
    /// <see cref="TreeIndex"/>.
    /// </summary>
    public IReadOnlyDictionary<MemberNode, MemberNodeViewModel> ModelToVm => _treeIndex.ModelToVm;

    /// <summary>
    /// <c>MemberNode</c> → owning <c>ActiveDb</c>. Used by the host's
    /// pending-edit store + multi-DB scope routing to write back to the
    /// right tree / xml. Backed by <see cref="TreeIndex"/>.
    /// </summary>
    public IReadOnlyDictionary<MemberNode, ActiveDb> ModelToDb => _treeIndex.ModelToDb;

    /// <summary>
    /// <c>ActiveDb</c> → its synthetic group root in the multi-DB tree.
    /// Empty in single-DB sessions (the slice routes those callers
    /// through <see cref="RootMembers"/> directly). Used by
    /// <see cref="FindNodeByPathInDb"/> and by the host's
    /// <c>ApplyAllFilters</c> to route per-DB filter sets to the correct
    /// subtree. Backed by <see cref="TreeIndex"/>.
    /// </summary>
    public IReadOnlyDictionary<ActiveDb, MemberNodeViewModel> DbToSynthetic => _treeIndex.DbToSynthetic;

    public ICommand ExpandAllCommand { get; }
    public ICommand CollapseAllCommand { get; }

    /// <summary>
    /// Raised once <see cref="BuildRootMembersFromActiveDbs"/> has rebuilt
    /// <see cref="RootMembers"/> + the lookup dictionaries. The host
    /// subscribes to re-seed pending-edit state, refresh rule hints,
    /// rebuild existing-issues, and similar post-rebuild hooks.
    /// </summary>
    public event Action? RootsRebuilt;

    /// <summary>
    /// Rebuilds <see cref="RootMembers"/> + the <see cref="TreeIndex"/>
    /// snapshot from the current active-DB set. Single-DB session puts
    /// top-level members of the anchor DB directly at root; multi-DB
    /// session creates one synthetic group per DB. Fires
    /// <see cref="RootsRebuilt"/> once <see cref="RootMembers"/> and
    /// <see cref="_treeIndex"/> are both fully populated — readers in the
    /// handler observe a consistent tree (#79).
    /// </summary>
    public void BuildRootMembersFromActiveDbs()
    {
        var activeDbs = _getActiveDbs();
        var anchorPlc = _getCurrentPlcName();

        // Build the new roots + new index snapshot into local builders. No
        // mutation of _treeIndex or RootMembers happens until the walk is
        // complete, so a subscribe callback that touches MemberTreeViewModel
        // mid-build (e.g. a property-changed handler) still sees the prior
        // consistent snapshot.
        var (newRoots, newIndex) = BuildTreeIndex(
            activeDbs, anchorPlc, _commentLanguagePolicy, _subscribeToVm);

        // Atomic install: replace the index in one assignment, then swap
        // RootMembers to a fresh ObservableCollection and raise a single
        // PropertyChanged. Order matters: any handler wired to
        // RootMembers.CollectionChanged (on the old collection) or
        // PropertyChanged(nameof(RootMembers)) would read TreeIndex via
        // ModelToVm — assign the index first so those reads observe the
        // new state. Using a fresh collection instead of Clear()+N×Add()
        // collapses N+1 CollectionChanged events into one PropertyChanged
        // event, closing the partial-state seam where a handler could
        // observe the new index but an incomplete RootMembers list (#122).
        _treeIndex = newIndex;
        RootMembers = new ObservableCollection<MemberNodeViewModel>(newRoots);

        RootsRebuilt?.Invoke();
    }

    /// <summary>
    /// Pure tree-build (#79 Phase 2): consumes the current active-DB set +
    /// anchor PLC name and produces the new top-level VM list paired with a
    /// fresh <see cref="TreeIndexState"/>. No side effects on
    /// <see cref="MemberTreeViewModel"/> state — callers install the result
    /// atomically.
    ///
    /// <para>
    /// The per-VM <paramref name="subscribeToVm"/> callback is invoked
    /// during the walk (non-recursive contract, #108). That's the only
    /// observable side effect — every other store touched here is a local
    /// builder.
    /// </para>
    /// </summary>
    private static (List<MemberNodeViewModel> Roots, TreeIndexState Index) BuildTreeIndex(
        IReadOnlyList<ActiveDb> activeDbs,
        string anchorPlc,
        CommentLanguagePolicy commentLanguagePolicy,
        Action<MemberNodeViewModel> subscribeToVm)
    {
        var roots = new List<MemberNodeViewModel>();
        var modelToVm = new Dictionary<MemberNode, MemberNodeViewModel>();
        var modelToDb = new Dictionary<MemberNode, ActiveDb>();
        var dbToSynthetic = new Dictionary<ActiveDb, MemberNodeViewModel>();

        if (activeDbs.Count == 1)
        {
            // Single-DB: flat list of top-level members, identical to legacy.
            var only = activeDbs[0];
            foreach (var member in only.Info.Members)
            {
                var vm = new MemberNodeViewModel(member, null, commentLanguagePolicy);
                // #108: subscribeToVm is non-recursive by contract, so the
                // builder walks the subtree itself. Subscribe the root, then
                // every descendant — same per-node fan-out as the multi-DB
                // path in BuildDbGroupRoot.
                subscribeToVm(vm);
                foreach (var descendant in vm.AllDescendants())
                    subscribeToVm(descendant);
                IndexSubtree(vm, only, modelToVm, modelToDb);
                roots.Add(vm);
            }
        }
        else if (activeDbs.Count > 1)
        {
            // Multi-DB: one synthetic group node per DB. Children are the DB's
            // real top-level members, reused by reference — Path strings stay
            // unchanged, so existing rule patterns / scope-detection on member
            // paths still match across DBs.
            // Cross-PLC name collision: two PLCs can each host a DB called
            // "DB_Foo". Tag any colliding synthetic root with its PLC prefix so
            // the user can tell them apart in the tree. The collision-safe
            // string is computed by the single shared formatter so the Pending
            // Edits row label (#145) matches this header exactly — never
            // duplicate this rule (see ActiveDbDisplayName). The collision
            // map is built once here, not per DB.
            var displayNames = new ActiveDbDisplayName(activeDbs, anchorPlc);
            for (int i = 0; i < activeDbs.Count; i++)
            {
                var db = activeDbs[i];
                var displayName = displayNames.Resolve(db);
                var groupVm = BuildDbGroupRoot(
                    db, displayName, commentLanguagePolicy, subscribeToVm,
                    modelToVm, modelToDb);
                dbToSynthetic[db] = groupVm;
                roots.Add(groupVm);
            }
        }
        // activeDbs.Count == 0: roots/index stay empty — same as legacy
        // (the old method cleared the dicts and never populated them).

        return (roots, new TreeIndexState(modelToVm, modelToDb, dbToSynthetic));
    }

    private static MemberNodeViewModel BuildDbGroupRoot(
        ActiveDb db,
        string displayName,
        CommentLanguagePolicy commentLanguagePolicy,
        Action<MemberNodeViewModel> subscribeToVm,
        Dictionary<MemberNode, MemberNodeViewModel> modelToVm,
        Dictionary<MemberNode, ActiveDb> modelToDb)
    {
        var info = db.Info;
        var synthetic = new MemberNode(
            name: displayName,
            datatype: "DB",
            startValue: null,
            path: info.Name,
            parent: null,
            children: info.Members);
        var groupVm = new MemberNodeViewModel(synthetic, null, commentLanguagePolicy);
        // Subscribe edited-value events on every minted VM (the synthetic
        // group root + every real descendant) so inline edits in any active
        // DB bubble up to the VM the same way single-DB edits do. The
        // synthetic group's StartValue is null and never raises the event,
        // but subscribing it keeps the "one callback per minted VM" contract
        // simple — see #108.
        subscribeToVm(groupVm);
        foreach (var descendant in groupVm.AllDescendants())
            subscribeToVm(descendant);
        groupVm.IsExpanded = true;
        // Map every real (non-synthetic) descendant to its VM + owning DB so
        // multi-DB scope hits route writes to the right tree / xml.
        foreach (var descendant in groupVm.AllDescendants())
        {
            // Skip the synthetic group node itself — its Model has Datatype="DB"
            // and isn't a real member. Identifying it by reference equality
            // against the synthetic instance avoids relying on Datatype string
            // matching, which is fragile.
            if (ReferenceEquals(descendant.Model, synthetic)) continue;
            modelToVm[descendant.Model] = descendant;
            modelToDb[descendant.Model] = db;
        }
        return groupVm;
    }

    /// <summary>
    /// Indexes every node in <paramref name="rootVm"/>'s subtree (root
    /// included) into the supplied builder dicts. Used by the single-DB
    /// code path; multi-DB indexes inside <see cref="BuildDbGroupRoot"/>
    /// with the synthetic-skip rule.
    /// </summary>
    private static void IndexSubtree(
        MemberNodeViewModel rootVm,
        ActiveDb db,
        Dictionary<MemberNode, MemberNodeViewModel> modelToVm,
        Dictionary<MemberNode, ActiveDb> modelToDb)
    {
        modelToVm[rootVm.Model] = rootVm;
        modelToDb[rootVm.Model] = db;
        foreach (var descendant in rootVm.AllDescendants())
        {
            modelToVm[descendant.Model] = descendant;
            modelToDb[descendant.Model] = db;
        }
    }

    /// <summary>
    /// Rebuilds <see cref="FlatMembers"/> from the current
    /// <see cref="RootMembers"/> tree state. Re-entry is silently
    /// ignored — the cascade that triggered the inner call is mid-flight
    /// and will refresh again when it returns.
    ///
    /// <para>
    /// <paramref name="insideRefreshScope"/> is invoked after the flat
    /// list has been rebuilt but while <see cref="IsRefreshing"/> is
    /// still <c>true</c>. The host uses this hook to restore selection +
    /// raise its <c>FlatListRefreshed</c> event without exposing a
    /// separate "set IsRefreshing manually" API on the slice. Code-behind
    /// gates <c>SelectionChanged</c> on <see cref="IsRefreshing"/> to
    /// suppress ghost row drops while the <c>ItemsSource</c> is mutating.
    /// </para>
    /// </summary>
    public void RebuildFlatList(Action? insideRefreshScope = null)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            _flatTreeManager.Refresh(RootMembers);
            insideRefreshScope?.Invoke();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>
    /// Toggles expand/collapse for a single node and refreshes the flat
    /// list so the visible rows reflect the new state.
    /// </summary>
    public void ToggleExpand(MemberNodeViewModel node)
    {
        _flatTreeManager.ToggleExpand(node, RootMembers);
    }

    /// <summary>
    /// Expands <paramref name="node"/> and every descendant, then rebuilds
    /// the flat list.
    /// </summary>
    public void ExpandAllChildren(MemberNodeViewModel node)
    {
        FlatTreeManager.ExpandAllChildren(node);
        RebuildFlatList();
    }

    /// <summary>
    /// Collapses <paramref name="node"/> and every descendant, then rebuilds
    /// the flat list.
    /// </summary>
    public void CollapseAllChildren(MemberNodeViewModel node)
    {
        FlatTreeManager.CollapseAllChildren(node);
        RebuildFlatList();
    }

    private void ExecuteExpandAll()
    {
        FlatTreeManager.ExpandAll(RootMembers);
        RebuildFlatList();
    }

    private void ExecuteCollapseAll()
    {
        FlatTreeManager.CollapseAll(RootMembers);
        RebuildFlatList();
    }

    /// <summary>
    /// O(1) resolve of a <see cref="MemberNode"/> to its tree VM via
    /// <see cref="ModelToVm"/>. Preferred over any path-string lookup
    /// when the caller already has the model — same path string in a
    /// different DB is a different model instance, so this disambiguates
    /// naturally (#82).
    /// </summary>
    public MemberNodeViewModel? FindVmByModel(MemberNode model) =>
        _treeIndex.ModelToVm.TryGetValue(model, out var vm) ? vm : null;

    /// <summary>
    /// Returns the active DB that owns <paramref name="model"/>, or null
    /// when the node is not part of the current tree (e.g. a stale model
    /// from before the last <c>RefreshTree</c>).
    /// </summary>
    public ActiveDb? FindActiveDbForModel(MemberNode model) =>
        _treeIndex.ModelToDb.TryGetValue(model, out var db) ? db : null;

    /// <summary>
    /// DB-scoped path lookup (#82). The bare-path overload was removed
    /// because path strings aren't unique across DBs in multi-DB
    /// sessions — every caller now passes the owning <see cref="ActiveDb"/>
    /// so the resolution is unambiguous regardless of tree shape.
    ///
    /// Works in both tree shapes:
    /// <list type="bullet">
    ///   <item>Multi-DB shape: walks <paramref name="owner"/>'s synthetic
    ///   subtree via <see cref="DbToSynthetic"/>.</item>
    ///   <item>Single-DB shape: walks <see cref="RootMembers"/> directly
    ///   when <paramref name="owner"/> is the (one) active DB. Anything
    ///   else returns null instead of falling through to a different DB —
    ///   that's exactly the cross-DB aliasing this method exists to prevent.</item>
    /// </list>
    /// </summary>
    public MemberNodeViewModel? FindNodeByPathInDb(string path, ActiveDb owner)
    {
        // Capture the snapshot once so we read DbToSynthetic and the
        // dependent walk against the same _treeIndex (#79).
        var index = _treeIndex;
        if (index.DbToSynthetic.TryGetValue(owner, out var synthetic))
        {
            foreach (var child in synthetic.Children)
            {
                var found = FindNodeByPathRecursive(child, path);
                if (found != null) return found;
            }
            return null;
        }

        // Single-DB shape: only resolve when owner is the sole active DB.
        // The lookup dictionaries already pin every model to its owning DB,
        // so we use the active-DB callback as the authoritative "is owner
        // active?" check without touching the active-set slice.
        var activeDbs = _getActiveDbs();
        if (activeDbs.Count == 1 && ReferenceEquals(activeDbs[0], owner))
        {
            foreach (var root in RootMembers)
            {
                var found = FindNodeByPathRecursive(root, path);
                if (found != null) return found;
            }
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
}
