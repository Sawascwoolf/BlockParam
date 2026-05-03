using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BlockParam.Diagnostics;
using Newtonsoft.Json;

namespace BlockParam.Updates;

/// <summary>
/// Cache-fronted update check.
///
/// Cache layout (<c>%APPDATA%\BlockParam\update-check.json</c>):
/// <code>
/// { "checkedAt": "2026-05-02T08:00:00Z", "release": { ... UpdateInfo ... } }
/// </code>
/// A null/missing release is also cached — so a successful "no newer
/// version" response doesn't hammer the API on every dialog open.
///
/// Network failures never bubble out: <see cref="CheckAsync"/> returns
/// the previous cache (if any), or null. By contract no consumer should
/// ever have to <c>try/catch</c> around it.
/// </summary>
public sealed class UpdateCheckService : IUpdateCheckService
{
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromHours(24);

    private readonly IReleaseFetcher _fetcher;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<UpdateCheckSettings> _readSettings;
    private readonly VersionTag _currentVersion;
    private readonly string _cachePath;
    private readonly TimeSpan _cacheTtl;

    public UpdateCheckService(
        IReleaseFetcher fetcher,
        VersionTag currentVersion,
        string cachePath,
        Func<UpdateCheckSettings> readSettings,
        Func<DateTime>? utcNow = null,
        TimeSpan? cacheTtl = null)
    {
        _fetcher = fetcher;
        _currentVersion = currentVersion;
        _cachePath = cachePath;
        _readSettings = readSettings;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _cacheTtl = cacheTtl ?? DefaultCacheTtl;
    }

    public UpdateInfo? GetCached()
    {
        var cache = LoadCache();
        return AsActionable(cache?.Release);
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var settings = SafeReadSettings();
        if (!settings.Enabled)
            return null;

        var cache = LoadCache();
        if (cache != null && _utcNow() - cache.CheckedAt < _cacheTtl)
            return AsActionable(cache.Release, settings);

        UpdateInfo? release;
        try
        {
            release = await _fetcher.FetchLatestAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The fetcher contract is "never throw," but defend against a
            // misbehaving custom IReleaseFetcher (or test double) so the
            // UI thread can't be poisoned by an update check.
            Log.Warning(ex, "UpdateCheck: fetcher threw — falling back to previous cache");
            release = cache?.Release;
        }

        // Fetch failed entirely AND we have nothing on disk: don't write
        // an empty cache, callers should be able to retry sooner.
        if (release == null && cache == null)
            return null;

        SaveCache(new CacheEnvelope { CheckedAt = _utcNow(), Release = release ?? cache?.Release });
        return AsActionable(release ?? cache?.Release, settings);
    }

    private UpdateInfo? AsActionable(UpdateInfo? release) =>
        AsActionable(release, SafeReadSettings());

    private UpdateInfo? AsActionable(UpdateInfo? release, UpdateCheckSettings settings)
    {
        if (release == null) return null;
        if (!settings.Enabled) return null;
        if (release.PreRelease && !settings.IncludePrereleases) return null;
        if (!VersionTag.TryParse(release.TagName, out var releaseVersion)) return null;
        if (releaseVersion.CompareTo(_currentVersion) <= 0) return null;

        return release;
    }

    private UpdateCheckSettings SafeReadSettings()
    {
        try { return _readSettings() ?? new UpdateCheckSettings(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "UpdateCheck: cannot read settings, defaulting to enabled");
            return new UpdateCheckSettings();
        }
    }

    private CacheEnvelope? LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            return JsonConvert.DeserializeObject<CacheEnvelope>(File.ReadAllText(_cachePath));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UpdateCheck: cannot read cache {Path}", _cachePath);
            return null;
        }
    }

    private void SaveCache(CacheEnvelope envelope)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_cachePath, JsonConvert.SerializeObject(envelope, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UpdateCheck: cannot write cache {Path}", _cachePath);
        }
    }

    // Public so Newtonsoft.Json can deserialize under TIA's CAS sandbox
    // (matches the LocalUsageTracker.UsageData rationale).
    public sealed class CacheEnvelope
    {
        [JsonProperty("checkedAt")]
        public DateTime CheckedAt { get; set; }

        [JsonProperty("release")]
        public UpdateInfo? Release { get; set; }
    }
}
