using System;
using System.Diagnostics;

namespace BlockParam.Diagnostics;

/// <summary>
/// Lightweight <see cref="IDisposable"/> timing scope for the pre-dialog DB-open
/// path. All output is machine-parseable OPEN-TIMING key=value lines written to
/// the existing <see cref="Log"/> sink — no new I/O surface.
///
/// Usage:
/// <code>
/// using (var t = OpenTiming.Stage("export", db: "Foo", plc: "CPU_1"))
/// {
///     // ... timed work ...
///     t.AddPredictors("xmlBytes", bytes);
/// }
/// </code>
/// </summary>
internal sealed class OpenTiming : IDisposable
{
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

    /// <summary>Opens a timing scope for <paramref name="stage"/>.</summary>
    /// <param name="stage">Short identifier, e.g. "export", "parse", "total".</param>
    /// <param name="extras">Pre-built key=value pairs appended verbatim, e.g. "db=Foo plc=CPU_1".</param>
    public static OpenTiming Stage(string stage, string extras = "")
        => new OpenTiming(stage, extras);

    /// <summary>
    /// Appends additional size-predictor pairs to the log line.
    /// Call any time before <see cref="Dispose"/>.
    /// </summary>
    public OpenTiming AddPredictors(string kvPairs)
    {
        _predictors = string.IsNullOrEmpty(_predictors)
            ? kvPairs
            : _predictors + " " + kvPairs;
        return this;
    }

    /// <summary>Stops the stopwatch and emits one OPEN-TIMING log line.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sw.Stop();

        var parts = "OPEN-TIMING stage=" + _stage;
        if (!string.IsNullOrEmpty(_extras)) parts += " " + _extras;
        parts += " ms=" + _sw.ElapsedMilliseconds;
        if (!string.IsNullOrEmpty(_predictors)) parts += " " + _predictors;

        Log.Information(parts);
    }
}
