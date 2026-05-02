using System.Threading;
using System.Threading.Tasks;

namespace BlockParam.Updates;

/// <summary>
/// Single seam separating the update-check policy (cache TTL, version
/// compare, skip-version) from the actual HTTP call. Tests inject a
/// fake fetcher so the service can be exercised without a network or a
/// flaky <see cref="System.Net.Http.HttpClient"/>.
/// </summary>
public interface IReleaseFetcher
{
    /// <summary>
    /// Fetch the latest published release. Returns null on any failure —
    /// timeout, non-200 status, parse error, network down. Never throws.
    /// </summary>
    Task<UpdateInfo?> FetchLatestAsync(CancellationToken ct);
}
