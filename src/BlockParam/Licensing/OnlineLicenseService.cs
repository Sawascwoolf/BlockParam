using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BlockParam.Diagnostics;

namespace BlockParam.Licensing;

/// <summary>
/// License service that validates Pro licenses via periodic heartbeats to a remote server.
/// Tracks concurrent sessions to prevent license sharing across VMs.
/// Falls back gracefully: cached license (48h) → free tier.
/// </summary>
public class OnlineLicenseService : ILicenseService
{
    public const string DefaultServerUrl = "https://license.lautimweb.de";

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromHours(2);
    private static readonly TimeSpan CacheGracePeriod = TimeSpan.FromHours(48);
    private static readonly int MaxRetryAttempts = 4;

    private readonly string _storagePath;
    private readonly string? _serverBaseUrl;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();

    private Timer? _heartbeatTimer;
    private LicenseData? _licenseData;
    private CachedLicenseResponse? _cache;
    private volatile bool _proActive;
    private volatile bool _disposed;
    private int _retryCount;

    public OnlineLicenseService(
        string storagePath,
        string? serverBaseUrl,
        Func<DateTime>? utcNow = null)
    {
        _storagePath = storagePath;
        _serverBaseUrl = serverBaseUrl?.TrimEnd('/');
        _utcNow = utcNow ?? (() => DateTime.UtcNow);

        _licenseData = LoadLicenseData();
        _cache = LoadCache();
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
                ErrorMessage = _cache?.ErrorMessage
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

                lock (_lock)
                {
                    _licenseData = new LicenseData
                    {
                        LicenseKey = licenseKey,
                        InstanceId = instanceId,
                        ActivatedAt = _utcNow()
                    };
                    SaveLicenseData(_licenseData);

                    _cache = new CachedLicenseResponse
                    {
                        ReceivedAtUtc = _utcNow(),
                        ExpiresAt = result["expiresAt"]?.ToObject<DateTime?>(),
                        MaxConcurrent = result["maxConcurrent"]?.ToObject<int>() ?? 1,
                        ActiveSessions = 1
                    };
                    SaveCache(_cache);
                    EvaluateTier();
                }

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
        lock (_lock)
        {
            if (_licenseData != null)
            {
                // Fire-and-forget deactivation on server
                _ = SendDeactivateAsync(_licenseData.LicenseKey!, _licenseData.InstanceId!);
            }

            _licenseData = null;
            _cache = null;
            _proActive = false;
            DeleteFile(LicenseDataPath);
            DeleteFile(CachePath);
        }

        StopHeartbeat();
        RaiseLicenseStateChanged();
    }

    public void StartHeartbeat()
    {
        if (_disposed || _licenseData == null || string.IsNullOrWhiteSpace(_serverBaseUrl))
            return;

        StopHeartbeat();
        _retryCount = 0;
        _heartbeatTimer = new Timer(
            _ => _ = SendHeartbeatAsync(),
            null,
            TimeSpan.Zero,  // First heartbeat immediately
            Timeout.InfiniteTimeSpan);  // One-shot; re-scheduled after each beat
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

                lock (_lock)
                {
                    _cache = new CachedLicenseResponse
                    {
                        ReceivedAtUtc = _utcNow(),
                        ExpiresAt = _cache?.ExpiresAt,
                        MaxConcurrent = result["maxSessions"]?.ToObject<int>() ?? 1,
                        ActiveSessions = result["activeSessions"]?.ToObject<int>() ?? 1
                    };
                    SaveCache(_cache);
                    EvaluateTier();
                }

                _retryCount = 0; // Success — reset retry counter
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
                }
                _retryCount = 0; // Server responded clearly — no retry
                ScheduleNextHeartbeat(HeartbeatInterval);
                RaiseLicenseStateChanged();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Warning(ex, "Heartbeat failed — using cached license (retry {Retry}/{Max})",
                _retryCount + 1, MaxRetryAttempts);
            lock (_lock)
            {
                EvaluateTier(); // Re-evaluate with cache
            }

            // Exponential backoff: 30s, 60s, 120s, 240s, then back to 2h
            if (_retryCount < MaxRetryAttempts)
            {
                var delay = TimeSpan.FromSeconds(30 * Math.Pow(2, _retryCount));
                _retryCount++;
                ScheduleNextHeartbeat(delay);
            }
            else
            {
                _retryCount = 0;
                ScheduleNextHeartbeat(HeartbeatInterval);
            }

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

    private string LicenseDataPath => Path.Combine(_storagePath, "license.json");
    private string CachePath => Path.Combine(_storagePath, "license_cache.dat");

    private LicenseData? LoadLicenseData()
    {
        try
        {
            if (!File.Exists(LicenseDataPath)) return null;
            var json = File.ReadAllText(LicenseDataPath);
            return JsonConvert.DeserializeObject<LicenseData>(json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cannot read license data from {Path}", LicenseDataPath);
            return null;
        }
    }

    private void SaveLicenseData(LicenseData data)
    {
        try
        {
            EnsureDirectory(_storagePath);
            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            File.WriteAllText(LicenseDataPath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cannot save license data to {Path}", LicenseDataPath);
        }
    }

    private CachedLicenseResponse? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var bytes = File.ReadAllBytes(CachePath);
            var json = Obfuscation.Deobfuscate(bytes);
            return JsonConvert.DeserializeObject<CachedLicenseResponse>(json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cannot read license cache from {Path}", CachePath);
            return null;
        }
    }

    private void SaveCache(CachedLicenseResponse cache)
    {
        try
        {
            EnsureDirectory(_storagePath);
            var json = JsonConvert.SerializeObject(cache);
            var bytes = Obfuscation.Obfuscate(json);
            File.WriteAllBytes(CachePath, bytes);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cannot save license cache to {Path}", CachePath);
        }
    }

    private static void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    private static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
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
