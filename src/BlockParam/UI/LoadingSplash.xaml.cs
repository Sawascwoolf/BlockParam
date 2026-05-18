using System.Windows;

namespace BlockParam.UI;

/// <summary>
/// Borderless, indeterminate pre-dialog splash (#125). Created and shown
/// only on <see cref="LoadingSplashController"/>'s dedicated STA dispatcher
/// thread — never on TIA's UI thread. No zoom host / icon helper on purpose:
/// it is ephemeral, chrome-less, and lives on a separate Dispatcher where
/// the shared zoom singleton must not be touched cross-thread.
/// </summary>
public partial class LoadingSplash : Window
{
    public LoadingSplash()
    {
        InitializeComponent();
    }
}
