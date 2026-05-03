using System.ComponentModel;
using System.Globalization;
using System.Threading;
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
        usageTracker.GetStatus().Returns(new UsageStatus(0, 200));
        usageTracker.RecordUsage(Arg.Any<int>()).Returns(true);

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
        usageTracker.GetStatus().Returns(new UsageStatus(0, 200));
        usageTracker.RecordUsage(Arg.Any<int>()).Returns(true);

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

    /// <summary>
    /// #62: Free-tier cap is per-change, not per-Apply. When the user stages
    /// more pending edits than they have quota left, Apply must be disabled
    /// (no partial apply, no silent over-cap write).
    /// </summary>
    [Fact]
    public void ApplyCommand_Disabled_WhenPendingExceedsRemainingQuota()
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var parser = new SimaticMLParser();
        var db = parser.Parse(xml);
        var analyzer = new HierarchyAnalyzer();
        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);

        // Free-tier with only 1 change left today.
        var usageTracker = Substitute.For<IUsageTracker>();
        usageTracker.GetStatus().Returns(new UsageStatus(199, 200));
        usageTracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var vm = new BulkChangeViewModel(db, xml, analyzer, bulkService, usageTracker, configLoader);

        // Stage two pending edits — one would fit (remaining=1), two will not.
        vm.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";
        vm.RootMembers.Single(m => m.Name == "Speed").EditableStartValue = "42";

        vm.PendingInlineEditCount.Should().Be(2);
        vm.ApplyCommand.CanExecute(null).Should().BeFalse(
            "Apply must be blocked when pending count > remaining quota — no partial-apply state");
    }

    /// <summary>
    /// #62 follow-up: when remaining quota covers the pending batch, Apply
    /// stays enabled. Sanity check that the over-cap gate doesn't false-positive.
    /// </summary>
    [Fact]
    public void ApplyCommand_Enabled_WhenPendingFitsUnderQuota()
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var parser = new SimaticMLParser();
        var db = parser.Parse(xml);
        var analyzer = new HierarchyAnalyzer();
        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);

        var usageTracker = Substitute.For<IUsageTracker>();
        usageTracker.GetStatus().Returns(new UsageStatus(190, 200));
        usageTracker.RecordUsage(Arg.Any<int>()).Returns(true);

        var vm = new BulkChangeViewModel(db, xml, analyzer, bulkService, usageTracker, configLoader);

        vm.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";
        vm.RootMembers.Single(m => m.Name == "Speed").EditableStartValue = "42";

        vm.ApplyCommand.CanExecute(null).Should().BeTrue(
            "two pending edits fit under remaining=10");
    }

    /// <summary>
    /// Builds a VM with a configurable usage status and optional license service —
    /// the ApplyTooltip tests vary remaining quota and Pro state, so the standard
    /// helper isn't flexible enough.
    /// </summary>
    private static BulkChangeViewModel CreateViewModelWithUsage(
        UsageStatus status, ILicenseService? licenseService = null)
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var parser = new SimaticMLParser();
        var db = parser.Parse(xml);
        var analyzer = new HierarchyAnalyzer();
        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var usageTracker = Substitute.For<IUsageTracker>();
        usageTracker.GetStatus().Returns(status);
        usageTracker.RecordUsage(Arg.Any<int>()).Returns(true);

        return new BulkChangeViewModel(db, xml, analyzer, bulkService, usageTracker,
            configLoader, licenseService: licenseService);
    }

    /// <summary>
    /// #62 UX: Pro users never see the cost line in the Apply tooltip — quota
    /// doesn't apply to them, surfacing it would just be noise.
    /// </summary>
    [Fact]
    public void ApplyTooltip_Pro_OmitsCostLine()
    {
        var license = Substitute.For<ILicenseService>();
        license.IsProActive.Returns(true);
        var vm = CreateViewModelWithUsage(new UsageStatus(150, 200), license);

        vm.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";
        vm.RootMembers.Single(m => m.Name == "Speed").EditableStartValue = "42";

        vm.ApplyTooltip.Should().NotContain(
            "remaining today",
            "Pro tier has no daily cap — surfacing remaining quota would be misleading");
    }

    /// <summary>
    /// #62 UX: With nothing pending, the Apply button is disabled and the tooltip
    /// has no cost to surface — fall back to the plain advisory.
    /// </summary>
    [Fact]
    public void ApplyTooltip_NoPending_OmitsCostLine()
    {
        var vm = CreateViewModelWithUsage(new UsageStatus(0, 200));
        vm.PendingInlineEditCount.Should().Be(0);
        vm.ApplyTooltip.Should().NotContain("remaining today");
    }

    /// <summary>
    /// #62 UX: A single inline edit with plenty of headroom is the unsurprising
    /// case — the cost line would just be noise. Stays as the plain advisory.
    /// </summary>
    [Fact]
    public void ApplyTooltip_SingleEditWithHeadroom_OmitsCostLine()
    {
        var vm = CreateViewModelWithUsage(new UsageStatus(0, 200)); // 200 remaining
        vm.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";

        vm.PendingInlineEditCount.Should().Be(1);
        vm.ApplyTooltip.Should().NotContain("remaining today",
            "1 change with 200 left is the unsurprising case — keep the tooltip quiet");
    }

    /// <summary>
    /// Pins UI culture to en-US for the body of <paramref name="action"/> so
    /// assertions on localized resource phrases are deterministic regardless of
    /// the dev/CI machine's OS language. Tests that don't read string values
    /// from <c>Res</c> don't need this.
    /// </summary>
    private static void WithEnglishUICulture(Action action)
    {
        var prevUI = Thread.CurrentThread.CurrentUICulture;
        var prev = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        try { action(); }
        finally
        {
            Thread.CurrentThread.CurrentUICulture = prevUI;
            Thread.CurrentThread.CurrentCulture = prev;
        }
    }

    /// <summary>
    /// #62 UX: Two or more pending changes warrant the cost line even with full
    /// headroom — bulk Apply on free tier should always preview the cost.
    /// </summary>
    [Fact]
    public void ApplyTooltip_MultipleEdits_AppendsCostLine()
    {
        WithEnglishUICulture(() =>
        {
            var vm = CreateViewModelWithUsage(new UsageStatus(0, 200));
            vm.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";
            vm.RootMembers.Single(m => m.Name == "Speed").EditableStartValue = "42";

            vm.PendingInlineEditCount.Should().Be(2);
            vm.ApplyTooltip.Should()
                .Contain("free changes remaining today",
                    "the cost line — not just any '2 of 200' substring — must be present")
                .And.Contain("2 of 200");
        });
    }

    /// <summary>
    /// #62 UX: Even a single edit gets the cost line once headroom drops below
    /// the tight-threshold — that's exactly when surfacing remaining quota
    /// is most useful.
    /// </summary>
    [Fact]
    public void ApplyTooltip_TightHeadroom_AppendsCostLine()
    {
        WithEnglishUICulture(() =>
        {
            var vm = CreateViewModelWithUsage(new UsageStatus(170, 200)); // 30 remaining
            vm.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";

            vm.PendingInlineEditCount.Should().Be(1);
            vm.ApplyTooltip.Should()
                .Contain("free changes remaining today",
                    "tight remaining quota deserves the cost line even for a single change")
                .And.Contain("1 of 30");
        });
    }

    /// <summary>
    /// #62 UX: <see cref="BulkChangeViewModel.ApplyTooltip"/> is bound directly in
    /// XAML — staging an inline edit must raise PropertyChanged for it, otherwise
    /// the binding never re-evaluates and the cost line goes stale. Without this
    /// test, a future refactor that drops the notification line from
    /// <c>RefreshPendingAndPreview</c> would silently break the feature.
    /// </summary>
    [Fact]
    public void ApplyTooltip_RaisesPropertyChanged_WhenPendingEditStaged()
    {
        var vm = CreateViewModelWithUsage(new UsageStatus(0, 200));
        var changedProps = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        // Stage two edits — both transitions (0 → 1 and 1 → 2) should re-fire ApplyTooltip.
        vm.RootMembers.Single(m => m.Name == "Enable").EditableStartValue = "false";
        vm.RootMembers.Single(m => m.Name == "Speed").EditableStartValue = "42";

        changedProps.Should().Contain(nameof(BulkChangeViewModel.ApplyTooltip),
            "the binding can't re-evaluate without this notification");
    }
}
