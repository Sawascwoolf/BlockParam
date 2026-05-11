using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Visibility converter that returns <see cref="Visibility.Visible"/> when
/// the input is a <see cref="PillGroupViewModel"/> (i.e. an explicit-grouping
/// header is being rendered) and <see cref="Visibility.Collapsed"/> otherwise.
/// Used in <c>PillMultiSelect.xaml</c>'s shared GroupItem template to swap
/// between the explicit-group header and the selected-first 1px divider.
/// </summary>
internal sealed class PillGroupHeaderVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PillGroupViewModel ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

/// <summary>
/// Visibility converter for the items presenter inside a GroupItem.
/// Returns <see cref="Visibility.Visible"/> when the bound IsExpanded value
/// is <c>true</c> or absent (selected-first grouping, no PillGroupViewModel),
/// and <see cref="Visibility.Collapsed"/> only when an explicit-group
/// <see cref="PillGroupViewModel.IsExpanded"/> is <c>false</c>.
/// </summary>
internal sealed class PillGroupExpandedVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}
