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
/// - Flash-of-splash guard: the window is not created until the splash has
///   been alive ~150 ms. If <see cref="Close"/> runs first (fast project),
///   no window is ever shown. (Acceptance criteria use approximate
///   thresholds; 150 ms is the concrete value the spike fixed for the
///   flash guard, so the &gt;300 ms-prep case still gets clearly visible
///   feedback well before the multi-second freeze ends.)
/// - <see cref="Close"/> is idempotent and safe from any thread.
/// - No <c>readonly struct</c> fields (the #131 partial-trust ldflda rule);
///   every field here is a reference type.
/// </summary>
public sealed class LoadingSplashController : IProgress<string>, IDisposable
{
    private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(150);

    private readonly LoadingSplashViewModel _vm;
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private LoadingSplash? _window;
    private DispatcherTimer? _showTimer;
    private int _closed;

    public LoadingSplashController(string title)
    {
        _vm = new LoadingSplashViewModel { Title = title };
    }

    /// <summary>For tests: whether the splash window was ever created.</summary>
    internal bool WindowShown => _window != null;

    /// <summary>For tests: wait for the splash thread to terminate.</summary>
    internal bool WaitForThreadExit(TimeSpan timeout) => _thread?.Join(timeout) ?? true;

    /// <summary>
    /// Starts the splash thread. Returns once its dispatcher is live
    /// (typically &lt;5 ms); the window itself appears later, gated by the
    /// flash-of-splash delay.
    /// </summary>
    public void Show()
    {
        var ready = new ManualResetEventSlim(false);

        _thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;

            _showTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
            {
                Interval = ShowDelay,
            };
            _showTimer.Tick += (_, _) =>
            {
                _showTimer!.Stop();
                if (Volatile.Read(ref _closed) != 0)
                {
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                    return;
                }
                _window = new LoadingSplash { DataContext = _vm };
                _window.Show();
            };
            _showTimer.Start();

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
    /// Sets the main step line (e.g. "Exporting DB_X…"). Pre-localized by
    /// the caller. Safe to call before the window appears.
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
    /// any thread. A no-op (no window ever shown) if called before the
    /// flash-of-splash delay elapses.
    /// </summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        var d = _dispatcher;
        if (d == null) return;
        d.BeginInvoke(() =>
        {
            _showTimer?.Stop();
            _window?.Close();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        });
    }

    public void Dispose() => Close();
}
