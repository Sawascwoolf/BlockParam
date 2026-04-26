using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using Newtonsoft.Json;
using Serilog;
using BlockParam.Licensing;
using BlockParam.Services;
using BlockParam.UI;

namespace BlockParam.DevLauncher;

/// <summary>
/// #20: Headless capture of the LicenseKeyDialog in the three visual states the
/// shared-license-file feature introduces — unmanaged baseline, managed+Pro
/// steady state, and the moment after IT rotates the key (cache invalidated,
/// awaiting heartbeat). Each scene gets its own temp storage + shared-file
/// location so the user's real %APPDATA%\BlockParam and %PROGRAMDATA%\BlockParam
/// are never touched.
/// </summary>
internal static class LicenseCapture
{
    public static void Run(string outDir)
    {
        var en = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = en;
        Thread.CurrentThread.CurrentUICulture = en;
        CultureInfo.DefaultThreadCurrentCulture = en;
        CultureInfo.DefaultThreadCurrentUICulture = en;
        UiZoomService.ReplaceShared(UiZoomService.CreateEphemeral());

        Directory.CreateDirectory(outDir);
        var sandboxRoot = Path.Combine(Path.GetTempPath(),
            $"BlockParamLicCapture_{Guid.NewGuid():N}");

        var scenes = new (string File, Action<string, string> Seed)[]
        {
            ("01_unmanaged_free.png", (storage, sharedKey) =>
            {
                // Nothing on disk → Free tier, no managed hint, dialog editable.
            }),
            ("02_managed_pro.png", (storage, sharedKey) =>
            {
                // IT pushed key, server already confirmed (cache present + matching).
                File.WriteAllText(sharedKey, "PRO-IT-1234-5678");
                WriteLicenseData(storage, "PRO-IT-1234-5678");
                WriteProCache(storage);
            }),
            ("03_managed_free_after_rotation.png", (storage, sharedKey) =>
            {
                // IT just rotated the key. User cache still grants Pro on the OLD
                // key, but AdoptSharedLicenseKeyIfPresent invalidates it on
                // construction — heartbeat hasn't run yet, so tier is Free.
                File.WriteAllText(sharedKey, "PRO-IT-NEW-ROTATED");
                WriteLicenseData(storage, "PRO-IT-OLD-EXPIRED");
                WriteProCache(storage);
            }),
        };

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var idx = 0;

        Action? runNext = null;
        runNext = () =>
        {
            if (idx >= scenes.Length) { app.Shutdown(); return; }
            var scene = scenes[idx++];

            var sceneRoot = Path.Combine(sandboxRoot, $"scene_{idx}");
            var storage = Path.Combine(sceneRoot, "user");
            var sharedKey = Path.Combine(sceneRoot, "shared", "license.key");
            Directory.CreateDirectory(storage);
            Directory.CreateDirectory(Path.GetDirectoryName(sharedKey)!);
            scene.Seed(storage, sharedKey);

            // Pass a non-null but bogus server URL so heartbeat code paths exist
            // but stay dormant — we never call StartHeartbeat or click Activate.
            var svc = new OnlineLicenseService(storage,
                serverBaseUrl: "https://example.invalid",
                sharedLicenseFilePath: sharedKey);

            var dialog = new LicenseKeyDialog(svc);
            dialog.ContentRendered += (_, _) =>
            {
                dialog.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var outPath = Path.Combine(outDir, scene.File);
                    Program.CaptureWindowToPng(dialog, outPath, scale: 2.0);
                    Log.Information("License scene saved: {Path}", outPath);
                    svc.Dispose();
                    dialog.Close();
                    runNext!();
                }), System.Windows.Threading.DispatcherPriority.Background);
            };
            dialog.Show();
        };

        runNext();
        app.Run();

        try { Directory.Delete(sandboxRoot, recursive: true); } catch { /* best effort */ }
    }

    private static void WriteLicenseData(string storage, string key)
    {
        var data = new OnlineLicenseService.LicenseData
        {
            LicenseKey = key,
            InstanceId = Guid.NewGuid().ToString(),
            ActivatedAt = DateTime.UtcNow,
        };
        File.WriteAllText(Path.Combine(storage, "license.json"),
            JsonConvert.SerializeObject(data, Formatting.Indented));
    }

    private static void WriteProCache(string storage)
    {
        var cache = new OnlineLicenseService.CachedLicenseResponse
        {
            ReceivedAtUtc = DateTime.UtcNow,
            ExpiresAt = null,
            MaxConcurrent = 1,
            ActiveSessions = 1,
        };
        var json = JsonConvert.SerializeObject(cache);
        File.WriteAllBytes(Path.Combine(storage, "license_cache.dat"),
            Obfuscation.Obfuscate(json));
    }
}
