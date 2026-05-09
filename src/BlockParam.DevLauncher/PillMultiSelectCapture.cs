using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;
using BlockParam.Services;
using BlockParam.UI.Controls.PillMultiSelect;

namespace BlockParam.DevLauncher;

/// <summary>
/// Headless capture for the <see cref="PillMultiSelect"/> control. Renders
/// two scenes that match the reference design — closed-with-3-selected and
/// open-popup-with-2-selected. Uses a minimal chromeless host window and
/// composites the popup's child into the same PNG (since the WPF Popup
/// lives in its own HWND, it is invisible to a window-level RenderTargetBitmap).
/// </summary>
internal static class PillMultiSelectCapture
{
    private static readonly (string Display, string Abbrev)[] DemoEmployees =
    {
        ("A. Kowalski", "AKO"),
        ("B. Schäfer", "BSC"),
        ("C. Hoffmann", "CHO"),
        ("D. Lang", "DLN"),
        ("E. Krüger", "EKR"),
        ("F. Baumann", "FBM"),
        ("G. Weber", "GWE"),
        ("H. Roth", "HRT"),
        ("I. Zentner", "IZN"),
        ("J. Fischer", "JFR"),
    };

    // Material person icon (24x24).
    private const string PersonIconPath =
        "M12 12c2.21 0 4-1.79 4-4s-1.79-4-4-4-4 1.79-4 4 1.79 4 4 4zm0 2c-2.67 0-8 1.34-8 4v2h16v-2c0-2.66-5.33-4-8-4z";

    // Material database / chip icon (24x24) — per-PLC pill leading glyph.
    private const string DatabaseIconPath =
        "M12 3C7.58 3 4 4.79 4 7v10c0 2.21 3.59 4 8 4s8-1.79 8-4V7c0-2.21-3.58-4-8-4zm0 2c3.87 0 6 1.5 6 2s-2.13 2-6 2-6-1.5-6-2 2.13-2 6-2zm0 14c-3.87 0-6-1.5-6-2v-2.4c1.43.86 3.6 1.4 6 1.4s4.57-.54 6-1.4V17c0 .5-2.13 2-6 2zm0-5c-3.87 0-6-1.5-6-2V9.6c1.43.86 3.6 1.4 6 1.4s4.57-.54 6-1.4V12c0 .5-2.13 2-6 2z";

    /// <summary>
    /// Per-PLC seed for the multi-PLC demo scenes: PLC name + a list of
    /// (db name, db number) pairs + which numbers are selected.
    /// </summary>
    private record PlcSeed(string Plc, (string Name, int Number)[] Dbs, int[] SelectedDbNumbers);

    private static readonly (string Name, int Number)[] PlcASample =
    {
        ("DB_ProcessControl_HighPriority", 10),
        ("DB_ProcessControl_LowPriority", 11),
        ("DB_PumpStation_001", 42),
        ("DB_ConfigParams", 99),
        ("DB_DiagnosticData", 100),
        ("DB_RecipeManager", 101),
        ("DB_TankSettings_3", 200),
    };

    private static readonly (string Name, int Number)[] PlcBSample =
    {
        ("DB_ConveyorControl", 5),
        ("DB_LabelPrinter", 6),
        ("DB_QualityCheck", 20),
        ("DB_PackagingLine", 21),
    };

    private static readonly (string Name, int Number)[] PlcCSample =
    {
        ("DB_HVAC_Zone1", 30),
        ("DB_HVAC_Zone2", 31),
    };

    private static readonly (string Name, int Number)[] PlcDSample =
    {
        ("DB_AirCompressor", 50),
        ("DB_WaterChiller", 51),
    };

