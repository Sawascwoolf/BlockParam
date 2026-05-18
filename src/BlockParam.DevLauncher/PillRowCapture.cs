using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;
using BlockParam.Localization;
using BlockParam.Models;
using BlockParam.Services;
using BlockParam.UI;
using BlockParam.UI.Controls.PillMultiSelect;

namespace BlockParam.DevLauncher;

/// <summary>
/// Headless capture for the pill row that lives at the top of
/// <c>BulkChangeDialog</c> (PlcPills + trailing "+ PLC" button). Renders
/// several states into one composite PNG so the row's visual behavior
/// can be iterated on without spinning up the full dialog.
///
/// <para>
/// Uses real DevLauncher fixtures from <c>%TEMP%\BlockParam\</c> so the
/// rendered pill abbreviations / labels match what the developer sees
/// when running the launcher interactively. To exercise multi-PLC
/// rendering (which the on-disk fixtures don't always provide), some
/// scenes re-assign synthetic <c>PlcName</c> values to the real DB
/// summaries — the DB names themselves stay real.
/// </para>
///
/// <para>
/// Sibling to <see cref="PillMultiSelectCapture"/>, which snapshots the
/// inner <c>PillMultiSelect</c> control with synthetic employee/DB data.
/// </para>
/// </summary>
internal static class PillRowCapture
{
    // Material database glyph (24x24), inlined to avoid pulling in the
    // app-level resource dictionary just for one icon.
    private const string DatabaseIconPath =
        "M12 3C7.58 3 4 4.79 4 7v10c0 2.21 3.59 4 8 4s8-1.79 8-4V7c0-2.21-3.58-4-8-4zm0 2c3.87 0 6 1.5 6 2s-2.13 2-6 2-6-1.5-6-2 2.13-2 6-2zm0 14c-3.87 0-6-1.5-6-2v-2.4c1.43.86 3.6 1.4 6 1.4s4.57-.54 6-1.4V17c0 .5-2.13 2-6 2zm0-5c-3.87 0-6-1.5-6-2V9.6c1.43.86 3.6 1.4 6 1.4s4.57-.54 6-1.4V12c0 .5-2.13 2-6 2z";

    private static readonly Color CanvasColor = Color.FromRgb(0xF6, 0xF7, 0xF9);
    private static readonly Color CompositeBgColor = Color.FromRgb(0xEE, 0xEF, 0xF2);
    private static readonly Color CaptionColor = Color.FromRgb(0x1F, 0x29, 0x37);
    private static readonly Color SubcaptionColor = Color.FromRgb(0x6B, 0x72, 0x80);

