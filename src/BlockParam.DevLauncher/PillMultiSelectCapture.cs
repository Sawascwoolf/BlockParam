using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    // ── Demo data DTOs ────────────────────────────────────────────────────────

    /// <summary>
    /// Demo employee row. Exposed as a public property set so
    /// <see cref="PillMultiSelect.DisplayMemberPath"/>,
    /// <see cref="PillMultiSelect.AbbreviationMemberPath"/>, and
    /// <see cref="PillMultiSelect.GroupKeyMemberPath"/> can resolve them.
    /// </summary>
    private sealed class DemoEmployee
    {
        public string Name { get; set; } = string.Empty;
        public string Abbrev { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
    }

    /// <summary>
    /// Demo data-block row bound via <see cref="PillMultiSelect.DisplayMemberPath"/>
    /// and <see cref="PillMultiSelect.AbbreviationMemberPath"/>.
    /// </summary>
    private sealed class DemoDb
    {
        public string Name { get; set; } = string.Empty;
        public string Number { get; set; } = string.Empty;
        public int DbNumber { get; set; }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static readonly DemoEmployee[] DemoEmployees =
    {
        new() { Name = "A. Kowalski",  Abbrev = "AKO", Department = "Engineering" },
        new() { Name = "B. Schäfer",   Abbrev = "BSC", Department = "Operations"  },
        new() { Name = "C. Hoffmann",  Abbrev = "CHO", Department = "Engineering" },
        new() { Name = "D. Lang",      Abbrev = "DLN", Department = "Operations"  },
        new() { Name = "E. Krüger",    Abbrev = "EKR", Department = "Engineering" },
        new() { Name = "F. Baumann",   Abbrev = "FBM", Department = "Operations"  },
        new() { Name = "G. Weber",     Abbrev = "GWE", Department = "Engineering" },
        new() { Name = "H. Roth",      Abbrev = "HRT", Department = "Quality"     },
        new() { Name = "I. Zentner",   Abbrev = "IZN", Department = "Engineering" },
        new() { Name = "J. Fischer",   Abbrev = "JFR", Department = "Quality"     },
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

    /// <summary>
    /// One step in a headless-capture sequence: a closure that builds the
    /// window for the scene and a closure that snaps it to disk once layout
    /// has settled. Both run on the WPF dispatcher thread inside
    /// <see cref="RunCaptureScenes"/>.
    /// </summary>
    private sealed record CaptureScene(Func<Window> Build, Action<Window> Capture);

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

    // ── Public entry points ───────────────────────────────────────────────────

    public static void Run(string outDir)
    {
        var sceneData = new (string File, string[] Selected, bool Open)[]
        {
            ("01_pill_closed.png", new[] { "AKO", "EKR", "GWE" }, false),
            ("02_pill_open.png",   new[] { "AKO", "BSC" },        true),
        };

        var scenes = new List<CaptureScene>();
        foreach (var (file, selected, open) in sceneData)
        {
            PillMultiSelect? control = null;
            scenes.Add(new CaptureScene(
                Build: () =>
                {
                    var (window, c) = BuildSceneWindow();
                    ApplySelectionByAbbrevDp(c, DemoEmployees, selected);
                    SetIsOpen(c, open);
                    control = c;
                    return window;
                },
                Capture: window =>
                {
                    var outPath = Path.Combine(outDir, file);
                    if (open)
                        CompositeTriggerAndPopupToPng(window, control!, outPath, scale: 2.0);
                    else
                        Program.CaptureWindowToPng(window, outPath, scale: 2.0);
                    Log.Information("Pill scene saved: {Path}", outPath);
                }));
        }

        RunCaptureScenes(outDir, scenes);
    }

    /// <summary>
    /// Grouped-popup scenes: open with mixed-selection tri-state headers,
    /// user-collapsed group, and search-forces-expanded-group. Exercises
    /// the GroupKeyMemberPath / PillGroupViewModel rendering and the
    /// search-into-collapsed expansion policy.
    /// </summary>
    public static void RunGrouped(string outDir)
    {
        // 2 of 5 in Engineering = tri-state header; 3 of 3 in Operations =
        // fully-checked header; 0 of 2 in Quality = unchecked. Picked so
        // every header state appears in one screenshot.
        var selectedAbbrevs = new[] { "AKO", "CHO", "BSC", "DLN", "FBM" };

        var scenes = new List<CaptureScene>
        {
            // 08 — grouped popup open, mixed selection: all three header states visible
            BuildGroupedScene(
                fileName: "08_pill_grouped_open.png",
                selectedAbbrevs: selectedAbbrevs,
                configure: _ => { /* default — all groups expanded, no search */ },
                outDir),

            // 09 — user collapsed the Operations group; tri-state header on
            // Engineering still visible; collapsed group hides its members.
            BuildGroupedScene(
                fileName: "09_pill_grouped_collapsed.png",
                selectedAbbrevs: selectedAbbrevs,
                configure: state =>
                {
                    if (state.Groups.TryGetValue("Operations", out var ops))
                        ops.IsExpanded = false;
                },
                outDir),

            // 10 — search active. "ann" matches Hoffmann (Engineering) and
            // Baumann (Operations). User had collapsed Operations; the
            // search expansion policy forces it open so the match is reachable.
            BuildGroupedScene(
                fileName: "10_pill_grouped_search.png",
                selectedAbbrevs: selectedAbbrevs,
                configure: state =>
                {
                    if (state.Groups.TryGetValue("Operations", out var ops))
                        ops.IsExpanded = false;
                    state.SearchText = "ann";
                },
                outDir),
        };

        RunCaptureScenes(outDir, scenes);
    }

    private static CaptureScene BuildGroupedScene(
        string fileName,
        string[] selectedAbbrevs,
        Action<PillMultiSelectInternalState> configure,
        string outDir)
    {
        PillMultiSelect? control = null;

        return new CaptureScene(
            Build: () =>
            {
                var (window, c) = BuildSceneWindow(grouped: true);
                ApplySelectionByAbbrevDp(c, DemoEmployees, selectedAbbrevs);
                SetIsOpen(c, open: true);
                configure(c.InternalState);
                control = c;
                return window;
            },
            Capture: window =>
            {
                var outPath = Path.Combine(outDir, fileName);
                CompositeTriggerAndPopupToPng(window, control!, outPath, scale: 2.0);
                Log.Information("Grouped pill scene saved: {Path}", outPath);
            });
    }

    /// <summary>
    /// Interactive demo: opens a normal (chromed, resizable) window hosting
    /// the multi-PLC pill row pre-seeded with the same fixtures as the
    /// capture scenes.
    /// </summary>
    public static void RunDbInteractive()
    {
        UiZoomService.ReplaceShared(UiZoomService.CreateEphemeral());

        var app = new Application();
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "UNHANDLED EXCEPTION in dispatcher");
            e.Handled = true;
        };

        var seeds = new[]
        {
            new PlcSeed("PLC_PowerStation", PlcASample, new[] { 10, 99 }),
            new PlcSeed("PLC_FillerLine", PlcBSample, new[] { 5, 21 }),
            new PlcSeed("PLC_HVAC", PlcCSample, new[] { 30 }),
            new PlcSeed("PLC_Utilities", PlcDSample, new[] { 50, 51 }),
        };

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var seed in seeds)
            wrap.Children.Add(BuildPlcPill(seed));

        var hint = new TextBlock
        {
            Text = "Click any pill to open its dropdown. Toggle selections to watch the "
                 + "overflow rules engage (full names → DB-numbers → \"+N more\"). "
                 + "Resize the window narrower to see the WrapPanel reflow.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            FontSize = 11,
            Margin = new Thickness(4, 0, 4, 12),
        };

        var stack = new StackPanel();
        stack.Children.Add(hint);
        stack.Children.Add(wrap);

        var host = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9)),
            Padding = new Thickness(20, 18, 20, 20),
            Child = stack,
        };

        var window = new Window
        {
            Title = "PillMultiSelect — interactive demo",
            Content = host,
            Width = 900,
            Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        app.Run(window);
    }

    /// <summary>
    /// Multi-PLC datablock demo. Renders scenes exercising the overflow rules
    /// and the WrapPanel layout for projects with many PLCs.
    /// </summary>
    public static void RunDb(string outDir)
    {
        var sceneData = new (string File, PlcSeed[] Plcs)[]
        {
            ("03_db_short.png", new[]
            {
                new PlcSeed("PLC_PowerStation", PlcASample, new[] { 99 }),
            }),
            ("04_db_chars_overflow.png", new[]
            {
                new PlcSeed("PLC_PowerStation", PlcASample, new[] { 10, 42, 100 }),
            }),
            ("05_db_count_overflow.png", new[]
            {
                new PlcSeed("PLC_PowerStation", PlcASample, new[] { 10, 11, 42, 99, 100, 200 }),
            }),
            ("06_db_multi_plc.png", new[]
            {
                new PlcSeed("PLC_PowerStation", PlcASample, new[] { 10, 99 }),
                new PlcSeed("PLC_FillerLine", PlcBSample, new[] { 5, 21 }),
            }),
            ("07_db_wrap.png", new[]
            {
                new PlcSeed("PLC_PowerStation", PlcASample, new[] { 10, 11, 42, 99, 100, 200 }),
                new PlcSeed("PLC_FillerLine", PlcBSample, new[] { 5, 21 }),
                new PlcSeed("PLC_HVAC", PlcCSample, new[] { 30 }),
                new PlcSeed("PLC_Utilities", PlcDSample, new[] { 50, 51 }),
            }),
        };

        var scenes = new List<CaptureScene>();
        foreach (var (file, plcs) in sceneData)
        {
            var width = file.Contains("wrap") ? 720.0 : double.NaN;
            scenes.Add(new CaptureScene(
                Build: () => BuildPlcRowWindow(plcs, width),
                Capture: window =>
                {
                    var outPath = Path.Combine(outDir, file);
                    Program.CaptureWindowToPng(window, outPath, scale: 2.0);
                    Log.Information("DB pill scene saved: {Path}", outPath);
                }));
        }

        RunCaptureScenes(outDir, scenes);
    }

    // ── Private orchestration ─────────────────────────────────────────────────

    private static void RunCaptureScenes(string outDir, IReadOnlyList<CaptureScene> scenes)
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

        var idx = 0;
        Action? runNext = null;
        runNext = () =>
        {
            if (idx >= scenes.Count) { app.Shutdown(); return; }
            var scene = scenes[idx++];
            var window = scene.Build();
            window.ContentRendered += (_, _) =>
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    PumpLayout(window);
                    scene.Capture(window);
                    window.Close();
                    runNext!();
                }), DispatcherPriority.Background);
            };
            window.Show();
        };

        runNext();
        app.Run();
    }

    // ── Builder helpers ───────────────────────────────────────────────────────

    private static Window BuildPlcRowWindow(IReadOnlyList<PlcSeed> plcs, double widthOrNaN)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var seed in plcs)
            wrap.Children.Add(BuildPlcPill(seed));

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
            window.SizeToContent = SizeToContent.WidthAndHeight;
        else
        {
            window.Width = widthOrNaN;
            window.SizeToContent = SizeToContent.Height;
        }
        return window;
    }

    /// <summary>
    /// Constructs one configured <see cref="PillMultiSelect"/> from a PLC
    /// seed using the new DP API. Used by both headless captures and the
    /// interactive demo so fixtures stay in sync.
    /// </summary>
    private static PillMultiSelect BuildPlcPill(PlcSeed seed)
    {
        var dbs = seed.Dbs
            .Select(d => new DemoDb { Name = d.Name, Number = $"DB{d.Number}", DbNumber = d.Number })
            .ToList();

        var options = PillOverflowOptions.DataBlockDefault();

        var pill = new PillMultiSelect
        {
            ItemsSource = dbs,
            DisplayMemberPath = nameof(DemoDb.Name),
            AbbreviationMemberPath = nameof(DemoDb.Number),
            Label = seed.Plc,
            Icon = Geometry.Parse(DatabaseIconPath),
            OverflowOptions = options,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 8, 8),
        };

        // Hover the pill to recover the full DB names that overflow has
        // collapsed/abbreviated. TooltipMode DP is the XAML-friendly way
        // to get built-in FullNames tooltip without a custom Func.
        pill.TooltipMode = PillTooltipMode.FullNames;

        // Pre-select items whose DbNumber is in the seed's chosen set.
        // Uses the SelectedItems DP directly — no reflection, no Loaded event.
        pill.SelectedItems = new ObservableCollection<object>(
            dbs.Where(d => seed.SelectedDbNumbers.Contains(d.DbNumber)));

        return pill;
    }

    private static (Window window, PillMultiSelect control) BuildSceneWindow(bool grouped = false)
    {
        var employees = DemoEmployees
            .Select(e => new DemoEmployee { Name = e.Name, Abbrev = e.Abbrev, Department = e.Department })
            .ToList();

        var control = new PillMultiSelect
        {
            ItemsSource = employees,
            DisplayMemberPath = nameof(DemoEmployee.Name),
            AbbreviationMemberPath = nameof(DemoEmployee.Abbrev),
            Label = grouped ? "Team" : "Mitarbeiter",
            Icon = Geometry.Parse(PersonIconPath),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        if (grouped)
        {
            // Pre-set grouping before SelectedItems so each row enters its
            // group already wired and aggregate header tri-state is computed
            // off the very first selection reconcile pass.
            control.GroupKeyMemberPath = nameof(DemoEmployee.Department);
            control.PopupWidth = 320;
            control.PopupMaxListHeight = 360;
        }

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
        return (window, control);
    }

    // ── Selection helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Pre-selects rows whose Abbreviation is in <paramref name="abbrevs"/> by
    /// filtering the control's actual <c>ItemsSource</c> and assigning the
    /// matching items to the <see cref="PillMultiSelect.SelectedItems"/> DP.
    /// Reading from <c>ItemsSource</c> (not the static seed) is critical —
    /// <see cref="PillSelectionSync"/> uses reference equality, so the
    /// SelectedItems entries must be the same object instances the rows
    /// wrap.
    /// </summary>
    private static void ApplySelectionByAbbrevDp(
        PillMultiSelect pill,
        IReadOnlyList<DemoEmployee> employees,
        string[] abbrevs)
    {
        var abbrevSet = new HashSet<string>(abbrevs, StringComparer.Ordinal);
        // Prefer the live ItemsSource: BuildSceneWindow copies the static
        // seed, and selection must reference the same instances as the rows.
        var source = pill.ItemsSource as IEnumerable<DemoEmployee> ?? employees;
        pill.SelectedItems = new ObservableCollection<object>(
            source.Where(e => abbrevSet.Contains(e.Abbrev)));
    }

    private static void SetIsOpen(PillMultiSelect pill, bool open)
    {
        pill.IsOpen = open;
    }

    // ── Layout / render helpers ───────────────────────────────────────────────

    private static void PumpLayout(Window window)
    {
        CommandManager.InvalidateRequerySuggested();
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private static void CompositeTriggerAndPopupToPng(
        Window window, PillMultiSelect control, string outputPath, double scale)
    {
        var popupChild = FindPopupChild(control)
            ?? throw new InvalidOperationException("Popup child not found.");
        popupChild.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        popupChild.Arrange(new Rect(popupChild.DesiredSize));
        popupChild.UpdateLayout();

        var trigger = FindToggleTrigger(control);
        var triggerOrigin = trigger.TranslatePoint(new Point(0, 0), window);

        var triggerHeight = trigger.RenderSize.Height;
        var popupSize = popupChild.RenderSize;
        const double popupVerticalGap = 4;

        var popupRight = triggerOrigin.X + popupSize.Width;
        var totalWidth = Math.Max(window.ActualWidth, popupRight + 24);
        var totalHeight = triggerOrigin.Y + triggerHeight + popupVerticalGap + popupSize.Height + 24;

        var dpi = 96.0 * scale;
        var pxW = (int)Math.Ceiling(totalWidth * scale);
        var pxH = (int)Math.Ceiling(totalHeight * scale);
        var canvas = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9)),
                null, new Rect(0, 0, totalWidth, totalHeight));

            var winBmp = RenderToBitmap(window, window.ActualWidth, window.ActualHeight, scale);
            dc.DrawImage(winBmp, new Rect(0, 0, window.ActualWidth, window.ActualHeight));

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

    private static FrameworkElement? FindPopupChild(PillMultiSelect control) =>
        control.PopupElement.Child as FrameworkElement;

    private static FrameworkElement FindToggleTrigger(PillMultiSelect control) =>
        control.TriggerElement;
}
