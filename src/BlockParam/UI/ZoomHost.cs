using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BlockParam.Services;

namespace BlockParam.UI;

/// <summary>
/// Attaches per-window UI zoom to a <see cref="Window"/>:
/// Ctrl+Scroll, Ctrl +/-, Ctrl+0 (reset) scale the window content via
/// <see cref="FrameworkElement.LayoutTransform"/>. The window itself does
/// not resize — content scales within the existing window bounds, like
/// browser zoom.
///
/// Zoom is shared across all dialogs via <see cref="UiZoomService.Shared"/>
/// so toggling zoom in one dialog updates every open dialog at once and the
/// preference persists across restarts (issue #14).
///
/// <para>
/// Requires <see cref="Window.Content"/> to be a <see cref="FrameworkElement"/>.
/// If the content root is a raw <see cref="UIElement"/> the zoom silently
/// no-ops on that window — all in-tree dialogs use Grid/DockPanel roots,
/// so this holds today.
/// </para>
///
/// <para>
/// WPF caveat: content hosted in a <see cref="System.Windows.Controls.Primitives.Popup"/>
/// lives in a separate HWND and does <b>not</b> inherit the LayoutTransform.
/// Help popups and tooltips therefore render unscaled relative to the rest
/// of the dialog. Overlays used by BulkChangeDialog (autocomplete, hint,
/// scope) were deliberately reworked to be inline Borders instead of Popups
/// so they scale correctly.
/// </para>
/// </summary>
internal static class ZoomHost
{
    public static void Attach(Window window) => Attach(window, UiZoomService.Shared);

    public static void Attach(Window window, UiZoomService service)
    {
        void ApplyZoom(double factor)
        {
            if (window.Content is not FrameworkElement root) return;

            root.LayoutTransform = Math.Abs(factor - 1.0) < 0.0001
                ? Transform.Identity
                : new ScaleTransform(factor, factor);
        }

        void OnZoomChanged(double factor) => ApplyZoom(factor);

        window.Loaded += (_, _) => ApplyZoom(service.ZoomFactor);
        service.ZoomChanged += OnZoomChanged;
        window.Closed += (_, _) => service.ZoomChanged -= OnZoomChanged;

        window.PreviewKeyDown += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;

            switch (e.Key)
            {
                case Key.OemPlus:
                case Key.Add:
                    service.ZoomIn();
                    e.Handled = true;
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    service.ZoomOut();
                    e.Handled = true;
                    break;
                case Key.D0:
                case Key.NumPad0:
                    service.ResetZoom();
                    e.Handled = true;
                    break;
            }
        };

        window.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
            if (e.Delta > 0) service.ZoomIn();
            else if (e.Delta < 0) service.ZoomOut();
            e.Handled = true;
        };
    }
}
