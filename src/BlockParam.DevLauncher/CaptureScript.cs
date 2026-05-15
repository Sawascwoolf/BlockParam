using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using BlockParam.Services;
using BlockParam.UI;

namespace BlockParam.DevLauncher;

/// <summary>
/// JSON-driven screenshot scripting for the website asset pipeline.
/// See assets/screenshots/scripts/ for concrete scripts, CLAUDE.md for usage.
/// </summary>
public sealed class CaptureScript
{
    [JsonProperty("output_dir")] public string? OutputDir { get; set; }
    [JsonProperty("dpi")] public double? Dpi { get; set; }
    [JsonProperty("viewport")] public Viewport? Viewport { get; set; }
    [JsonProperty("fixture")] public string? Fixture { get; set; }
    [JsonProperty("udt_dir")] public string? UdtDir { get; set; }
    [JsonProperty("tag_table_dir")] public string? TagTableDir { get; set; }
    [JsonProperty("rules_dir")] public string? RulesDir { get; set; }
    [JsonProperty("scenes")] public List<Scene> Scenes { get; set; } = new();
}

public sealed class Viewport
{
    [JsonProperty("width")] public double Width { get; set; }
    [JsonProperty("height")] public double Height { get; set; }
}

public sealed class Scene
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("filename")] public string Filename { get; set; } = "";
    [JsonProperty("dialog")] public string? Dialog { get; set; }
    [JsonProperty("viewport")] public Viewport? Viewport { get; set; }
    [JsonProperty("expand")] public List<string>? Expand { get; set; }
    [JsonProperty("filter")] public FilterState? Filter { get; set; }

    /// <summary>Path of a leaf member to select (populates AvailableScopes).</summary>
    [JsonProperty("select")] public string? Select { get; set; }

    /// <summary>Ancestor path to scope the bulk edit to ("" = DB root).</summary>
    [JsonProperty("scope")] public string? Scope { get; set; }

    /// <summary>Pending value to stage (yellow highlight, no apply).</summary>
    [JsonProperty("newValue")] public string? NewValue { get; set; }

    /// <summary>
    /// If true, invoke the Set command after applying scope+newValue. Moves
    /// the live bulk preview into the Pending-edits queue (same as clicking
    /// the "Set" button in the inspector). Used for scenes that showcase the
    /// pending queue / Apply buttons.
    /// </summary>
    [JsonProperty("stageBulk")] public bool? StageBulk { get; set; }

    /// <summary>Inspector sidebar display options.</summary>
    [JsonProperty("sidebar")] public SidebarState? Sidebar { get; set; }

    /// <summary>
    /// If true, accept this autocomplete suggestion value into NewValue
    /// (equivalent to clicking the suggestion in the popup). Applied
    /// AFTER newValue so a scene can type a prefix to open the popup and
    /// then accept a full suggestion in the same frame.
    /// </summary>
    [JsonProperty("acceptSuggestion")] public string? AcceptSuggestion { get; set; }

    /// <summary>
    /// If true, invoke the Apply command after Set / accept. Writes all
    /// pending edits to the in-memory XML (no confirmation dialog in
    /// capture mode). Used for end-of-workflow scenes.
    /// </summary>
    [JsonProperty("apply")] public bool? Apply { get; set; }

    /// <summary>
    /// If true, do NOT call ResetTreeState before applying this scene's
    /// directives. The prior scene's expansions, selection, scope, pending
    /// edits and sidebar state are preserved, so multi-step workflows can
    /// build up cumulatively frame by frame (e.g. typing one character at
    /// a time while an earlier scene's selection and autocomplete popup
    /// remain in place).
    /// </summary>
    [JsonProperty("preserveState")] public bool? PreserveState { get; set; }

    /// <summary>
    /// Drives an inline-edit in the flat list (cell-level autocomplete).
    /// Sets EditableStartValue on the target node, then populates the
    /// InlineSuggestions overlay filtered by the same text so the scripted
    /// snapshot reflects what the interactive user would see mid-typing.
    /// </summary>
    [JsonProperty("inlineEdit")] public InlineEditState? InlineEdit { get; set; }

    /// <summary>
    /// Paints a fake mouse cursor at window-relative (x, y) DIPs so GIF
    /// viewers can see where a click/type is happening. Cleared between
    /// scenes unless the next scene sets it again.
    /// </summary>
    [JsonProperty("cursor")] public CursorState? Cursor { get; set; }

    /// <summary>
    /// Marks an autocomplete suggestion row as "mouse over" in the next
    /// snapshot — useful to insert a hover frame before a scene that
    /// accepts the same suggestion. Matched by AutocompleteSuggestion.DisplayName.
    /// </summary>
    [JsonProperty("hoverSuggestion")] public string? HoverSuggestion { get; set; }

    /// <summary>
    /// Paints a click ring at the last-known cursor position. Valid values:
    /// "press"   — small tight ring, for the frame where the click target
    ///             is still visible (dropdown open, suggestion row still
    ///             there). Place BEFORE the action frame.
    /// "release" — large expanded ring, for the action frame itself (accept,
    ///             Set, Apply, revert, scope click).
    /// Omit the field for frames with no click animation.
    /// </summary>
    [JsonProperty("click")] public string? Click { get; set; }

    /// <summary>
    /// If true, paints the fake scope-dropdown overlay below the Scope
    /// ComboBox — same visual as clicking the chevron on the real combo.
    /// Cleared automatically at the start of every non-preserveState scene,
    /// so you need to set it on every consecutive frame the dropdown should
    /// remain visible.
    /// </summary>
    [JsonProperty("openScopeDropdown")] public bool? OpenScopeDropdown { get; set; }

    /// <summary>
    /// Per-scene override for the UI zoom factor. Omit to use the default
    /// (UiZoomService.DefaultZoom, currently 1.2). Applied via the ephemeral
    /// capture-mode UiZoomService so it scales content without resizing the
    /// window (the scene viewport is authoritative). Reset to default at the
    /// start of every scene so per-scene overrides don't leak forward.
    /// </summary>
    [JsonProperty("zoom")] public double? Zoom { get; set; }

    /// <summary>
    /// AncestorPath of the scope row to pre-select in the open scope overlay,
    /// so the row reads as "mouse is about to click this". Requires
    /// <see cref="OpenScopeDropdown"/> = true.
    /// </summary>
    [JsonProperty("hoverScope")] public string? HoverScope { get; set; }

    /// <summary>
    /// Path of a single pending inline edit to revert — same effect as clicking
    /// the per-row undo (↶) button in the Pending Edits panel.
    /// </summary>
    [JsonProperty("revertPending")] public string? RevertPending { get; set; }

    /// <summary>
    /// If true, clears the entire pending-edits queue — same effect as
    /// clicking "Undo all" in the sidebar or "Discard" in the toolbar.
    /// </summary>
    [JsonProperty("discardAllPending")] public bool? DiscardAllPending { get; set; }

    // ===== Active-set primitives (#96) ======================================

    /// <summary>
    /// Active-set mutation block for multi-DB chapter scenes (#96).
    /// Applied before <see cref="Select"/> / <see cref="Scope"/> so that
    /// DB additions/removals rebuild the tree before member lookup runs.
    /// </summary>
    [JsonProperty("activeSet")] public ActiveSetScene? ActiveSet { get; set; }

    // ===== Scene kind =====================================================

    /// <summary>
    /// Scene kind. Default (null or "dialog") renders the BulkChangeDialog.
    /// "chapter" marks an intertitle card; the capture loop skips these and
    /// they are rendered out-of-band by
    /// assets/screenshots/workflow/chapters/render-chapters.sh, which reads
    /// <see cref="ChapterTitle"/> / <see cref="ChapterSubtitle"/> from this
    /// same manifest and writes the PNG to the canonical scene filename.
    /// "external" marks a hand-supplied PNG (e.g. TIA Portal painpoint
    /// screenshot) already present in the workflow folder; the capture loop
    /// skips these so it doesn't overwrite the source PNG with a dialog
    /// snapshot.
    /// </summary>
    [JsonProperty("kind")] public string? Kind { get; set; }

    /// <summary>Main title text for a chapter card (kind=chapter). Consumed by render-chapters.sh.</summary>
    [JsonProperty("chapterTitle")] public string? ChapterTitle { get; set; }

    /// <summary>Subtitle phrases joined by " • " (U+2022). Consumed by render-chapters.sh.</summary>
    [JsonProperty("chapterSubtitle")] public string? ChapterSubtitle { get; set; }
}

