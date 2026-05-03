using BlockParam.Models;

namespace BlockParam.Services;

/// <summary>
/// Analyzes a Data Block's member hierarchy to find all members with the same
/// name as a selected member, grouped by scope level (parent, grandparent, ... root).
/// </summary>
public class HierarchyAnalyzer
{
    /// <summary>
    /// Starting from a selected member, finds all identically named members
    /// (same name AND same datatype) in the DB and groups them by hierarchy level.
    /// </summary>
    public AnalysisResult Analyze(DataBlockInfo db, MemberNode selectedMember)
    {
        var withinDb = AnalyzeWithinDb(db, selectedMember);
        return new AnalysisResult(selectedMember, withinDb);
    }

    /// <summary>
    /// Multi-DB analysis (#58). Generates the existing within-DB scope levels
    /// PLUS one cross-DB sibling per within-DB scope, plus an "All selected
    /// DBs" mega-scope covering every same-name match across every DB.
    ///
    /// Cross-DB lift rule: a scope whose <see cref="ScopeLevel.MatchingMembers"/>
    /// has paths {p1, p2, ...} is lifted to a cross-DB sibling whose
    /// MatchingMembers is every member in every active DB whose Path is in
    /// {p1, p2, ...} and whose Datatype matches the selected member. DBs
    /// that don't have any of those paths contribute zero members and are
    /// silently skipped (#58 decision).
    /// </summary>
    public AnalysisResult AnalyzeMulti(
        IReadOnlyList<DataBlockInfo> activeDbs,
        DataBlockInfo selectedDb,
        MemberNode selectedMember)
    {
        var withinDb = AnalyzeWithinDb(selectedDb, selectedMember);

        // Single-DB session degrades to legacy behavior.
        if (activeDbs.Count <= 1)
            return new AnalysisResult(selectedMember, withinDb);

        var combined = new List<ScopeLevel>(withinDb);
        var crossDbScopes = new List<ScopeLevel>();

        foreach (var w in withinDb)
        {
            var lifted = LiftToCrossDb(w, activeDbs, selectedMember);
            if (lifted != null && lifted.MatchCount > w.MatchCount)
                crossDbScopes.Add(lifted);
        }
        combined.AddRange(crossDbScopes);

        // "All selected DBs" mega-scope: every same-name + same-datatype
        // match across every active DB. Equivalent to lifting the broadest
        // within-DB scope cross-DB, but emitted unconditionally so it shows
        // up even when the selected DB has only the selected member itself.
        var megaScope = BuildAllSelectedDbsScope(activeDbs, selectedMember);
        if (megaScope != null
            && (combined.Count == 0
                || megaScope.MatchCount > combined.Max(s => s.MatchCount)))
        {
            combined.Add(megaScope);
        }

        return new AnalysisResult(selectedMember, combined);
    }

    private List<ScopeLevel> AnalyzeWithinDb(DataBlockInfo db, MemberNode selectedMember)
    {
        var targetName = selectedMember.Name;
        var targetDatatype = selectedMember.Datatype;

        // Find all members with the same name and datatype across the entire DB
        var allMatches = db.AllMembers()
            .Where(m => m.Name == targetName && m.Datatype == targetDatatype)
            .ToList();

        // Array-element special case: when the user selects e.g. Motors[3] of
        // a 1..N primitive array, all siblings ([1], [2], ...) have different
        // names ("[1]", "[2]", ...) so same-name matching above yields nothing.
        // Offer an explicit "all elements of <array>" scope instead.
        var arraySiblingScope = BuildArrayElementScope(db, selectedMember);

        // Multi-dim arrays: offer scopes per leading-index prefix, so the user
        // can bulk-edit one row/slice instead of the whole matrix.
        var partialDimScopes = BuildPartialDimArrayScopes(db, selectedMember, allMatches);

        if (allMatches.Count <= 1 && arraySiblingScope == null && partialDimScopes.Count == 0)
        {
            return new List<ScopeLevel>();
        }

        var scopes = allMatches.Count > 1
            ? BuildScopeLevels(db, selectedMember, allMatches)
            : new List<ScopeLevel>();

        if (arraySiblingScope != null)
        {
            // Place the array-elements scope first so it appears as the
            // narrowest option in the UI.
            scopes.Insert(0, arraySiblingScope);
        }

        // Merge partial-dim scopes in narrowest-first order by MatchCount.
        foreach (var partial in partialDimScopes)
        {
            int insertAt = 0;
            while (insertAt < scopes.Count && scopes[insertAt].MatchCount < partial.MatchCount)
                insertAt++;
            scopes.Insert(insertAt, partial);
        }

        return scopes;
    }

