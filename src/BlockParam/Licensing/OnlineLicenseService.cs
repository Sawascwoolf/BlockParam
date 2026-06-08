using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BlockParam.Diagnostics;
using BlockParam.Services;
using BlockParam.Services.Storage;

namespace BlockParam.Licensing;

/// <summary>
/// License service that validates Pro licenses via periodic heartbeats to a remote server.
/// Tracks concurrent sessions to prevent license sharing across VMs.
/// Falls back gracefully: cached license (48h) → free tier.
///
/// Concurrency model (#170 audit):
///   - <c>lock (_lock)</c> guards mutable state: <c>_licenseData</c>, <c>_cache</c>,
///     <c>_proActive</c>, <c>_retryCount</c>. All writes go through the lock.
///   - <c>volatile bool _proActive</c>: written under <c>_lock</c> (atomically with
///     the cache/licenseData it derives from); read lock-free from UI binding threads
///     via <see cref="IsProActive"/>/<see cref="CurrentTier"/>. The <c>volatile</c>
///     ensures the lock-free reads see the latest write.
///   - <c>volatile bool _disposed</c>: lifecycle flag, single-writer (<see cref="Dispose"/>),
///     multi-reader. No lock needed — <c>volatile</c> is the correct primitive.
///   - <c>_heartbeatTimer</c>: owned via <c>Interlocked.Exchange</c>. Cannot use
///     <c>_lock</c> because timer callbacks acquire the lock — holding it while
///     disposing the timer would risk deadlock.
///   - <c>_isManagedKey</c>: set once in the constructor, read-only after construction.
///   - Lock-free reads of <c>_licenseData</c> in <see cref="StartHeartbeat"/> and
///     <see cref="SendHeartbeatAsync"/> are fast-path null checks. Safe because
///     <c>LicenseData</c> instances are effectively immutable after assignment
///     (only ever replaced, never mutated in place) and reference writes are
///     atomic on .NET. The full state is re-read under lock when needed.
///   - HTTP / file I/O MUST run outside <c>lock (_lock)</c>. State is captured under
///     the lock, then persisted after releasing it.
/// </summary>
public class OnlineLicenseService : ILicenseService
{
    public const string DefaultServerUrl = "https://license.lautimweb.de";

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromHours(2);
    private static readonly TimeSpan CacheGracePeriod = TimeSpan.FromHours(48);
    private static readonly int MaxRetryAttempts = 4;

    private readonly IBlockParamStorage _storage;
    private readonly StoragePath _licenseDataPath;
    private readonly StoragePath _cachePath;
    private readonly StoragePath _sharedLicenseFilePath;
    private readonly string? _serverBaseUrl;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();

    private Timer? _heartbeatTimer;
    private LicenseData? _licenseData;
    private CachedLicenseResponse? _cache;
    private volatile bool _proActive;
    private volatile bool _disposed;
    private bool _isManagedKey;
    private int _retryCount;

    /// <summary>
    /// Default machine-wide license file path used by multi-seat deployments
    /// (#20). IT pushes a key to this path via batch / SCCM / Intune / GPO and
    /// every seat on the machine adopts it on next start. UNC / network
    /// paths are explicitly out of scope — only this local path is read.
    /// </summary>
    public static string DefaultSharedLicenseFilePath => AppDirectories.SharedLicenseFile;

    public OnlineLicenseService(
        string storagePath,
        string? serverBaseUrl,
        Func<DateTime>? utcNow = null,
        string? sharedLicenseFilePath = null)
        : this(FileSystemBlockParamStorage.Instance,
               StoragePath.FromAbsolute(storagePath),
               serverBaseUrl,
               utcNow,
               string.IsNullOrWhiteSpace(sharedLicenseFilePath)
                   ? default
                   : StoragePath.FromAbsolute(sharedLicenseFilePath!))
    {
    }

    public OnlineLicenseService(
        IBlockParamStorage storage,
        StoragePath storageDir,
        string? serverBaseUrl,
        Func<DateTime>? utcNow = null,
        StoragePath sharedLicenseFilePath = default)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _licenseDataPath = storageDir / "license.json";
        _cachePath = storageDir / "license_cache.dat";
        _sharedLicenseFilePath = sharedLicenseFilePath;
        _serverBaseUrl = serverBaseUrl?.TrimEnd('/');
        _utcNow = utcNow ?? (() => DateTime.UtcNow);

