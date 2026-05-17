using System.Text;
using BlockParam.Localization;

namespace BlockParam.Services;

/// <summary>
/// Single source of truth for the user-facing "Set all N in …" bulk-scope
/// label (#143).
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
/// Every caller routes through <see cref="Pattern"/> /
/// <see cref="Format(ScopeLevel)"/> so the renderings cannot drift again.
/// The single resx template is <c>MenuTitle_SetAll</c> ("Set all {0} in
/// {1}"), reused across <c>en</c>/<c>de</c>.
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
    /// The full user-facing label, e.g. <c>Set all 4 in *.resetButton.elementId</c>.
    /// Routed through the single <c>MenuTitle_SetAll</c> resx template so the
    /// dropdown, Set button, tooltip, ToString and context menu render
    /// identically and localize together.
    /// </summary>
    public static string Format(ScopeLevel scope) =>
        Res.Format("MenuTitle_SetAll", scope.MatchCount, Pattern(scope));
}