    /// <summary>
    /// Builds the cross-DB sibling for a within-DB scope by matching the
    /// scope's member paths against every active DB. Returns null if the
    /// lifted scope has the same match count as the input (i.e. only the
    /// selected DB had any of those paths) — no point showing a "cross-DB"
    /// scope that isn't actually wider.
    /// </summary>
    private static ScopeLevel? LiftToCrossDb(
        ScopeLevel withinDbScope,
        IReadOnlyList<DataBlockInfo> activeDbs,
        MemberNode selectedMember)
    {
        var paths = new HashSet<string>(
            withinDbScope.MatchingMembers.Select(m => m.Path),
            StringComparer.Ordinal);
        var datatype = selectedMember.Datatype;

        var lifted = new List<MemberNode>();
        foreach (var db in activeDbs)
        {
            foreach (var m in db.AllMembers())
            {
                if (m.Datatype == datatype && paths.Contains(m.Path))
                    lifted.Add(m);
            }
        }

        if (lifted.Count == 0) return null;

        return new ScopeLevel(
            ancestorName: $"{withinDbScope.AncestorName} — across all selected DBs",
            ancestorPath: withinDbScope.AncestorPath,
            depth: -1000 + withinDbScope.Depth, // Cross-DB scopes sort after their within-DB sibling.
            matchingMembers: lifted);
    }

    /// <summary>
    /// "All selected DBs" mega-scope: every same-name + same-datatype match
    /// in every active DB. The broadest possible scope in multi-DB mode.
    /// </summary>
    private static ScopeLevel? BuildAllSelectedDbsScope(
        IReadOnlyList<DataBlockInfo> activeDbs,
        MemberNode selectedMember)
    {
        var name = selectedMember.Name;
        var datatype = selectedMember.Datatype;

        var matches = new List<MemberNode>();
        foreach (var db in activeDbs)
        {
            foreach (var m in db.AllMembers())
            {
                if (m.Name == name && m.Datatype == datatype)
                    matches.Add(m);
            }
        }

        if (matches.Count <= 1) return null;

        return new ScopeLevel(
            ancestorName: "All selected DBs",
            ancestorPath: "",
            depth: -2000, // Sorts last (broadest).
            matchingMembers: matches);
    }

    /// <summary>
    /// If the selected member is an array element (e.g. <c>Motors[3]</c>),
    /// returns a scope that covers every element of the enclosing array.
    /// For arrays of UDT we also want nested-member bulk edits (e.g. all
    /// <c>Speed</c> fields); that case is already handled by the same-name
    /// matching path above and does not need a dedicated scope here.
    /// </summary>
    private static ScopeLevel? BuildArrayElementScope(DataBlockInfo db, MemberNode selectedMember)
    {
        if (!selectedMember.IsArrayElement) return null;
        var arrayParent = selectedMember.Parent;
        if (arrayParent == null || !arrayParent.IsArray) return null;
        if (arrayParent.Children.Count <= 1) return null;

        return new ScopeLevel(
            ancestorName: $"{db.Name}.{arrayParent.Path}",
            ancestorPath: arrayParent.Path,
            depth: arrayParent.Depth,
            matchingMembers: arrayParent.Children.ToList());
    }