        _licenseData = LoadLicenseData();
        _cache = LoadCache();
        AdoptSharedLicenseKeyIfPresent();
        EvaluateTier();
    }

    public LicenseTier CurrentTier => _proActive ? LicenseTier.Pro : LicenseTier.Free;
    public bool IsProActive => _proActive;

    public event EventHandler? LicenseStateChanged;

    public LicenseInfo GetLicenseInfo()
    {
        lock (_lock)
        {
            return new LicenseInfo
            {
                Tier = CurrentTier,
                LicenseKey = _licenseData?.LicenseKey,
                ValidUntil = _cache?.ExpiresAt,
                MaxConcurrent = _cache?.MaxConcurrent ?? 0,
                CurrentConcurrent = _cache?.ActiveSessions ?? 0,
                IsServerReachable = _cache != null && (_utcNow() - _cache.ReceivedAtUtc).TotalMinutes < 5,
                ErrorMessage = _cache?.ErrorMessage,
                IsManagedKey = _isManagedKey,
                ManagedKeyFilePath = _isManagedKey ? ManagedKeyFilePathString() : null
            };
        }
    }

    public async Task<LicenseActivationResult> ActivateKeyAsync(string licenseKey)
    {
        if (string.IsNullOrWhiteSpace(_serverBaseUrl))
            return LicenseActivationResult.ServerError("No license server configured");

        // Reuse existing instanceId if available, otherwise generate once and persist
        var instanceId = _licenseData?.InstanceId ?? Guid.NewGuid().ToString();

        try
        {
            // Dictionary, not anonymous type — under TIA's partial-trust CAS
            // sandbox Newtonsoft cannot reflect into the compiler-generated
            // `internal sealed` anonymous type from another assembly.
            var body = new Dictionary<string, object?>
            {
                ["licenseKey"] = licenseKey,
                ["instanceId"] = instanceId,
                ["machineName"] = Environment.MachineName,
                ["addinVersion"] = GetAddinVersion(),
            };

            var response = await PostAsync("/api/license/activate", body);

            if (response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                var json = await response.Content.ReadAsStringAsync();
                var result = JObject.Parse(json);

                LicenseData dataToSave;
                CachedLicenseResponse cacheToSave;
                lock (_lock)
                {
                    _licenseData = new LicenseData
                    {
                        LicenseKey = licenseKey,
                        InstanceId = instanceId,
                        ActivatedAt = _utcNow()
                    };
                    dataToSave = _licenseData;

                    _cache = new CachedLicenseResponse
                    {
                        ReceivedAtUtc = _utcNow(),
                        ExpiresAt = result["expiresAt"]?.ToObject<DateTime?>(),
                        MaxConcurrent = result["maxConcurrent"]?.ToObject<int>() ?? 1,
                        ActiveSessions = 1
                    };
                    cacheToSave = _cache;
                    EvaluateTier();
                }

                SaveLicenseData(dataToSave);
                SaveCache(cacheToSave);
                RaiseLicenseStateChanged();

                return LicenseActivationResult.Success(GetLicenseInfo());
            }

            if ((int)response.StatusCode == 409)
            {
                var json = await response.Content.ReadAsStringAsync();
                var result = JObject.Parse(json);
                var current = result["current"]?.ToObject<int>() ?? 0;
                var max = result["max"]?.ToObject<int>() ?? 0;
                return LicenseActivationResult.TooManySessions(current, max);
            }

            return LicenseActivationResult.InvalidKey();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Warning(ex, "License activation failed: server unreachable");
            return LicenseActivationResult.ServerError(ex.Message);
        }
    }

    public void DeactivateKey()
    {
        string? keyToDeactivate = null;
        string? instanceToDeactivate = null;

        lock (_lock)
        {
            if (_licenseData != null)
            {
                keyToDeactivate = _licenseData.LicenseKey;
                instanceToDeactivate = _licenseData.InstanceId;
            }

            _licenseData = null;
            _cache = null;
            _proActive = false;
        }

        if (keyToDeactivate != null)
            _ = SendDeactivateAsync(keyToDeactivate!, instanceToDeactivate!);
        BestEffortDelete(_licenseDataPath);
        BestEffortDelete(_cachePath);

        StopHeartbeat();
        RaiseLicenseStateChanged();
    }

    public void StartHeartbeat()
    {
        if (_disposed || _licenseData == null || string.IsNullOrWhiteSpace(_serverBaseUrl))
            return;

        lock (_lock) { _retryCount = 0; }

        var fresh = new Timer(
            _ => _ = SendHeartbeatAsync(),
            null,
            TimeSpan.Zero,  // First heartbeat immediately
            Timeout.InfiniteTimeSpan);  // One-shot; re-scheduled after each beat

        // Atomic swap so a concurrent StopHeartbeat / Dispose can't leak the
        // previous timer (or this one — see post-swap _disposed check below).
        var previous = Interlocked.Exchange(ref _heartbeatTimer, fresh);
        previous?.Dispose();

        // Lost the race against Dispose: the timer we just installed will
        // never be reaped by Dispose's Exchange because Dispose already ran.
        if (_disposed)
        {
            var leaked = Interlocked.Exchange(ref _heartbeatTimer, null);
            leaked?.Dispose();
        }
    }

    private void ScheduleNextHeartbeat(TimeSpan delay)
    {
        try
        {
            _heartbeatTimer?.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) { /* timer was disposed during shutdown */ }
    }

    public void StopHeartbeat()
    {
        // #20: Only stop the local timer here. Do NOT deactivate the server session —
        // that turned every dialog close into a "session ended", so the next open came
        // back as Free and the user had to re-activate forever. Deactivation is now
        // limited to the explicit DeactivateKey path (Remove-key button / uninstall).
        var timer = Interlocked.Exchange(ref _heartbeatTimer, null);
        timer?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var timer = Interlocked.Exchange(ref _heartbeatTimer, null);
        timer?.Dispose();
    }

    // --- Heartbeat ---

    private async Task SendHeartbeatAsync()
    {
        if (_licenseData == null || string.IsNullOrWhiteSpace(_serverBaseUrl))
            return;

        try
        {
            var body = new Dictionary<string, object?>
            {
                ["licenseKey"] = _licenseData.LicenseKey,
                ["instanceId"] = _licenseData.InstanceId,
            };

            var response = await PostAsync("/api/license/heartbeat", body);

            if (response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                var json = await response.Content.ReadAsStringAsync();
                var result = JObject.Parse(json);

                CachedLicenseResponse cacheToSave;
                lock (_lock)
                {
                    _cache = new CachedLicenseResponse
                    {
                        ReceivedAtUtc = _utcNow(),
                        ExpiresAt = _cache?.ExpiresAt,
                        MaxConcurrent = result["maxSessions"]?.ToObject<int>() ?? 1,
                        ActiveSessions = result["activeSessions"]?.ToObject<int>() ?? 1
                    };
                    cacheToSave = _cache;
                    EvaluateTier();
                    _retryCount = 0;
                }

                // Safe outside lock: one-shot timer + ScheduleNextHeartbeat serializes beats.
                SaveCache(cacheToSave);
                ScheduleNextHeartbeat(HeartbeatInterval);
                RaiseLicenseStateChanged();
                return;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                lock (_lock)
                {
                    if (_cache != null)
                        _cache.ErrorMessage = "License rejected by server";
                    _proActive = false;
                    _retryCount = 0; // Server responded clearly — no retry
                }
                ScheduleNextHeartbeat(HeartbeatInterval);
                RaiseLicenseStateChanged();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Read+update _retryCount under the lock so a rapid Stop/Start
            // cycle (which leaves an in-flight callback) can't race a fresh
            // heartbeat resetting the counter.
            int attempt;
            TimeSpan delay;
            lock (_lock)
            {
                EvaluateTier(); // Re-evaluate with cache
                attempt = _retryCount + 1;
                if (_retryCount < MaxRetryAttempts)
                {
                    // Exponential backoff: 30s, 60s, 120s, 240s, then back to 2h
                    delay = TimeSpan.FromSeconds(30 * Math.Pow(2, _retryCount));
                    _retryCount++;
                }
                else
                {
                    delay = HeartbeatInterval;
                    _retryCount = 0;
                }
            }
            Log.Warning(ex, "Heartbeat failed — using cached license (retry {Retry}/{Max})",
                attempt, MaxRetryAttempts);
            ScheduleNextHeartbeat(delay);
            RaiseLicenseStateChanged();
        }
    }

    private async Task SendDeactivateAsync(string licenseKey, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(_serverBaseUrl))
            return;

        try
        {
            var body = new Dictionary<string, object?>
            {
                ["licenseKey"] = licenseKey,
                ["instanceId"] = instanceId,
            };
            await PostAsync("/api/license/deactivate", body);
        }
        catch
        {
            // Fire-and-forget: swallow errors
        }
    }

    // --- Shared license file (#20: multi-seat deployments) ---

    /// <summary>
    /// If a managed license file exists at <see cref="_sharedLicenseFilePath"/> and
    /// holds a key that differs from the per-user cache, replace the cached key.
    /// IT rolls out / rotates the key by writing this file via deployment tooling
    /// (batch / SCCM / Intune / GPO); every seat picks up the change on next start
    /// without user interaction. The cached server response is invalidated when the
    /// key changes — the heartbeat will re-validate against the server.
    /// Called only from the constructor (single-threaded) — intentionally
    /// lock-free. Do not move into a post-construction code path without
    /// adding lock protection (which would reintroduce I/O-in-lock).
    /// </summary>
    private void AdoptSharedLicenseKeyIfPresent()
    {
        // Local copy of the readonly struct — never call instance members on
        // the field directly (partial-trust IL gate, see UiZoomService).
        var shared = _sharedLicenseFilePath;
        if (shared.IsEmpty) return;

        var sharedKey = TryReadSharedLicenseKey(shared);
        if (string.IsNullOrEmpty(sharedKey)) return;

        _isManagedKey = true;
        var sharedPath = shared.FullPath;

        // Same key as already cached → keep existing instanceId and cache so we
        // don't churn the server-side session on every Add-In start.
        if (_licenseData != null &&
            string.Equals(_licenseData.LicenseKey, sharedKey, StringComparison.Ordinal))
        {
            // Logged so support can grep "did this seat see the file at all on
            // this start?" — fires once per TIA launch, not per heartbeat, so
            // it doesn't flood the log even on the same-key (no-op) path.
            Log.Information("Managed license file at {Path} matches cached key — no change",
                sharedPath);
            return;
        }

        // Key changed (rotation or first-time rollout). Replace the local copy and
        // drop the stale cache so EvaluateTier doesn't grant Pro on the old key.
        Log.Information("Adopting managed license key from {Path} (rotation or first rollout)",
            sharedPath);
        _licenseData = new LicenseData
        {
            LicenseKey = sharedKey,
            InstanceId = _licenseData?.InstanceId ?? Guid.NewGuid().ToString(),
            ActivatedAt = _utcNow()
        };
        SaveLicenseData(_licenseData);

        _cache = null;
        BestEffortDelete(_cachePath);
    }

    private string? TryReadSharedLicenseKey(StoragePath path)
    {
        try
        {
            if (!_storage.FileExists(path)) return null;
            var content = _storage.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(content) ? null : content;
        }
        catch (Exception ex)
        {
            // Unreadable shared file is non-fatal — fall back to user-local cache.
            Log.Warning(ex, "Cannot read managed license file at {Path}", path.FullPath);
            return null;
        }
    }

    // --- Tier Evaluation ---

    private void EvaluateTier()
    {
        if (_licenseData == null)
        {
            _proActive = false;
            return;
        }

        // Has a valid cached response within grace period?
        if (_cache != null && !_cache.HasError)
        {
            var age = _utcNow() - _cache.ReceivedAtUtc;
            _proActive = age < CacheGracePeriod;

            if (!_proActive && _cache.ErrorMessage == null)
                _cache.ErrorMessage = "Offline grace period expired";
            return;
        }

        _proActive = false;
    }

    // --- HTTP ---

    private async Task<HttpResponseMessage> PostAsync(string path, object body)
    {
        var json = JsonConvert.SerializeObject(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, _serverBaseUrl + path)
        {
            Content = content
        };
        request.Headers.Add("User-Agent", $"BlockParam/{GetAddinVersion()}");

        return await Http.SendAsync(request);
    }

    private static HttpClient CreateHttpClient()
    {
        // .NET Framework 4.x defaults to TLS 1.0 — explicitly enable TLS 1.2/1.3.
        // NOTE: ServicePointManager.SecurityProtocol is a process-wide setting.
        // In a TIA Portal Add-In this runs in the TIA process, so this affects all
        // .NET HTTP connections. TLS 1.2+ is the safe default; TIA itself requires it
        // for online connections, so this should not cause compatibility issues.
        System.Net.ServicePointManager.SecurityProtocol =
            System.Net.SecurityProtocolType.Tls12 |
            System.Net.SecurityProtocolType.Tls13;

        var handler = new HttpClientHandler
        {
            UseProxy = true,
            UseDefaultCredentials = true  // NTLM/Kerberos proxy auth
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    // --- Persistence ---
    //
    // All file I/O goes through IBlockParamStorage so tests can substitute an
    // in-memory fake and so the "no new File.*/Directory.* outside the storage
    // layer" guardrail (#85) stays satisfied. WriteAll* auto-creates parents,
    // which is why there's no explicit EnsureDirectory.

    private LicenseData? LoadLicenseData()
    {
        var path = _licenseDataPath;
        try
        {
            if (!_storage.FileExists(path)) return null;
            var json = _storage.ReadAllText(path);
            return JsonConvert.DeserializeObject<LicenseData>(json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cannot read license data from {Path}", path.FullPath);
            return null;
        }
    }

    private void SaveLicenseData(LicenseData data)
    {
        var path = _licenseDataPath;
        try
        {
            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            _storage.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cannot save license data to {Path}", path.FullPath);
        }
    }

    private CachedLicenseResponse? LoadCache()
    {
        var path = _cachePath;
        try
        {
            if (!_storage.FileExists(path)) return null;
            var bytes = _storage.ReadAllBytes(path);
            var json = Obfuscation.Deobfuscate(bytes);
            return JsonConvert.DeserializeObject<CachedLicenseResponse>(json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cannot read license cache from {Path}", path.FullPath);
            return null;
        }
    }

    private void SaveCache(CachedLicenseResponse cache)
    {
        var path = _cachePath;
        try
        {
            var json = JsonConvert.SerializeObject(cache);
            var bytes = Obfuscation.Obfuscate(json);
            _storage.WriteAllBytes(path, bytes);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cannot save license cache to {Path}", path.FullPath);
        }
    }

    private void BestEffortDelete(StoragePath path)
    {
        try { _storage.DeleteFile(path); }
        catch { /* best effort */ }
    }

    private string? ManagedKeyFilePathString()
    {
        // Property accessor on a readonly struct field needs a local copy
        // (partial-trust IL gate). Returns null when no shared file is
        // configured, mirroring the legacy nullable-string field semantics.
        var shared = _sharedLicenseFilePath;
        return shared.IsEmpty ? null : shared.FullPath;
    }

    private static string GetAddinVersion()
    {
        return typeof(OnlineLicenseService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private void RaiseLicenseStateChanged()
    {
        try { LicenseStateChanged?.Invoke(this, EventArgs.Empty); }
        catch { /* don't let subscriber exceptions crash the heartbeat */ }
    }

    // --- Internal models ---
    // Public so Newtonsoft.Json can reach the constructor under TIA's
    // partial-trust CAS sandbox — see UiZoomService.UiSettingsDto for context.

    public class LicenseData
    {
        public string? LicenseKey { get; set; }
        public string? InstanceId { get; set; }
        public DateTime ActivatedAt { get; set; }
    }

    public class CachedLicenseResponse
    {
        public DateTime ReceivedAtUtc { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public int MaxConcurrent { get; set; }
        public int ActiveSessions { get; set; }
        public string? ErrorMessage { get; set; }

        [JsonIgnore]
        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    }
}
