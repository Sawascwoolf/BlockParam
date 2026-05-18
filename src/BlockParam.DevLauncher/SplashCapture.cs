using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using BlockParam.Localization;
using BlockParam.Services;
using BlockParam.UI;

namespace BlockParam.DevLauncher;

/// <summary>
/// #125: headless capture of the pre-dialog loading splash. The splash only
/// ever appears on the real TIA Add-In path (the launcher constructs the
/// dialog VM directly and never runs <c>BulkChangeContextMenu.OnClick</c>),
/// so the only way to keep its marketing screenshot in sync with the actual
/// XAML is to render <see cref="LoadingSplash"/> itself — bound to a
/// representative <see cref="LoadingSplashViewModel"/> — rather than driving
/// the controller's STA thread.
///
/// Sibling of <see cref="LicenseCapture"/> / <see cref="RulesCapture"/>.
///
/// Regenerate:
/// <code>
/// dotnet build src/BlockParam.DevLauncher -c Debug
/// src/BlockParam.DevLauncher/bin/Debug/net48/BlockParam.DevLauncher.exe \
///     --capture-splash assets/screenshots/loading-splash.png
/// </code>
/// </summary>
internal static class SplashCapture
{
    public static void Run(string outPath)
    {
        // Force en-US so the title / step / counter render in English
        // regardless of OS culture (matches every other capture mode).
        var en = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = en;
        Thread.CurrentThread.CurrentUICulture = en;
        CultureInfo.DefaultThreadCurrentCulture = en;
        CultureInfo.DefaultThreadCurrentUICulture = en;
        UiZoomService.ReplaceShared(UiZoomService.CreateEphemeral());

        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "UNHANDLED EXCEPTION");
            e.Handled = true;
        };

        // Representative multi-DB state — mirrors the issue #125 mock-up:
        // "Exporting DB_ProcessPlant_A1…" with the "(2 of 3)" counter line
        // visible, so the screenshot documents the per-DB progress + counter.
        var vm = new LoadingSplashViewModel
        {
            Title = Res.Get("Splash_Title"),
            StatusText = Res.Format("Splash_ExportingDb", "DB_ProcessPlant_A1"),
            CounterText = Res.Format("Splash_Counter", 2, 3),
        };

        var splash = new LoadingSplash
        {
            DataContext = vm,
            // The live splash is CenterScreen + Topmost; neither matters for
            // an off-screen RenderTargetBitmap, but leaving them avoids any
            // first-show layout difference vs. production.
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        splash.ContentRendered += (_, _) =>
        {
            // The marquee ProgressBar is indeterminate: at the first render
            // frame the moving block sits at the far left and reads as empty.
            // Let the storyboard advance ~600 ms so the captured frame shows
            // the bar mid-sweep, the way a user actually sees it.
            var settle = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(600),
            };
            settle.Tick += (_, _) =>
            {
                settle.Stop();
                Program.CaptureWindowToPng(splash, outPath, scale: 2.0);
                Log.Information("Loading-splash screenshot saved: {Path}", outPath);
                splash.Close();
                app.Shutdown();
            };
            settle.Start();
        };

        splash.Show();
        app.Run();
    }
}
