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
}
