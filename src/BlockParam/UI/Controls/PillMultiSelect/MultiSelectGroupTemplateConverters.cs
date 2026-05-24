using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Visibility converter that returns <see cref="Visibility.Visible"/> when
/// the input is a <see cref="MultiSelectGroupViewModel"/> (i.e. an explicit-grouping
/// header is being rendered) and <see cref="Visibility.Collapsed"/> otherwise.
/// Used in <c>PillMultiSelect.xaml</c>'s shared GroupItem template to swap
/// between the explicit-group header and the selected-first 1px divider.
/// </summary>
// `public`, not `internal`: this converter is referenced by XAML
// `{StaticResource GroupHeaderVis}` and applied to a `{Binding ... Converter=…}`.
// When grouping is active, WPF's binding pipeline reflects on the converter
// instance from PresentationFramework — same partial-trust foreign-assembly
// reflection rule as the bound pill VM types. See #141. Today no production
// host enables grouping on the pill, but keep the safety net so the next one
// doesn't reopen this bug.
public sealed class MultiSelectGroupHeaderVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is MultiSelectGroupViewModel ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

/// <summary>
/// Visibility converter for the items presenter inside a GroupItem.
/// Returns <see cref="Visibility.Visible"/> when the bound IsExpanded value
/// is <c>true</c> or absent (selected-first grouping, no MultiSelectGroupViewModel),
/// and <see cref="Visibility.Collapsed"/> only when an explicit-group
/// <see cref="MultiSelectGroupViewModel.IsExpanded"/> is <c>false</c>.
/// </summary>
// `public`, not `internal`: same #141 reasoning as
// MultiSelectGroupHeaderVisibilityConverter above.
public sealed class MultiSelectGroupExpandedVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}
