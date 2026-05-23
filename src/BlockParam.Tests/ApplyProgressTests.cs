using System;
using System.Collections.Generic;
using BlockParam.Config;
using BlockParam.Licensing;
using BlockParam.Services;
using BlockParam.SimaticML;
using BlockParam.UI;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Coverage for the Apply progress orchestration introduced in #146. The
/// production splash lives on its own STA dispatcher thread and is not
/// exercised here — these tests pin the orchestration contract:
///
/// <list type="bullet">
///   <item><description>Every Apply attempt opens exactly one progress
///   session (the user always sees an indicator, even when Apply early-
///   returns post-write).</description></item>
///   <item><description>The handle is always disposed — no dangling
///   splash thread after success, cancel, or exception.</description></item>
///   <item><description>Apply &amp; Close fires <c>ShowSummaryAndClose</c>
///   so the user sees a "✓ Applied N" line before the host dialog
///   vanishes (the core trust gap from #146).</description></item>
///   <item><description>Plain Apply never fires <c>ShowSummaryAndClose</c>
///   — its dialog stays open and StatusText repaints, so a held summary
///   would be duplicate noise.</description></item>
///   <item><description><c>ApplyFinished</c> is raised on every code
///   path so the host dialog can re-foreground above TIA.</description></item>
/// </list>
/// </summary>
public class ApplyProgressTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Recording fake — captures Begin/Report/ShowSummaryAndClose/Dispose
    /// calls so tests can assert the orchestration shape without spinning
    /// up a real WPF splash.
    /// </summary>
    private sealed class RecordingApplyProgress : IApplyProgressService
    {
        public List<RecordingHandle> Handles { get; } = new();

        public IApplyProgressHandle Begin(string initialStatus)
        {
            var h = new RecordingHandle(initialStatus);
            Handles.Add(h);
            return h;
        }

        public sealed class RecordingHandle : IApplyProgressHandle
        {
            public string InitialStatus { get; }
            public List<string> Reports { get; } = new();
            public List<string> Counters { get; } = new();
            public string? Summary { get; private set; }
            public int SummaryHoldMs { get; private set; }
            public int DisposeCount { get; private set; }
            public int SummaryCallCount { get; private set; }
            // Mirrors the WpfApplyProgressHandle Interlocked gate: Dispose
            // and ShowSummaryAndClose are mutually exclusive, only the first
            // one called does work (review nit #6). Without this, the fake
            // diverges from prod and a test that asserts "exactly one close
            // path ran" would pass on the fake and fail on real WPF (or
            // vice versa).
            private int _closed;
            public bool Closed => _closed != 0;

            public RecordingHandle(string initialStatus)
            {
                InitialStatus = initialStatus;
            }

            public void Report(string status) => Reports.Add(status);

            public void SetCounter(string counter) => Counters.Add(counter);

            public void ShowSummaryAndClose(string summary, int holdMs)
            {
                SummaryCallCount++;
                if (System.Threading.Interlocked.Exchange(ref _closed, 1) != 0) return;
                Summary = summary;
                SummaryHoldMs = holdMs;
            }

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _closed, 1) != 0) return;
                DisposeCount++;
            }
        }
    }

    private ConfigLoader CreateEmptyConfig()
    {
        var tempDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"test_apply_progress_{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(tempDir);
        _tempDirs.Add(tempDir);
        var configPath = System.IO.Path.Combine(tempDir, "config.json");
        System.IO.File.WriteAllText(configPath, @"{ ""rules"": [] }");
        return new ConfigLoader(configPath);
    }

    private static IUsageTracker UsageTracker(int used = 0, int limit = 200)
    {
        var t = Substitute.For<IUsageTracker>();
        t.GetStatus().Returns(new UsageStatus(used, limit));
        t.RecordUsage(Arg.Any<int>()).Returns(true);
        return t;
    }

    private (BulkChangeViewModel vm, RecordingApplyProgress progress) MakeVm(
        IUsageTracker? usageTracker = null)
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = new SimaticMLParser().Parse(xml);
        var analyzer = new HierarchyAnalyzer();
        var configLoader = CreateEmptyConfig();
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var progress = new RecordingApplyProgress();
        var vm = new BulkChangeViewModel(
            db, xml, analyzer, bulkService,
            usageTracker ?? UsageTracker(),
            configLoader,
            onApply: _ => { /* no-op — we are not testing the import side */ },
            applyProgress: progress);
        return (vm, progress);
    }

    /// <summary>
    /// Apply opens exactly one progress session and disposes it. The user
    /// always sees a "Working…" indicator, regardless of how many edits.
    /// </summary>
    [Fact]
    public void Apply_OpensAndClosesOneProgressSession()
    {
        var (vm, progress) = MakeVm();
        vm.Tree.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";

        vm.ApplyCommand.Execute(null);

        progress.Handles.Should().HaveCount(1, "plain Apply opens one session");
        progress.Handles[0].DisposeCount.Should().Be(1,
            "exactly one close path runs (the using-var Dispose) — pins the " +
            "mutual-exclusion contract that ShowSummaryAndClose isn't also " +
            "called on the plain-Apply path");
        progress.Handles[0].Summary.Should().BeNull(
            "plain Apply never holds a summary — dialog stays open and " +
            "StatusText repaints; a held splash would be duplicate noise");
    }

    /// <summary>
    /// Apply &amp; Close: the close path runs via ShowSummaryAndClose, not via
    /// the using-var Dispose. The two are mutually exclusive in production
    /// (see <c>WpfApplyProgressHandle</c>'s <c>Interlocked.Exchange</c>
    /// gate); this test pins that contract through the recording fake.
    /// </summary>
    [Fact]
    public void ApplyAndClose_ClosesViaSummaryNotDispose()
    {
        var (vm, progress) = MakeVm();
        vm.Tree.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";

        vm.ApplyAndCloseCommand.Execute(null);

        progress.Handles.Should().HaveCount(1);
        progress.Handles[0].SummaryCallCount.Should().Be(1,
            "Apply &amp; Close routes through ShowSummaryAndClose exactly once");
        progress.Handles[0].DisposeCount.Should().Be(0,
            "the subsequent using-var Dispose is gated and must be a no-op — " +
            "double-closing the underlying splash would tear down its " +
            "dispatcher thread twice");
    }

    /// <summary>
    /// Apply &amp; Close holds a "✓ Applied N" summary on the splash before
    /// closing. This is the core trust-gap fix from #146: without it the
    /// host dialog vanishes the same tick and the user has no confirmation
    /// anything happened.
    /// </summary>
    [Fact]
    public void ApplyAndClose_HoldsSummaryOnSplashBeforeClose()
    {
        var (vm, progress) = MakeVm();
        vm.Tree.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";

        var closeFiredAtSummary = false;
        vm.RequestClose += () =>
        {
            // Capture whether a summary was set by the time the close
            // signal arrived — guards against a future refactor that
            // re-orders close before the summary call.
            closeFiredAtSummary = progress.Handles[0].Summary != null;
        };

        vm.ApplyAndCloseCommand.Execute(null);

        progress.Handles.Should().HaveCount(1);
        progress.Handles[0].Summary.Should().NotBeNullOrEmpty(
            "Apply &amp; Close must surface a summary on the splash");
        progress.Handles[0].SummaryHoldMs.Should().BeGreaterThan(0,
            "the hold must be non-zero so the user actually sees it");
        closeFiredAtSummary.Should().BeTrue(
            "RequestClose must fire AFTER the summary is set — otherwise " +
            "the dialog vanishes and the splash's summary becomes invisible");
    }

    /// <summary>
    /// ApplyFinished is raised on every Apply outcome so the dialog code-
    /// behind can re-foreground above TIA. The guarantee comes from the
    /// <c>finally</c> block in <c>ExecuteApplyCore</c>; this and the two
    /// failure-path tests below pin that — if a future edit moves the
    /// <c>ApplyFinished?.Invoke()</c> line out of <c>finally</c> into the
    /// <c>try</c> body, the failure paths will stop firing the event and
    /// these tests fail.
    /// </summary>
    [Fact]
    public void Apply_Success_RaisesApplyFinished()
    {
        var (vm, _) = MakeVm();
        vm.Tree.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";

        var finishedCount = 0;
        vm.ApplyFinished += () => finishedCount++;

        vm.ApplyCommand.Execute(null);

        finishedCount.Should().Be(1,
            "every Apply attempt must raise ApplyFinished so the host can " +
            "re-foreground the dialog above TIA (#146 H3)");
    }

    /// <summary>
    /// User declined the compile-prompt on an inconsistent block (the
    /// <c>OnApply</c> closure in <c>ActiveDbFactory</c> throws
    /// <see cref="OperationCanceledException"/>). <c>CommitChanges</c>
    /// catches it and returns false; the host must STILL receive
    /// ApplyFinished so the dialog re-foregrounds. Pins the
    /// <c>finally</c>-block contract on the user-cancel path.
    /// </summary>
    [Fact]
    public void Apply_UserCancelDuringCommit_StillRaisesApplyFinished()
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = new SimaticMLParser().Parse(xml);
        var configLoader = CreateEmptyConfig();
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var vm = new BulkChangeViewModel(
            db, xml, new HierarchyAnalyzer(), bulkService,
            UsageTracker(), configLoader,
            onApply: _ => throw new OperationCanceledException(
                "User declined to compile the inconsistent block."),
            applyProgress: new RecordingApplyProgress());
        vm.Tree.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";

        var finishedCount = 0;
        vm.ApplyFinished += () => finishedCount++;

        vm.ApplyCommand.Execute(null);

        finishedCount.Should().Be(1,
            "compile-cancel must still fire ApplyFinished — the dialog stays " +
            "open and TIA may have stolen foreground during the failed export");
    }

    /// <summary>
    /// <c>OnApply</c> threw a non-cancellation exception (e.g. TIA import
    /// failed). The VM routes this through <c>HandleErrorWithRollback</c>
    /// inside the catch. The <c>finally</c> must still raise
    /// ApplyFinished so the dialog isn't left buried behind TIA after
    /// the error dialog dismisses.
    /// </summary>
    [Fact]
    public void Apply_ImportThrows_StillRaisesApplyFinished()
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = new SimaticMLParser().Parse(xml);
        var configLoader = CreateEmptyConfig();
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var messageBox = Substitute.For<IMessageBoxService>();
        // No backup callback wired → the error path goes to the
        // "no backup available" branch which just sets a status string
        // (no user prompt), so the test runs without an interactive stub.
        var vm = new BulkChangeViewModel(
            db, xml, new HierarchyAnalyzer(), bulkService,
            UsageTracker(), configLoader,
            onApply: _ => throw new InvalidOperationException("TIA import failed"),
            messageBox: messageBox,
            applyProgress: new RecordingApplyProgress());
        vm.Tree.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";

        var finishedCount = 0;
        vm.ApplyFinished += () => finishedCount++;

        vm.ApplyCommand.Execute(null);

        finishedCount.Should().Be(1,
            "exception during import must still fire ApplyFinished from the " +
            "finally block — without it the dialog stays buried behind TIA " +
            "after the error MessageBox dismisses");
    }

    /// <summary>
    /// Over-quota Apply early-returns BEFORE the progress session opens
    /// (no work happens), so the splash never appears. This matches the
    /// existing over-quota guard at <c>ExecuteApplyCore</c> — surfacing a
    /// splash just to flash and close would be jarring.
    /// </summary>
    [Fact]
    public void Apply_OverQuota_DoesNotOpenProgressSession()
    {
        // 200/200 used → RemainingToday == 0; even one pending edit is over-cap.
        var (vm, progress) = MakeVm(usageTracker: UsageTracker(used: 200, limit: 200));
        vm.Tree.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";

        vm.ApplyCommand.Execute(null);

        progress.Handles.Should().BeEmpty(
            "the over-quota guard early-returns before the import phase " +
            "— no splash should flash on the user");
    }

    /// <summary>
    /// The NoOp service hands out a handle whose Report / ShowSummary /
    /// Dispose never throw. This is the default for tests and DevLauncher
    /// where the real WPF splash would spin up unwanted dispatcher
    /// threads on every Apply.
    /// </summary>
    [Fact]
    public void NoOpService_IsSafelyCallable()
    {
        IApplyProgressService svc = new NoOpApplyProgressService();
        var handle = svc.Begin("starting");
        handle.Report("step 1");
        handle.ShowSummaryAndClose("done", holdMs: 50);
        handle.Dispose();
        // Reach this line without exception → contract satisfied.
    }
}
