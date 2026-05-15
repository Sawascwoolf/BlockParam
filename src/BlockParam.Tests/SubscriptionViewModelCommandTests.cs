using System;
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
/// Command-guard coverage for <see cref="SubscriptionViewModel"/>.
///
/// <para>
/// Only the <c>ShowUpdateDetailsCommand</c> <c>CanExecute</c> guard is
/// exercised here — its predicate is <c>() =&gt; HasUpdateAvailable</c>.
/// The update-check setup mirrors
/// <c>SubscriptionViewModelTests.InitializeUpdateCheck_WithCachedRelease_PopulatesBadge</c>
/// (a stub update service exposing a cached <see cref="UpdateInfo"/>) so
/// the guard can be driven both false (no cached release) and true.
/// </para>
///
/// <para>
/// The command bodies are deliberately NOT executed:
/// <c>ExecuteShowUpdateDetails</c> opens an <c>UpdateAvailableDialog</c>,
/// <c>ExecuteEnterLicenseKey</c> opens a <c>LicenseKeyDialog</c>, and
/// <c>ExecuteUpgradeToPro</c> shells out to a browser. They need a dialog
/// seam (a Tier-B boundary) before their execution can be unit-tested
/// without spawning real windows — see the GAP notes below.
/// </para>
/// </summary>
public class SubscriptionViewModelCommandTests
{
    [Fact]
    public void ShowUpdateDetailsCommand_CanExecute_FalseWhenNoUpdateAvailable()
    {
        // Stub service with no cached release → AvailableUpdate stays null →
        // HasUpdateAvailable false → guard false.
        var vm = CreateVm(updateService: new StubUpdateService(cached: null));

        vm.InitializeUpdateCheck(forceRefresh: false);

        vm.HasUpdateAvailable.Should().BeFalse(
            "no cached release means there is nothing to show details for");
        vm.ShowUpdateDetailsCommand.CanExecute(null).Should().BeFalse(
            "the guard is () => HasUpdateAvailable, which is false here");
    }

    [Fact]
    public void ShowUpdateDetailsCommand_CanExecute_TrueWhenCachedReleasePresent()
    {
        // Same setup shape as the false case but with a cached release, so
        // the false→true transition is driven purely by HasUpdateAvailable.
        var info = new UpdateInfo { TagName = "v9.9.9", Name = "Great release" };
        var vm = CreateVm(updateService: new StubUpdateService(cached: info));

        vm.InitializeUpdateCheck(forceRefresh: false);

        vm.HasUpdateAvailable.Should().BeTrue(
            "a cached release populates AvailableUpdate");
        vm.ShowUpdateDetailsCommand.CanExecute(null).Should().BeTrue(
            "the guard follows HasUpdateAvailable, which is now true");
    }

    [Fact]
    public void ShowUpdateDetailsCommand_CanExecute_NoUpdateService_StaysFalse()
    {
        // Defensive: with no update service at all the badge never appears,
        // so the guard must remain false (and not throw).
        var vm = CreateVm(updateService: null);

        vm.InitializeUpdateCheck(forceRefresh: false);

        vm.HasUpdateAvailable.Should().BeFalse();
        vm.ShowUpdateDetailsCommand.CanExecute(null).Should().BeFalse(
            "without an update service there is never an update to detail");
    }

    // GAP: ExecuteShowUpdateDetails / ExecuteEnterLicenseKey open real WPF
    // dialogs (UpdateAvailableDialog / LicenseKeyDialog) and
    // ExecuteUpgradeToPro shells out to a browser. Their execution can't be
    // command-tested without a production dialog seam (Tier-B boundary).
    // Not adding one here (no production changes); only the guard is tested.

    // ---------- fixtures (copied locally; no shared state) ----------

    private static SubscriptionViewModel CreateVm(
        IUpdateCheckService? updateService,
        bool isPro = false)
    {
        var tracker = new StubUsageTracker(used: 0, limit: 200);
        var license = new StubLicenseService(isPro);
        var msg = new SpyMessageBox();
        return new SubscriptionViewModel(
            tracker, license, updateService,
            new ConfigLoader(null), msg, Dispatcher.CurrentDispatcher);
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
        // Manual add/remove avoids CS0067 ("event never used").
        public event EventHandler? LicenseStateChanged { add { } remove { } }
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
        public UsageStatus GetStatus() => new UsageStatus(_used, DailyLimit);
        public bool RecordUsage(int count)
        {
            if (_used + count > DailyLimit) return false;
            _used += count;
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
        public bool AskYesNo(string message, string title) => false;
        public void ShowError(string message, string title) { }
        public void ShowInfo(string message, string title) { }
        public ApplyStashCancelResult AskApplyStashCancel(string message, string title)
            => ApplyStashCancelResult.Cancel;
        public AddOrReplaceResult AskAddOrReplace(string message, string title)
            => AddOrReplaceResult.Cancel;
        public CloseWithStashResult AskCloseWithStash(string message, string title)
            => CloseWithStashResult.Cancel;
    }
}
