namespace BlockParam.Models;

/// <summary>
/// Lightweight description of a Data Block in the active TIA project, used by the
/// in-dialog DB-switcher dropdown (#59). Holds only what the picker UI needs —
/// no parsed members, no XML — so enumeration can be cheap and lazy.
///
/// <para>
/// <b>Identity is (<see cref="PlcName"/>, <see cref="FolderPath"/>, <see cref="Name"/>).</b>
/// DB names + numbers are unique within a single PLC software unit, not across
/// the whole project — a project with multiple PLCs can have two DBs both
/// called <c>DB_Unit_A</c> at the root. The picker today scopes to the active
/// PLC (the one that owns the right-clicked DB), so <see cref="PlcName"/> is
/// usually unused for display, but it's part of the identity so stashes
/// keyed by it stay correct if/when cross-PLC discovery is added.
/// </para>
/// </summary>
public class DataBlockSummary
{
    public DataBlockSummary(
        string name,
        string folderPath,
        string blockType = "GlobalDB",
        bool isInstanceDb = false,
        string plcName = "")
    {
        Name = name;
        FolderPath = folderPath;
        BlockType = blockType;
        IsInstanceDb = isInstanceDb;
        PlcName = plcName;
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

    /// <summary>
    /// Owning PLC software unit name. Empty when the host can't (or doesn't
    /// need to) supply it — e.g. DevLauncher fixtures with no real project.
    /// Required for unambiguous identity in multi-PLC projects: name + number
    /// are only unique within a single PLC.
    /// </summary>
    public string PlcName { get; }

    public override string ToString()
    {
        var path = string.IsNullOrEmpty(FolderPath) ? Name : $"{FolderPath}/{Name}";
        return string.IsNullOrEmpty(PlcName) ? path : $"{PlcName}:{path}";
    }
}
