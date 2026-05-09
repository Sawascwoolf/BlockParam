using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace BlockParam.UI.Controls.PillMultiSelect;

public partial class PillMultiSelect : UserControl
{
    public PillMultiSelect()
    {
        InitializeComponent();
    }

    private void OnPopupOpened(object sender, System.EventArgs e)
    {
        // Push focus into the search box so the user can start typing
        // immediately on click — same affordance as Linear / Notion / cmdk.
        SearchBox.Focus();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        // Stop the click from bubbling to the parent ToggleButton — clicking
        // the X is "wipe selection", not "open the popup". Without this the
        // user gets both behaviors per click which is jarring.
        e.Handled = true;
    }
}