    /// <summary>
    /// For multi-dimensional arrays, offers one scope per non-empty subset of
    /// fixed dimensions. Example: selecting <c>Motors[2,1].Speed</c> inside
    /// <c>Array[1..3, 1..2] of UDT_Motor</c> yields scopes
    /// <c>Motors[2,*]</c> (fix dim 0) and <c>Motors[*,1]</c> (fix dim 1),
    /// both narrower than the full "Motors" scope. A "*" in the label marks
    /// an unfixed dimension; fixed dimensions carry the selected element's
    /// own index value.
    /// </summary>
    private static List<ScopeLevel> BuildPartialDimArrayScopes(
        DataBlockInfo db, MemberNode selectedMember, IReadOnlyList<MemberNode> allMatches)
    {
        var result = new List<ScopeLevel>();

        // Locate the nearest array-element ancestor (or the node itself).
        MemberNode? arrayElement = selectedMember.IsArrayElement ? selectedMember : null;
        var walk = selectedMember.Parent;
        while (arrayElement == null && walk != null)
        {
            if (walk.IsArrayElement) arrayElement = walk;
            walk = walk.Parent;
        }
        if (arrayElement == null) return result;

        var arrayParent = arrayElement.Parent;
        if (arrayParent == null || !arrayParent.IsArray) return result;

        var selectedIndices = ParseArrayIndices(arrayElement.Name);
        if (selectedIndices == null || selectedIndices.Length < 2) return result;

        var dims = selectedIndices.Length;
        // Enumerate every non-empty proper subset of fixed dims. Mask bit i set
        // = dim i is fixed to selectedIndices[i]. Skip 0 (= full array, already
        // offered) and (1<<dims)-1 (= the selected element itself).
        var fullMask = (1 << dims) - 1;
        var emittedSignatures = new HashSet<string>();

        for (int mask = 1; mask < fullMask; mask++)
        {
            var matchingSiblings = arrayParent.Children
                .Where(sib =>
                {
                    var idx = ParseArrayIndices(sib.Name);
                    if (idx == null || idx.Length != dims) return false;
                    for (int d = 0; d < dims; d++)
                        if ((mask & (1 << d)) != 0 && idx[d] != selectedIndices[d])
                            return false;
                    return true;
                })
                .ToList();

            if (matchingSiblings.Count <= 1) continue;

            // Dedup: different masks can produce the same sibling set when a
            // dimension has only one legal value. Keep only one scope per set.
            var signature = string.Join("|", matchingSiblings.Select(s => s.Path));
            if (!emittedSignatures.Add(signature)) continue;

            IReadOnlyList<MemberNode> members;
            if (ReferenceEquals(arrayElement, selectedMember))
            {
                members = matchingSiblings;
            }
            else
            {
                // Array of UDT/Struct: scope over same-name descendants of the
                // fixed-dim siblings.
                members = allMatches
                    .Where(m => matchingSiblings.Any(sib => IsDescendantOf(m, sib)))
                    .ToList();
            }

            if (members.Count <= 1) continue;

            var labelParts = new string[dims];
            for (int d = 0; d < dims; d++)
                labelParts[d] = (mask & (1 << d)) != 0 ? selectedIndices[d].ToString() : "*";
            var prefixLabel = $"[{string.Join(",", labelParts)}]";

            result.Add(new ScopeLevel(
                ancestorName: $"{db.Name}.{arrayParent.Path}{prefixLabel}",
                ancestorPath: $"{arrayParent.Path}{prefixLabel}",
                depth: arrayParent.Depth + 1,
                matchingMembers: members));
        }

        return result;
    }

