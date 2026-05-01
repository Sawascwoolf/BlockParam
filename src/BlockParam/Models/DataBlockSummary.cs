namespace BlockParam.Models;

/// <summary>
/// Lightweight description of a Data Block in the active TIA project, used by the
/// in-dialog DB-switcher dropdown (#59). Holds only what the picker UI needs —
/// no parsed members, no XML — so enumeration can be cheap and lazy.
/// </summary>
public class DataBlockSummary
{
    public DataBlockSummary(
        string name,
        string folderPath,
        string blockType = "GlobalDB",
        bool isInstanceDb = false)
    {
        Name = name;
        FolderPath = folderPath;
        BlockType = blockType;
        IsInstanceDb = isInstanceDb;
    }

    /// <summary>DB name as shown in the TIA project tree (e.g. "DB_ProcessPlant_A1").</summary>
    public string Name { get; }

    /// <summary>
    /// Slash-separated folder path inside the PLC's Program blocks tree
    /// (empty for blocks at the root). Surfaced as a dim breadcrumb in the
    /// dropdown so users can disambiguate same-named blocks across folders.
    /// </summary>
    public string FolderPath { get; }

    /// <summary>"GlobalDB" or "InstanceDB".</summary>
    public string BlockType { get; }

    /// <summary>
    /// True for instance-DBs of FBs. The dropdown shows them with a small
    /// badge so users know they are picking an instance, not a global DB.
    /// </summary>
    public bool IsInstanceDb { get; }

    public override string ToString() =>
        string.IsNullOrEmpty(FolderPath) ? Name : $"{FolderPath}/{Name}";
}