    public static void Run(string outDir)
    {
        var en = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = en;
        Thread.CurrentThread.CurrentUICulture = en;
        CultureInfo.DefaultThreadCurrentCulture = en;
        CultureInfo.DefaultThreadCurrentUICulture = en;
        UiZoomService.ReplaceShared(UiZoomService.CreateEphemeral());

        Directory.CreateDirectory(outDir);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "UNHANDLED EXCEPTION");
            e.Handled = true;
        };

        Action? runNext = null;
        var scenes = new (string File, string[] SelectedAbbrevs, bool Open)[]
        {
            ("01_pill_closed.png", new[] { "AKO", "EKR", "GWE" }, false),
            ("02_pill_open.png",   new[] { "AKO", "BSC" },        true),
        };

        var idx = 0;
        runNext = () =>
        {
            if (idx >= scenes.Length) { app.Shutdown(); return; }
            var scene = scenes[idx++];

            var (window, control, vm) = BuildSceneWindow();
            ApplySelection(vm, scene.SelectedAbbrevs);
            vm.IsOpen = scene.Open;

            window.ContentRendered += (_, _) =>
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    PumpLayout(window);
                    var outPath = Path.Combine(outDir, scene.File);
                    if (scene.Open)
                        CompositeTriggerAndPopupToPng(window, control, outPath, scale: 2.0);
                    else
                        Program.CaptureWindowToPng(window, outPath, scale: 2.0);
                    Log.Information("Pill scene saved: {Path}", outPath);
                    window.Close();
                    runNext!();
                }), DispatcherPriority.Background);
            };
            window.Show();
        };

        runNext();
        app.Run();
    }

    /// <summary>
    /// Multi-PLC datablock demo. Renders four scenes that exercise the
    /// overflow rules (full names below threshold, DB-numbers above, "+N more"
    /// collapse) and the WrapPanel layout for projects with many PLCs.
    /// </summary>
    public static void RunDb(string outDir)
    {
        var en = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = en;
        Thread.CurrentThread.CurrentUICulture = en;
        CultureInfo.DefaultThreadCurrentCulture = en;
        CultureInfo.DefaultThreadCurrentUICulture = en;
        UiZoomService.ReplaceShared(UiZoomService.CreateEphemeral());

        Directory.CreateDirectory(outDir);

        var scenes = new (string File, PlcSeed[] Plcs)[]
        {
            // Below thresholds: full DB names rendered.
            ("03_db_short.png", new[]
            {
                new PlcSeed("PLC_PowerStation", PlcASample, new[] { 99 }),
            }),
            // Char-threshold trip: 3 long names → switch to DB-numbers.
            ("04_db_chars_overflow.png", new[]
            {
                new PlcSeed("PLC_PowerStation", PlcASample, new[] { 10, 42, 100 }),
            }),
            // Count + collapse trip: 6 selected → DB-numbers AND "+N more".
            ("05_db_count_overflow.png", new[]
            {
                new PlcSeed("PLC_PowerStation", PlcASample, new[] { 10, 11, 42, 99, 100, 200 }),
            }),
            // Two PLCs side by side, each carrying its own selection.
            ("06_db_multi_plc.png", new[]
            {
                new PlcSeed("PLC_PowerStation", PlcASample, new[] { 10, 99 }),
                new PlcSeed("PLC_FillerLine", PlcBSample, new[] { 5, 21 }),
            }),
            // Four PLCs — WrapPanel demonstrates row-2 wrap on a narrower host.
            ("07_db_wrap.png", new[]
            {
                new PlcSeed("PLC_PowerStation", PlcASample, new[] { 10, 11, 42, 99, 100, 200 }),
                new PlcSeed("PLC_FillerLine", PlcBSample, new[] { 5, 21 }),
                new PlcSeed("PLC_HVAC", PlcCSample, new[] { 30 }),
                new PlcSeed("PLC_Utilities", PlcDSample, new[] { 50, 51 }),
            }),
        };

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "UNHANDLED EXCEPTION");
            e.Handled = true;
        };

        var idx = 0;
        Action? runNext = null;
        runNext = () =>
        {
            if (idx >= scenes.Length) { app.Shutdown(); return; }
            var scene = scenes[idx++];
            // Wrap scenario uses a narrower host so the second row actually appears.
            var width = scene.File.Contains("wrap") ? 720.0 : double.NaN;
            var window = BuildPlcRowWindow(scene.Plcs, width);

            window.ContentRendered += (_, _) =>
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    PumpLayout(window);
                    var outPath = Path.Combine(outDir, scene.File);
                    Program.CaptureWindowToPng(window, outPath, scale: 2.0);
                    Log.Information("DB pill scene saved: {Path}", outPath);
                    window.Close();
                    runNext!();
                }), DispatcherPriority.Background);
            };
            window.Show();
        };

        runNext();
        app.Run();
    }

    /// <summary>
    /// Builds a chromeless host whose content is a WrapPanel of one
    /// <see cref="PillMultiSelect"/> per supplied PLC seed. Each pill carries
    /// the PLC name as its label and a <see cref="PillOverflowFormatter"/>
    /// configured for the DB defaults.
    /// </summary>
    private static Window BuildPlcRowWindow(IReadOnlyList<PlcSeed> plcs, double widthOrNaN)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };

        foreach (var seed in plcs)
        {
            var vm = new PillMultiSelectViewModel
            {
                Label = seed.Plc,
                Icon = Geometry.Parse(DatabaseIconPath),
            };
            foreach (var (name, num) in seed.Dbs)
                vm.AddItem(new PillMultiSelectItemViewModel(name, $"DB{num}", num));
            foreach (var num in seed.SelectedDbNumbers)
            {
                var item = vm.Items.FirstOrDefault(i => (int?)i.Payload == num);
                if (item != null) item.IsSelected = true;
            }

            // Wire DB overflow rules: full names below threshold, DB-numbers
            // above (count OR chars), then collapse with "+N more".
            var options = PillOverflowOptions.DataBlockDefault();
            vm.DisplayFormatter = selected => PillOverflowFormatter.Format(selected, options);

            var control = new PillMultiSelect
            {
                DataContext = vm,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 8, 8),
            };
            wrap.Children.Add(control);
        }

        var host = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9)),
            Padding = new Thickness(20, 20, 20, 12),
            Child = wrap,
        };

        var window = new Window
        {
            Content = host,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
        };
        if (double.IsNaN(widthOrNaN))
        {
            window.SizeToContent = SizeToContent.WidthAndHeight;
        }
        else
        {
            window.Width = widthOrNaN;
            window.SizeToContent = SizeToContent.Height;
        }
        return window;
    }

    private static (Window window, PillMultiSelect control, PillMultiSelectViewModel vm) BuildSceneWindow()
    {
        var vm = new PillMultiSelectViewModel
        {
            Label = "Mitarbeiter",
            Icon = Geometry.Parse(PersonIconPath),
        };
        foreach (var (display, abbrev) in DemoEmployees)
            vm.AddItem(new PillMultiSelectItemViewModel(display, abbrev));

        var control = new PillMultiSelect
        {
            DataContext = vm,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        // Outer host: light page background to mimic an embedded usage,
        // generous padding so the pill + popup don't kiss the edges.
        var host = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9)),
            Padding = new Thickness(20),
            Child = control,
        };

        var window = new Window
        {
            Content = host,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
        };
        return (window, control, vm);
    }

    private static void ApplySelection(PillMultiSelectViewModel vm, string[] abbrevs)
    {
        foreach (var abbrev in abbrevs)
        {
            foreach (var item in vm.Items)
            {
                if (item.Abbreviation == abbrev)
                {
                    item.IsSelected = true;
                    break;
                }
            }
        }
    }

    private static void PumpLayout(Window window)
    {
        // Make sure the popup (if open) has fully arranged before we snap.
        // ContextIdle drains every higher-priority queue, then Render flushes
        // the final visual update.
        CommandManager.InvalidateRequerySuggested();
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    /// <summary>
    /// Renders the host window (containing the trigger pill) and the open
    /// popup's child element into a single composite PNG. The Popup lives in
    /// its own HWND so a window-level RenderTargetBitmap would miss it; we
    /// render both pieces independently and stitch them onto a transparent
    /// canvas, with the popup placed flush under the trigger.
    /// </summary>
    private static void CompositeTriggerAndPopupToPng(
        Window window, PillMultiSelect control, string outputPath, double scale)
    {
        var popupChild = FindPopupChild(control)
            ?? throw new InvalidOperationException("Popup child not found.");
        // Force the popup's own visual tree to measure/arrange at its natural size.
        popupChild.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        popupChild.Arrange(new Rect(popupChild.DesiredSize));
        popupChild.UpdateLayout();

        // The trigger lives inside Border > UserControl > Grid > ToggleButton.
        // Its origin relative to the host window's content root determines
        // where the popup should sit in the composite.
        var trigger = FindToggleTrigger(control)
            ?? throw new InvalidOperationException("Pill trigger not found.");
        var triggerOrigin = trigger.TranslatePoint(new Point(0, 0), window);

        var triggerHeight = trigger.RenderSize.Height;
        var popupSize = popupChild.RenderSize;
        // Popup's VerticalOffset in XAML is 4 — match it visually.
        const double popupVerticalGap = 4;

        // Final composite size: tall enough to fit window + popup tail; wide
        // enough to fit either the window or the popup's right edge.
        var popupRight = triggerOrigin.X + popupSize.Width;
        var totalWidth = Math.Max(window.ActualWidth, popupRight + 24);   // +24 padding
        var totalHeight = triggerOrigin.Y + triggerHeight + popupVerticalGap + popupSize.Height + 24;

        var dpi = 96.0 * scale;
        var pxW = (int)Math.Ceiling(totalWidth * scale);
        var pxH = (int)Math.Ceiling(totalHeight * scale);
        var canvas = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // Page background under everything (matches host Border).
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9)),
                null, new Rect(0, 0, totalWidth, totalHeight));

            // Render the host window's visual tree (trigger pill + padding).
            var winBmp = RenderToBitmap(window, window.ActualWidth, window.ActualHeight, scale);
            dc.DrawImage(winBmp, new Rect(0, 0, window.ActualWidth, window.ActualHeight));

            // Render the popup child below the trigger.
            var popBmp = RenderToBitmap(popupChild, popupSize.Width, popupSize.Height, scale);
            var popX = triggerOrigin.X;
            var popY = triggerOrigin.Y + triggerHeight + popupVerticalGap;
            dc.DrawImage(popBmp, new Rect(popX, popY, popupSize.Width, popupSize.Height));
        }
        canvas.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(canvas));
        using var fs = File.Create(outputPath);
        encoder.Save(fs);
    }

    private static RenderTargetBitmap RenderToBitmap(Visual v, double width, double height, double scale)
    {
        var dpi = 96.0 * scale;
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(width * scale),
            (int)Math.Ceiling(height * scale),
            dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(v);
        return rtb;
    }

    private static FrameworkElement? FindPopupChild(PillMultiSelect control)
    {
        // The popup's named in XAML as PillPopup.
        var field = typeof(PillMultiSelect).GetField("PillPopup",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(control) is System.Windows.Controls.Primitives.Popup popup)
            return popup.Child as FrameworkElement;
        return null;
    }

    private static FrameworkElement? FindToggleTrigger(PillMultiSelect control)
    {
        var field = typeof(PillMultiSelect).GetField("PillTrigger",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(control) as FrameworkElement;
    }
}
