using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BlockParam.Services;

namespace BlockParam.UI;

/// <summary>
/// Discoverable surface for the UI zoom feature: shows the current zoom
/// percentage with − / + step buttons and a clickable percentage label that
/// resets to <see cref="UiZoomService.DefaultZoom"/> (mirrors Ctrl+0).
///
/// Reuses <see cref="UiZoomService.Shared"/> by default so a click here is
/// indistinguishable from Ctrl+scroll or Ctrl +/-: the same persisted state
/// drives every open dialog.
/// </summary>
public partial class ZoomIndicator : UserControl
{
    private UiZoomService _service = UiZoomService.Shared;

    public ZoomIndicator()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Swap the backing service. Used by tests and by capture mode to point
    /// the indicator at an ephemeral service rather than the global singleton.
    /// </summary>
    internal void Bind(UiZoomService service)
    {
        _service.ZoomChanged -= OnZoomChanged;
        _service = service;
        if (IsLoaded)
        {
            _service.ZoomChanged += OnZoomChanged;
            UpdateLabel(_service.ZoomFactor);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _service.ZoomChanged += OnZoomChanged;
        UpdateLabel(_service.ZoomFactor);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _service.ZoomChanged -= OnZoomChanged;
    }

    private void OnZoomChanged(double factor) => UpdateLabel(factor);

    private void UpdateLabel(double factor)
    {
        PercentButton.Content = string.Format(
            CultureInfo.InvariantCulture, "{0}%", (int)Math.Round(factor * 100));
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => _service.ZoomIn();
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => _service.ZoomOut();
    private void OnResetClick(object sender, RoutedEventArgs e) => _service.ResetZoom();
}
