using BlockParam.Models;

namespace BlockParam.Services;

/// <summary>
/// Caches tag table data in memory to avoid repeated Openness API calls.
/// Follows the same Invalidate() pattern as ConfigLoader.
/// </summary>
public class TagTableCache
{
    private readonly ITagTableReader _reader;
    private readonly Dictionary<string, IReadOnlyList<TagTableEntry>> _cache = new();
    private IReadOnlyList<string>? _tableNames;
    private HashSet<string>? _allConstantNames;
    private Dictionary<string, TagTableEntry>? _entriesByName;

    public TagTableCache(ITagTableReader reader)
    {
        _reader = reader;
    }

    public IReadOnlyList<TagTableEntry> GetEntries(string tableName)
    {
        if (_cache.TryGetValue(tableName, out var cached))
            return cached;

        var entries = _reader.ReadTagTable(tableName);
        _cache[tableName] = entries;
        return entries;
    }

    public IReadOnlyList<string> GetTableNames()
    {
        return _tableNames ??= _reader.GetTagTableNames();
    }

    /// <summary>
    /// Gets entries from all tag tables whose name matches the pattern (glob/wildcard).
    /// E.g., "MOD_*" returns entries from "MOD_Halle1", "MOD_Halle2", etc.
    /// </summary>
    public IReadOnlyList<TagTableEntry> GetEntriesByPattern(string tableNamePattern)
    {
        var allNames = GetTableNames();
        var matching = allNames.Where(n => GlobMatcher.IsMatch(n, tableNamePattern));
        return matching.SelectMany(n => GetEntries(n)).ToList();
    }

    /// <summary>
    /// Returns all constant names from all loaded tag tables as a set for fast lookup.
    /// Used by validation to distinguish constants from invalid literal values.
    /// </summary>
    public HashSet<string> GetAllConstantNames()
    {
        if (_allConstantNames != null) return _allConstantNames;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tableName in GetTableNames())
        {
            foreach (var entry in GetEntries(tableName))
                names.Add(entry.Name);
        }
        _allConstantNames = names;
        return names;
    }

    /// <summary>
    /// Looks up a constant by name across all loaded tag tables.
    /// First match wins; returns null if the constant is not found.
    /// </summary>
    public TagTableEntry? FindConstant(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var index = GetEntriesByNameIndex();
        return index.TryGetValue(name, out var entry) ? entry : null;
    }

    /// <summary>
    /// Resolves a constant to an integer value. Handles plain decimal ("42"),
    /// TIA hex ("16#2A"), binary ("2#101010"), and octal ("8#52") forms.
    /// Returns false if the name is unknown or the value cannot be parsed as int.
    /// </summary>
    public bool TryGetConstantValue(string name, out int value)
    {
        value = 0;
        var entry = FindConstant(name);
        if (entry == null) return false;
        return TryParseIntLiteral(entry.Value, out value);
    }

    /// <summary>
    /// Parses a TIA integer literal. Supports decimal, 16#hex, 2#bin, 8#oct.
    /// </summary>
    public static bool TryParseIntLiteral(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        var hashIndex = text.IndexOf('#');
        if (hashIndex > 0)
        {
            var prefix = text.Substring(0, hashIndex);
            var digits = text.Substring(hashIndex + 1);
            int radix = prefix switch
            {
                "16" => 16,
                "2" => 2,
                "8" => 8,
                _ => 0
            };
            if (radix == 0) return false;
            try
            {
                value = Convert.ToInt32(digits, radix);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return int.TryParse(text, out value);
    }

    private Dictionary<string, TagTableEntry> GetEntriesByNameIndex()
    {
        if (_entriesByName != null) return _entriesByName;

        var index = new Dictionary<string, TagTableEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var tableName in GetTableNames())
        {
            foreach (var entry in GetEntries(tableName))
            {
                // First occurrence wins — mirrors how TIA itself would surface
                // the first-defined constant in name-collision cases.
                if (!index.ContainsKey(entry.Name))
                    index[entry.Name] = entry;
            }
        }
        _entriesByName = index;
        return index;
    }

    public void Invalidate()
    {
        _cache.Clear();
        _tableNames = null;
        _allConstantNames = null;
        _entriesByName = null;
    }
}
