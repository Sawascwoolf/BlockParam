using Newtonsoft.Json;

namespace BlockParam.Updates;

/// <summary>
/// One parsed GitHub Release entry (the subset BlockParam cares about).
/// Persisted as-is into the on-disk cache so a stale-cache load yields the
/// same object the live fetch would have.
/// </summary>
public sealed class UpdateInfo
{
    [JsonProperty("tagName")]
    public string TagName { get; set; } = "";

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("htmlUrl")]
    public string HtmlUrl { get; set; } = "";

    /// <summary>Raw markdown release notes from the GitHub API.</summary>
    [JsonProperty("body")]
    public string Body { get; set; } = "";

    [JsonProperty("prerelease")]
    public bool PreRelease { get; set; }

    [JsonProperty("publishedAt")]
    public DateTime? PublishedAt { get; set; }
}
