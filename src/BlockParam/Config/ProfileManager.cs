using System.IO;
using Newtonsoft.Json;
using BlockParam.Models;

namespace BlockParam.Config;

/// <summary>
/// CRUD operations for change profiles (presets).
/// Stored as a JSON array in profiles.json.
/// </summary>
public class ProfileManager
{
    private readonly string _filePath;
    private List<ChangeProfile>? _profiles;

    public ProfileManager(string filePath)
    {
        _filePath = filePath;
    }

    public IReadOnlyList<ChangeProfile> GetAll()
    {
        EnsureLoaded();
        return _profiles!;
    }

    public void Save(ChangeProfile profile)
    {
        EnsureLoaded();
        var existing = _profiles!.FindIndex(p => p.Name == profile.Name);
        if (existing >= 0)
            _profiles[existing] = profile;
        else
            _profiles.Add(profile);

        WriteToDisk();
    }

    public void Delete(string name)
    {
        EnsureLoaded();
        _profiles!.RemoveAll(p => p.Name == name);
        WriteToDisk();
    }

    public ChangeProfile? FindByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        EnsureLoaded();
        return _profiles!.FirstOrDefault(p => p.Name == name);
    }

    private void EnsureLoaded()
    {
        if (_profiles != null) return;

        if (!File.Exists(_filePath))
        {
            _profiles = new List<ChangeProfile>();
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            _profiles = JsonConvert.DeserializeObject<List<ChangeProfile>>(json)
                ?? new List<ChangeProfile>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _profiles = new List<ChangeProfile>();
        }
    }

    private void WriteToDisk()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonConvert.SerializeObject(_profiles, Formatting.Indented);
        File.WriteAllText(_filePath, json);
    }
}
