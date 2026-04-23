using System.IO;
using Newtonsoft.Json;

namespace BlockParam.Licensing;

/// <summary>
/// Local, offline usage tracker that stores a daily counter in an encrypted file.
/// Uses Windows DPAPI (ProtectedData) when available, falls back to obfuscation.
/// </summary>
public class LocalUsageTracker : IUsageTracker
{
    private readonly string _storagePath;
    private readonly Func<DateTime> _dateProvider;

    public int DailyLimit { get; }
    public int InlineEditDailyLimit { get; }

    public LocalUsageTracker(
        string storagePath,
        int dailyLimit = 3,
        int inlineEditDailyLimit = 50,
        Func<DateTime>? dateProvider = null)
    {
        _storagePath = storagePath;
        DailyLimit = dailyLimit;
        InlineEditDailyLimit = inlineEditDailyLimit;
        _dateProvider = dateProvider ?? (() => DateTime.Now);
    }

    public UsageStatus GetStatus()
    {
        var data = ReadData();
        return new UsageStatus(data.Count, DailyLimit);
    }

    public UsageStatus GetInlineStatus()
    {
        var data = ReadData();
        return new UsageStatus(data.InlineCount, InlineEditDailyLimit);
    }

    public bool RecordUsage()
    {
        var data = ReadData();
        if (data.Count >= DailyLimit)
            return false;

        data.Count++;
        WriteData(data);
        return true;
    }

    public bool RecordInlineEdit()
    {
        var data = ReadData();
        if (data.InlineCount >= InlineEditDailyLimit)
            return false;

        data.InlineCount++;
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

            // Basic tamper detection: counts should be within limits
            if (data.Count < 0 || data.Count > DailyLimit + 10
                || data.InlineCount < 0 || data.InlineCount > InlineEditDailyLimit + 10)
            {
                return new UsageData { Date = TodayString(), Count = 0, InlineCount = 0 };
            }

            return data;
        }
        catch
        {
            // Corrupt file: reset gracefully
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

        // Use a temporary file + rename for atomicity
        var tempPath = _storagePath + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        if (File.Exists(_storagePath))
            File.Delete(_storagePath);
        File.Move(tempPath, _storagePath);
    }

    private string TodayString() => _dateProvider().ToString("yyyy-MM-dd");

    private class UsageData
    {
        public string Date { get; set; } = "";
        public int Count { get; set; }
        public int InlineCount { get; set; }
    }
}