    /// <summary>
    /// Parses an array-element node name like <c>"[2,1]"</c> into the index
    /// tuple <c>[2, 1]</c>. Returns null if the name is not in that form.
    /// </summary>
    private static int[]? ParseArrayIndices(string name)
    {
        if (name.Length < 3 || name[0] != '[' || name[name.Length - 1] != ']')
            return null;
        var inner = name.Substring(1, name.Length - 2);
        var parts = inner.Split(',');
        var indices = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i].Trim(), out indices[i])) return null;
        }
        return indices;
    }

    private List<ScopeLevel> BuildScopeLevels(
        DataBlockInfo db,
        MemberNode selectedMember,
        List<MemberNode> allMatches)
    {
        var scopes = new List<ScopeLevel>();

        // Collect all ancestor levels from the selected member up to the root
        var ancestors = GetAncestors(selectedMember);

        foreach (var ancestor in ancestors)
        {
            // Find all matches that are descendants of this ancestor
            var matchesInScope = allMatches
                .Where(m => IsDescendantOf(m, ancestor))
                .ToList();

            if (matchesInScope.Count > 1)
            {
                scopes.Add(new ScopeLevel(
                    ancestorName: $"{db.Name}.{ancestor.Path}",
                    ancestorPath: ancestor.Path,
                    depth: ancestor.Depth,
                    matchingMembers: matchesInScope));
            }
        }

        // Add DB root scope (all matches in the entire DB)
        if (allMatches.Count > 1)
        {
            scopes.Add(new ScopeLevel(
                ancestorName: db.Name,
                ancestorPath: "",
                depth: -1, // Root level
                matchingMembers: allMatches));
        }

        // Remove duplicate scopes (where match count is the same as a broader scope)
        scopes = DeduplicateScopes(scopes);

        return scopes;
    }

    /// <summary>
    /// Returns all ancestors of a member, from immediate parent up (excluding the member itself).
    /// </summary>
    private static List<MemberNode> GetAncestors(MemberNode member)
    {
        var ancestors = new List<MemberNode>();
        var current = member.Parent;
        while (current != null)
        {
            ancestors.Add(current);
            current = current.Parent;
        }
        return ancestors;
    }

    /// <summary>
    /// Checks if a member is a descendant of a potential ancestor.
    /// </summary>
    private static bool IsDescendantOf(MemberNode member, MemberNode potentialAncestor)
    {
        var current = member.Parent;
        while (current != null)
        {
            if (ReferenceEquals(current, potentialAncestor))
                return true;
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// Removes scopes where the match count equals a broader (higher) scope,
    /// as they would be redundant in the UI.
    /// </summary>
    private static List<ScopeLevel> DeduplicateScopes(List<ScopeLevel> scopes)
    {
        if (scopes.Count <= 1)
            return scopes;

        var result = new List<ScopeLevel> { scopes[0] };
        for (int i = 1; i < scopes.Count; i++)
        {
            if (scopes[i].MatchCount != scopes[i - 1].MatchCount)
            {
                result.Add(scopes[i]);
            }
        }
        return result;
    }
}

/// <summary>
/// Result of a hierarchy analysis for a selected member.
/// </summary>
public class AnalysisResult
{
    public AnalysisResult(MemberNode selectedMember, IReadOnlyList<ScopeLevel> scopes)
    {
        SelectedMember = selectedMember;
        Scopes = scopes;
    }

    public MemberNode SelectedMember { get; }

    /// <summary>Scope levels ordered from narrowest (parent) to broadest (DB root).</summary>
    public IReadOnlyList<ScopeLevel> Scopes { get; }

    /// <summary>True if bulk operations are available (more than one match found).</summary>
    public bool HasBulkOptions => Scopes.Count > 0;
}

/// <summary>
/// Represents one scope level for a bulk operation.
/// </summary>
public class ScopeLevel
{
    public ScopeLevel(
        string ancestorName,
        string ancestorPath,
        int depth,
        IReadOnlyList<MemberNode> matchingMembers)
    {
        AncestorName = ancestorName;
        AncestorPath = ancestorPath;
        Depth = depth;
        MatchingMembers = matchingMembers;
    }

    /// <summary>Display name of the ancestor (e.g. "TP307.drive1" or "TP307")</summary>
    public string AncestorName { get; }

    /// <summary>Full path of the ancestor (empty for DB root)</summary>
    public string AncestorPath { get; }

    /// <summary>Hierarchy depth of the ancestor (-1 for DB root)</summary>
    public int Depth { get; }

    /// <summary>All members matching the target name within this scope</summary>
    public IReadOnlyList<MemberNode> MatchingMembers { get; }

    /// <summary>Number of members that would be affected by this bulk operation</summary>
    public int MatchCount => MatchingMembers.Count;

    public override string ToString() => $"Set all {MatchCount} in '{AncestorName}'";
}
