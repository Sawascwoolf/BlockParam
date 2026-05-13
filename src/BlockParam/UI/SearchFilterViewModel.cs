using System.Threading;
using System.Windows.Threading;
using BlockParam.Localization;
using BlockParam.Models;

namespace BlockParam.UI;

/// <summary>
/// Search + tree-filter slice (#80 slice 6).
///
/// <para>
/// Owns the search box state (<see cref="SearchQuery"/> with its 200&#160;ms
/// debounce timer, <see cref="SearchHitCount"/>, <see cref="HasSearchQuery"/>),
/// the rule-filter banner counters
/// (<see cref="HiddenByRuleCount"/> /
/// <see cref="ShowRuleFilterBanner"/> / <see cref="RuleFilterBannerText"/>),
/// and the SetPoint-only toggle plus its capability/tooltip properties
/// (<see cref="ShowSetpointsOnly"/>, <see cref="CanShowSetpointsOnly"/>,
/// <see cref="ShowSetpointsOnlyTooltip"/>).
/// </para>
///
/// <para>
/// The actual filter application stays on the host VM —
/// <c>ApplyAllFilters()</c> + <c>RefreshFlatList()</c> walk the multi-DB
/// tree and remain too entangled with host state (DB→synthetic-root map,
/// rule loader, exclude-set builder) to migrate without a much larger
/// change. The slice fires a single <c>onFiltersChanged</c> callback for
/// every state transition that requires the tree to re-filter
/// (debounced search keystroke, SetPoint toggle, scripted flush), and an
/// optional <c>onSetpointsTurnedOn</c> callback so the host can refresh
/// the UDT cache on the OFF→ON transition before the filter pass runs.
/// </para>
///
/// <para>
/// <see cref="ShowConstants"/> is intentionally <i>not</i> here even
/// though the issue body lists it under this slice: it does not affect
/// the tree filter, only the autocomplete suggestion list, and the
/// natural home is the autocomplete slice in a future touch-up.
/// </para>
/// </summary>
public class SearchFilterViewModel : ViewModelBase, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<DataBlockInfo> _getAnchorInfo;
    private readonly bool _hasUdtRefresh;
    private readonly Action _onFiltersChanged;
    private readonly Action? _onSetpointsTurnedOn;

    private string _searchQuery = "";
    private int _searchHitCount;
    private int _hiddenByRuleCount;
    private bool _showSetpointsOnly;
    private Timer? _searchDebounceTimer;
    private bool _disposed;

    public SearchFilterViewModel(
        Dispatcher dispatcher,
        Func<DataBlockInfo> getAnchorInfo,
        bool hasUdtRefresh,
        Action onFiltersChanged,
        Action? onSetpointsTurnedOn = null)
    {
        _dispatcher = dispatcher;
        _getAnchorInfo = getAnchorInfo;
        _hasUdtRefresh = hasUdtRefresh;
        _onFiltersChanged = onFiltersChanged;
        _onSetpointsTurnedOn = onSetpointsTurnedOn;
    }

    /// <summary>
    /// Search box text. Setter debounces 200&#160;ms before firing
    /// <c>onFiltersChanged</c>; WPF's <c>Binding.Delay</c> uses a
    /// <c>DispatcherTimer</c> that doesn't fire reliably inside TIA Portal's
    /// WinForms host, hence the explicit <see cref="Timer"/>.
    /// </summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                OnPropertyChanged(nameof(HasSearchQuery));
                _searchDebounceTimer?.Dispose();
                _searchDebounceTimer = new Timer(_ =>
                {
                    _dispatcher.BeginInvoke(new Action(_onFiltersChanged));
                }, null, 200, Timeout.Infinite);
            }
        }
    }

    /// <summary>Total search hits across all active DBs (the host writes this from <c>ApplyAllFilters</c>).</summary>
    public int SearchHitCount
    {
        get => _searchHitCount;
        internal set => SetProperty(ref _searchHitCount, value);
    }

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(_searchQuery);

    /// <summary>
    /// Number of leaf members hidden by <c>excludeFromSetpoints</c> rules.
    /// Host writes this from <c>ApplyAllFilters</c>; banner properties below
    /// re-raise automatically when it changes.
    /// </summary>
    public int HiddenByRuleCount
    {
        get => _hiddenByRuleCount;
        internal set
        {
            if (SetProperty(ref _hiddenByRuleCount, value))
            {
                OnPropertyChanged(nameof(ShowRuleFilterBanner));
                OnPropertyChanged(nameof(RuleFilterBannerText));
            }
        }
    }

    public bool ShowRuleFilterBanner => _hiddenByRuleCount > 0;

    public string RuleFilterBannerText => Res.Format("Dialog_RuleFilterBanner", _hiddenByRuleCount);

    /// <summary>
    /// When on, hide every leaf that is not a UDT-resolved SetPoint.
    /// AND-combined with the always-on rule filter. On OFF→ON the optional
    /// <c>onSetpointsTurnedOn</c> callback runs first so the host can
    /// re-export UDT types if its cache is stale, then the filter pass
    /// fires via <c>onFiltersChanged</c>.
    /// </summary>
    public bool ShowSetpointsOnly
    {
        get => _showSetpointsOnly;
        set
        {
            if (value && !_showSetpointsOnly && _onSetpointsTurnedOn != null)
                _onSetpointsTurnedOn();

            if (SetProperty(ref _showSetpointsOnly, value))
                _onFiltersChanged();
        }
    }

    /// <summary>
    /// The SetPoint-only checkbox is enabled unless the filter cannot work —
    /// i.e. the anchor DB references UDTs that are not cached AND no refresh
    /// path is wired. When a refresh callback exists, toggling on triggers
    /// a fresh export automatically.
    /// </summary>
    public bool CanShowSetpointsOnly
        => _getAnchorInfo().UnresolvedUdts.Count == 0 || _hasUdtRefresh;

    /// <summary>Tooltip shown on the SetPoint-only checkbox.</summary>
    public string ShowSetpointsOnlyTooltip
    {
        get
        {
            var info = _getAnchorInfo();
            if (info.UnresolvedUdts.Count == 0)
                return "Only show members marked as SetPoint (Einstellwert) in the UDT type definition.";

            if (_hasUdtRefresh)
                return "Only show members marked as SetPoint. UDT types will be re-exported from TIA Portal when enabled.";

            var missing = string.Join(", ", info.UnresolvedUdts);
            return $"Disabled: UDT type definitions are missing ({missing}) and no PLC connection is available to export them.";
        }
    }

    /// <summary>
    /// Re-raise <see cref="CanShowSetpointsOnly"/> +
    /// <see cref="ShowSetpointsOnlyTooltip"/>. The host calls this after
    /// the anchor DB changes or a successful UDT cache refresh, since both
    /// derive from <c>_active.Info.UnresolvedUdts</c> and the slice has no
    /// way to observe that.
    /// </summary>
    public void RaiseSetpointsCapabilityChanged()
    {
        OnPropertyChanged(nameof(CanShowSetpointsOnly));
        OnPropertyChanged(nameof(ShowSetpointsOnlyTooltip));
    }

    /// <summary>
    /// True when a search keystroke has scheduled a debounce timer that
    /// hasn't fired yet. Scripting / test seam.
    /// </summary>
    internal bool HasPendingSearchDebounce => _searchDebounceTimer != null;

    /// <summary>
    /// Cancels the pending 200&#160;ms <see cref="SearchQuery"/> debounce
    /// and fires the filter callback synchronously. Used by scripted
    /// scenarios (DevLauncher captures, regression tests) so the next
    /// frame reflects the staged search state.
    /// </summary>
    internal void FlushPendingSearch()
    {
        _searchDebounceTimer?.Dispose();
        _searchDebounceTimer = null;
        _onFiltersChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _searchDebounceTimer?.Dispose();
        _searchDebounceTimer = null;
    }
}
