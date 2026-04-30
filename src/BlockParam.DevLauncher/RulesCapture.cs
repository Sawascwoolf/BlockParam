using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Serilog;
using BlockParam.Config;
using BlockParam.Services;
using BlockParam.UI;

namespace BlockParam.DevLauncher;

/// <summary>
/// Headless capture of the ConfigEditorDialog (rules editor) for the
/// website hero screenshot. Loads rules from a fixed fixtures directory so
/// the shot is reproducible regardless of the developer's %APPDATA%, and
/// pre-selects the first rule so the right-side detail panel is populated.
/// </summary>
internal static class RulesCapture
{
    public static void Run(string outPath, string rulesDir)
    {
        var en = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = en;
        Thread.CurrentThread.CurrentUICulture = en;
        CultureInfo.DefaultThreadCurrentCulture = en;
        CultureInfo.DefaultThreadCurrentUICulture = en;
        UiZoomService.ReplaceShared(UiZoomService.CreateEphemeral());

        var fullOut = Path.GetFullPath(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOut)!);

        if (!Directory.Exists(rulesDir))
            throw new DirectoryNotFoundException($"Rules dir not found: {rulesDir}");

        var configLoader = new ConfigLoader(configPath: null,
            scriptedRulesDirOverride: Path.GetFullPath(rulesDir));

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "UNHANDLED EXCEPTION");
            e.Handled = true;
        };

        var vm = new ConfigEditorViewModel(configLoader);

        var dialog = new ConfigEditorDialog(vm)
        {
            Width = 800,
            Height = 600,
        };

        dialog.ContentRendered += (_, _) =>
        {
            dialog.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (vm.RuleFiles.Count > 0)
                    vm.SelectedFile = vm.RuleFiles.First();

                dialog.UpdateLayout();
                dialog.Dispatcher.Invoke(() => { },
                    System.Windows.Threading.DispatcherPriority.ContextIdle);
                dialog.Dispatcher.Invoke(() => { },
                    System.Windows.Threading.DispatcherPriority.Render);

                Program.CaptureWindowToPng(dialog, fullOut, scale: 2.0);
                Log.Information("Rules editor saved: {Path}", fullOut);

                dialog.Close();
                app.Shutdown();
            }), System.Windows.Threading.DispatcherPriority.Background);
        };

        dialog.Show();
        app.Run();
    }
}
