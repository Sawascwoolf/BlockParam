using System;
using System.IO;
using Newtonsoft.Json;
using BlockParam.Licensing;

namespace BlockParam.DevLauncher;

/// <summary>
/// Seeds a throwaway storage directory that makes a real
/// <see cref="OnlineLicenseService"/> report Pro without contacting a server —
/// an activated <c>license.json</c> plus a fresh, still-in-grace
/// <c>license_cache.dat</c>.
///
/// <para>
/// Used by the license dialog capture (<see cref="LicenseCapture"/>) and by
/// capture-script mode in <see cref="Program"/> (#198), so the workflow video
/// records the Pro status line instead of the free-tier quota counter.
/// The caller's real <c>%APPDATA%\BlockParam</c> is never touched.
/// </para>
/// </summary>
internal static class ProLicenseSandbox
{
    /// <summary>
    /// Creates a fresh sandbox directory under the temp folder, seeds it with
    /// an active Pro license, and returns its path. The caller owns the
    /// directory and may delete it when done.
    /// </summary>
    public static string CreateProStorage(string licenseKey = "PRO-CAPTURE-0000-0000")
    {
        var storage = Path.Combine(Path.GetTempPath(),
            $"BlockParamProSandbox_{Guid.NewGuid():N}");
        Directory.CreateDirectory(storage);
        WriteLicenseData(storage, licenseKey);
        WriteProCache(storage);
        return storage;
    }

    /// <summary>Writes an activated <c>license.json</c> for <paramref name="key"/>.</summary>
    public static void WriteLicenseData(string storage, string key)
    {
        var data = new OnlineLicenseService.LicenseData
        {
            LicenseKey = key,
            InstanceId = Guid.NewGuid().ToString(),
            ActivatedAt = DateTime.UtcNow,
        };
        File.WriteAllText(Path.Combine(storage, "license.json"),
            JsonConvert.SerializeObject(data, Formatting.Indented));
    }

    /// <summary>
    /// Writes a server response cache dated now, so the service grants Pro
    /// from cache alone (inside its grace period) and never needs the network.
    /// </summary>
    public static void WriteProCache(string storage)
    {
        var cache = new OnlineLicenseService.CachedLicenseResponse
        {
            ReceivedAtUtc = DateTime.UtcNow,
            ExpiresAt = null,
            MaxConcurrent = 1,
            ActiveSessions = 1,
        };
        var json = JsonConvert.SerializeObject(cache);
        File.WriteAllBytes(Path.Combine(storage, "license_cache.dat"),
            Obfuscation.Obfuscate(json));
    }
}