public sealed class CursorState
{
    [JsonProperty("x")] public double? X { get; set; }
    [JsonProperty("y")] public double? Y { get; set; }

    /// <summary>
    /// Named target — resolved after layout so coords track scroll / filter
    /// changes without per-scene hardcoding. Formats:
    ///   "search"                      -> search TextBox in the toolbar
    ///   "apply"                       -> Apply button in the bottom bar
    ///   "suggestion:&lt;name&gt;"          -> autocomplete row with that DisplayName
    ///                                   (sidebar popup OR inline overlay)
    ///   "addDbButton"                 -> the "+" add-DB button in the pill toolbar
    ///   "chip:&lt;DbName&gt;"               -> center of the pill trigger that contains
    ///                                   &lt;DbName&gt; (the chip body for solo)
    ///   "chipClose:&lt;DbName&gt;"          -> the × (ClearButton) on the pill that
    ///                                   contains &lt;DbName&gt;
    ///   "stashHeader:&lt;DbName&gt;"        -> the "PENDING IN &lt;DbName&gt;" header button
    ///                                   in the stash section of the inspector
    /// </summary>
    [JsonProperty("target")] public string? Target { get; set; }
}

public sealed class InlineEditState
{
    /// <summary>Path of the leaf member whose cell is being edited.</summary>
    [JsonProperty("path")] public string? Path { get; set; }
    /// <summary>Value typed so far into the cell's TextBox.</summary>
    [JsonProperty("typed")] public string? Typed { get; set; }
    /// <summary>
    /// Optional: accept a specific suggestion (same as clicking the
    /// overlay row). Implies commit.
    /// </summary>
    [JsonProperty("acceptSuggestion")] public string? AcceptSuggestion { get; set; }
}

