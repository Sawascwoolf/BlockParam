using BlockParam.Config;
using BlockParam.Licensing;
using BlockParam.Services;
using BlockParam.SimaticML;
using BlockParam.UI;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BlockParam.Tests;

public class BulkChangeViewModelTests
{
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
}
