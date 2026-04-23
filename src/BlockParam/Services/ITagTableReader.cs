using BlockParam.Models;

namespace BlockParam.Services;

/// <summary>
/// Reads constant entries from TIA Portal tag tables via the Openness API.
/// Separate from ITiaPortalAdapter (Interface Segregation Principle).
/// </summary>
public interface ITagTableReader
{
    /// <summary>
    /// Reads all constant entries from the specified tag table.
    /// Returns empty list if table does not exist.
    /// </summary>
    IReadOnlyList<TagTableEntry> ReadTagTable(string tableName);

    /// <summary>
    /// Returns the names of all tag tables in the current PLC program.
    /// </summary>
    IReadOnlyList<string> GetTagTableNames();
}