    public static void Run(string outPath)
    {
        // Force en-US so captions and button text render in English
        // regardless of OS culture.
        var en = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = en;
        Thread.CurrentThread.CurrentUICulture = en;
        CultureInfo.DefaultThreadCurrentCulture = en;
        CultureInfo.DefaultThreadCurrentUICulture = en;
        UiZoomService.ReplaceShared(UiZoomService.CreateEphemeral());

        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Load real DB names from the DevLauncher fixture directory so the
        // capture matches what the developer sees when launching interactively.
        var fixtureDir = Path.Combine(Path.GetTempPath(), "BlockParam");
        var realDbs = Program.EnumerateDevLauncherDbs(fixtureDir, anchorPlc: null);
        Log.Information("PillRowCapture: loaded {Count} real fixture DBs from {Dir}",
            realDbs.Count, fixtureDir);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "UNHANDLED EXCEPTION");
            e.Handled = true;
        };

        var defs = BuildSceneDefs(realDbs);
        var bitmaps = new List<(string Title, string? Subtitle, BitmapSource Bmp, string? SectionHeader)>();
        // Scene bitmaps render at 2.0x for crisp sources, then get
        // downsampled when drawn into the composite canvas (scale 1.0).
        // That keeps the final PNG within FHD width while preserving
        // legible pill chrome.
        const double sceneScale = 2.0;
        const double compositeScale = 1.0;

        var idx = 0;
        Action? runNext = null;
        runNext = () =>
        {
            if (idx >= defs.Count) { app.Shutdown(); return; }
            var def = defs[idx++];
            var (window, popupHost) = def.Build();
            window.ContentRendered += (_, _) =>
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    PumpLayout(window);
                    BitmapSource bmp;
                    if (popupHost != null)
                        bmp = CompositeRowAndPopup(window, popupHost, sceneScale);
                    else if (window.Tag is HoverTooltipInfo hover)
                        bmp = CompositeRowAndTooltip(window, hover, sceneScale);
                    else
                        bmp = RenderToBitmap(window, window.ActualWidth, window.ActualHeight, sceneScale);
                    bitmaps.Add((def.Title, def.Subtitle, bmp, def.SectionHeader));
                    window.Close();
                    runNext!();
                }), DispatcherPriority.Background);
            };
            window.Show();
        };

        runNext();
        app.Run();

        StitchAndSave(bitmaps, outPath, compositeScale);
        Log.Information("Pill-row composite saved: {Path}", outPath);
    }

    // ── Scene definitions ─────────────────────────────────────────────────────

    private sealed record SceneDef(
        string Title,
        string? Subtitle,
        Func<(Window Window, FrameworkElement? PopupHost)> Build,
        string? SectionHeader = null);

    /// <summary>
    /// Synthetic PLC seed for the showcase section: PLC name + DB names with
    /// numbers + which numbers start active. Numbers are set so the
    /// abbreviation chip ("DB42") renders in the trigger pill — useful for
    /// design iteration where the real fixtures' Number=null state would
    /// produce empty abbreviations.
    /// </summary>
    private sealed record SyntheticPlc(
        string PlcName,
        (string Name, int Number)[] Dbs,
        int[] Selected);

    /// <summary>
    /// One PLC's pill content for a scene. <paramref name="Source"/> is the
    /// real <see cref="DataBlockSummary"/>s from <c>%TEMP%\BlockParam\</c>;
    /// <paramref name="OverridePlc"/> lets a scene rebrand them under a
    /// synthetic PLC name to exercise multi-PLC rendering.
    /// </summary>
    private sealed record PillSeed(
        string OverridePlc,
        IReadOnlyList<DataBlockSummary> Source,
        IReadOnlyList<string> ActiveNames);

    private static IReadOnlyList<SceneDef> BuildSceneDefs(
        IReadOnlyList<DataBlockSummary> realDbs)
    {
        var scenes = new List<SceneDef>();

        if (realDbs.Count == 0)
        {
            // Hosted CI runners have no %TEMP%\BlockParam\ exports, so the
            // real-fixtures section is empty there. Emit one placeholder for
            // context, then fall through to the synthetic + #141 scenes so
            // --capture-pill-row is still meaningful on CI (which is the only
            // place the screenshots job actually runs). Before this, an empty
            // %TEMP% short-circuited the WHOLE capture to just this placeholder
            // — the synthetic showcase never rendered on CI at all.
            scenes.Add(new("No real fixtures found",
                $"Expected DBs in {Path.Combine(Path.GetTempPath(), "BlockParam")} — synthetic + #141 scenes follow.",
                () => BuildEmptyScene(),
                SectionHeader: "Real fixtures (from %TEMP%\\BlockParam\\)"));
        }
        else
        {
            // Anchor = first real DB. The user's interactive session lands on
            // this DB by default (BulkChangeDialog opens with index 0 active).
            var anchor = realDbs[0];
            var firstThree = realDbs.Take(Math.Min(3, realDbs.Count)).ToList();

            // DevLauncher fixtures don't carry a real PLC name; rebrand to a
            // demo label so the always-on PLC label policy is visible in the
            // composite. Real TIA Openness sessions populate the PLC name
            // for free.
            const string demoPlc = "PLC_1";

            scenes.Add(new("Real fixtures · single DB active (anchor only)",
                $"Active set: {anchor.Name}. Matches the dialog's launch state.",
                () => BuildRowScene(new[]
                {
                    new PillSeed(
                        OverridePlc: demoPlc,
                        Source: realDbs,
                        ActiveNames: new[] { anchor.Name }),
                }, openIndex: -1, openAddPlc: false),
                SectionHeader: "Real fixtures (from %TEMP%\\BlockParam\\)"));

            scenes.Add(new("Real fixtures · multiple DBs active (same PLC)",
                $"Active set: {string.Join(", ", firstThree.Select(d => d.Name))}",
                () => BuildRowScene(new[]
                {
                    new PillSeed(
                        OverridePlc: demoPlc,
                        Source: realDbs,
                        ActiveNames: firstThree.Select(d => d.Name).ToList()),
                }, openIndex: -1, openAddPlc: false)));

            scenes.Add(new("Real fixtures · first pill's popup open",
                "Reproduces the 'no abbreviations in row' state when DB.Number is null.",
                () => BuildRowScene(new[]
                {
                    new PillSeed(
                        OverridePlc: demoPlc,
                        Source: realDbs,
                        ActiveNames: new[] { anchor.Name }),
                }, openIndex: 0, openAddPlc: false)));
        }

        // ── Synthetic multi-PLC showcase ─────────────────────────────────────
        // Pure synthetic data (numbers + PLC labels are made up) so the
        // overflow / wrap / multi-PLC visuals can be iterated on without
        // depending on what the developer happens to have in %TEMP%.
        scenes.AddRange(BuildSyntheticShowcaseScenes());

        // #141 regression repro — numberless instance DB, closed pill, NO
        // OverflowOptions. Always rendered (it's synthetic, not gated on
        // %TEMP% fixtures) so the screenshots job verifies the fix on CI.
        scenes.Add(BuildIssue141NumberlessScene());

        return scenes;
    }

    private static IReadOnlyList<SceneDef> BuildSyntheticShowcaseScenes()
    {
        // Compact fixture set used across multiple showcase scenes so each
        // one stays focused on the visual it's demonstrating.
        var power = new SyntheticPlc("PLC_PowerStation", new[]
        {
            ("DB_ProcessControlHigh", 10),
            ("DB_ProcessControlLow", 11),
            ("DB_PumpStation", 42),
            ("DB_ConfigParams", 99),
            ("DB_TankSettings", 200),
        }, new[] { 10, 99 });

        var filler = new SyntheticPlc("PLC_FillerLine", new[]
        {
            ("DB_Conveyor", 5),
            ("DB_Label", 6),
            ("DB_Quality", 20),
        }, new[] { 5, 6 });

        var hvac = new SyntheticPlc("PLC_HVAC", new[]
        {
            ("DB_Zone1", 30),
            ("DB_Zone2", 31),
        }, new[] { 30 });

        var util = new SyntheticPlc("PLC_Utilities", new[]
        {
            ("DB_AirCompressor", 50),
            ("DB_WaterChiller", 51),
        }, new[] { 50, 51 });

        return new List<SceneDef>
        {
            new("Single PLC · three DBs selected (synthetic, no other PLCs)",
                "Abbreviation overflow visible. '+ PLC' button hidden: only one PLC in the project.",
                () => BuildSyntheticScene(new[]
                {
                    new SyntheticPlc("", new[]
                    {
                        ("DB_Recipe", 42),
                        ("DB_Tank", 11),
                        ("DB_Pump", 7),
                    }, new[] { 42, 11, 7 }),
                }, openIndex: -1, openAddPlc: false, fixedWidth: double.NaN,
                   showAddButton: false),
                SectionHeader: "Synthetic multi-PLC showcase"),

            new("Multi-PLC · two pills, mixed selections (more PLCs in project)",
                "'+ PLC' visible because the project has additional PLCs (HVAC, Utilities) not yet in the row.",
                () => BuildSyntheticScene(
                    new[] { power, filler },
                    openIndex: -1, openAddPlc: false, fixedWidth: double.NaN,
                    showAddButton: true)),

            new("Multi-PLC · four pills, 720 px (wrap)",
                "Forces WrapPanel reflow onto a second row. All project PLCs already in row, so '+ PLC' is hidden.",
                () => BuildSyntheticScene(
                    new[] { power, filler, hvac, util },
                    openIndex: -1, openAddPlc: false, fixedWidth: 720,
                    showAddButton: false)),

            new("Multi-PLC · first pill's popup open",
                "Synthetic data with DB numbers populated — abbreviations render in each row.",
                () => BuildSyntheticScene(
                    new[] { power, filler },
                    openIndex: 0, openAddPlc: false, fixedWidth: double.NaN,
                    showAddButton: true)),

            new("Multi-PLC · '+ PLC' popup open",
                "Project-wide picker grouped by PlcName. Three candidate PLCs (FillerLine, HVAC, Utilities) available.",
                () => BuildSyntheticPlusPlcScene(
                    activePlcs: new[] { power },
                    candidates: new[] { filler, hvac, util })),

            // Empty-pill workflow (#pill-refactor · "+ PLC" adds an empty pill,
            // user opens it to pick DBs).
            new("After '+ PLC' click · empty pill added to row",
                "PLC_FillerLine just got added via '+ PLC'. The new pill has no active DBs yet — just the PLC name, no count badge. Two PLCs remain available so '+ PLC' is still visible.",
                () =>
                {
                    var emptyFiller = new SyntheticPlc(filler.PlcName, filler.Dbs,
                        Selected: System.Array.Empty<int>());
                    return BuildSyntheticScene(
                        new[] { power, emptyFiller },
                        openIndex: -1, openAddPlc: false,
                        fixedWidth: double.NaN, showAddButton: true);
                },
                SectionHeader: "Empty-pill workflow (after '+ PLC' adds a PLC)"),

            new("User opens the empty pill to pick DBs",
                "Same row, the user just clicked the freshly-added PLC_FillerLine pill. Its dropdown is open with every DB unchecked — picking one makes the pill populated (next scene).",
                () =>
                {
                    var emptyFiller = new SyntheticPlc(filler.PlcName, filler.Dbs,
                        Selected: System.Array.Empty<int>());
                    return BuildSyntheticScene(
                        new[] { power, emptyFiller },
                        openIndex: 1, openAddPlc: false,
                        fixedWidth: double.NaN, showAddButton: true);
                }),

            new("After picking a DB · empty pill is now populated",
                "The user checked DB_Conveyor in the open dropdown. The pill is now indistinguishable from the active-derived pills: count badge = 1, abbreviation 'DB_Conveyor' in the trigger.",
                () =>
                {
                    var filledFiller = new SyntheticPlc(filler.PlcName, filler.Dbs,
                        Selected: new[] { 5 });
                    return BuildSyntheticScene(
                        new[] { power, filledFiller },
                        openIndex: -1, openAddPlc: false,
                        fixedWidth: double.NaN, showAddButton: true);
                }),

            new("Hover tooltip · pill in abbreviation mode",
                "Joined full names exceed 30 chars so the trigger degrades to DB-numbers. Hovering the pill in production surfaces this tooltip with the real names — the static composite renders an equivalent overlay so the affordance is visible.",
                () =>
                {
                    // PowerStation in abbreviation mode (38 chars of joined
                    // full names trips AbbreviateAfterChars=30).
                    var powerOverflow = new SyntheticPlc("PLC_PowerStation", new[]
                    {
                        ("DB_ProcessControlHigh", 10),
                        ("DB_ProcessControlLow", 11),
                        ("DB_PumpStation", 42),
                        ("DB_ConfigParams", 99),
                        ("DB_TankSettings", 200),
                    }, new[] { 10, 99, 200 });
                    return BuildSyntheticScene(
                        new[] { powerOverflow, filler },
                        openIndex: -1,
                        openAddPlc: false,
                        fixedWidth: double.NaN,
                        showAddButton: true,
                        hoverTooltipIndex: 0);
                }),
        };
    }

    /// <summary>
    /// #141 regression repro. A numberless instance DB
    /// (<c>Gen_Main_IDB</c> — <c>_IDB</c> suffix, no DB number → empty
    /// <see cref="DataBlockListItem.Abbreviation"/>) in a CLOSED pill built
    /// WITHOUT <c>OverflowOptions</c>. That is the exact path the fix
    /// repairs: the default <c>PillTriggerToken</c> summary (no
    /// <c>_displayFormatter</c>). Pre-fix the trigger rendered BLANK (only
    /// the count badge + ×); post-fix it falls back to the full DB name.
    /// Pure synthetic so it renders on every run, including hosted CI where
    /// there are no <c>%TEMP%\BlockParam\</c> fixtures.
    /// </summary>
    private static SceneDef BuildIssue141NumberlessScene() =>
        new("#141 · numberless instance DB · closed pill, no OverflowOptions",
            "Gen_Main_IDB has no DB number so its abbreviation is empty. "
            + "With no OverflowOptions wired the trigger summary uses the "
            + "default token path — pre-fix this rendered blank, post-fix it "
            + "falls back to the full DB name.",
            () => BuildRowScene(new[]
            {
                new PillSeed(
                    OverridePlc: "CPU_1",
                    Source: new[]
                    {
                        new DataBlockSummary(
                            name: "Gen_Main_IDB",
                            folderPath: "",
                            blockType: "InstanceDB",
                            isInstanceDb: true,
                            plcName: "CPU_1",
                            number: null),
                    },
                    ActiveNames: new[] { "Gen_Main_IDB" }),
            }, openIndex: -1, openAddPlc: false, wireOverflowOptions: false),
            SectionHeader: "#141 regression — closed-pill default-path summary");

    // ── Window construction ──────────────────────────────────────────────────

    private static (Window Window, FrameworkElement? PopupHost) BuildRowScene(
        IReadOnlyList<PillSeed> seeds,
        int openIndex,
        bool openAddPlc,
        bool showAddButton = false,
        IReadOnlyList<DataBlockSummary>? addPlcSource = null,
        int hoverTooltipIndex = -1,
        bool wireOverflowOptions = true)
    {
        // Route pill construction through PlcPillGroupsService.Build so the
        // capture stays honest if Build's ordering / anchor / extraPlcs
        // logic changes later. We fabricate ActiveDb seeds (with stub
        // DataBlockInfo and no XML — capture never reads either) and let
        // Build pick the order, label, and PLC groupings.
        var perPlcItems = new Dictionary<string, IReadOnlyList<DataBlockListItem>>(
            StringComparer.Ordinal);
        var activeDbs = new List<ActiveDb>();
        var extraPlcs = new List<string>();

        foreach (var seed in seeds)
        {
            var plc = seed.OverridePlc;
            var items = seed.Source
                .Select(db => new DataBlockListItem(
                    summary: RebrandPlc(db, plc),
                    isActive: seed.ActiveNames.Contains(db.Name, StringComparer.Ordinal),
                    isAnchor: false))
                .ToList();
            perPlcItems[plc] = items;

            foreach (var sourceDb in seed.Source)
            {
                if (!seed.ActiveNames.Contains(sourceDb.Name, StringComparer.Ordinal))
                    continue;
                var info = new DataBlockInfo(
                    name: sourceDb.Name,
                    number: sourceDb.Number ?? 0,
                    memoryLayout: "Optimized",
                    blockType: "GlobalDB",
                    members: System.Array.Empty<MemberNode>());
                activeDbs.Add(new ActiveDb(info, xml: "", onApply: null, plcName: plc));
            }
            if (seed.ActiveNames.Count == 0)
                extraPlcs.Add(plc);
        }

        Func<string, Task<IReadOnlyList<DataBlockListItem>>> loadDbs = plc =>
            Task.FromResult(perPlcItems.TryGetValue(plc, out var items)
                ? items
                : System.Array.Empty<DataBlockListItem>());

        var anchorPlc = seeds.Count > 0 ? seeds[0].OverridePlc : "";
        var pillVms = PlcPillGroupsService.Build(
            activeDbs: activeDbs,
            anchorPlcName: anchorPlc,
            loadDbsForPlc: loadDbs,
            extraPlcs: extraPlcs).ToList();

        // The capture bypasses each pill's async LoadCommand entirely (so
        // popup-open scenes render synchronously). Pre-populate AvailableDbs
        // with the full per-PLC list, then re-point SelectedDbs at the same
        // instances — PillSelectionSync uses reference equality, so the
        // selected items have to be the very objects backing the rows or
        // the trigger renders an empty count.
        foreach (var vm in pillVms)
        {
            if (!perPlcItems.TryGetValue(vm.PlcName, out var items)) continue;
            vm.AvailableDbs.Clear();
            foreach (var it in items)
                vm.AvailableDbs.Add(it);
            vm.SelectedDbs.Clear();
            foreach (var it in items)
                if (it.IsActive) vm.SelectedDbs.Add(it);
        }

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        PillMultiSelect? popupHost = null;
        PillMultiSelect? hoverPill = null;
        IReadOnlyList<DataBlockListItem>? hoverPillItems = null;
        for (int i = 0; i < pillVms.Count; i++)
        {
            var vm = pillVms[i];
            var ms = new PillMultiSelect
            {
                ItemsSource = vm.AvailableDbs,
                SelectedItems = vm.SelectedDbs,
                Label = vm.Label,
                DisplayMemberPath = nameof(DataBlockListItem.Display),
                AbbreviationMemberPath = nameof(DataBlockListItem.Abbreviation),
                Icon = Geometry.Parse(DatabaseIconPath),
                TooltipMode = PillTooltipMode.FullNames,
                Margin = new Thickness(0, 0, 8, 0),
            };
            if (wireOverflowOptions)
            {
                // Mirror BulkChangeDialog.xaml's DbPillOverflowOptions so the
                // trigger renders full DB names until they exceed ~30 chars
                // or 4 entries, then degrades to abbreviations. The #141 repro
                // scene passes wireOverflowOptions:false to exercise the
                // DEFAULT token path (no _displayFormatter) — the exact path
                // the fix repairs for a numberless instance DB.
                ms.OverflowOptions = PillOverflowOptions.DataBlockDefault();
            }
            if (i == openIndex)
            {
                // Set IsOpen on the control directly so PlcPillViewModel's
                // async LoadCommand doesn't fire (it would clear+refill
                // AvailableDbs and race the capture).
                ms.IsOpen = true;
                popupHost = ms;
            }
            if (i == hoverTooltipIndex)
            {
                hoverPill = ms;
                hoverPillItems = vm.AvailableDbs
                    .Where(it => vm.SelectedDbs.Contains(it))
                    .ToList();
            }
            wrap.Children.Add(ms);
        }

        // Trailing "+ PLC" button. Honor the production CanAddPlc rule:
        // omit the button when the scene has no PLCs outside the active
        // row to add. When openAddPlc=true the project-wide picker is
        // built alongside; the capture composites its popup-child onto
        // the canvas.
        if (showAddButton || openAddPlc)
        {
            var addBtn = new Button
            {
                Content = Res.Get("PillRow_AddDb"),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
            };
            wrap.Children.Add(addBtn);
        }

        FrameworkElement? plcListBorder = null;
        if (openAddPlc && addPlcSource != null)
        {
            // Mirror the production "+ PLC" popup in BulkChangeDialog.xaml:
            // a flat ListBox of candidate PLC names. No DB checkboxes —
            // clicking a PLC adds it to the row as an empty pill.
            var distinctPlcs = addPlcSource
                .Select(db => db.PlcName ?? "")
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var listBox = new ListBox
            {
                ItemsSource = distinctPlcs,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                MaxHeight = 320,
                ItemTemplate = BuildPlcListItemTemplate(),
            };
            plcListBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                MinWidth = 180,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Color.FromRgb(0, 0, 0),
                    Opacity = 0.18,
                    BlurRadius = 12,
                    ShadowDepth = 2,
                },
                Child = listBox,
            };
        }

        // When the "+ PLC" list is showing, stack it under the wrap row so
        // it appears in roughly the same spot the live popup would land
        // (just below the trailing "+ PLC" button). Single render captures
        // both — no special compositing needed.
        FrameworkElement hostContent = plcListBorder != null
            ? new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children = { wrap, plcListBorder },
            }
            : (FrameworkElement)wrap;

        var host = new Border
        {
            Background = new SolidColorBrush(CanvasColor),
            Padding = new Thickness(20, 20, 20, 12),
            Child = hostContent,
        };

        var window = new Window
        {
            Content = host,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight,
        };
        if (hoverPill != null && hoverPillItems != null)
            window.Tag = new HoverTooltipInfo(hoverPill, hoverPillItems);
        return (window, popupHost);
    }

    /// <summary>
    /// Carried on <see cref="Window.Tag"/> when a scene wants the capture
    /// pipeline to composite a synthetic hover-tooltip overlay onto the
    /// scene image. Matches the WPF tooltip styling produced by
    /// <see cref="PillTooltipMode.FullNames"/>: one line per selected
    /// DB, white background with thin border and drop shadow.
    /// </summary>
    private sealed record HoverTooltipInfo(
        PillMultiSelect Pill,
        IReadOnlyList<DataBlockListItem> SelectedItems);

    /// <summary>
    /// Builds a row scene out of <see cref="SyntheticPlc"/> seeds. DB numbers
    /// come from the seed so abbreviations render in the trigger — useful
    /// for design iteration where real fixtures' Number=null state would
    /// hide the abbreviations.
    /// </summary>
    private static (Window Window, FrameworkElement? PopupHost) BuildSyntheticScene(
        IReadOnlyList<SyntheticPlc> plcs,
        int openIndex,
        bool openAddPlc,
        double fixedWidth,
        bool showAddButton = false,
        IReadOnlyList<SyntheticPlc>? addPlcCandidates = null,
        int hoverTooltipIndex = -1)
    {
        var seeds = plcs.Select(p => SeedFromSynthetic(p)).ToList();
        var (window, popupHost) = BuildRowScene(
            seeds,
            openIndex,
            openAddPlc,
            showAddButton: showAddButton,
            hoverTooltipIndex: hoverTooltipIndex,
            addPlcSource: addPlcCandidates?
                .SelectMany(c => c.Dbs.Select(d => new DataBlockSummary(
                    name: d.Name,
                    folderPath: "",
                    plcName: c.PlcName,
                    number: d.Number)))
                .ToList());
        if (!double.IsNaN(fixedWidth))
        {
            // Override SizeToContent from BuildRowScene so wrap scenes get
            // a fixed width and the WrapPanel reflows.
            window.SizeToContent = SizeToContent.Height;
            window.Width = fixedWidth;
        }
        return (window, popupHost);
    }

    /// <summary>
    /// "+ PLC" popup scene: builds a tiny row with the active PLCs in the
    /// pill row, then opens the project-wide picker (grouped by PlcName)
    /// alongside it.
    /// </summary>
    private static (Window Window, FrameworkElement? PopupHost) BuildSyntheticPlusPlcScene(
        IReadOnlyList<SyntheticPlc> activePlcs,
        IReadOnlyList<SyntheticPlc> candidates)
    {
        return BuildSyntheticScene(
            activePlcs,
            openIndex: -1,
            openAddPlc: true,
            fixedWidth: double.NaN,
            addPlcCandidates: candidates);
    }

    private static PillSeed SeedFromSynthetic(SyntheticPlc p)
    {
        var summaries = p.Dbs.Select(d => new DataBlockSummary(
            name: d.Name,
            folderPath: "",
            plcName: p.PlcName,
            number: d.Number)).ToList();
        var activeNames = p.Dbs
            .Where(d => p.Selected.Contains(d.Number))
            .Select(d => d.Name)
            .ToList();
        return new PillSeed(p.PlcName, summaries, activeNames);
    }

    /// <summary>
    /// DataTemplate matching the production "+ PLC" popup item template
    /// (BulkChangeDialog.xaml): a small DB icon + the PLC name. Built
    /// imperatively because the capture file isn't a XAML resource.
    /// </summary>
    private static DataTemplate BuildPlcListItemTemplate()
    {
        var sp = new FrameworkElementFactory(typeof(StackPanel));
        sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        sp.SetValue(FrameworkElement.MarginProperty, new Thickness(2));

        var icon = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        icon.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse(DatabaseIconPath));
        icon.SetValue(System.Windows.Shapes.Path.FillProperty,
            new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)));
        icon.SetValue(System.Windows.Shapes.Path.StretchProperty, Stretch.Uniform);
        icon.SetValue(FrameworkElement.WidthProperty, 14.0);
        icon.SetValue(FrameworkElement.HeightProperty, 14.0);
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        sp.AppendChild(icon);

        var tb = new FrameworkElementFactory(typeof(TextBlock));
        tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        tb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        sp.AppendChild(tb);

        return new DataTemplate { VisualTree = sp };
    }

    /// <summary>
    /// Returns a copy of <paramref name="db"/> with PlcName overridden.
    /// Used by multi-PLC scenes that re-brand real DBs under synthetic PLC
    /// labels without changing their names or numbers.
    /// </summary>
    private static DataBlockSummary RebrandPlc(DataBlockSummary db, string plc) =>
        new(
            name: db.Name,
            folderPath: db.FolderPath,
            blockType: db.BlockType,
            isInstanceDb: db.IsInstanceDb,
            plcName: string.IsNullOrEmpty(plc) ? db.PlcName : plc,
            number: db.Number);

    private static (Window Window, FrameworkElement? PopupHost) BuildEmptyScene()
    {
        var tb = new TextBlock
        {
            Text = "No real DevLauncher fixtures found. Export a DB from TIA "
                 + "Portal into %TEMP%\\BlockParam\\ first.",
            TextWrapping = TextWrapping.Wrap,
            Width = 480,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            FontSize = 12,
        };
        var host = new Border
        {
            Background = new SolidColorBrush(CanvasColor),
            Padding = new Thickness(20),
            Child = tb,
        };
        var window = new Window
        {
            Content = host,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight,
        };
        return (window, null);
    }

    // ── Rendering / composition ──────────────────────────────────────────────

    private static BitmapSource RenderToBitmap(Visual v, double width, double height, double scale)
    {
        var dpi = 96.0 * scale;
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(width * scale),
            (int)Math.Ceiling(height * scale),
            dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(v);
        return rtb;
    }

    /// <summary>
    /// Renders the row plus a synthetic hover tooltip floating below the
    /// pill named in <paramref name="hover"/>. Mimics WPF's default
    /// tooltip styling so the static PNG conveys the same "hover here to
    /// see full names" affordance the live control offers when its
    /// trigger is in abbreviation mode.
    /// </summary>
    private static BitmapSource CompositeRowAndTooltip(
        Window window, HoverTooltipInfo hover, double scale)
    {
        // Build the tooltip element. WPF's default tooltip is roughly:
        // white background, 1px #767676 border, 8px padding, drop shadow.
        var lines = hover.SelectedItems.Select(it => it.Display);
        var tooltipText = new TextBlock
        {
            Text = string.Join(Environment.NewLine, lines),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
        };
        var tooltip = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x76)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(8, 6, 8, 6),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(0, 0, 0),
                Opacity = 0.22,
                BlurRadius = 8,
                ShadowDepth = 2,
            },
            Child = tooltipText,
        };

        // Force the tooltip through measure/arrange so RenderSize is real.
        tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        tooltip.Arrange(new Rect(tooltip.DesiredSize));
        tooltip.UpdateLayout();

        var trigger = hover.Pill.TriggerElement;
        var triggerOrigin = trigger.TranslatePoint(new Point(0, 0), window);
        var triggerHeight = trigger.RenderSize.Height;
        var tipSize = tooltip.RenderSize;
        const double tooltipGap = 6;
        // Position the tip slightly indented under the trigger, matching
        // where Windows lays a real tooltip from a mouse pointer at the
        // bottom-left of the hovered element.
        var tipX = triggerOrigin.X + 16;
        var tipY = triggerOrigin.Y + triggerHeight + tooltipGap;

        // Canvas must fit the tooltip even if the row is narrower than it.
        var totalWidth = Math.Max(window.ActualWidth, tipX + tipSize.Width + 24);
        var totalHeight = Math.Max(window.ActualHeight, tipY + tipSize.Height + 18);

        var dpi = 96.0 * scale;
        var pxW = (int)Math.Ceiling(totalWidth * scale);
        var pxH = (int)Math.Ceiling(totalHeight * scale);
        var canvas = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(CanvasColor), null,
                new Rect(0, 0, totalWidth, totalHeight));
            var rowBmp = RenderToBitmap(window, window.ActualWidth, window.ActualHeight, scale);
            dc.DrawImage(rowBmp, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            var tipBmp = RenderToBitmap(tooltip, tipSize.Width, tipSize.Height, scale);
            dc.DrawImage(tipBmp, new Rect(tipX, tipY, tipSize.Width, tipSize.Height));
        }
        canvas.Render(dv);
        return canvas;
    }

    private static BitmapSource CompositeRowAndPopup(
        Window window, FrameworkElement host, double scale)
    {
        // Locate the Popup. PillMultiSelect exposes it via PopupElement; for
        // the "+ PLC" scene we pass the picker control itself, which is a
        // PillMultiSelect — so the lookup is identical.
        var pms = host as PillMultiSelect
            ?? throw new InvalidOperationException("PopupHost must be a PillMultiSelect.");
        var popupChild = pms.PopupElement.Child as FrameworkElement
            ?? throw new InvalidOperationException("Popup child not found.");
        popupChild.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        popupChild.Arrange(new Rect(popupChild.DesiredSize));
        popupChild.UpdateLayout();

        var trigger = pms.TriggerElement;
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
            dc.DrawRectangle(new SolidColorBrush(CanvasColor), null,
                new Rect(0, 0, totalWidth, totalHeight));

            var winBmp = RenderToBitmap(window, window.ActualWidth, window.ActualHeight, scale);
            dc.DrawImage(winBmp, new Rect(0, 0, window.ActualWidth, window.ActualHeight));

            var popBmp = RenderToBitmap(popupChild, popupSize.Width, popupSize.Height, scale);
            var popX = triggerOrigin.X;
            var popY = triggerOrigin.Y + triggerHeight + popupVerticalGap;
            dc.DrawImage(popBmp, new Rect(popX, popY, popupSize.Width, popupSize.Height));
        }
        canvas.Render(dv);
        return canvas;
    }

    /// <summary>
    /// Three-column masonry stitch sized landscape for a 1920x1080 monitor.
    /// Each scene drops into the currently-shortest column; scenes wider
    /// than a single column span 2 columns (or all 3 for the wrap scene).
    /// Section headers always span full width and level every column.
    /// </summary>
    private static void StitchAndSave(
        IReadOnlyList<(string Title, string? Subtitle, BitmapSource Bmp, string? SectionHeader)> scenes,
        string outPath,
        double scale)
    {
        // Final PNG width = compositeWidth * scale. 1900 * 1 = 1900 px ≤ 1920,
        // so the composite fits an FHD screen at 100% zoom with a thin
        // margin. Landscape orientation: 3 columns spread scenes
        // horizontally instead of stacking them tall.
        const int columnCount = 3;
        const double compositeWidth = 1900;
        const double padding = 20;
        const double colGap = 16;
        const double interSceneGap = 18;
        const double titleHeight = 22;
        const double subtitleHeight = 18;
        const double titleSubGap = 4;
        const double captionImageGap = 4;
        const double sectionHeaderHeight = 30;
        const double sectionHeaderTopGap = 12;
        const double sectionHeaderBottomGap = 8;

        var columnWidth =
            (compositeWidth - padding * 2 - colGap * (columnCount - 1)) / columnCount;

        var titleFace = new Typeface(new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var subFace = new Typeface(new FontFamily("Segoe UI"),
            FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        var sectionFace = new Typeface(new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        // ── Pass 1: compute placement (column, x, y) for each scene ─────────
        // Each column tracks its own bottom edge. Scenes that don't fit in
        // a single column span 2 or all 3, and section headers always
        // level every column to the current max before placement.
        var placements = new List<Placement>(scenes.Count);
        var colY = new double[columnCount];
        var colX = new double[columnCount];
        for (int c = 0; c < columnCount; c++)
        {
            colY[c] = padding;
            colX[c] = padding + c * (columnWidth + colGap);
        }
        int sceneNumber = 1;

        foreach (var (title, subtitle, bmp, section) in scenes)
        {
            // How many columns the scene needs. 1 if it fits, else 2,
            // else the full row.
            int span = 1;
            if (bmp.Width > columnWidth)
                span = 2;
            if (bmp.Width > 2 * columnWidth + colGap)
                span = columnCount;

            double cellWidth = span == columnCount
                ? compositeWidth - padding * 2
                : columnWidth * span + colGap * (span - 1);

            FormattedText? subText = null;
            double subHeight = 0;
            if (!string.IsNullOrEmpty(subtitle))
            {
                subText = new FormattedText(subtitle!,
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    subFace, 11, new SolidColorBrush(SubcaptionColor), 96.0)
                {
                    MaxTextWidth = cellWidth,
                };
                subHeight = Math.Max(subtitleHeight, subText.Height);
            }

            double cellHeight = titleHeight
                + (subText != null ? titleSubGap + subHeight : 0)
                + captionImageGap
                + bmp.Height;

            double sectionY = 0;
            bool hasSection = !string.IsNullOrEmpty(section);
            if (hasSection)
            {
                var maxY = colY.Max();
                if (sceneNumber > 1) maxY += sectionHeaderTopGap;
                sectionY = maxY;
                var afterSection = maxY + sectionHeaderHeight + sectionHeaderBottomGap;
                for (int c = 0; c < columnCount; c++) colY[c] = afterSection;
            }

            int leftCol;
            double cellX, cellY;
            if (span == columnCount)
            {
                // Full-width: level all columns, place across the entire row.
                cellY = colY.Max();
                cellX = padding;
                leftCol = 0;
                var after = cellY + cellHeight + interSceneGap;
                for (int c = 0; c < columnCount; c++) colY[c] = after;
            }
            else if (span == 2)
            {
                // Find the consecutive-column pair whose taller side is
                // lowest — that minimises wasted vertical space.
                leftCol = 0;
                double bestY = double.PositiveInfinity;
                for (int c = 0; c <= columnCount - 2; c++)
                {
                    var y = Math.Max(colY[c], colY[c + 1]);
                    if (y < bestY) { bestY = y; leftCol = c; }
                }
                cellY = bestY;
                cellX = colX[leftCol];
                var after = cellY + cellHeight + interSceneGap;
                colY[leftCol] = colY[leftCol + 1] = after;
            }
            else
            {
                // Single column: drop into the shortest one.
                leftCol = 0;
                for (int c = 1; c < columnCount; c++)
                    if (colY[c] < colY[leftCol]) leftCol = c;
                cellY = colY[leftCol];
                cellX = colX[leftCol];
                colY[leftCol] = cellY + cellHeight + interSceneGap;
            }

            placements.Add(new Placement(
                Number: sceneNumber,
                Title: title,
                Subtitle: subtitle,
                Bmp: bmp,
                Section: hasSection ? section : null,
                SectionY: sectionY,
                CellX: cellX,
                CellY: cellY,
                CellWidth: cellWidth,
                Spans: span > 1));
            sceneNumber++;
        }

        var totalHeight = colY.Max() - interSceneGap + padding;

        // ── Pass 2: draw onto canvas ────────────────────────────────────────
        var dpi = 96.0 * scale;
        var pxW = (int)Math.Ceiling(compositeWidth * scale);
        var pxH = (int)Math.Ceiling(totalHeight * scale);
        var canvas = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(CompositeBgColor), null,
                new Rect(0, 0, compositeWidth, totalHeight));

            foreach (var p in placements)
            {
                if (p.Section != null)
                {
                    // Divider line + bold heading spanning full width.
                    dc.DrawRectangle(
                        new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xD6)),
                        null,
                        new Rect(padding, p.SectionY - 2,
                            compositeWidth - padding * 2, 1));
                    var sectionText = new FormattedText(p.Section,
                        CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        sectionFace, 16,
                        new SolidColorBrush(CaptionColor), 96.0);
                    dc.DrawText(sectionText, new Point(padding, p.SectionY + 4));
                }

                double y = p.CellY;
                var titleText = new FormattedText(
                    $"{p.Number}. {p.Title}",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    titleFace, 14,
                    new SolidColorBrush(CaptionColor), 96.0)
                {
                    MaxTextWidth = p.CellWidth,
                };
                dc.DrawText(titleText, new Point(p.CellX, y));
                y += titleHeight;

                if (!string.IsNullOrEmpty(p.Subtitle))
                {
                    var subText = new FormattedText(p.Subtitle!,
                        CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        subFace, 11,
                        new SolidColorBrush(SubcaptionColor), 96.0)
                    {
                        MaxTextWidth = p.CellWidth,
                    };
                    dc.DrawText(subText, new Point(p.CellX, y + titleSubGap));
                    y += titleSubGap + Math.Max(subtitleHeight, subText.Height);
                }
                y += captionImageGap;
                dc.DrawImage(p.Bmp, new Rect(p.CellX, y, p.Bmp.Width, p.Bmp.Height));
            }
        }
        canvas.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(canvas));
        using var fs = File.Create(outPath);
        encoder.Save(fs);
    }

    private sealed record Placement(
        int Number,
        string Title,
        string? Subtitle,
        BitmapSource Bmp,
        string? Section,
        double SectionY,
        double CellX,
        double CellY,
        double CellWidth,
        bool Spans);

    private static void PumpLayout(Window window)
    {
        CommandManager.InvalidateRequerySuggested();
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }
}
