using System;
using System.Globalization;
using System.Threading;
using Serilog;
using BlockParam.Localization;
using BlockParam.Services;
using BlockParam.UI;

namespace BlockParam.DevLauncher;

/// <summary>
/// #127: LIVE demo of the pre-dialog loading splash, driving the real
/// <see cref="LoadingSplashController"/> — real STA render thread, real 1.5s
/// reveal timer, real random quip pick. Unlike <see cref="SplashCapture"/>
/// (which renders a static VM snapshot for the marketing screenshot), this
/// exercises the actual timing behavior so the quip rules can be eyeballed
/// without a TIA Portal install.
///
/// The DevLauncher main thread plays the part of TIA's UI thread: it calls
/// Show / Report / SetCounter / Close on the controller exactly the way
/// <c>BulkChangeContextMenu.OnClick</c> does, with sleeps standing in for the
/// synchronous Openness export/parse work.
///
/// <code>
/// dotnet build src/BlockParam.DevLauncher -c Debug
/// # slow open (~6s of fake prep) — quip appears after ~1.5s and stays:
/// src/BlockParam.DevLauncher/bin/Debug/net48/BlockParam.DevLauncher.exe --demo-splash
/// # fast open (~0.8s) — quip must NEVER appear:
/// src/BlockParam.DevLauncher/bin/Debug/net48/BlockParam.DevLauncher.exe --demo-splash fast
/// </code>
/// </summary>
internal static class SplashDemo
{
    public static void Run(bool slow)
    {
        // en-US so the demo reads in English regardless of OS culture.
        var en = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = en;
        Thread.CurrentThread.CurrentUICulture = en;
        CultureInfo.DefaultThreadCurrentCulture = en;
        CultureInfo.DefaultThreadCurrentUICulture = en;

        // Pick + localize the quip here, on this (TIA-stand-in) thread — the
        // splash render thread must never touch Res. This is exactly what
        // BulkChangeContextMenu does.
        var quipKey = LoadingHumorService.PickKey();
        var quip = Res.Get(quipKey);
        Log.Information("Demo: picked quip {Key} = \"{Text}\"", quipKey, quip);
        Log.Information(slow
            ? "Demo: SLOW open (~6s prep) — quip should appear ~1.5s in and then sit still."
            : "Demo: FAST open (~0.8s prep) — quip should NEVER appear.");

        var splash = new LoadingSplashController(Res.Get("Splash_Title"), quip);
        splash.Show();
        splash.Report(Res.Get("Splash_Preparing"));

        if (slow)
        {
            // ~6s of staged "work". The reveal timer trips at 1.5s, so the
            // quip surfaces partway through Exporting and holds for the rest.
            Step(splash, "Splash_RefreshingUdtCache", null, 700);
            Step(splash, "Splash_LoadingUdtCache", null, 700);
            splash.Report(Res.Format("Splash_ExportingDb", "DB_ProcessPlant_A1"));
            splash.SetCounter(Res.Format("Splash_Counter", 1, 3));
            Thread.Sleep(1500);   // crosses the 1.5s threshold here → quip shows
            Log.Information("Demo: ~1.5s elapsed — quip should now be visible (dim/italic, under the status line).");
            splash.Report(Res.Format("Splash_ParsingDb", "DB_ProcessPlant_A1"));
            Thread.Sleep(1200);
            splash.SetCounter(Res.Format("Splash_Counter", 2, 3));
            splash.Report(Res.Format("Splash_ExportingDb", "DB_Conveyor_B2"));
            Thread.Sleep(1200);
            splash.SetCounter(string.Empty);
            Step(splash, "Splash_LoadingConfig", null, 900);
        }
        else
        {
            // Fast path: finishes well before the 1.5s threshold, so the timer
            // never fires and HumorLine stays empty.
            splash.Report(Res.Format("Splash_ExportingDb", "DB_ProcessPlant_A1"));
            Thread.Sleep(800);
        }

        splash.Close();
        // Give the splash thread a beat to tear its window down before the
        // process exits (the thread is background, so this is just for a clean
        // visual close).
        Thread.Sleep(400);
        Log.Information("Demo: done. Re-run a few times to see different random quips; pass 'fast' to confirm no quip on quick opens.");
    }

    private static void Step(LoadingSplashController splash, string statusKey, string? counter, int ms)
    {
        splash.Report(Res.Get(statusKey));
        if (counter != null) splash.SetCounter(counter);
        Thread.Sleep(ms);
    }
}
