using BlockParam.Diagnostics;
using BlockParam.Services.Storage;
using Newtonsoft.Json;

namespace BlockParam.Licensing;

/// <summary>
/// Local, offline usage tracker that stores a daily counter in an encrypted file.
/// Uses Windows DPAPI (ProtectedData) when available, falls back to obfuscation.
///
/// Persistence goes through <see cref="IBlockParamStorage"/> so the production
/// file-system path and unit tests share the same code path. Direct
/// <c>File.*</c> / <c>Directory.*</c> calls live in
/// <see cref="FileSystemBlockParamStorage"/>, not here (#85 guardrail).
/// </summary>
public class LocalUsageTracker : IUsageTracker
{
    public const int DefaultDailyLimit = 200;

    private readonly IBlockParamStorage _storage;
    private readonly StoragePath _storagePath;
    private readonly Func<DateTime> _dateProvider;

    public int DailyLimit { get; }

    public LocalUsageTracker(
        string storagePath,
        int dailyLimit = DefaultDailyLimit,
        Func<DateTime>? dateProvider = null)
        : this(FileSystemBlockParamStorage.Instance,
               StoragePath.FromAbsolute(storagePath),
               dailyLimit,
               dateProvider)
    {
    }

    public LocalUsageTracker(
        IBlockParamStorage storage,
        StoragePath storagePath,
        int dailyLimit = DefaultDailyLimit,
        Func<DateTime>? dateProvider = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _storagePath = storagePath;
        DailyLimit = dailyLimit;
        _dateProvider = dateProvider ?? (() => DateTime.Now);
    }

    public UsageStatus GetStatus()
    {
        var data = ReadData();
        return new UsageStatus(data.Count, DailyLimit);
    }

    public bool RecordUsage(int count)
    {
        if (count <= 0) return true;

        var data = ReadData();
        if (data.Count + count > DailyLimit)
            return false;

        data.Count += count;
        WriteData(data);
        return true;
    }

    private UsageData ReadData()
    {
        // Local copy so any property access on the readonly StoragePath field
        // emits ldloca rather than ldflda — partial-trust IL gate (see
        // CLAUDE.md "Hard rules" and UiZoomService).
        var path = _storagePath;

        if (!_storage.FileExists(path))
            return new UsageData { Date = TodayString(), Count = 0 };

        try
        {
            var bytes = _storage.ReadAllBytes(path);
            var json = Obfuscation.Deobfuscate(bytes);
            var data = JsonConvert.DeserializeObject<UsageData>(json);

            if (data == null || data.Date != TodayString())
            {
                // New day or corrupted: reset
                return new UsageData { Date = TodayString(), Count = 0 };
            }

            // Basic tamper detection: count must be within a sane range.
            if (data.Count < 0 || data.Count > DailyLimit + 100)
            {
                return new UsageData { Date = TodayString(), Count = 0 };
            }

            return data;
        }
        catch (Exception ex)
        {
            // Corrupt file: reset gracefully. Log so repeated resets don't
            // stay silent — they could signal tampering or a real read bug.
            Log.Warning(ex, "LocalUsageTracker: resetting corrupt usage file {Path}", path.FullPath);
            return new UsageData { Date = TodayString(), Count = 0 };
        }
    }

    private void WriteData(UsageData data)
    {
        var path = _storagePath;
        var json = JsonConvert.SerializeObject(data);
        var bytes = Obfuscation.Obfuscate(json);

        // Write-temp + atomic-replace via storage. IBlockParamStorage.Replace
        // is atomic on NTFS (Win32 ReplaceFile) and falls back to overwrite-copy
        // on cross-volume / non-NTFS — a torn write here just resets the counter
        // on next read so the fallback is acceptable. WriteAllBytes auto-creates
        // the parent directory, so no separate EnsureDirectory needed.
        var tempPath = new StoragePath(path.FullPath + ".tmp");
        _storage.WriteAllBytes(tempPath, bytes);
        _storage.Replace(tempPath, path);
    }

    private string TodayString() => _dateProvider().ToString("yyyy-MM-dd");

    // Public so Newtonsoft.Json can reach the constructor under TIA's
    // partial-trust CAS sandbox — see UiZoomService.UiSettingsDto for context.
    // Legacy "InlineCount" fields in saved JSON are silently ignored on read
    // (Newtonsoft drops unknown properties) and dropped on next write.
    public class UsageData
    {
        public string Date { get; set; } = "";
        public int Count { get; set; }
    }
}