public sealed class SidebarState
{
    [JsonProperty("collapsed")] public bool? Collapsed { get; set; }
}

public sealed class FilterState
{
    [JsonProperty("setpointsOnly")] public bool? SetpointsOnly { get; set; }
    [JsonProperty("searchText")] public string? SearchText { get; set; }
}

/// <summary>
/// Active-set mutation sub-block for multi-DB chapter scenes (#96).
/// Fields are applied in declaration order: dropdown open → addPeer →
/// closeChip → solo → reactivate → promptAnswer is wired before any
/// of the above that triggers a prompt.
/// </summary>
public sealed class ActiveSetScene
{
    /// <summary>
    /// Set <c>vm.ActiveSet.IsAddDbPopupOpen = true</c> so the dropdown
    /// is visible in the snapshot. Does NOT enumerate or reload the DB
    /// list — <see cref="AddPeer"/> does that. Use this alone for
    /// scenes that just show the open dropdown (mdb02, mdb03, mdb04).
    /// </summary>
    [JsonProperty("openAddDropdown")] public bool? OpenAddDropdown { get; set; }

    /// <summary>
    /// DB name to add to the active set via
    /// <c>vm.ActiveSet.AddActiveDbFromSummary</c>. The DB must exist
    /// in <c>%TEMP%\BlockParam\</c> (discoverable by
    /// <c>EnumerateDevLauncherDbs</c>). Also forces the add-DB dropdown
    /// closed (the real add gesture closes it). Enables mdb05, mdb06,
    /// mdb07, mdb20.
    /// </summary>
    [JsonProperty("addPeer")] public string? AddPeer { get; set; }

    /// <summary>
    /// DB name whose chip × is clicked — invokes
    /// <c>vm.ActiveSet.RequestRemoveActiveDb</c>. If the DB has pending
    /// edits, the <see cref="PromptAnswer"/> answer is consumed by the
    /// 3-way Apply/Stash/Cancel prompt. Enables mdb16, mdb17, mdb18,
    /// mdb19.
    /// </summary>
    [JsonProperty("closeChip")] public string? CloseChip { get; set; }

    /// <summary>
    /// DB name to solo — invokes
    /// <c>vm.ActiveSet.SoloActiveDbByReference</c>. Collapses the active
    /// set to that single DB; any dropped peers with pending edits
    /// trigger the prompt (consumed by <see cref="PromptAnswer"/>).
    /// Enables mdb26, mdb27.
    /// </summary>
    [JsonProperty("solo")] public string? Solo { get; set; }

    /// <summary>
    /// DB name of a stashed DB to reactivate — invokes
    /// <c>vm.ActiveSet.SwitchToStashedDbCommand</c>. When ≥2 DBs are
    /// currently active the Add-or-Replace 3-way prompt fires; answer is
    /// consumed from <see cref="PromptAnswer"/>. Enables mdb21, mdb22,
    /// mdb23, mdb24, mdb25.
    /// </summary>
    [JsonProperty("reactivate")] public string? Reactivate { get; set; }

