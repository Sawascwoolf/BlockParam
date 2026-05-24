using System.Windows.Controls;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// The dropdown half of <see cref="PillMultiSelect"/> — search box, item
/// list (with optional grouping + tri-state headers), and the Select all /
/// Reset footer — factored out as a standalone <see cref="UserControl"/>
/// so it can be embedded outside the pill's <see cref="System.Windows.Controls.Primitives.Popup"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>DataContext contract:</b> a <see cref="MultiSelectInternalState"/>.
/// All bindings inside this control resolve against that state — there are
/// no DPs on this UserControl itself.
/// </para>
/// <para>
/// <b>When hosted by <see cref="PillMultiSelect"/>:</b> the state is
/// inherited from the pill's outer Grid (the pill assigns its
/// <c>_internalState</c> as the content's DataContext in its constructor);
/// no extra setup is needed.
/// </para>
/// <para>
/// <b>Standalone usage</b> (DataGrid cells, fly-out panels, sidebar lists):
/// the host wires up its own state. The four collaborators
/// (<see cref="MemberPathResolver"/>, <see cref="MultiSelectItemSource"/>,
/// <see cref="MultiSelectSelectionSync"/>, <see cref="MultiSelectFormatter"/>)
/// are <c>public</c> for exactly this reason. Minimal wire-up:
/// <code>
/// var state    = new MultiSelectInternalState();
/// var resolver = new MemberPathResolver();
/// var source   = new MultiSelectItemSource(state, resolver) { ItemsSource = myItems, DisplayMemberPath = "Name" };
/// var sync     = new MultiSelectSelectionSync(state, source, resolver);
/// var _        = new MultiSelectFormatter(state, source); // formatter is trigger-only but harmless here
/// sync.SetSelectedItems(mySelectedList);
///
/// var dropdown = new MultiSelectDropdown { DataContext = state };
/// // host the dropdown wherever — DataGrid cell editor, Popup, panel, etc.
/// </code>
/// See <c>README.md</c> for the longer write-up.
/// </para>
/// </remarks>
public partial class MultiSelectDropdown : UserControl
{
    public MultiSelectDropdown()
    {
        InitializeComponent();
        MultiSelectLog.Information("MultiSelectDropdown: control instantiated");
    }

    /// <summary>
    /// Push focus into the search box. Called by hosts that open the
    /// dropdown in response to a click, so the user can start typing
    /// immediately (Linear / Notion / cmdk affordance). No-op when the
    /// search box is hidden via <c>ShowSearchBox=false</c>.
    /// </summary>
    public void FocusSearchBox() => SearchBox.Focus();
}
