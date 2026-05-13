using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Windows.Threading;
using BlockParam.Models;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Focused tests for the search + tree-filter slice (#80 slice 6).
/// </summary>
public class SearchFilterViewModelTests
{
    [Fact]
    public void Defaults_AreEmpty()
    {
        var vm = Build();

        vm.SearchQuery.Should().BeEmpty();
        vm.HasSearchQuery.Should().BeFalse();
        vm.SearchHitCount.Should().Be(0);
        vm.HiddenByRuleCount.Should().Be(0);
        vm.ShowRuleFilterBanner.Should().BeFalse();
        vm.RuleFilterBannerText.Should().Contain("0");
        vm.ShowSetpointsOnly.Should().BeFalse();
        vm.HasPendingSearchDebounce.Should().BeFalse();
    }

    [Fact]
    public void SearchQuery_Setter_RaisesPropertyChangedAndSchedulesDebounce()
    {
        var vm = Build();
        var raised = Capture(vm);

        vm.SearchQuery = "Speed";

        vm.SearchQuery.Should().Be("Speed");
        vm.HasSearchQuery.Should().BeTrue();
        vm.HasPendingSearchDebounce.Should().BeTrue(
            "a 200ms debounce timer must be armed after a keystroke");
        raised.Should().Contain(nameof(SearchFilterViewModel.SearchQuery));
        raised.Should().Contain(nameof(SearchFilterViewModel.HasSearchQuery));
    }

    [Fact]
    public void FlushPendingSearch_CancelsTimerAndFiresCallback()
    {
        int callbackCount = 0;
        var vm = Build(onFiltersChanged: () => callbackCount++);

        vm.SearchQuery = "Speed";
        vm.HasPendingSearchDebounce.Should().BeTrue();
        // The setter armed a 200ms timer; without flush the callback would fire
        // asynchronously after delay. We verify flush cancels the timer + fires
        // immediately.
        callbackCount.Should().Be(0, "setter alone does not invoke callback synchronously");

        vm.FlushPendingSearch();

        vm.HasPendingSearchDebounce.Should().BeFalse();
        callbackCount.Should().Be(1, "FlushPendingSearch must fire the filter callback");
    }

    [Fact]
    public void SearchHitCount_Set_RaisesPropertyChanged()
    {
        var vm = Build();
        var raised = Capture(vm);

        vm.SearchHitCount = 7;

        vm.SearchHitCount.Should().Be(7);
        raised.Should().Contain(nameof(SearchFilterViewModel.SearchHitCount));
    }

    [Fact]
    public void HiddenByRuleCount_Set_RaisesBannerPropertiesAndCount()
    {
        var vm = Build();
        var raised = Capture(vm);

        vm.HiddenByRuleCount = 3;

        vm.HiddenByRuleCount.Should().Be(3);
        vm.ShowRuleFilterBanner.Should().BeTrue();
        raised.Should().Contain(nameof(SearchFilterViewModel.HiddenByRuleCount));
        raised.Should().Contain(nameof(SearchFilterViewModel.ShowRuleFilterBanner));
        raised.Should().Contain(nameof(SearchFilterViewModel.RuleFilterBannerText));
    }

    [Fact]
    public void HiddenByRuleCount_SameValue_DoesNotRaise()
    {
        var vm = Build();
        var raised = Capture(vm);

        vm.HiddenByRuleCount = 0; // already 0

        raised.Should().BeEmpty();
    }

    [Fact]
    public void ShowSetpointsOnly_OffToOn_FiresSetpointsCallbackThenFilterCallback()
    {
        int setpointsCallback = 0;
        int filtersCallback = 0;
        var vm = Build(
            onFiltersChanged: () => filtersCallback++,
            onSetpointsTurnedOn: () => setpointsCallback++);

        vm.ShowSetpointsOnly = true;

        vm.ShowSetpointsOnly.Should().BeTrue();
        setpointsCallback.Should().Be(1, "OFF→ON transition must fire the UDT-refresh callback");
        filtersCallback.Should().Be(1, "every toggle must request a filter pass");
    }

    [Fact]
    public void ShowSetpointsOnly_OnToOff_DoesNotFireSetpointsCallback()
    {
        int setpointsCallback = 0;
        int filtersCallback = 0;
        var vm = Build(
            onFiltersChanged: () => filtersCallback++,
            onSetpointsTurnedOn: () => setpointsCallback++);

        vm.ShowSetpointsOnly = true;
        setpointsCallback.Should().Be(1);

        vm.ShowSetpointsOnly = false;

        vm.ShowSetpointsOnly.Should().BeFalse();
        setpointsCallback.Should().Be(1, "ON→OFF transition must not re-refresh UDTs");
        filtersCallback.Should().Be(2, "OFF→ON and ON→OFF both must request a filter pass");
    }

    [Fact]
    public void ShowSetpointsOnly_SameValue_DoesNotFireFilterCallback()
    {
        int filtersCallback = 0;
        var vm = Build(onFiltersChanged: () => filtersCallback++);

        vm.ShowSetpointsOnly = false; // already false

        filtersCallback.Should().Be(0);
    }

    [Fact]
    public void CanShowSetpointsOnly_FalseWhenUnresolvedAndNoRefresh()
    {
        var info = MakeInfo(unresolved: new[] { "messageConfig_UDT" });
        var vm = Build(getAnchorInfo: () => info, hasUdtRefresh: false);

        vm.CanShowSetpointsOnly.Should().BeFalse();
        vm.ShowSetpointsOnlyTooltip.Should().Contain("messageConfig_UDT");
        vm.ShowSetpointsOnlyTooltip.Should().Contain("Disabled");
    }

    [Fact]
    public void CanShowSetpointsOnly_TrueWhenHasRefreshEvenIfUnresolved()
    {
        var info = MakeInfo(unresolved: new[] { "messageConfig_UDT" });
        var vm = Build(getAnchorInfo: () => info, hasUdtRefresh: true);

        vm.CanShowSetpointsOnly.Should().BeTrue();
        vm.ShowSetpointsOnlyTooltip.Should().Contain("re-exported");
    }

    [Fact]
    public void CanShowSetpointsOnly_TrueWhenAllUdtsResolved()
    {
        var info = MakeInfo(unresolved: new string[0]);
        var vm = Build(getAnchorInfo: () => info);

        vm.CanShowSetpointsOnly.Should().BeTrue();
        vm.ShowSetpointsOnlyTooltip.Should().Contain("SetPoint");
    }

    [Fact]
    public void RaiseSetpointsCapabilityChanged_NotifiesCapabilityAndTooltip()
    {
        var vm = Build();
        var raised = Capture(vm);

        vm.RaiseSetpointsCapabilityChanged();

        raised.Should().Contain(nameof(SearchFilterViewModel.CanShowSetpointsOnly));
        raised.Should().Contain(nameof(SearchFilterViewModel.ShowSetpointsOnlyTooltip));
    }

    /// <summary>
    /// Headline reason the slice uses a <see cref="System.Threading.Timer"/>
    /// + dispatcher trampoline rather than WPF's built-in <c>Binding.Delay</c>
    /// is to coalesce consecutive keystrokes: a burst of edits inside the
    /// 200&#160;ms window must result in exactly one filter pass on the
    /// final value, not one per keystroke. Regression guard so a future
    /// refactor of the setter (e.g. dropping the <c>_searchDebounceTimer?.Dispose()</c>
    /// before re-arming) doesn't silently restore per-keystroke filtering.
    /// </summary>
    [Fact]
    public void SearchQuery_RapidKeystrokes_CoalesceIntoSingleCallback()
    {
        int callbackCount = 0;
        var vm = Build(onFiltersChanged: () => Interlocked.Increment(ref callbackCount));

        vm.SearchQuery = "S";
        vm.SearchQuery = "Sp";
        vm.SearchQuery = "Spe";

        // Wait past the 200ms debounce window so the third (latest) timer
        // can fire; the first two should have been disposed and never run.
        Thread.Sleep(400);

        // Timer callbacks land on the threadpool and post their work via
        // _dispatcher.BeginInvoke(...). The test thread's dispatcher queue
        // doesn't pump on its own — drain it explicitly so the coalesced
        // callback runs before we assert.
        PumpDispatcher();

        callbackCount.Should().Be(1,
            "three consecutive keystrokes inside the 200ms window must coalesce " +
            "into one filter pass; otherwise WPF would over-filter on every keystroke");
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var vm = Build();

        var ex1 = Record.Exception(() => vm.Dispose());
        ex1.Should().BeNull();

        var ex2 = Record.Exception(() => vm.Dispose());
        ex2.Should().BeNull();
    }

    [Fact]
    public void Dispose_AfterArmedDebounce_DoesNotThrow()
    {
        var vm = Build();
        vm.SearchQuery = "Speed";
        vm.HasPendingSearchDebounce.Should().BeTrue();

        var ex = Record.Exception(() => vm.Dispose());
        ex.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static SearchFilterViewModel Build(
        Func<DataBlockInfo>? getAnchorInfo = null,
        bool hasUdtRefresh = false,
        Action? onFiltersChanged = null,
        Action? onSetpointsTurnedOn = null)
    {
        return new SearchFilterViewModel(
            Dispatcher.CurrentDispatcher,
            getAnchorInfo ?? (() => MakeInfo()),
            hasUdtRefresh,
            onFiltersChanged ?? (() => { }),
            onSetpointsTurnedOn);
    }

    private static DataBlockInfo MakeInfo(IReadOnlyList<string>? unresolved = null) =>
        new DataBlockInfo(
            "DB_Test", 1, "Optimized", "GlobalDB",
            Array.Empty<MemberNode>(),
            unresolved);

    private static List<string?> Capture(INotifyPropertyChanged vm)
    {
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return raised;
    }

    /// <summary>
    /// Drain the current thread's dispatcher queue by pushing a frame that
    /// exits as soon as a background-priority continuation runs. Needed in
    /// tests where the slice's debounce timer posts work via
    /// <c>BeginInvoke</c> — without pumping, the posted action sits in the
    /// queue and our assertions race the dispatcher.
    /// </summary>
    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