    /// <summary>
    /// Canned answer for the NEXT prompt raised during this scene's
    /// active-set operation. Accepted values:
    /// <list type="bullet">
    ///   <item><description><c>"apply"</c>  — Apply &amp; switch (stash prompt)</description></item>
    ///   <item><description><c>"stash"</c>  — Stash &amp; switch (stash prompt); enables mdb18, mdb19</description></item>
    ///   <item><description><c>"cancel"</c> — Cancel (any prompt)</description></item>
    ///   <item><description><c>"add"</c>    — Add alongside (reactivate prompt); enables mdb23, mdb24</description></item>
    ///   <item><description><c>"replace"</c>— Replace active set (reactivate prompt); enables mdb25</description></item>
    /// </list>
    /// Omit for gestures that produce no prompt (adding a DB with no
    /// pending edits, soloing when nothing is pending, etc.).
    /// </summary>
    [JsonProperty("promptAnswer")] public string? PromptAnswer { get; set; }

    /// <summary>
    /// When true, renders the 3-way prompt as an in-tree overlay in the
    /// captured frame WITHOUT performing the triggering gesture (#96).
    /// The overlay shows the text and button labels that the real
    /// <see cref="BlockParam.UI.ThreeButtonDialog"/> would display, determined
    /// by which gesture field is set (<see cref="CloseChip"/> →
    /// Apply/Stash/Cancel; <see cref="Reactivate"/> → Add/Replace/Cancel).
    /// The overlay is populated from localization strings so the English
    /// text is correct regardless of OS culture.
    ///
    /// <para>
    /// Use in a scene that captures the prompt visual; follow it with a
    /// <c>preserveState</c> scene that carries the same gesture +
    /// <see cref="PromptAnswer"/> to actually dismiss the prompt and
    /// advance state.
    /// </para>
    /// </summary>
    [JsonProperty("promptCapture")] public bool? PromptCapture { get; set; }
}

public static class CaptureScriptLoader
{
    /// <summary>
    /// Loads the script and returns the script plus the directory the script
    /// file lives in, so relative paths (fixture, output_dir) can be resolved
    /// against it — making scripts portable across checkouts.
    /// </summary>
    public static (CaptureScript Script, string BaseDir) Load(string path)
    {
        var json = File.ReadAllText(path);
        var script = JsonConvert.DeserializeObject<CaptureScript>(json)
            ?? throw new InvalidDataException($"Script parsed as null: {path}");
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return (script, baseDir);
    }

    public static string ResolveRelative(string baseDir, string pathOrName)
        => Path.IsPathRooted(pathOrName) ? pathOrName : Path.GetFullPath(Path.Combine(baseDir, pathOrName));
}

