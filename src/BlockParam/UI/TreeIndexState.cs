using System.Collections.Generic;
using BlockParam.Models;

namespace BlockParam.UI;

/// <summary>
/// Immutable snapshot of the three model→VM / model→DB / DB→synthetic
/// lookup dictionaries owned by <see cref="MemberTreeViewModel"/> (#79).
///
/// <para>
/// Every tree rebuild
/// (<see cref="MemberTreeViewModel.BuildRootMembersFromActiveDbs"/>) builds
/// one fresh <see cref="TreeIndexState"/> in locals and installs it in a
/// single atomic assignment — readers never observe a partial state where
/// roots have been minted but a descendant is still missing from
/// <see cref="ModelToVm"/>, or where <see cref="DbToSynthetic"/> still has
/// keys from the prior tree.
/// </para>
///
/// <para>
/// Storage uses plain <see cref="IReadOnlyDictionary{TKey,TValue}"/> rather
/// than the <c>System.Collections.Immutable</c> NuGet types so net48 builds
/// don't pull a new transitive dependency — same trade-off as
/// <see cref="ActiveSetState"/> (#78). Per-rebuild allocation cost is
/// negligible compared to the per-node <see cref="MemberNodeViewModel"/>
/// construction the rebuild already performs.
/// </para>
///
/// <para>
/// Member identity is the <c>(ActiveDb, MemberNode)</c> pair per #82 — the
/// same path string in two DBs is two distinct <see cref="MemberNode"/>
/// instances, so the by-reference dictionary keying disambiguates without
/// any path-string scanning.
/// </para>
/// </summary>
public sealed class TreeIndexState
{
    public TreeIndexState(
        IReadOnlyDictionary<MemberNode, MemberNodeViewModel> modelToVm,
        IReadOnlyDictionary<MemberNode, ActiveDb> modelToDb,
        IReadOnlyDictionary<ActiveDb, MemberNodeViewModel> dbToSynthetic)
    {
        ModelToVm = modelToVm;
        ModelToDb = modelToDb;
        DbToSynthetic = dbToSynthetic;
    }

    /// <summary>
    /// <c>MemberNode</c> → owning tree VM. O(1) replacement for any
    /// path-string walk; unambiguous in multi-DB sessions where the same
    /// path string can appear in multiple DBs (#82).
    /// </summary>
    public IReadOnlyDictionary<MemberNode, MemberNodeViewModel> ModelToVm { get; }

    /// <summary>
    /// <c>MemberNode</c> → owning <c>ActiveDb</c>. Used by the host's
    /// pending-edit store + multi-DB scope routing to write back to the
    /// right tree / xml.
    /// </summary>
    public IReadOnlyDictionary<MemberNode, ActiveDb> ModelToDb { get; }

    /// <summary>
    /// <c>ActiveDb</c> → its synthetic group root in the multi-DB tree.
    /// Empty in single-DB sessions (the slice routes those callers through
    /// <c>RootMembers</c> directly). Used by
    /// <see cref="MemberTreeViewModel.FindNodeByPathInDb"/> and by the
    /// host's per-DB filter routing.
    /// </summary>
    public IReadOnlyDictionary<ActiveDb, MemberNodeViewModel> DbToSynthetic { get; }

    /// <summary>Empty snapshot — the initial state before any tree build.</summary>
    public static TreeIndexState Empty { get; } = new(
        new Dictionary<MemberNode, MemberNodeViewModel>(0),
        new Dictionary<MemberNode, ActiveDb>(0),
        new Dictionary<ActiveDb, MemberNodeViewModel>(0));
}
