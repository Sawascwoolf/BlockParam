using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BlockParam.Diagnostics;

/// <summary>
/// Minimal partial-trust-safe replacement for Serilog. Keeps the same
/// call-site shape (named placeholders like <c>"{Path}"</c>) so swapping
/// <c>using Serilog;</c> for <c>using BlockParam.Diagnostics;</c> is the
/// only edit at most call sites.
/// </summary>
/// <remarks>
/// Serilog 3.x does not pass IL verification under the TIA Add-In CAS
/// sandbox — the JIT verifier rejects assemblies that use unverifiable IL
/// before any granted permissions are even consulted. See
/// <c>docs/research/06-addin-deployment.md</c> for the spike that pinned
/// the failure to <c>Serilog.Parsing.PropertyToken.get_IsPositional</c>.
/// This shim uses only verifiable BCL calls.
/// </remarks>
public static class Log
{
    private static readonly object _gate = new();
    private static readonly Regex PlaceholderRegex = new(@"\{[^{}]+\}", RegexOptions.Compiled);

    public static void Information(string template, params object?[] args) => Write("INF", null, template, args);
    public static void Warning(string template, params object?[] args) => Write("WRN", null, template, args);
    public static void Warning(Exception? ex, string template, params object?[] args) => Write("WRN", ex, template, args);
    public static void Error(Exception? ex, string template, params object?[] args) => Write("ERR", ex, template, args);

    private static void Write(string level, Exception? ex, string template, object?[] args)
    {
        try
        {
            var line = Format(level, ex, template, args);
            var path = ResolveLogPath();
            lock (_gate) File.AppendAllText(path, line);
        }
        catch
        {
            // Logging must never crash the addin — swallow any I/O failure.
        }
    }

    private static string Format(string level, Exception? ex, string template, object?[] args)
    {
        var msg = SubstituteTemplate(template, args ?? Array.Empty<object?>());
        var sb = new StringBuilder(msg.Length + 64);
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        sb.Append(" [").Append(level).Append("] ").Append(msg).Append(Environment.NewLine);
        if (ex != null) sb.Append(ex).Append(Environment.NewLine);
        return sb.ToString();
    }

    private static string SubstituteTemplate(string template, object?[] args)
    {
        if (args.Length == 0) return template;

        // Rewrite Serilog-style "{Name}" / "{Name:format}" placeholders to
        // indexed "{0}" / "{0:format}" so string.Format consumes them in
        // the order they appear.
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
            // The template did not match the args — emit a useful fallback
            // rather than crashing the log call.
            return template + " | args=" + string.Join(", ", args);
        }
    }

    // Recomputed on every write so a TIA session that spans midnight rolls
    // over to the next day's file instead of appending to yesterday's. The
    // computation is cheap and Directory.CreateDirectory is idempotent.
    private static string ResolveLogPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlockParam", "logs");
        Directory.CreateDirectory(dir);
        var version = typeof(Log).Assembly.GetName().Version;
        var fileName = $"bulkchange-v{version}-{DateTime.Now:yyyy-MM-dd}.log";
        return Path.Combine(dir, fileName);
    }
}
