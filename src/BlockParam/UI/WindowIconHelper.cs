using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace BlockParam.UI;

internal static class WindowIconHelper
{
    public static void SetIcon(Window window)
    {
        try
        {
            var uri = new Uri("pack://application:,,,/BlockParam;component/Resources/BlockParam_logo.ico", UriKind.Absolute);
            window.Icon = BitmapFrame.Create(uri);
        }
        catch
        {
            // Icon is decorative — silently fall back to the default WPF icon.
        }
    }
}
