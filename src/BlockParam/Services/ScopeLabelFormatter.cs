using System.Text;
using BlockParam.Localization;

namespace BlockParam.Services;

/// <summary>
/// Single source of truth for the bulk-scope dropdown wording (#143) and
/// the scope-pattern fragment shared with the Set button (#174).
///
/// <para>
/// Before this class there were 5+ independent renderings of the same
/// concept — two XAML <c>ItemTemplate</c>s, <see cref="ScopeLevel.ToString"/>,
/// the Set-button caption, and the Set-button tooltip — each composing the
/// concrete, DB-qualified <see cref="ScopeLevel.AncestorName"/> by hand.
/// They drifted, advertised one concrete example path instead of the
/// pattern the scope matches, and never named the leaf field being written.
/// </para>
///
/// <para>
/// This formatter emits the <em>pattern</em>, not an example. The varying
/// instance / DB segment becomes a <c>*</c> wildcard (the same convention
/// already used for unfixed array dimensions, e.g. <c>Motors[2,*]</c>), and
/// the leaf member actually being set is appended. Cross-DB scopes use the
/// same leading <c>*</c> (the DB is the thing that varies), so the awkward
/// "<c> -- across all selected DBs</c>" string append disappears — the
/// pattern already states it.
/// </para>
///
/// <para>
/// <see cref="Pattern"/> is the single pattern source — the Set button
/// composes it with its own will-change count via <c>MenuTitle_SetAll</c>
/// ("Set all {0} in {1}"). <see cref="Format(ScopeLevel)"/> is the dropdown
/// wording — it deliberately uses a <em>different</em> resx template,
/// <c>Scope_DropdownItem</c> ("{0} member(s) in {1}"), because the
/// dropdown describes the scope's <em>size</em> (always
/// <see cref="ScopeLevel.MatchCount"/>) while the button advertises the
/// <em>action</em> (only members whose effective value differs from
/// <c>NewValue</c>). Sharing one "Set all N" template caused the two
/// counters to contradict each other once 1+ member already matched (#174).
/// </para>
/// </summary>
public static class ScopeLabelFormatter
{
    private const string Wildcard = "*";

    /// <summary>
    /// The scope-match pattern, e.g. <c>*.resetButton.elementId</c>.
    /// Leading <c>*</c> = the varying instance/DB segment; the middle is the
    /// scope's ancestor path (empty for a DB-root or all-DBs scope); the
    /// trailing segment is the leaf member being written.
    /// </summary>
    public static string Pattern(ScopeLevel scope)
    {
        var sb = new StringBuilder(Wildcard);
        if (!string.IsNullOrEmpty(scope.AncestorPath))
        {
            sb.Append('.');
            sb.Append(scope.AncestorPath);
        }
        if (!string.IsNullOrEmpty(scope.LeafName))
        {
            sb.Append('.');
            sb.Append(scope.LeafName);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The dropdown / overlay item label, e.g.
    /// <c>4 member(s) in *.resetButton.elementId</c>. Always uses
    /// <see cref="ScopeLevel.MatchCount"/> (the in-scope total). The Set
    /// button does NOT route through this — it composes its own caption with
    /// <c>MenuTitle_SetAll</c> + the will-change count, so once some members
    /// already hold the target value the two counters intentionally differ
    /// (#174). The wording omits the "Set" verb so the dropdown reads as
    /// "pick a scope of this size", not "perform this action".
    /// </summary>
    public static string Format(ScopeLevel scope) =>
        Res.Format("Scope_DropdownItem", scope.MatchCount, Pattern(scope));
}