public static class SceneApplier
{
    /// <summary>
    /// Applies a scene's state (filter, expansions) to the live ViewModel.
    /// Expanding a node implicitly expands all its ancestors so the flat
    /// list contains it after the refresh.
    /// </summary>
    public static void Apply(Scene scene, BulkChangeViewModel vm, BulkChangeDialog dialog)
    {
        // Cursor, hover-preview, and prompt overlay are transient per-scene —
        // always clear at the top so stale marks from the previous frame don't
        // leak forward even when preserveState keeps everything else.
        dialog.HideCursor();
        ClearHoverPreview(vm, dialog);
        dialog.HideScopeDropdownScripted();
        dialog.HidePromptOverlayScripted();

        // Reset zoom to the default (or per-scene override) so a previous
        // scene's override doesn't leak into subsequent frames.
        BlockParam.Services.UiZoomService.Shared.SetZoom(
            scene.Zoom ?? BlockParam.Services.UiZoomService.DefaultZoom);

        // Reset any expansion/selection/value leaked from a prior scene, so
        // each scene is fully declarative and order-independent — unless the
        // scene opts into preserveState for a multi-step workflow.
        if (scene.PreserveState != true)
        {
            ResetTreeState(vm);
            dialog.HideInlineOverlayScripted();
            dialog.HideInlineHintOverlayScripted();
        }

        // Active-set mutations (#96) run before member-selection / scope so
        // that tree rebuilds triggered by add/remove land before any path
        // lookups. The ScriptedMessageBoxService is armed with the scene's
        // promptAnswer FIRST so it's in place if AddPeer/CloseChip/Solo/
        // Reactivate immediately raises a 3-way prompt.
        if (scene.ActiveSet != null)
            ApplyActiveSet(scene, vm, dialog);

        if (scene.Filter != null)
        {
            if (scene.Filter.SetpointsOnly.HasValue)
                vm.Filter.ShowSetpointsOnly = scene.Filter.SetpointsOnly.Value;
            if (scene.Filter.SearchText != null)
            {
                vm.Filter.SearchQuery = scene.Filter.SearchText;
                vm.FlushPendingSearch();
            }
        }

        if (scene.Expand != null && scene.Expand.Count > 0)
        {
            foreach (var path in scene.Expand)
            {
                var node = FindByPath(vm.Tree.RootMembers, path);
                if (node == null)
                {
                    Serilog.Log.Warning("Scene {Id}: expand path not found: {Path}", scene.Id, path);
                    continue;
                }
                ExpandAncestors(node);
            }
            vm.RefreshFlatList();
        }

        if (scene.Select != null)
        {
            var node = FindByPath(vm.Tree.RootMembers, scene.Select);
            if (node == null)
            {
                Serilog.Log.Warning("Scene {Id}: select path not found: {Path}", scene.Id, scene.Select);
            }
            else
            {
                ExpandAncestors(node);
                vm.RefreshFlatList();
                // SelectedFlatMember requires the node to be in the flat list;
                // RefreshFlatList above guarantees that.
                var flat = vm.Tree.FlatMembers.FirstOrDefault(m => m.Path == node.Path);
                vm.Selection.SelectedFlatMember = flat ?? node;
            }
        }

        if (scene.Scope != null)
        {
            var scope = vm.Selection.AvailableScopes.FirstOrDefault(s => s.AncestorPath == scene.Scope);
            if (scope == null)
            {
                Serilog.Log.Warning(
                    "Scene {Id}: scope path not found: '{Path}'. Available: {Avail}",
                    scene.Id, scene.Scope,
                    string.Join(", ", vm.Selection.AvailableScopes.Select(s => $"'{s.AncestorPath}'")));
            }
            else
            {
                vm.Selection.SelectedScope = scope;
            }
        }

        if (scene.NewValue != null)
        {
            vm.NewValue = scene.NewValue;
            // FlushPendingHighlighting runs UpdateHighlighting, which calls
            // EnsureVisible on every affected node — so the yellow rows
            // smart-expand into view on their own. It also rebuilds the
            // BulkPreview and PendingEdits collections synchronously.
            vm.FlushPendingHighlighting();
        }

        if (scene.AcceptSuggestion != null)
        {
            // AcceptSuggestion runs its work synchronously and cancels the
            // debounce timer — no FlushPendingHighlighting needed, and calling
            // it here would re-run UpdateFilteredSuggestions and re-open the
            // overlay.
            vm.AcceptSuggestion(scene.AcceptSuggestion);
        }

        if (scene.StageBulk == true)
        {
            if (vm.SetPendingCommand.CanExecute(null))
                vm.SetPendingCommand.Execute(null);
            else
                Serilog.Log.Warning("Scene {Id}: stageBulk requested but SetPendingCommand disabled", scene.Id);
        }

        if (scene.RevertPending != null)
        {
            var entry = vm.Pending.PendingEdits.FirstOrDefault(p => p.Node?.Path == scene.RevertPending);
            if (entry == null)
                Serilog.Log.Warning("Scene {Id}: revertPending path not found in queue: {Path}. Queue: {Paths}",
                    scene.Id, scene.RevertPending,
                    string.Join(", ", vm.Pending.PendingEdits.Select(p => p.Node?.Path ?? "?")));
            else
                vm.UndoPendingEdit(entry);
        }

        if (scene.DiscardAllPending == true)
        {
            if (vm.Pending.HasPendingEdits)
                vm.DiscardPendingSilent();
            else
                Serilog.Log.Warning("Scene {Id}: discardAllPending requested but queue is empty", scene.Id);
        }

        if (scene.Apply == true)
        {
            if (vm.ApplyCommand.CanExecute(null))
                vm.ApplyCommand.Execute(null);
            else
                Serilog.Log.Warning("Scene {Id}: apply requested but ApplyCommand disabled", scene.Id);
        }

        if (scene.Sidebar != null)
        {
            if (scene.Sidebar.Collapsed.HasValue)
                vm.Inspector.IsInspectorCollapsed = scene.Sidebar.Collapsed.Value;
        }

        if (scene.InlineEdit != null)
            ApplyInlineEdit(scene, vm, dialog);

        // Hover preview after InlineEdit so the overlay's suggestions exist.
        if (scene.HoverSuggestion != null)
            ApplyHoverPreview(vm, dialog, scene.HoverSuggestion);

        // Open the fake scope dropdown overlay *after* any scope change in the
        // same scene, so AvailableScopes reflects the (possibly new) selected
        // member and the hover row resolves.
        if (scene.OpenScopeDropdown == true)
            dialog.ShowScopeDropdownForScripted(scene.HoverScope);

        // Cursor last so it paints above everything else in the frame.
        if (scene.Cursor != null)
            ApplyCursor(scene, dialog);

        // Click ring uses the last-known cursor position — apply AFTER cursor
        // so a scene can re-resolve the target AND fire the click in one step.
        if (scene.Click != null)
            dialog.ShowClickRingAtLastCursor(scene.Click);
        else
            dialog.HideClickRing();
    }

