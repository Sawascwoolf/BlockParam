using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BlockParam.PillSample;

public partial class App : Application
{
    // Optional "--capture <out.png>" mode for CI / sanity-checking from a
    // headless context. Renders the MainWindow to a PNG and exits — keeps
    // the rest of the app trivial.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();

        if (e.Args.Length >= 2 && e.Args[0] == "--capture")
        {
            var outPath = Path.GetFullPath(e.Args[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            window.Loaded += (_, _) =>
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    CaptureWindowToPng(window, outPath, scale: 2.0);
                    Shutdown();
                }));
        }

        window.Show();
    }

    private static void CaptureWindowToPng(Window window, string outPath, double scale)
    {
        var w = (int)Math.Ceiling(window.ActualWidth * scale);
        var h = (int)Math.Ceiling(window.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(outPath);
        encoder.Save(fs);
    }
}
