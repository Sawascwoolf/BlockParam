using System;
#if DIAGNOSTICS
using System.Diagnostics;
#endif

namespace BlockParam.Diagnostics;

/// <summary>
/// Lightweight <see cref="IDisposable"/> timing scope for the pre-dialog
/// DB-open path. Emits machine-parseable <c>OPEN-TIMING</c> key=value lines
/// to the <see cref="Log"/> sink — but ONLY when the project is built with
/// the <c>DIAGNOSTICS</c> constant (dev/test builds:
/// <c>dotnet build -p:Diagnostics=true</c>).
///
/// <para>
/// In the shipped marketplace build <c>DIAGNOSTICS</c> is undefined and every
/// member is a zero-cost no-op: <see cref="Stage"/> returns a shared singleton,
/// no <see cref="Stopwatch"/> is allocated or started, and nothing is logged.
/// The <c>using (OpenTiming.Stage(...))</c> scopes in product code therefore
/// compile and run unchanged while keeping the hot open path and production
/// logs clean — no interleaved <c>#if</c> needed at the call sites.
/// </para>
/// </summary>
internal sealed class OpenTiming : IDisposable
{
#if DIAGNOSTICS
    private readonly string _stage;
    private readonly string _extras; // pre-built key=value pairs from ctor
    private readonly Stopwatch _sw;
    private string _predictors = "";
    private bool _disposed;

    private OpenTiming(string stage, string extras)
    {
        _stage = stage;
        _extras = extras;
        _sw = Stopwatch.StartNew();
    }
#else
    // No-op shared instance for the shipped build. Dispose/AddPredictors are
    // stateless when DIAGNOSTICS is undefined, so one singleton is safe across
    // nested/overlapping scopes and avoids per-scope allocation.
    private static readonly OpenTiming Noop = new OpenTiming();
    private OpenTiming() { }
#endif

    /// <summary>Opens a timing scope for <paramref name="stage"/>.</summary>
    /// <param name="stage">Short identifier, e.g. "export", "parse", "total".</param>
    /// <param name="extras">Pre-built key=value pairs appended verbatim, e.g. "db=Foo plc=CPU_1".</param>
    public static OpenTiming Stage(string stage, string extras = "")
#if DIAGNOSTICS
        => new OpenTiming(stage, extras);
#else
        => Noop;
#endif

    /// <summary>
    /// Appends additional size-predictor pairs to the log line.
    /// Call any time before <see cref="Dispose"/>. No-op when DIAGNOSTICS is off.
    /// </summary>
    public OpenTiming AddPredictors(string kvPairs)
    {
#if DIAGNOSTICS
        _predictors = string.IsNullOrEmpty(_predictors)
            ? kvPairs
            : _predictors + " " + kvPairs;
#endif
        return this;
    }

    /// <summary>Stops the stopwatch and emits one OPEN-TIMING log line (DIAGNOSTICS only).</summary>
    public void Dispose()
    {
#if DIAGNOSTICS
        if (_disposed) return;
        _disposed = true;
        _sw.Stop();

        var parts = "OPEN-TIMING stage=" + _stage;
        if (!string.IsNullOrEmpty(_extras)) parts += " " + _extras;
        parts += " ms=" + _sw.ElapsedMilliseconds;
        if (!string.IsNullOrEmpty(_predictors)) parts += " " + _predictors;

        Log.Information(parts);
#endif
    }
}
