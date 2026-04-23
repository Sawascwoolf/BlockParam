namespace BlockParam.Services;

/// <summary>
/// Logs all bulk change operations for traceability.
/// </summary>
public class ChangeLogger
{
    private readonly List<ChangeLogEntry> _entries = new();
    private readonly Action<ChangeLogEntry>? _sink;

    /// <summary>
    /// Creates a logger. Optionally provide a sink for real-time output (e.g. file writer).
    /// </summary>
    public ChangeLogger(Action<ChangeLogEntry>? sink = null)
    {
        _sink = sink;
    }

    public void Log(ChangeLogEntry entry)
    {
        _entries.Add(entry);
        _sink?.Invoke(entry);
    }

    public IReadOnlyList<ChangeLogEntry> Entries => _entries;

    public void Clear() => _entries.Clear();

    /// <summary>
    /// Formats all entries as a human-readable log string.
    /// </summary>
    public string FormatLog()
    {
        return string.Join(Environment.NewLine, _entries.Select(e => e.ToString()));
    }
}

public class ChangeLogEntry
{
    public ChangeLogEntry(
        DateTime timestamp,
        string dbName,
        string memberPath,
        string datatype,
        string oldValue,
        string newValue,
        string scope)
    {
        Timestamp = timestamp;
        DbName = dbName;
        MemberPath = memberPath;
        Datatype = datatype;
        OldValue = oldValue;
        NewValue = newValue;
        Scope = scope;
    }

    public DateTime Timestamp { get; }
    public string DbName { get; }
    public string MemberPath { get; }
    public string Datatype { get; }
    public string OldValue { get; }
    public string NewValue { get; }
    public string Scope { get; }

    public override string ToString() =>
        $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] {DbName}.{MemberPath} ({Datatype}): " +
        $"'{OldValue}' → '{NewValue}' [Scope: {Scope}]";
}