    private static void ApplyCursor(Scene scene, BulkChangeDialog dialog)
    {
        var c = scene.Cursor!;
        if (c.Target != null)
        {
            // Force a layout pass so ItemContainerGenerator has produced
            // the suggestion row containers we're about to look up.
            dialog.UpdateLayout();
            var pt = dialog.ResolveCursorTarget(c.Target);
            if (pt == null)
            {
                Serilog.Log.Warning("Scene {Id}: cursor target '{T}' not found", scene.Id, c.Target);
                return;
            }
            dialog.ShowCursorAt(pt.Value.X, pt.Value.Y);
            return;
        }
        if (c.X.HasValue && c.Y.HasValue)
        {
            dialog.ShowCursorAt(c.X.Value, c.Y.Value);
            return;
        }
        Serilog.Log.Warning("Scene {Id}: cursor has neither target nor x/y", scene.Id);
    }

    private static void ApplyHoverPreview(
        BulkChangeViewModel vm, BulkChangeDialog dialog, string displayName)
    {
        bool matched = false;
        foreach (var s in vm.Autocomplete.FilteredSuggestions)
        {
            if (s.DisplayName == displayName) { s.IsHoverPreview = true; matched = true; }
        }
        if (dialog.InlineOverlayList.ItemsSource is System.Collections.IEnumerable items)
        {
            foreach (var o in items)
            {
                if (o is AutocompleteSuggestion s && s.DisplayName == displayName)
                {
                    s.IsHoverPreview = true;
                    matched = true;
                }
            }
        }
        if (!matched)
            Serilog.Log.Warning("Scene: hoverSuggestion '{Name}' not found in either suggestion list", displayName);
    }

    private static void ClearHoverPreview(BulkChangeViewModel vm, BulkChangeDialog dialog)
    {
        foreach (var s in vm.Autocomplete.FilteredSuggestions) s.IsHoverPreview = false;
        if (dialog.InlineOverlayList.ItemsSource is System.Collections.IEnumerable items)
            foreach (var o in items)
                if (o is AutocompleteSuggestion s) s.IsHoverPreview = false;
    }

    private static void ApplyInlineEdit(Scene scene, BulkChangeViewModel vm, BulkChangeDialog dialog)
    {
        var ie = scene.InlineEdit!;
        if (ie.Path == null)
        {
            Serilog.Log.Warning("Scene {Id}: inlineEdit missing 'path'", scene.Id);
            return;
        }

        var node = FindByPath(vm.Tree.RootMembers, ie.Path);
        if (node == null)
        {
            Serilog.Log.Warning("Scene {Id}: inlineEdit path not found: {Path}", scene.Id, ie.Path);
            return;
        }

        // Make sure the cell is in the flat list so the overlay anchors correctly.
        ExpandAncestors(node);
        vm.RefreshFlatList();

        // Accept-suggestion short-circuits typing: apply the chosen value and
        // close the overlay (same as clicking an entry in the interactive UI).
        if (ie.AcceptSuggestion != null)
        {
            node.EditableStartValue = ie.AcceptSuggestion;
            dialog.HideInlineOverlayScripted();
            return;
        }

        if (ie.Typed != null)
        {
            node.EditableStartValue = ie.Typed;
            dialog.ShowInlineOverlayForScripted(node, ie.Typed);
        }
    }

    private static void ResetTreeState(BulkChangeViewModel vm)
    {
        // Clear any pending edits staged by a prior scene so a clean run
        // doesn't inherit them. DiscardPendingSilent skips the confirm dialog.
        if (vm.Pending.HasPendingEdits)
            vm.DiscardPendingSilent();
        vm.Inspector.IsInspectorCollapsed = false;
        foreach (var root in vm.Tree.RootMembers)
            CollapseRecursive(root);
        vm.Selection.SelectedFlatMember = null;
        vm.Selection.SelectedScope = null;
        vm.Filter.SearchQuery = "";
        // Reset NewValue without tripping the user-touched flag so the next
        // scene's member selection can still drive the normal prefill path.
        vm.ResetNewValueSilent();
        vm.FlushPendingHighlighting();
        vm.FlushPendingSearch();
        vm.RefreshFlatList();
        // Close the add-DB dropdown so it doesn't leak into the next scene.
        vm.ActiveSet.IsAddDbPopupOpen = false;
        vm.ActiveSet.IsDataBlocksDropdownOpen = false;
    }

