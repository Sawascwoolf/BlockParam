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
    /// because <see cref="AddDbGroupRoot"/> already walks every descendant.
    /// </para>
    /// </summary>
    private readonly Action<MemberNodeViewModel> _subscribeToVm;

    private readonly Dictionary<MemberNode, MemberNodeViewModel> _modelToVm = new();
    private readonly Dictionary<MemberNode, ActiveDb> _modelToDb = new();
    private readonly Dictionary<ActiveDb, MemberNodeViewModel> _dbToSynthetic = new();

    private bool _isRefreshing;

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

        RootMembers = new ObservableCollection<MemberNodeViewModel>();
        ExpandAllCommand = new RelayCommand(ExecuteExpandAll);
        CollapseAllCommand = new RelayCommand(ExecuteCollapseAll);
    }

    /// <summary>
    /// Top-level tree VMs. Single-DB session = one entry per top-level
    /// member of the anchor DB; multi-DB session = one synthetic
    /// <c>Datatype="DB"</c> group per active DB whose children are that
    /// DB's real members.
    /// </summary>
    public ObservableCollection<MemberNodeViewModel> RootMembers { get; }

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
    /// <c>MemberNode</c> → owning tree VM. O(1) replacement for the
    /// path-string walk in <see cref="FindNodeByPath"/>; unambiguous in
    /// multi-DB sessions where the same path string can appear in
    /// multiple DBs.
    /// </summary>
    public IReadOnlyDictionary<MemberNode, MemberNodeViewModel> ModelToVm => _modelToVm;

    /// <summary>
    /// <c>MemberNode</c> → owning <c>ActiveDb</c>. Used by the host's
    /// pending-edit store + multi-DB scope routing to write back to the
    /// right tree / xml.
    /// </summary>
    public IReadOnlyDictionary<MemberNode, ActiveDb> ModelToDb => _modelToDb;

    /// <summary>
    /// <c>ActiveDb</c> → its synthetic group root in the multi-DB tree.
    /// Empty in single-DB sessions. Used by <see cref="FindNodeByPathInDb"/>
    /// and by the host's <c>ApplyAllFilters</c> to route per-DB filter
    /// sets to the correct subtree.
    /// </summary>
    public IReadOnlyDictionary<ActiveDb, MemberNodeViewModel> DbToSynthetic => _dbToSynthetic;

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
    /// Rebuilds <see cref="RootMembers"/> + the three lookup dictionaries
    /// from the current active-DB set. Single-DB session puts top-level
    /// members of the anchor DB directly at root; multi-DB session creates
    /// one synthetic group per DB. Fires <see cref="RootsRebuilt"/> when
    /// the new shape is ready.
    /// </summary>
    public void BuildRootMembersFromActiveDbs()
    {
        // Always rebuild the routing dictionaries so every model node is
        // mapped to its current VM + owning DB. Stale entries from a prior
        // tree would route writes to disposed VMs.
        _modelToVm.Clear();
        _modelToDb.Clear();
        _dbToSynthetic.Clear();
        RootMembers.Clear();

        var activeDbs = _getActiveDbs();

        if (activeDbs.Count == 1)
        {
            // Single-DB: flat list of top-level members, identical to legacy.
            var only = activeDbs[0];
            foreach (var member in only.Info.Members)
            {
                var vm = new MemberNodeViewModel(member, null, _commentLanguagePolicy);
                // #108: _subscribeToVm is non-recursive by contract, so the
                // slice walks the subtree itself. Subscribe the root, then
                // every descendant — same per-node fan-out as the multi-DB
                // path in AddDbGroupRoot.
                _subscribeToVm(vm);
                foreach (var descendant in vm.AllDescendants())
                    _subscribeToVm(descendant);
                IndexSubtree(vm, only);
                RootMembers.Add(vm);
            }
        }
        else
        {
            // Multi-DB: one synthetic group node per DB. Children are the DB's
            // real top-level members, reused by reference — Path strings stay
            // unchanged, so existing rule patterns / scope-detection on member
            // paths still match across DBs.
            // Cross-PLC name collision: two PLCs can each host a DB called
            // "DB_Foo". Tag any colliding synthetic root with its PLC prefix so
            // the user can tell them apart in the tree.
            var anchorPlc = _getCurrentPlcName();
            var nameCounts = activeDbs
                .GroupBy(d => d.Info.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            for (int i = 0; i < activeDbs.Count; i++)
            {
                var db = activeDbs[i];
                var plc = i == 0 ? anchorPlc : db.PlcName;
                bool collides = nameCounts.TryGetValue(db.Info.Name, out var c) && c > 1;
                var displayName = collides && !string.IsNullOrEmpty(plc)
                    ? $"{plc} / {db.Info.Name}"
                    : db.Info.Name;
                AddDbGroupRoot(db, displayName);
            }
        }

        RootsRebuilt?.Invoke();
    }

    private void AddDbGroupRoot(ActiveDb db, string displayName)
    {
        var info = db.Info;
        var synthetic = new MemberNode(
            name: displayName,
            datatype: "DB",
            startValue: null,
            path: info.Name,
            parent: null,
            children: info.Members);
        var groupVm = new MemberNodeViewModel(synthetic, null, _commentLanguagePolicy);
        // Subscribe edited-value events on every minted VM (the synthetic
        // group root + every real descendant) so inline edits in any active
        // DB bubble up to the VM the same way single-DB edits do. The
        // synthetic group's StartValue is null and never raises the event,
        // but subscribing it keeps the "one callback per minted VM" contract
        // simple — see #108.
        _subscribeToVm(groupVm);
        foreach (var descendant in groupVm.AllDescendants())
            _subscribeToVm(descendant);
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
    /// <see cref="ModelToVm"/>. Preferred over <see cref="FindNodeByPath"/>
    /// when the caller already has the model — same path string in a
    /// different DB is a different model instance, so this disambiguates
    /// naturally.
    /// </summary>
    public MemberNodeViewModel? FindVmByModel(MemberNode model) =>
        _modelToVm.TryGetValue(model, out var vm) ? vm : null;

    /// <summary>
    /// Returns the active DB that owns <paramref name="model"/>, or null
    /// when the node is not part of the current tree (e.g. a stale model
    /// from before the last <c>RefreshTree</c>).
    /// </summary>
    public ActiveDb? FindActiveDbForModel(MemberNode model) =>
        _modelToDb.TryGetValue(model, out var db) ? db : null;

    /// <summary>
    /// Walks <see cref="RootMembers"/> for a node with the given
    /// <paramref name="path"/>. In multi-DB sessions prefer
    /// <see cref="FindNodeByPathInDb"/> — bare path lookup can hit any DB
    /// that happens to share the path string.
    /// </summary>
    public MemberNodeViewModel? FindNodeByPath(string path)
    {
        foreach (var root in RootMembers)
        {
            var found = FindNodeByPathRecursive(root, path);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// DB-scoped variant of <see cref="FindNodeByPath"/>. Callers pass this
    /// when member Paths can repeat across DBs (the stash holds the bare
    /// member path, not "DbName.MemberPath") — without scoping, a
    /// reactivate-additive would replay edits onto whichever DB happened
    /// to be checked first.
    ///
    /// Strict: callers must only invoke this when the active set is in
    /// multi-DB shape (i.e. after the State cascade has populated
    /// <see cref="_dbToSynthetic"/> for <paramref name="owner"/>). If the
    /// synthetic root isn't found, returns null — a future ordering bug
    /// surfaces as "stashed edit dropped" instead of silently aliasing
    /// onto another DB. (The diagnostic log call lives on the host;
    /// the slice intentionally has no <c>Serilog</c> dependency.)
    /// </summary>
    public MemberNodeViewModel? FindNodeByPathInDb(string path, ActiveDb owner)
    {
        if (!_dbToSynthetic.TryGetValue(owner, out var synthetic))
            return null;
        foreach (var child in synthetic.Children)
        {
            var found = FindNodeByPathRecursive(child, path);
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
}
