using Newtonsoft.Json;

namespace BlockParam.Updates;

/// <summary>
/// Persisted in <c>%APPDATA%\BlockParam\config.json</c> under the
/// <c>updateCheck</c> key. An IT-deployed override at
/// <c>%PROGRAMDATA%\BlockParam\config.json</c> wins when present
/// (matches the managed-license pattern from #20).
/// </summary>
public sealed class UpdateCheckSettings
{
    /// <summary>
    /// Master opt-out. When false the service never hits the network and
    /// the dialog never shows the "update available" badge. Air-gapped
    /// engineering networks set this via GPO.
    /// </summary>
    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Show pre-release tags as updates too. Off by default — most users
    /// shouldn't be nudged toward an rc build.
    /// </summary>
    [JsonProperty("includePrereleases")]
    public bool IncludePrereleases { get; set; }

    /// <summary>
    /// Tag the user clicked "Skip this version" on (e.g. <c>v0.4.0</c>).
    /// Suppress the badge while a release with this tag is the latest;
    /// re-show on any newer version.
    /// </summary>
    [JsonProperty("skippedVersion")]
    public string? SkippedVersion { get; set; }
}
