using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BlockParam.Config;
using BlockParam.Diagnostics;
using BlockParam.Licensing;
using BlockParam.Localization;
using BlockParam.Updates;

namespace BlockParam.UI;

/// <summary>
/// Subscription / usage / update slice (#80 slice 2).
///
/// <para>
/// Owns the daily-cap usage display, license-tier label, license-key
/// dialog opener, upgrade prompt, and the update-available badge.
/// Holds the three service references (<see cref="ILicenseService"/>,
/// <see cref="IUsageTracker"/>, <see cref="IUpdateCheckService"/>) so
/// the host VM doesn't need to know about them — Apply pipeline calls
/// go through <see cref="RecordUsage"/>, <see cref="GetUsageStatus"/>,
/// <see cref="UpdateUsageStatus"/> and <see cref="MaybeWarnLimitReachedOnce"/>.
/// </para>
///
/// <para>
/// Emits <see cref="StateChanged"/> after usage / license / update
/// changes so the host can re-raise tooltip + Apply-related
/// properties that compose with subscription state (e.g.
/// <c>BulkChangeViewModel.ApplyTooltip</c>).
/// </para>
/// </summary>
public class SubscriptionViewModel : ViewModelBase, IDisposable
{
    private readonly ILicenseService? _licenseService;
    private readonly IUsageTracker _usageTracker;
    private readonly IUpdateCheckService? _updateCheckService;
    private readonly ConfigLoader _configLoader;
    private readonly IMessageBoxService _messageBox;
    private readonly Dispatcher _dispatcher;
    private EventHandler? _licenseStateChangedHandler;

    private bool _limitWarningShown;
    private UpdateInfo? _availableUpdate;
    private string _usageStatusText = "";
    private string _licenseTierText = "";
    private bool _disposed;

    /// <summary>
    /// Raised after any subscription-side state changes that the host
    /// composes with (license tier, remaining quota, limit-reached). Used
    /// by the host to re-raise <c>ApplyTooltip</c> on tier / quota shifts.
    /// </summary>
    public event Action? StateChanged;

    public SubscriptionViewModel(
        IUsageTracker usageTracker,
        ILicenseService? licenseService,
        IUpdateCheckService? updateCheckService,
        ConfigLoader configLoader,
        IMessageBoxService messageBox,
        Dispatcher dispatcher)
    {
        _usageTracker = usageTracker;
        _licenseService = licenseService;
        _updateCheckService = updateCheckService;
        _configLoader = configLoader;
        _messageBox = messageBox;
        _dispatcher = dispatcher;

        EnterLicenseKeyCommand = new RelayCommand(ExecuteEnterLicenseKey);
        UpgradeToProCommand = new RelayCommand(ExecuteUpgradeToPro);
        ShowUpdateDetailsCommand = new RelayCommand(ExecuteShowUpdateDetails, () => HasUpdateAvailable);

        if (_licenseService != null)
        {
            _licenseStateChangedHandler = (_, __) =>
                _dispatcher.BeginInvoke(new Action(UpdateUsageStatus));
            _licenseService.LicenseStateChanged += _licenseStateChangedHandler;
        }
    }

    // --- VM-facing properties ---

    public string UsageStatusText
    {
        get => _usageStatusText;
        private set => SetProperty(ref _usageStatusText, value);
    }

    public string LicenseTierText
    {
        get => _licenseTierText;
        private set => SetProperty(ref _licenseTierText, value);
    }

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
    /// Update available (#61). Null when no newer version is on offer
    /// (offline / opted out / already on latest / skipped).
    /// </summary>
    public UpdateInfo? AvailableUpdate
    {
        get => _availableUpdate;
        private set
        {
            if (!SetProperty(ref _availableUpdate, value)) return;
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
            var current = typeof(SubscriptionViewModel).Assembly.GetName().Version;
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

    public ICommand EnterLicenseKeyCommand { get; }
    public ICommand UpgradeToProCommand { get; }
    public ICommand ShowUpdateDetailsCommand { get; }

    // --- Host-facing passthroughs (composed into ApplyTooltip etc.) ---

    public bool IsProActive => _licenseService?.IsProActive == true;

    public UsageStatus GetUsageStatus() => _usageTracker.GetStatus();

    public bool RecordUsage(int count) => _usageTracker.RecordUsage(count);

    // --- Operations ---

    /// <summary>
    /// Refresh the visible usage / tier strings and broadcast
    /// <see cref="StateChanged"/> so composers (Apply tooltip) re-render.
    /// </summary>
    public void UpdateUsageStatus()
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

        OnPropertyChanged(nameof(ShowLicenseKeyButton));
        OnPropertyChanged(nameof(LicenseKeyButtonText));
        OnPropertyChanged(nameof(IsLimitReached));
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Shows the daily-cap-reached modal once per dialog open, when the user
    /// first attempts to stage or edit a change while at 0 remaining quota.
    /// Staging itself isn't blocked — Apply is the choke point — but a single
    /// proactive heads-up beats discovering it via a disabled Apply button.
    /// </summary>
    public void MaybeWarnLimitReachedOnce()
    {
        if (_limitWarningShown) return;
        if (!_usageTracker.GetStatus().IsLimitReached) return;

        _limitWarningShown = true;
        _messageBox.ShowInfo(
            Res.Get("LimitReached_Modal_Message"),
            Res.Get("LimitReached_Modal_Title"));
    }

    public void InitializeUpdateCheck(bool forceRefresh = true)
    {
        if (_updateCheckService == null) return;

        // Synchronous cached read so the badge shows up the moment the
        // dialog opens — no flash where it appears half a second later.
        try { AvailableUpdate = _updateCheckService.GetCached(); }
        catch (Exception ex) { Log.Warning(ex, "UpdateCheck: GetCached threw"); }

        if (!forceRefresh) return;

        // Fire-and-forget refresh — never blocks the UI thread, never
        // surfaces an error. Cache TTL gates the actual network hit.
        // If the dialog closes while the background fetch is in flight,
        // the dispatcher callback no-ops so we don't raise PropertyChanged
        // on a disposed VM.
        _ = Task.Run(async () =>
        {
            try
            {
                var info = await _updateCheckService.CheckAsync().ConfigureAwait(false);
                _ = _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed) return;
                    AvailableUpdate = info;
                }));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "UpdateCheck: background CheckAsync threw");
            }
        });
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

    private void ExecuteShowUpdateDetails()
    {
        var info = _availableUpdate;
        if (info == null || _updateCheckService == null) return;

        var dialog = new UpdateAvailableDialog(info);
        dialog.Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        dialog.ShowDialog();
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

        // Unsubscribe so the lambda's `this` capture stops keeping the VM alive.
        // We do NOT dispose _licenseService — the caller that constructed it owns it.
        if (_licenseService != null && _licenseStateChangedHandler != null)
        {
            _licenseService.LicenseStateChanged -= _licenseStateChangedHandler;
            _licenseStateChangedHandler = null;
        }
    }
}