    /// <summary>
    /// Applies the <see cref="Scene.ActiveSet"/> block to the live VM.
    /// Order: arm prompt answer → openAddDropdown → addPeer → closeChip →
    /// solo → reactivate. The prompt answer is armed first so the
    /// ScriptedMessageBoxService already has it when any of the above
    /// immediately raises a 3-way dialog.
    ///
    /// <para>
    /// When <see cref="ActiveSetScene.PromptCapture"/> is true, the triggering
    /// gesture (<see cref="ActiveSetScene.CloseChip"/> or
    /// <see cref="ActiveSetScene.Reactivate"/>) is NOT executed. Instead the
    /// prompt overlay is shown in the captured frame with the English text and
    /// button labels the real dialog would display (#96). A follow-on scene
    /// (with <c>preserveState</c>) carries the same gesture + promptAnswer to
    /// actually dismiss the prompt and advance state.
    /// </para>
    /// </summary>
    private static void ApplyActiveSet(Scene scene, BulkChangeViewModel vm, BulkChangeDialog dialog)
    {
        var block = scene.ActiveSet!;

        // When promptCapture=true, show the prompt overlay for the current
        // frame WITHOUT executing the triggering gesture. The follow-on scene
        // executes the gesture and consumes the scripted answer.
        if (block.PromptCapture == true)
        {
            ShowPromptOverlayForScene(block, vm, dialog);
            return;
        }

        // Arm the scripted prompt answer before any gesture that may raise one.
        if (vm.MessageBoxService is ScriptedMessageBoxService scripted
            && block.PromptAnswer != null)
        {
            scripted.PromptAnswer = block.PromptAnswer;
        }

        // Open the add-DB dropdown overlay (visual only — does not add a DB).
        if (block.OpenAddDropdown == true)
        {
            // Force the available list to be loaded so the overlay populates.
            if (vm.ActiveSet.OpenDataBlocksDropdownCommand.CanExecute(null))
                vm.ActiveSet.OpenDataBlocksDropdownCommand.Execute(null);
            else
                vm.ActiveSet.IsAddDbPopupOpen = true;
        }

        // Add a peer DB by name.
        if (block.AddPeer != null)
        {
            // Look up the summary from the DB enumeration so PlcName is set correctly.
            var available = vm.ActiveSet.State.Dbs;
            // Force the available list to load if not yet done.
            if (vm.ActiveSet.OpenDataBlocksDropdownCommand.CanExecute(null))
                vm.ActiveSet.OpenDataBlocksDropdownCommand.Execute(null);

            var summary = vm.ActiveSet.FilteredDataBlocks
                .FirstOrDefault(s => string.Equals(s.Name, block.AddPeer,
                    System.StringComparison.OrdinalIgnoreCase));

            if (summary == null)
            {
                Serilog.Log.Warning(
                    "Scene {Id}: activeSet.addPeer '{Name}' not found in FilteredDataBlocks; " +
                    "is the fixture in %TEMP%\\BlockParam\\?",
                    scene.Id, block.AddPeer);
            }
            else
            {
                vm.ActiveSet.AddActiveDbFromSummary(summary);
                Serilog.Log.Information(
                    "Scene {Id}: activeSet.addPeer added {Name}", scene.Id, summary.Name);
            }
            // Close the dropdown — the real gesture closes it after adding.
            vm.ActiveSet.IsAddDbPopupOpen = false;
            vm.ActiveSet.IsDataBlocksDropdownOpen = false;
        }

        // Close a chip by DB name (triggers Apply/Stash/Cancel prompt if pending).
        if (block.CloseChip != null)
        {
            var db = vm.ActiveSet.State.Dbs
                .FirstOrDefault(d => string.Equals(d.Info.Name, block.CloseChip,
                    System.StringComparison.OrdinalIgnoreCase));
            if (db == null)
            {
                Serilog.Log.Warning(
                    "Scene {Id}: activeSet.closeChip '{Name}' not found in active set (active: {Active})",
                    scene.Id, block.CloseChip,
                    string.Join(", ", vm.ActiveSet.State.Dbs.Select(d => d.Info.Name)));
            }
            else
            {
                vm.ActiveSet.RequestRemoveActiveDb(db);
                Serilog.Log.Information(
                    "Scene {Id}: activeSet.closeChip requested remove of {Name}", scene.Id, db.Info.Name);
            }
        }

        // Solo a DB by name (collapses active set to just that DB).
        if (block.Solo != null)
        {
            var db = vm.ActiveSet.State.Dbs
                .FirstOrDefault(d => string.Equals(d.Info.Name, block.Solo,
                    System.StringComparison.OrdinalIgnoreCase));
            if (db == null)
            {
                Serilog.Log.Warning(
                    "Scene {Id}: activeSet.solo '{Name}' not found in active set",
                    scene.Id, block.Solo);
            }
            else
            {
                vm.ActiveSet.SoloActiveDbByReference(db);
                Serilog.Log.Information(
                    "Scene {Id}: activeSet.solo → {Name}", scene.Id, db.Info.Name);
            }
        }

        // Reactivate a stashed DB by name.
        if (block.Reactivate != null)
        {
            var stash = vm.ActiveSet.StashedDbs
                .FirstOrDefault(s => string.Equals(s.DbName, block.Reactivate,
                    System.StringComparison.OrdinalIgnoreCase));
            if (stash == null)
            {
                Serilog.Log.Warning(
                    "Scene {Id}: activeSet.reactivate '{Name}' not found in stashed DBs (stashed: {Stashed})",
                    scene.Id, block.Reactivate,
                    string.Join(", ", vm.ActiveSet.StashedDbs.Select(s => s.DbName)));
            }
            else
            {
                vm.ActiveSet.SwitchToStashedDbCommand.Execute(stash);
                Serilog.Log.Information(
                    "Scene {Id}: activeSet.reactivate → {Name}", scene.Id, stash.DbName);
            }
        }
    }

