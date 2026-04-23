namespace BlockParam.Models;

/// <summary>
/// Represents a parsed Data Block with its complete member tree.
/// </summary>
public class DataBlockInfo
{
    public DataBlockInfo(
        string name,
        int number,
        string memoryLayout,
        string blockType,
        IReadOnlyList<MemberNode> members,
        IReadOnlyList<string>? unresolvedUdts = null)
    {
        Name = name;
        Number = number;
        MemoryLayout = memoryLayout;
        BlockType = blockType;
        Members = members;
        UnresolvedUdts = unresolvedUdts ?? Array.Empty<string>();
    }

    /// <summary>DB name (e.g. "DB_Foerderer1")</summary>
    public string Name { get; }

    /// <summary>DB number</summary>
    public int Number { get; }

    /// <summary>"Optimized" or "Standard"</summary>
    public string MemoryLayout { get; }

    /// <summary>"GlobalDB" or "InstanceDB"</summary>
    public string BlockType { get; }

    /// <summary>Top-level members in the Static section</summary>
    public IReadOnlyList<MemberNode> Members { get; }

    /// <summary>
    /// UDT type names referenced by this DB that the SetPoint resolver could not resolve.
    /// Empty when no resolver was supplied or every referenced UDT is known.
    /// Used to gate the "Show setpoints only" filter in the UI.
    /// </summary>
    public IReadOnlyList<string> UnresolvedUdts { get; }

    /// <summary>
    /// Recursively enumerates all members in the tree (depth-first).
    /// </summary>
    public IEnumerable<MemberNode> AllMembers()
    {
        return EnumerateRecursive(Members);
    }

    private static IEnumerable<MemberNode> EnumerateRecursive(IReadOnlyList<MemberNode> members)
    {
        foreach (var member in members)
        {
            yield return member;
            foreach (var child in EnumerateRecursive(member.Children))
            {
                yield return child;
            }
        }
    }

    public override string ToString() => $"{Name} (DB{Number}, {MemoryLayout}, {BlockType})";
}
