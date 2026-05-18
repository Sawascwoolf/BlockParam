using BlockParam.Config;
using BlockParam.Licensing;
using BlockParam.Services;
using BlockParam.SimaticML;
using BlockParam.UI;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Command-level coverage for <see cref="BulkChangeViewModel"/> commands that
/// previously had no command-level tests: ApplyAndCloseCommand,
/// DiscardPendingCommand (the #21 mis-click regression), UpdateCommentsCommand
/// + its CanExecute guard, and the ClearManualSelectionCommand CanExecute guard.
/// Mirrors the harness pattern of <see cref="BulkChangeViewModelTests"/> but
/// keeps its own temp-dir cleanup so it shares no state with that class.
/// </summary>
public class BulkChangeViewModelCommandTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Builds a ConfigLoader backed by a real temp config + a single rules
    /// file containing <paramref name="ruleJson"/>. Copied from the sibling
    /// test class so this class owns its own cleanup list.
    /// </summary>
    private ConfigLoader CreateConfigLoaderWithRule(string ruleJson)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test_cmd_vm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        _tempDirs.Add(tempDir);

        var configPath = Path.Combine(tempDir, "config.json");
        File.WriteAllText(configPath, @"{ ""version"": ""1.0"" }");

        var rulesDir = Path.Combine(tempDir, "rules");
        Directory.CreateDirectory(rulesDir);
        File.WriteAllText(Path.Combine(rulesDir, "test-rules.json"), ruleJson);

        return new ConfigLoader(configPath);
    }

    private static IUsageTracker UsageTracker(int used = 0, int limit = 200)
    {
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(used, limit));
        tracker.RecordUsage(Arg.Any<int>()).Returns(true);
        return tracker;
    }

    private BulkChangeViewModel CreateViewModel(
        string fixtureName = "flat-db.xml",
        string? ruleJson = null,
        IUsageTracker? usageTracker = null,
        IMessageBoxService? messageBox = null)
    {
        var xml = TestFixtures.LoadXml(fixtureName);
        var db = new SimaticMLParser().Parse(xml);
        var analyzer = new HierarchyAnalyzer();
        // Always back the VM with an isolated temp config (empty ruleset when
        // no rule is supplied). `new ConfigLoader(null)` would pick up the
        // developer's ambient %APPDATA%\BlockParam\config.json, making these
        // tests non-hermetic — see UpdateCommentsCommand_CanExecute_False_
        // WhenNoCommentConfig for why that bites.
        var configLoader = CreateConfigLoaderWithRule(ruleJson ?? @"{ ""rules"": [] }");
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);

        return new BulkChangeViewModel(
            db, xml, analyzer, bulkService,
            usageTracker ?? UsageTracker(),
            configLoader,
            messageBox: messageBox);
    }

    // ─────────────────────────────────────────────────────────────────────
    // ApplyAndCloseCommand
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ExecuteApplyAndClose runs ExecuteApply and only fires RequestClose when
    /// the apply actually succeeded. With a valid staged pending change and
    /// ample quota, Apply succeeds → the dialog must close.
    /// </summary>
    [Fact]
    public void ApplyAndCloseCommand_RaisesRequestClose_WhenApplySucceeds()
    {
        var vm = CreateViewModel();
        vm.Tree.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";
        vm.Pending.PendingInlineEditCount.Should().Be(1, "one valid edit is staged");

        var closed = false;
        vm.RequestClose += () => closed = true;

        vm.ApplyAndCloseCommand.Execute(null);

        closed.Should().BeTrue(
            "a successful apply must fire RequestClose so the dialog closes");
    }

    /// <summary>
    /// Regression guard: when the pending batch exceeds the remaining daily
    /// quota, ExecuteApply early-returns before committing and never sets
    /// _lastApplySucceeded — so RequestClose must NOT fire and the dialog
    /// stays open for the user to drop edits or upgrade.
    /// </summary>
    [Fact]
    public void ApplyAndCloseCommand_DoesNotClose_WhenOverDailyQuota()
    {
        // 200/200 used → RemainingToday == 0, so even one pending edit is over-cap.
        var vm = CreateViewModel(usageTracker: UsageTracker(used: 200, limit: 200));
        vm.Tree.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";
        vm.Pending.PendingInlineEditCount.Should().Be(1);

        var closed = false;
        vm.RequestClose += () => closed = true;

        vm.ApplyAndCloseCommand.Execute(null);

        closed.Should().BeFalse(
            "an over-quota apply must not close the dialog — the edit is not committed");
    }

    /// <summary>
    /// #159 H1 + H3: a successful single-DB Apply must leave the tree showing
    /// the committed values with no pending edits — exactly as the old
    /// per-edit-loop + full RefreshTree re-parse did. This pins the
    /// observable contract of the batched write (H1) plus the in-place
    /// post-Apply patch that replaces the full re-parse (H3): the patched
    /// StartValue is read through to the VM and the staged edits are cleared.
    /// </summary>
    [Fact]
    public void ApplyCommand_SingleDb_PatchesTreeInPlace_AndClearsPending()
    {
        var vm = CreateViewModel();
        var speed = vm.Tree.RootMembers.Single(m => m.Name == "Speed");
        var enable = vm.Tree.RootMembers.Single(m => m.Name == "Enable");
        speed.EditableStartValue = "4242";
        enable.EditableStartValue = "true";
        vm.Pending.PendingInlineEditCount.Should().Be(2, "two valid edits are staged");

        vm.ApplyCommand.Execute(null);

        vm.Pending.PendingInlineEditCount.Should().Be(0,
            "a committed Apply clears every staged edit (H3 in-place patch)");
        vm.Tree.RootMembers.Single(m => m.Name == "Speed").StartValue
            .Should().Be("4242",
                "the committed value must read through without a full re-parse (H3)");
        vm.Tree.RootMembers.Single(m => m.Name == "Enable").StartValue
            .Should().Be("true");
    }

    /// <summary>
    /// #159 H1 + H3: clearing a value (empty pending edit) through Apply must
    /// land as a null/empty StartValue, matching what the re-parse path
    /// produced (the writer removes the &lt;StartValue&gt; element; the patch
    /// maps the empty change to a null model value).
    /// </summary>
    [Fact]
    public void ApplyCommand_SingleDb_ClearedValue_PatchesToEmpty()
    {
        var vm = CreateViewModel();
        var speed = vm.Tree.RootMembers.Single(m => m.Name == "Speed");
        speed.StartValue.Should().NotBeNullOrEmpty("flat-db Speed has an explicit value");
        speed.EditableStartValue = "";
        vm.Pending.PendingInlineEditCount.Should().Be(1, "one clear is staged");

        vm.ApplyCommand.Execute(null);

        vm.Pending.PendingInlineEditCount.Should().Be(0);
        vm.Tree.RootMembers.Single(m => m.Name == "Speed").HasStartValue
            .Should().BeFalse("a cleared value reverts to the type default (no StartValue)");
    }

    // ─────────────────────────────────────────────────────────────────────
    // DiscardPendingCommand  (#21 mis-click regression)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// #21: With pending edits staged, confirming the discard prompt
    /// (AskYesNo → true) clears every staged edit.
    /// </summary>
    [Fact]
    public void DiscardPendingCommand_ConfirmYes_ClearsPendingEdits()
    {
        var mb = Substitute.For<IMessageBoxService>();
        mb.AskYesNo(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var vm = CreateViewModel(messageBox: mb);

        vm.Tree.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";
        vm.Tree.RootMembers.Single(m => m.Name == "Speed").EditableStartValue = "42";
        vm.Pending.PendingInlineEditCount.Should().Be(2, "two edits staged");

        vm.DiscardPendingCommand.Execute(null);

        vm.Pending.PendingInlineEditCount.Should().Be(0,
            "confirming the discard prompt must clear all staged edits");
    }

    /// <summary>
    /// #21 KEY REGRESSION: a mis-click used to silently wipe staged edits.
    /// When the user declines the confirm prompt (AskYesNo → false), every
    /// staged edit MUST be preserved untouched.
    /// </summary>
    [Fact]
    public void DiscardPendingCommand_ConfirmNo_PreservesPendingEdits()
    {
        var mb = Substitute.For<IMessageBoxService>();
        mb.AskYesNo(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        var vm = CreateViewModel(messageBox: mb);

        vm.Tree.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";
        vm.Tree.RootMembers.Single(m => m.Name == "Speed").EditableStartValue = "42";
        vm.Pending.PendingInlineEditCount.Should().Be(2);

        vm.DiscardPendingCommand.Execute(null);

        vm.Pending.PendingInlineEditCount.Should().Be(2,
            "#21: declining the confirm dialog must NOT wipe staged edits");
    }

    /// <summary>
    /// Tier B guard: DiscardPendingCommand is only enabled when there is at
    /// least one pending inline edit to discard.
    /// </summary>
    [Fact]
    public void DiscardPendingCommand_CanExecute_TracksPendingCount()
    {
        var vm = CreateViewModel();

        vm.DiscardPendingCommand.CanExecute(null).Should().BeFalse(
            "nothing staged → nothing to discard");

        vm.Tree.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";

        vm.DiscardPendingCommand.CanExecute(null).Should().BeTrue(
            "one staged edit → discard becomes available");
    }

    // ─────────────────────────────────────────────────────────────────────
    // UpdateCommentsCommand + CanExecuteUpdateComments
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// CanExecuteUpdateComments == (Selection.HasScope &amp;&amp; HasCommentConfig).
    /// False with no scope / no comment config; true only once a scope is
    /// selected AND a rule with a non-empty CommentTemplate is loaded.
    /// </summary>
    [Fact]
    public void UpdateCommentsCommand_CanExecute_RequiresScopeAndCommentConfig()
    {
        // udt-instances-db.xml: ModuleId leaves live under Msg_CommError UDT
        // instances; GenerateForScope walks up to the parent, so the rule
        // targets the parent path "Msg_CommError" (mirrors the sibling suite).
        var rule = @"{ ""rules"": [{ ""pathPattern"": ""Msg_CommError"",
            ""commentTemplate"": ""{db}"" }] }";
        var vm = CreateViewModel("udt-instances-db.xml", ruleJson: rule);

        vm.HasCommentConfig.Should().BeTrue("the loaded rule has a commentTemplate");
        vm.UpdateCommentsCommand.CanExecute(null).Should().BeFalse(
            "no scope selected yet → command must stay disabled");

        FlatTreeManager.ExpandAll(vm.Tree.RootMembers);
        vm.RefreshFlatList();
        var leaf = vm.Tree.FlatMembers.First(m => m.Name == "ModuleId" && m.IsLeaf);
        vm.Selection.SelectedFlatMember = leaf;
        vm.Selection.SelectedScope =
            vm.Selection.AvailableScopes.OrderByDescending(s => s.MatchCount).First();

        vm.UpdateCommentsCommand.CanExecute(null).Should().BeTrue(
            "scope selected AND a commentTemplate rule loaded → command enabled");
    }

    /// <summary>
    /// CanExecuteUpdateComments must stay false when a scope is selected but
    /// the config has no commentTemplate rule (the HasCommentConfig half of
    /// the guard).
    /// </summary>
    [Fact]
    public void UpdateCommentsCommand_CanExecute_False_WhenNoCommentConfig()
    {
        // Explicit empty ruleset via an isolated temp config — `new ConfigLoader(null)`
        // would pick up the developer's ambient %APPDATA% config, which may carry a
        // commentTemplate rule and make HasCommentConfig spuriously true.
        var vm = CreateViewModel("udt-instances-db.xml", ruleJson: @"{ ""rules"": [] }");

        FlatTreeManager.ExpandAll(vm.Tree.RootMembers);
        vm.RefreshFlatList();
        var leaf = vm.Tree.FlatMembers.First(m => m.Name == "ModuleId" && m.IsLeaf);
        vm.Selection.SelectedFlatMember = leaf;
        vm.Selection.SelectedScope =
            vm.Selection.AvailableScopes.OrderByDescending(s => s.MatchCount).First();

        vm.Selection.HasScope.Should().BeTrue("a scope is selected");
        vm.HasCommentConfig.Should().BeFalse("no commentTemplate rule is configured");
        vm.UpdateCommentsCommand.CanExecute(null).Should().BeFalse(
            "scope alone is not enough — the comment-config half of the guard fails");
    }

    /// <summary>
    /// Executing UpdateCommentsCommand with a valid commentTemplate rule and a
    /// selected scope stages comment changes: HasPendingChanges flips true and
    /// StatusText moves off its initial value. Asserts state/flags, not the
    /// localized string (which comes from Res.Format).
    /// </summary>
    [Fact]
    public void UpdateCommentsCommand_Execute_SetsPendingAndUpdatesStatus()
    {
        var rule = @"{ ""rules"": [{ ""pathPattern"": ""Msg_CommError"",
            ""commentTemplate"": ""{db}"" }] }";
        var vm = CreateViewModel("udt-instances-db.xml", ruleJson: rule,
            messageBox: Substitute.For<IMessageBoxService>());

        FlatTreeManager.ExpandAll(vm.Tree.RootMembers);
        vm.RefreshFlatList();
        var leaf = vm.Tree.FlatMembers.First(m => m.Name == "ModuleId" && m.IsLeaf);
        vm.Selection.SelectedFlatMember = leaf;
        vm.Selection.SelectedScope =
            vm.Selection.AvailableScopes.OrderByDescending(s => s.MatchCount).First();

        var initialStatus = vm.StatusText;
        vm.UpdateCommentsCommand.CanExecute(null).Should().BeTrue();

        vm.UpdateCommentsCommand.Execute(null);

        vm.HasPendingChanges.Should().BeTrue(
            "writing comment previews into the DB must mark the dialog dirty");
        vm.StatusText.Should().NotBe(initialStatus,
            "the command must report progress via StatusText (Comments_Updated)");
    }

    // ─────────────────────────────────────────────────────────────────────
    // ClearManualSelectionCommand  (Tier B guard only)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tier B guard: ClearManualSelectionCommand.CanExecute ==
    /// (Selection.ManualSelectionCount &gt; 0). False with nothing manually
    /// selected; true once a Ctrl+Click multi-selection exists.
    /// </summary>
    [Fact]
    public void ClearManualSelectionCommand_CanExecute_TracksManualSelectionCount()
    {
        var vm = CreateViewModel("udt-instances-db.xml");

        vm.ClearManualSelectionCommand.CanExecute(null).Should().BeFalse(
            "no manual selection yet");

        FlatTreeManager.ExpandAll(vm.Tree.RootMembers);
        vm.RefreshFlatList();
        var leaves = vm.Tree.FlatMembers.Where(m => m.IsLeaf).Take(2).ToList();
        leaves.Should().HaveCount(2, "fixture must expose at least 2 leaves");

        vm.UpdateManualSelection(
            added: leaves,
            removed: Array.Empty<MemberNodeViewModel>(),
            isFilterRehydration: false);

        vm.Selection.ManualSelectionCount.Should().Be(2);
        vm.ClearManualSelectionCommand.CanExecute(null).Should().BeTrue(
            "a manual multi-selection exists → clearing it becomes available");
    }
}
