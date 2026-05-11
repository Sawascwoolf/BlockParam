using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Types;
using BlockParam.Diagnostics;
using BlockParam.Localization;

namespace BlockParam.Services;

/// <summary>
/// Re-exports every UDT under a PLC whose TIA <c>ModifiedDate</c> /
/// <c>InterfaceModifiedDate</c> is newer than the cached XML on disk (or whose
/// cache file is missing). Precise replacement for a time-based TTL — only
/// stale entries get touched.
///
/// Inconsistent UDTs surface TIA's per-type inconsistency exception during
/// export; those are collected and offered as a single "compile and retry"
/// prompt via <see cref="InconsistentUdtRetry"/> (#27).
/// </summary>
public interface IUdtCacheRefresher
{
    /// <summary>
    /// Refreshes the UDT XML cache under <paramref name="exportDir"/> for
    /// <paramref name="plcSoftware"/>. Returns the number of files written
    /// (initial pass + successful post-prompt retries).
    /// </summary>
    int Refresh(PlcSoftware plcSoftware, string exportDir);
}

public sealed class UdtCacheRefresher : IUdtCacheRefresher
{
    private readonly IUserPrompt _prompt;

    public UdtCacheRefresher(IUserPrompt prompt)
    {
        _prompt = prompt;
    }

    public int Refresh(PlcSoftware plcSoftware, string exportDir)
    {
        Directory.CreateDirectory(exportDir);
        int refreshed = 0;
        // Each entry captures its PlcType via closures; nothing of the TIA object
        // survives outside this method's stack frame — V20 Add-In Publisher rejects
        // engineering-object types stored in fields.
        var inconsistent = new List<(string displayName, Func<bool> compile, Func<bool> reExport)>();

        var typeGroup = plcSoftware.TypeGroup;
        foreach (var (type, groupPath) in EnumerateTypesRecursive(typeGroup, parentPath: null))
            refreshed += ExportIfStale(type, exportDir, groupPath, inconsistent);

        if (inconsistent.Count > 0)
        {
            refreshed += InconsistentUdtRetry.RetryAfterCompile(
                inconsistent,
                nameOf: i => i.displayName,
                tryCompile: i => i.compile(),
                tryReExport: i => i.reExport(),
                askUser: AskUserToCompile);
        }

        return refreshed;
    }

    private bool AskUserToCompile(IReadOnlyList<string> udtNames)
    {
        var ok = _prompt.AskYesNo(
            title: Res.Get("Udt_InconsistentPromptTitle"),
            message: Res.Format("Udt_InconsistentPrompt", udtNames.Count, string.Join(", ", udtNames)));
        if (!ok) Log.Information("User declined compile for {Count} inconsistent UDT(s)", udtNames.Count);
        return ok;
    }

    private static int ExportIfStale(
        PlcType type, string exportDir, string? groupPath,
        List<(string displayName, Func<bool> compile, Func<bool> reExport)> inconsistent)
    {
        var filePath = Path.Combine(exportDir, $"{FileNameFor(type, groupPath)}.xml");
        var displayName = groupPath == null ? type.Name : $"{groupPath}/{type.Name}";

        try
        {
            var tiaModified = type.ModifiedDate;
            try
            {
                var interfaceModified = type.InterfaceModifiedDate;
                if (interfaceModified > tiaModified) tiaModified = interfaceModified;
            }
            catch { /* some types may not expose this — fall back to ModifiedDate only */ }

            if (File.Exists(filePath))
            {
                var fileMtime = File.GetLastWriteTime(filePath);
                if (fileMtime >= tiaModified) return 0;
            }

            File.Delete(filePath);
            type.Export(new FileInfo(filePath), ExportOptions.WithDefaults);
            return 1;
        }
        catch (Exception ex) when (InconsistencyDetector.Matches(ex))
        {
            Log.Warning("UDT '{Name}' cannot be exported: inconsistent — will offer compile", displayName);
            var capturedType = type;
            inconsistent.Add((
                displayName,
                compile: () => TryCompileUdt(capturedType, displayName),
                reExport: () => TryReExportUdt(capturedType, filePath, displayName)));
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to refresh UDT cache for {Name}", displayName);
            return 0;
        }
    }

    private static bool TryCompileUdt(PlcType type, string displayName)
    {
        try
        {
            var compilable = type.GetService<ICompilable>();
            if (compilable == null)
            {
                Log.Warning("No ICompilable service found for UDT {Name}", displayName);
                return false;
            }
            var result = compilable.Compile();
            Log.Information("Compiled UDT {Name}: {State}", displayName, result.State);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to compile UDT {Name}", displayName);
            return false;
        }
    }

    private static bool TryReExportUdt(PlcType type, string filePath, string displayName)
    {
        try
        {
            if (File.Exists(filePath)) File.Delete(filePath);
            type.Export(new FileInfo(filePath), ExportOptions.WithDefaults);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to re-export UDT {Name} after compile", displayName);
            return false;
        }
    }

    private static IEnumerable<(PlcType type, string? groupPath)> EnumerateTypesRecursive(
        PlcTypeGroup group, string? parentPath)
    {
        foreach (var type in group.Types)
            yield return (type, parentPath);

        foreach (var sub in group.Groups)
        {
            var subPath = parentPath == null ? sub.Name : $"{parentPath}/{sub.Name}";
            foreach (var entry in EnumerateTypesRecursive(sub, subPath))
                yield return entry;
        }
    }

    private static string FileNameFor(PlcType type, string? groupPath)
    {
        var raw = groupPath == null ? type.Name : $"{groupPath.Replace('/', '_')}_{type.Name}";
        return SafeFileName.Sanitize(raw);
    }
}
