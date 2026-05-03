using System.Threading;
using System.Threading.Tasks;

namespace BlockParam.Updates;

/// <summary>
/// Lightweight, fire-and-forget update check against GitHub Releases.
/// All methods are safe to call from a background thread and never throw.
/// </summary>
public interface IUpdateCheckService
{
    /// <summary>
    /// Returns the latest cached release if one is on disk, regardless of
    /// staleness — used by the dialog at open time so the badge can render
    /// instantly without waiting on the network.
    /// </summary>
    UpdateInfo? GetCached();

    /// <summary>
    /// Checks for an update.
    ///
    /// Cache-first: when the cache file is &lt; cache TTL old, returns the
    /// cached result without touching the network. Otherwise issues one
    /// anonymous GET to GitHub (3 s timeout). Network failures are
    /// swallowed and resolve to the previous cache (if any) — never an
    /// exception.
    /// </summary>
    /// <returns>
    /// The latest release info, or null when update checks are disabled
    /// or the GitHub call failed and there is no usable cache.
    /// </returns>
    Task<UpdateInfo?> CheckAsync(CancellationToken ct = default);
}
