using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BlockParam.Config;
using BlockParam.Licensing;
using BlockParam.UI;
using BlockParam.Updates;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Focused tests for the subscription slice (#80 slice 2).
/// </summary>
public class SubscriptionViewModelTests
{
    [Fact]
    public void UpdateUsageStatus_FreeTier_ShowsRemainingQuota()
    {
        var (vm, _, _, _, _) = CreateVm(used: 30, limit: 200, isPro: false);

        vm.UpdateUsageStatus();

        vm.UsageStatusText.Should().Contain("170");
        vm.UsageStatusText.Should().Contain("200");
        vm.LicenseTierText.Should().NotBeEmpty();
        vm.IsLimitReached.Should().BeFalse();
    }

    [Fact]
    public void UpdateUsageStatus_Pro_ShowsProBranding()
    {
        var (vm, _, _, _, _) = CreateVm(used: 0, limit: 200, isPro: true);

        vm.UpdateUsageStatus();

        vm.IsProActive.Should().BeTrue();
        vm.LicenseTierText.Should().NotBeEmpty();
        // Pro should not show a remaining-quota string with the daily-limit number.
        vm.UsageStatusText.Should().NotContain("/200");
    }

    [Fact]
    public void UpdateUsageStatus_FiresStateChanged()
    {
        var (vm, _, _, _, _) = CreateVm(used: 0, limit: 200, isPro: false);

        int stateChangedCount = 0;
        vm.StateChanged += () => stateChangedCount++;

        vm.UpdateUsageStatus();

        stateChangedCount.Should().Be(1);
    }

    [Fact]
    public void MaybeWarnLimitReachedOnce_BelowLimit_DoesNothing()
    {
        var (vm, _, _, _, msg) = CreateVm(used: 100, limit: 200, isPro: false);

        vm.MaybeWarnLimitReachedOnce();

        msg.InfoShownCount.Should().Be(0);
    }

    [Fact]
    public void MaybeWarnLimitReachedOnce_AtLimit_ShowsOncePerInstance()
    {
        var (vm, _, _, _, msg) = CreateVm(used: 200, limit: 200, isPro: false);

        vm.MaybeWarnLimitReachedOnce();
        vm.MaybeWarnLimitReachedOnce();
        vm.MaybeWarnLimitReachedOnce();

        msg.InfoShownCount.Should().Be(1);
    }

    [Fact]
    public void RecordUsage_Delegates_ToTracker()
    {
        var (vm, _, tracker, _, _) = CreateVm(used: 0, limit: 200, isPro: false);

        var ok = vm.RecordUsage(5);

        ok.Should().BeTrue();
        tracker.RecordedTotal.Should().Be(5);
    }

    [Fact]
    public void RecordUsage_OverLimit_ReturnsFalseAndDoesNotIncrement()
    {
        var (vm, _, tracker, _, _) = CreateVm(used: 198, limit: 200, isPro: false);

        var ok = vm.RecordUsage(5);

        ok.Should().BeFalse();
        tracker.RecordedTotal.Should().Be(0);
    }

    [Fact]
    public void InitializeUpdateCheck_NoService_NoBadge()
    {
        var (vm, _, _, _, _) = CreateVm(used: 0, limit: 200, isPro: false, updateService: null);

        vm.InitializeUpdateCheck(forceRefresh: false);

        vm.HasUpdateAvailable.Should().BeFalse();
        vm.AvailableUpdate.Should().BeNull();
        vm.UpdateBadgeText.Should().BeEmpty();
        vm.UpdateBadgeTooltip.Should().BeEmpty();
    }

    [Fact]
    public void InitializeUpdateCheck_WithCachedRelease_PopulatesBadge()
    {
        var info = new UpdateInfo { TagName = "v9.9.9", Name = "Great release" };
        var update = new StubUpdateService(cached: info);
        var (vm, _, _, _, _) = CreateVm(used: 0, limit: 200, isPro: false, updateService: update);

        vm.InitializeUpdateCheck(forceRefresh: false);

        vm.HasUpdateAvailable.Should().BeTrue();
        vm.AvailableUpdate.Should().Be(info);
        vm.UpdateBadgeText.Should().Contain("9.9.9");
        vm.UpdateBadgeTooltip.Should().Contain("Great release");
    }

    [Fact]
    public void Dispose_UnsubscribesFromLicenseStateChanged()
    {
        var license = new RecordingLicenseService();
        var (vm, _, _, _, _) = CreateVm(used: 0, limit: 200, isPro: false, license: license);

        license.SubscriberCount.Should().Be(1, "ctor subscribes the handler");

        vm.Dispose();
        vm.Dispose(); // idempotent

        license.SubscriberCount.Should().Be(0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fixtures
    // ─────────────────────────────────────────────────────────────────────

    private static (SubscriptionViewModel vm,
        StubLicenseService license,
        StubUsageTracker tracker,
        StubUpdateService? update,
        SpyMessageBox msg) CreateVm(
        int used,
        int limit,
        bool isPro,
        IUpdateCheckService? updateService = null,
        ILicenseService? license = null)
    {
        var lic = license ?? new StubLicenseService(isPro);
        var tracker = new StubUsageTracker(used, limit);
        var upd = updateService;
        var msg = new SpyMessageBox();
        var vm = new SubscriptionViewModel(
            tracker, lic, upd,
            new ConfigLoader(null), msg, Dispatcher.CurrentDispatcher);
        return (vm,
            lic as StubLicenseService ?? new StubLicenseService(isPro),
            tracker,
            upd as StubUpdateService,
            msg);
    }

    private sealed class StubLicenseService : ILicenseService
    {
        public StubLicenseService(bool isPro) { IsProActive = isPro; }
        public LicenseInfo GetLicenseInfo() => new LicenseInfo { Tier = LicenseTier.Free };
        public LicenseTier CurrentTier => IsProActive ? LicenseTier.Pro : LicenseTier.Free;
        public bool IsProActive { get; }
        public Task<LicenseActivationResult> ActivateKeyAsync(string licenseKey)
            => Task.FromResult(LicenseActivationResult.InvalidKey("stub"));
        public void DeactivateKey() { }
        public void StartHeartbeat() { }
        public void StopHeartbeat() { }
        public event EventHandler? LicenseStateChanged;
        public void Dispose() { }
    }

    /// <summary>
    /// Stub that exposes a subscriber-count so Dispose tests can verify the
    /// VM unsubscribes from <see cref="ILicenseService.LicenseStateChanged"/>.
    /// </summary>
    private sealed class RecordingLicenseService : ILicenseService
    {
        public int SubscriberCount { get; private set; }
        public event EventHandler? LicenseStateChanged
        {
            add { SubscriberCount++; }
            remove { SubscriberCount--; }
        }
        public LicenseInfo GetLicenseInfo() => new LicenseInfo { Tier = LicenseTier.Free };
        public LicenseTier CurrentTier => LicenseTier.Free;
        public bool IsProActive => false;
        public Task<LicenseActivationResult> ActivateKeyAsync(string licenseKey)
            => Task.FromResult(LicenseActivationResult.InvalidKey("stub"));
        public void DeactivateKey() { }
        public void StartHeartbeat() { }
        public void StopHeartbeat() { }
        public void Dispose() { }
    }

    private sealed class StubUsageTracker : IUsageTracker
    {
        private int _used;
        public StubUsageTracker(int used, int limit)
        {
            _used = used;
            DailyLimit = limit;
        }
        public int DailyLimit { get; }
        public int RecordedTotal { get; private set; }
        public UsageStatus GetStatus() => new UsageStatus(_used, DailyLimit);
        public bool RecordUsage(int count)
        {
            if (_used + count > DailyLimit) return false;
            _used += count;
            RecordedTotal += count;
            return true;
        }
    }

    private sealed class StubUpdateService : IUpdateCheckService
    {
        private readonly UpdateInfo? _cached;
        public StubUpdateService(UpdateInfo? cached) { _cached = cached; }
        public UpdateInfo? GetCached() => _cached;
        public Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
            => Task.FromResult<UpdateInfo?>(_cached);
    }

    private sealed class SpyMessageBox : IMessageBoxService
    {
        public int InfoShownCount { get; private set; }
        public bool AskYesNo(string message, string title) => false;
        public void ShowError(string message, string title) { }
        public void ShowInfo(string message, string title) { InfoShownCount++; }
        public ApplyStashCancelResult AskApplyStashCancel(string message, string title)
            => ApplyStashCancelResult.Cancel;
        public AddOrReplaceResult AskAddOrReplace(string message, string title)
            => AddOrReplaceResult.Cancel;
        public CloseWithStashResult AskCloseWithStash(string message, string title)
            => CloseWithStashResult.Cancel;
    }
}
