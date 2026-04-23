using System;
using System.Windows;
using System.Windows.Media.Imaging;
using Serilog;

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
        catch (Exception ex)
        {
            Log.Debug("Could not load window icon: {Message}", ex.Message);
        }
    }
}
