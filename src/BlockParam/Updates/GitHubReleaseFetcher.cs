using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlockParam.Diagnostics;
using Newtonsoft.Json.Linq;

namespace BlockParam.Updates;

/// <summary>
/// Anonymous GitHub Releases fetcher. Hits the public API at most once
/// per check, swallows every error to a returned <c>null</c>, and uses
/// a 3-second timeout per the issue spec — air-gapped TIA workstations
/// must never have their dialog open delayed by a hung connection.
/// </summary>
public sealed class GitHubReleaseFetcher : IReleaseFetcher
{
    private const string DefaultEndpoint =
        "https://api.github.com/repos/Sawascwoolf/BlockParam/releases/latest";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    // One static client; matches the OnlineLicenseService pattern. Setting
    // ServicePointManager here is safe — if licensing already opted into
    // TLS 1.2/1.3 the assignment is a no-op.
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string _endpoint;

    public GitHubReleaseFetcher(string? endpoint = null)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint!;
    }

    public async Task<UpdateInfo?> FetchLatestAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
            // GitHub rejects API requests without a User-Agent.
            request.Headers.Add("User-Agent", $"BlockParam/{GetAssemblyVersion()}");
            request.Headers.Add("Accept", "application/vnd.github+json");

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(RequestTimeout);

            using var response = await Http.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Information("UpdateCheck: GitHub returned {Status}", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseRelease(body);
        }
        catch (Exception ex)
        {
            Log.Information("UpdateCheck: fetch failed silently ({Type}: {Message})",
                ex.GetType().Name, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Tolerant parse — only the four fields we need. A schema change on
    /// GitHub's side won't crash, it'll just yield an UpdateInfo with the
    /// missing fields blank.
    /// </summary>
    internal static UpdateInfo? ParseRelease(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var token = JToken.Parse(json);
            if (token.Type != JTokenType.Object) return null;

            var obj = (JObject)token;
            var info = new UpdateInfo
            {
                TagName = (string?)obj["tag_name"] ?? "",
                Name = (string?)obj["name"] ?? "",
                HtmlUrl = (string?)obj["html_url"] ?? "",
                Body = (string?)obj["body"] ?? "",
                PreRelease = (bool?)obj["prerelease"] ?? false,
                PublishedAt = (DateTime?)obj["published_at"],
            };
            return string.IsNullOrEmpty(info.TagName) ? null : info;
        }
        catch
        {
            return null;
        }
    }

    private static string GetAssemblyVersion() =>
        typeof(GitHubReleaseFetcher).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static HttpClient CreateHttpClient()
    {
        System.Net.ServicePointManager.SecurityProtocol =
            System.Net.SecurityProtocolType.Tls12 |
            System.Net.SecurityProtocolType.Tls13;

        var handler = new HttpClientHandler
        {
            UseProxy = true,
            UseDefaultCredentials = true
        };
        return new HttpClient(handler)
        {
            // Belt-and-suspenders: per-request CancelAfter is the primary
            // bound; this is a hard cap if the linked token stops working.
            Timeout = TimeSpan.FromSeconds(5)
        };
    }
}
