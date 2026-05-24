using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Injectable diagnostic sink for the PillMultiSelect control.
/// Defaults to no-op so vendoring projects pull in zero host helpers
/// (the control's README promises "BCL + WPF only"). The BlockParam host
/// wires <see cref="Sink"/> once at startup to forward into its own
/// <c>BlockParam.Diagnostics.Log</c>; vendoring projects either ignore it
/// or point it at their own logger.
/// </summary>
/// <remarks>
/// Call shape mirrors Serilog (<c>Information("hello {Name}", value)</c>)
/// so the existing diagnostic shims around the host->control DP boundary
/// (added in #141 to make empty-bubble bugs observable) stay one-line.
/// Implementation is partial-trust safe — no readonly-struct field access,
/// no unverifiable IL.
/// </remarks>
public static class MultiSelectLog
{
    private static readonly Regex PlaceholderRegex = new(@"\{[^{}]+\}", RegexOptions.Compiled);

    /// <summary>
    /// Receives formatted log messages. Null = silent (default). Set once
    /// at app startup; the control swallows any exception the sink throws.
    /// </summary>
    public static Action<string>? Sink { get; set; }

    public static void Information(string template, params object?[] args)
    {
        var sink = Sink;
        if (sink == null) return;
        try { sink(Format(template, args)); }
        catch
        {
            // Diagnostic sinks must never crash the UI thread.
        }
    }

    private static string Format(string template, object?[] args)
    {
        if (args == null || args.Length == 0) return template;

        int idx = 0;
        var rewritten = PlaceholderRegex.Replace(template, m =>
        {
            if (idx >= args.Length) return m.Value;
            var inner = m.Value.Substring(1, m.Value.Length - 2);
            var colon = inner.IndexOf(':');
            var fmt = colon >= 0 ? inner.Substring(colon) : "";
            var slot = "{" + idx + fmt + "}";
            idx++;
            return slot;
        });
        try
        {
            return string.Format(CultureInfo.InvariantCulture, rewritten, args);
        }
        catch
        {
            return template + " | args=" + string.Join(", ", args);
        }
    }
}
