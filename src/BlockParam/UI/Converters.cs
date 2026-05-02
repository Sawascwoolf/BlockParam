using System.Globalization;
using System.Windows;
using System.Windows.Data;
using BlockParam.Config;

namespace BlockParam.UI;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is Visibility.Visible;
        if (parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag;
    }
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? false : true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? false : true;
    }
}

/// <summary>
/// Converts a RuleSource enum to/from bool for RadioButton binding.
/// ConverterParameter must be the RuleSource enum value name (e.g. "Local").
/// </summary>
public class RuleSourceToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is RuleSource source && parameter is string name)
            return source.ToString() == name;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string name
            && Enum.TryParse<RuleSource>(name, out var result))
            return result;
        return System.Windows.Data.Binding.DoNothing;
    }
}

/// <summary>
/// Converts a bool to a <see cref="GridLength"/> for binding row/column heights to
/// expand/collapse state. True → expanded length (parameter, default "*"); false → 0.
/// Parameter accepts any <see cref="GridLengthConverter"/> string ("*", "Auto", "2*", "150").
/// </summary>
public class BoolToGridLengthConverter : IValueConverter
{
    private static readonly GridLengthConverter _glc = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true)
            return new GridLength(0);
        var spec = parameter as string;
        if (string.IsNullOrEmpty(spec))
            return new GridLength(1, GridUnitType.Star);
        return (GridLength)_glc.ConvertFromString(spec)!;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string s && !string.IsNullOrEmpty(s)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