    /// <summary>
    /// Populates and shows the <c>PromptOverlay</c> inside the dialog for
    /// capture-mode frames that need the 3-way prompt visible (#96).
    /// Determines prompt kind from the active-set block fields:
    ///   • <see cref="ActiveSetScene.CloseChip"/> → Apply/Stash/Cancel prompt
    ///   • <see cref="ActiveSetScene.Reactivate"/> → Add/Replace/Cancel prompt
    /// Text is built from the same localization strings the real
    /// <see cref="BlockParam.UI.WpfMessageBoxService"/> uses, so English text
    /// is correct when running with en-US culture (as capture mode forces).
    /// </summary>
    private static void ShowPromptOverlayForScene(
        ActiveSetScene block, BulkChangeViewModel vm, BulkChangeDialog dialog)
    {
        // --- Apply/Stash/Cancel: triggered by closeChip ---
        if (block.CloseChip != null)
        {
            var db = vm.ActiveSet.State.Dbs
                .FirstOrDefault(d => string.Equals(d.Info.Name, block.CloseChip,
                    System.StringComparison.OrdinalIgnoreCase));
            int pendingCount = db != null ? vm.Pending.PendingEdits.Count : 1;
            string dbName = block.CloseChip;
            var message = BlockParam.Localization.Res.Format(
                "Dialog_SwitchDb_KeepConfirm_Text", pendingCount, dbName);
            dialog.ShowPromptOverlayScripted(
                message,
                BlockParam.Localization.Res.Get("Dialog_SwitchDb_KeepConfirm_ApplyButton"),
                BlockParam.Localization.Res.Get("Dialog_SwitchDb_KeepConfirm_StashButton"),
                BlockParam.Localization.Res.Get("Dialog_Cancel"));
            return;
        }

        // --- Add/Replace/Cancel: triggered by reactivate ---
        if (block.Reactivate != null)
        {
            var message = BlockParam.Localization.Res.Format(
                "Reactivate_AdditiveOrReplace_Text", block.Reactivate);
            dialog.ShowPromptOverlayScripted(
                message,
                BlockParam.Localization.Res.Get("Reactivate_AdditiveOrReplace_AddButton"),
                BlockParam.Localization.Res.Get("Reactivate_AdditiveOrReplace_ReplaceButton"),
                BlockParam.Localization.Res.Get("Dialog_Cancel"));
            return;
        }

        Serilog.Log.Warning(
            "[promptCapture] No closeChip or reactivate gesture specified — cannot determine prompt kind");
    }

    private static void CollapseRecursive(MemberNodeViewModel node)
    {
        node.IsExpanded = false;
        foreach (var c in node.Children) CollapseRecursive(c);
    }



    private static void ExpandAncestors(MemberNodeViewModel node)
    {
        for (var cur = node; cur != null; cur = cur.Parent)
            cur.IsExpanded = true;
    }

    private static MemberNodeViewModel? FindByPath(
        IEnumerable<MemberNodeViewModel> nodes, string path)
    {
        foreach (var n in nodes)
        {
            if (n.Path == path) return n;
            var hit = FindByPath(n.Children, path);
            if (hit != null) return hit;
        }
        return null;
    }
}
