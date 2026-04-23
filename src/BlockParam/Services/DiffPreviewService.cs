using BlockParam.Models;
using BlockParam.SimaticML;

namespace BlockParam.Services;

/// <summary>
/// Computes a diff preview showing what would change without actually modifying anything.
/// </summary>
public class DiffPreviewService
{
    /// <summary>
    /// Computes a list of DiffEntries showing old vs new values for all members in scope.
    /// Does NOT modify the XML.
    /// </summary>
    public IReadOnlyList<DiffEntry> ComputeDiff(
        DataBlockInfo db,
        IReadOnlyList<MemberNode> targetMembers,
        string newValue)
    {
        return targetMembers.Select(m => new DiffEntry(
            m.Path,
            m.Datatype,
            m.StartValue ?? "",
            newValue
        )).ToList();
    }

    /// <summary>Number of members that will actually change (old != new).</summary>
    public int CountChanges(IReadOnlyList<DiffEntry> diff) =>
        diff.Count(d => d.IsChanged);
}
