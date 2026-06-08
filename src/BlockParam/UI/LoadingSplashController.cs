using System;
using System.Threading;
using System.Windows.Threading;

namespace BlockParam.UI;

/// <summary>
/// Owns the pre-dialog loading splash (#125, Option C) on its own dedicated
/// STA <see cref="Dispatcher"/> thread so the indeterminate marquee keeps
/// animating smoothly even while TIA's UI thread is blocked in synchronous
/// Openness <c>Export()</c> calls (spike-proven — see issue #125).
///
/// Hard rules (carried from the #125 spike):
/// - The splash thread RENDERS ONLY. It must never touch Openness or
///   <c>BlockParam.Localization.Res</c>. Status strings are localized on
///   the caller (TIA) thread and pushed in via <see cref="Report"/> /
///   <see cref="SetCounter"/>, which marshal onto the splash dispatcher.
/// - The splash is shown as fast as possible: the window is created the
///   moment the splash thread is up, before <see cref="Show"/> returns, so
///   it is guaranteed visible before the TIA thread dives into the
///   multi-second Openness freeze. There is deliberately NO flash-of-splash
///   delay — prep time cannot be predicted up front (issue #125 decision),
///   so a sub-frame flash on an unusually fast prep is accepted in exchange
///   for never missing the feedback when it is actually needed.
/// - <see cref="Close"/> is idempotent and safe from any thread.
/// - No <c>readonly struct</c> fields (the #131 partial-trust ldflda rule);
///   every field here is a reference type.
/// </summary>
public sealed class LoadingSplashController : IProgress<string>, IDisposable
{
    private readonly LoadingSplashViewModel _vm;
    private readonly string _humorLine;
    private Thread? _thread;
    private volatile Dispatcher? _dispatcher;
    private LoadingSplash? _window;
    private DispatcherTimer? _humorTimer;
    private int _closed;

    /// <param name="title">Splash title (pre-localized).</param>
    /// <param name="humorLine">
    /// Optional pre-localized quip (#127). Empty (the default) means no quip —
    /// the Apply-time splash and any other caller that wants a strictly
    /// professional splash pass nothing. When non-empty it is revealed once,
    /// after <see cref="HumorRevealDelay"/> of elapsed prep time, and held for
    /// the rest of the session. Pre-localized by the caller because the splash
    /// render thread must never touch <c>Res</c> (the #125 render-only rule).
    /// </param>
    public LoadingSplashController(string title, string humorLine = "")
    {
        _vm = new LoadingSplashViewModel { Title = title };
        _humorLine = humorLine ?? string.Empty;
    }

    /// <summary>The ~1.5s slow-load threshold #127 specifies (mirrors #125's flash guard).</summary>
    private const double HumorRevealSeconds = 1.5;

    /// <summary>
    /// How long the splash must already have been up before the quip line is
    /// revealed (#127): fast opens stay strictly professional, only a load
    /// that is already slow earns a quip. Settable for tests; defaults to
    /// <see cref="HumorRevealSeconds"/>.
    /// </summary>
    internal TimeSpan HumorRevealDelay { get; set; } = TimeSpan.FromSeconds(HumorRevealSeconds);

    /// <summary>For tests: the quip currently shown (empty until revealed).</summary>
    internal string HumorLine => _vm.HumorLine;

    /// <summary>For tests: whether the splash window was created.</summary>
    internal bool WindowShown => _window != null;

    /// <summary>For tests: wait for the splash thread to terminate.</summary>
    internal bool WaitForThreadExit(TimeSpan timeout) => _thread?.Join(timeout) ?? true;

    /// <summary>
    /// Starts the splash thread and shows the window. Blocks the caller only
    /// for the few milliseconds it takes the splash thread to spin up and
    /// create the window, then returns — from that point the splash paints
    /// on its own dispatcher while the caller is free to block in Openness.
    /// </summary>
    public void Show()
    {
        var ready = new ManualResetEventSlim(false);

        _thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;

            // Defensive only: in this codebase Close() cannot land before
            // Show() returns (single caller thread), but if it ever did the
            // window must not appear.
            if (Volatile.Read(ref _closed) == 0)
            {
                _window = new LoadingSplash { DataContext = _vm };
                _window.Show();
                StartHumorTimer();
            }
            else
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }

            ready.Set();
            Dispatcher.Run();
        })
        {
            Name = "BlockParam.LoadingSplash",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Arms the one-shot quip reveal (#127) on the splash dispatcher thread.
    /// Runs on that thread (called from the thread body), so it touches the VM
    /// safely. If the splash closes before the delay elapses — the fast,
    /// strictly-professional path — the dispatcher shuts down and the timer
    /// never ticks, so <see cref="LoadingSplashViewModel.HumorLine"/> is never
    /// evaluated and there is no cost on the happy path.
    /// </summary>
    private void StartHumorTimer()
    {
        if (string.IsNullOrEmpty(_humorLine)) return;

        var timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = HumorRevealDelay,
        };
        timer.Tick += (_, _) =>
        {
            // One quip per session: fire once, then stop. The text never
            // churns for the rest of the splash. Capturing the local (rather
            // than the field) keeps this non-null regardless of teardown.
            timer.Stop();
            _vm.HumorLine = _humorLine;
        };
        _humorTimer = timer;
        timer.Start();
    }

    /// <summary>
    /// Sets the main step line (e.g. "Exporting DB_X…"). Pre-localized by
    /// the caller.
    /// </summary>
    public void Report(string status) => Post(() => _vm.StatusText = status);

    /// <summary>
    /// Sets the secondary "(N of M)" counter line, or clears it with an
    /// empty string. Pre-localized by the caller.
    /// </summary>
    public void SetCounter(string counter) => Post(() => _vm.CounterText = counter);

    private void Post(Action action)
    {
        var d = _dispatcher;
        if (d == null || Volatile.Read(ref _closed) != 0) return;
        d.BeginInvoke(action);
    }

    /// <summary>
    /// Closes the splash and tears down its thread. Idempotent; safe from
    /// any thread.
    /// </summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        var d = _dispatcher;
        if (d == null) return;
        d.BeginInvoke(() =>
        {
            _humorTimer?.Stop();
            _window?.Close();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        });
    }

    public void Dispose() => Close();
}
