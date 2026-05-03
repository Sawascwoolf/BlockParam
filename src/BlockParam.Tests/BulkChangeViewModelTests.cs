using BlockParam.Config;
using BlockParam.Licensing;
using BlockParam.Services;
using BlockParam.SimaticML;
using BlockParam.UI;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BlockParam.Tests;

public class BulkChangeViewModelTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static BulkChangeViewModel CreateViewModel()
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var parser = new SimaticMLParser();
        var db = parser.Parse(xml);
        var analyzer = new HierarchyAnalyzer();
        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var usageTracker = Substitute.For<IUsageTracker>();
        usageTracker.GetStatus().Returns(new UsageStatus(0, 3));
        usageTracker.GetInlineStatus().Returns(new UsageStatus(0, 10));

        return new BulkChangeViewModel(db, xml, analyzer, bulkService, usageTracker, configLoader);
    }

    private ConfigLoader CreateConfigLoaderWithRule(string ruleJson)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test_vm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        _tempDirs.Add(tempDir);

        var configPath = Path.Combine(tempDir, "config.json");
        File.WriteAllText(configPath, @"{ ""version"": ""1.0"" }");

        var rulesDir = Path.Combine(tempDir, "rules");
        Directory.CreateDirectory(rulesDir);
        File.WriteAllText(Path.Combine(rulesDir, "test-rules.json"), ruleJson);

        return new ConfigLoader(configPath);
    }

    private BulkChangeViewModel CreateViewModelWithRule(string fixtureName, string ruleJson)
    {
        var xml = TestFixtures.LoadXml(fixtureName);
        var parser = new SimaticMLParser();
        var db = parser.Parse(xml);
        var analyzer = new HierarchyAnalyzer();
        var configLoader = CreateConfigLoaderWithRule(ruleJson);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var usageTracker = Substitute.For<IUsageTracker>();
        usageTracker.GetStatus().Returns(new UsageStatus(0, 3));
        usageTracker.GetInlineStatus().Returns(new UsageStatus(0, 10));
        usageTracker.RecordInlineEdit().Returns(true);
        usageTracker.RecordUsage().Returns(true);

        return new BulkChangeViewModel(db, xml, analyzer, bulkService, usageTracker, configLoader);
    }

    /// <summary>
    /// Regression: typing into NewValue schedules a 150ms debounce that runs
    /// UpdateFilteredSuggestions. If AcceptSuggestion doesn't cancel that
    /// trailing timer, the timer fires after FilteredSuggestions is cleared
    /// and repopulates it — re-opening the autocomplete overlay right after
    /// a suggestion was accepted. The fix: AcceptSuggestion disposes the
    /// timer so only its own synchronous work decides the final state.
    /// </summary>
    [Fact]
    public void AcceptSuggestion_cancels_pending_NewValue_debounce()
    {
        var vm = CreateViewModel();

        vm.NewValue = "But"; // setter schedules the debounce timer
        vm.HasPendingValueDebounce.Should().BeTrue(
            "the setter should have scheduled a debounce timer");

        vm.AcceptSuggestion("Butterfly");

        vm.HasPendingValueDebounce.Should().BeFalse(
            "AcceptSuggestion must cancel the trailing timer so it can't re-populate FilteredSuggestions");
    }

    /// <summary>
    /// #26: Pre-existing values that violate a configured rule should be surfaced
    /// in <see cref="BulkChangeViewModel.ExistingIssues"/> on dialog load —
    /// without the user needing to edit them first.
    /// </summary>
    [Fact]
    public void ExistingIssues_PopulatedOnLoad_ForOutOfRangeValues()
    {
        // flat-db.xml has Speed=1500 — fail a rule that caps Speed at 1000.
        var rule = @"{ ""rules"": [{ ""pathPattern"": ""Speed"", ""datatype"": ""Int"",
            ""constraints"": { ""min"": 0, ""max"": 1000 } }] }";
        var vm = CreateViewModelWithRule("flat-db.xml", rule);

        vm.HasExistingIssues.Should().BeTrue("Speed=1500 violates max=1000");
        vm.ExistingIssuesCount.Should().Be(1);
        vm.ExistingIssues.Single().Node.Name.Should().Be("Speed");
        vm.ExistingIssues.Single().CurrentValue.Should().Be("1500");
        vm.ExistingIssues.Single().Node.HasExistingViolation.Should().BeTrue();
    }

    /// <summary>
    /// #26: When no rules apply (or no values violate them), <see cref="BulkChangeViewModel.ExistingIssues"/>
    /// must stay empty. Otherwise the inspector would surface noise.
    /// </summary>
    [Fact]
    public void ExistingIssues_Empty_WhenNoRules()
    {
        var vm = CreateViewModel(); // no rules configured
        vm.HasExistingIssues.Should().BeFalse();
        vm.ExistingIssuesCount.Should().Be(0);
    }

    /// <summary>
    /// #26: Pre-existing rule violations are findings, not blockers — they must
    /// NOT prevent Apply. Only invalid PENDING edits (HasInlineError) block Apply.
    /// </summary>
    [Fact]
    public void ExistingIssues_DoNotBlockApply()
    {
        var rule = @"{ ""rules"": [{ ""pathPattern"": ""Speed"", ""datatype"": ""Int"",
            ""constraints"": { ""min"": 0, ""max"": 1000 } }] }";
        var vm = CreateViewModelWithRule("flat-db.xml", rule);

        // Pre-existing violation present
        vm.HasExistingIssues.Should().BeTrue();
        // No pending edits, no inline errors
        vm.HasInlineErrors.Should().BeFalse(
            "existing violations must not flip HasInlineError — that's the Apply blocker");

        // Stage a valid pending edit on a *different* member so Apply has work
        var enable = vm.RootMembers.Single(m => m.Name == "Enable");
        enable.EditableStartValue = "false";

        vm.ApplyCommand.CanExecute(null).Should().BeTrue(
            "a pre-existing violation on Speed must not block applying a valid pending edit on Enable");
    }

    /// <summary>
    /// #65: SetButtonText must reflect the count of members that will actually
    /// be staged (effective value differs from NewValue), not the raw scope
    /// size. Previously the button advertised "Set 6 in 'X'" even when 5 of
    /// those 6 already held the target value — only 1 ended up pending after
    /// click, confusing the user and skewing per-change quota math.
    /// </summary>
    [Fact]
    public void SetButtonText_counts_only_members_that_will_actually_change()
    {
        var vm = CreateViewModelWithRule("udt-instances-db.xml", @"{ ""rules"": [] }");

        FlatTreeManager.ExpandAll(vm.RootMembers);
        vm.RefreshFlatList();

        // Fixture has 4 ModuleId leaves, all already at "42".
        var moduleId = vm.FlatMembers.First(m => m.Name == "ModuleId" && m.IsLeaf);
        vm.SelectedFlatMember = moduleId;

        var dbScope = vm.AvailableScopes.First(s => s.MatchCount == 4);
        vm.SelectedScope = dbScope;

        vm.NewValue = "42"; // matches every selected member — 0 would change
        vm.SetButtonText.Should().Be("Set 0 in 'UdtInstancesDB'",
            "all 4 already hold '42' so the button must advertise 0, not 4");
        vm.SetPendingCommand.CanExecute(null).Should().BeFalse(
            "enable state and label come from the same predicate");

        vm.NewValue = "99"; // different — all 4 would change
        vm.SetButtonText.Should().Be("Set 4 in 'UdtInstancesDB'");
        vm.SetPendingCommand.CanExecute(null).Should().BeTrue();
    }

    /// <summary>
    /// #65: Manual-select label (Ctrl+Click multi-select) is also bound by the
    /// same "would actually change" predicate. Selecting 4 members where 3
    /// already match the typed value must show "Set 1 selected", not 4.
    /// </summary>
    [Fact]
    public void SetButtonText_manual_mode_excludes_already_matching_members()
    {
        var vm = CreateViewModelWithRule("udt-instances-db.xml", @"{ ""rules"": [] }");

        FlatTreeManager.ExpandAll(vm.RootMembers);
        vm.RefreshFlatList();

        // Pick 3 ModuleId leaves (all "42") + 1 MessageId leaf (e.g. "101").
        var moduleIds = vm.FlatMembers.Where(m => m.Name == "ModuleId" && m.IsLeaf).Take(3).ToList();
        var messageId = vm.FlatMembers.First(m => m.Name == "MessageId" && m.IsLeaf);
        var picks = moduleIds.Concat(new[] { messageId }).ToList();

        vm.UpdateManualSelection(picks, Array.Empty<MemberNodeViewModel>(), false);
        vm.IsManualMode.Should().BeTrue();

        // Type "42": three ModuleIds already match, the MessageId ("101") doesn't.
        // Only 1 member would actually change.
        vm.NewValue = "42";
        vm.SetButtonText.Should().Be(BlockParam.Localization.Res.Format("Dialog_SetManualCount", 1),
            "3 of 4 selected members already hold '42' so only 1 would actually change");
        vm.SetPendingCommand.CanExecute(null).Should().BeTrue();
    }
}
