using System.IO;
using BlockParam.Diagnostics;
using Newtonsoft.Json;

namespace BlockParam.Licensing;

/// <summary>
/// Local, offline usage tracker that stores a daily counter in an encrypted file.
/// Uses Windows DPAPI (ProtectedData) when available, falls back to obfuscation.
/// </summary>
public class LocalUsageTracker : IUsageTracker
{
    public const int DefaultDailyLimit = 200;

    private readonly string _storagePath;
    private readonly Func<DateTime> _dateProvider;

    public int DailyLimit { get; }

    public LocalUsageTracker(
        string storagePath,
        int dailyLimit = DefaultDailyLimit,
        Func<DateTime>? dateProvider = null)
    {
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
        if (!File.Exists(_storagePath))
            return new UsageData { Date = TodayString(), Count = 0 };

        try
        {
            var bytes = File.ReadAllBytes(_storagePath);
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
            Log.Warning(ex, "LocalUsageTracker: resetting corrupt usage file {Path}", _storagePath);
            return new UsageData { Date = TodayString(), Count = 0 };
        }
    }

    private void WriteData(UsageData data)
    {
        var dir = Path.GetDirectoryName(_storagePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonConvert.SerializeObject(data);
        var bytes = Obfuscation.Obfuscate(json);

        var tempPath = _storagePath + ".tmp";
        File.WriteAllBytes(tempPath, bytes);

        try
        {
            // File.Replace is atomic on NTFS via the Win32 ReplaceFile API —
            // no gap between deleting the destination and renaming the temp
            // file where another writer (or another Add-In instance) could
            // create the destination and make our Move throw. .NET 5+ has
            // File.Move(overwrite: true); we target net48 and don't want
            // P/Invoke MoveFileEx just for this counter.
            if (File.Exists(_storagePath))
                File.Replace(tempPath, _storagePath, destinationBackupFileName: null);
            else
                File.Move(tempPath, _storagePath);
        }
        catch (IOException ex)
        {
            // ReplaceFile fails across volumes and on non-NTFS filesystems.
            // Fall back to overwrite-copy — non-atomic but FS-agnostic, and
            // a torn write here just resets the counter on next read.
            Log.Warning(ex, "LocalUsageTracker: File.Replace fell back to overwrite-copy for {Path}", _storagePath);
            File.Copy(tempPath, _storagePath, overwrite: true);
            try { File.Delete(tempPath); } catch (IOException) { /* best-effort cleanup */ }
        }
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
