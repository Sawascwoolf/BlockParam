using BlockParam.Models;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Focused tests for the bulk-preview slice (#80 slice 5).
/// </summary>
public class BulkPreviewViewModelTests
{
    [Fact]
    public void Defaults_AreEmpty()
    {
        var vm = new BulkPreviewViewModel(() => "");

        vm.Entries.Should().BeEmpty();
        vm.HasEntries.Should().BeFalse();
        vm.Count.Should().Be(0);
        vm.ConflictCount.Should().Be(0);
        vm.HasConflict.Should().BeFalse();
        vm.ConflictWarning.Should().BeEmpty();
        vm.Summary.Should().BeEmpty();
    }

    [Fact]
    public void Add_PopulatesEntriesAndCount()
    {
        var vm = new BulkPreviewViewModel(() => "999");
        vm.Add(MakeEntry("Speed1", "100", "999", conflict: false));
        vm.Add(MakeEntry("Speed2", "100", "999", conflict: false));

        vm.Count.Should().Be(2);
        vm.HasEntries.Should().BeTrue();
    }

    [Fact]
    public void Clear_EmptiesEntries()
    {
        var vm = new BulkPreviewViewModel(() => "999");
        vm.Add(MakeEntry("Speed1", "100", "999", conflict: false));

        vm.Clear();

        vm.Entries.Should().BeEmpty();
        vm.HasEntries.Should().BeFalse();
    }

    [Fact]
    public void ConflictCount_CountsRowsWithPendingConflictFlag()
    {
        var vm = new BulkPreviewViewModel(() => "999");
        vm.Add(MakeEntry("A", "100", "999", conflict: false));
        vm.Add(MakeEntry("B", "100", "999", conflict: true));
        vm.Add(MakeEntry("C", "100", "999", conflict: true));

        vm.ConflictCount.Should().Be(2);
        vm.HasConflict.Should().BeTrue();
    }

    [Fact]
    public void ConflictWarning_NoConflicts_IsEmpty()
    {
        var vm = new BulkPreviewViewModel(() => "");
        vm.Add(MakeEntry("A", "0", "1", conflict: false));

        vm.ConflictWarning.Should().BeEmpty();
    }

    [Fact]
    public void ConflictWarning_SingularAndPlural()
    {
        var vm = new BulkPreviewViewModel(() => "");
        vm.Add(MakeEntry("A", "0", "1", conflict: true));
        vm.ConflictWarning.Should().Contain("1 overlap");

        vm.Add(MakeEntry("B", "0", "1", conflict: true));
        vm.ConflictWarning.Should().Contain("2 overlap");
    }

    [Fact]
    public void Summary_Empty_IsEmpty()
    {
        var vm = new BulkPreviewViewModel(() => "999");
        vm.Summary.Should().BeEmpty();
    }

    [Fact]
    public void Summary_HomogeneousOriginals_RendersOrigToNew()
    {
        var vm = new BulkPreviewViewModel(() => "85");
        vm.Add(MakeEntry("A", "42", "85", conflict: false));
        vm.Add(MakeEntry("B", "42", "85", conflict: false));

        vm.Summary.Should().Be("42 ⇢ 85");
    }

    [Fact]
    public void Summary_HeterogeneousOriginals_RendersCountTargets()
    {
        var vm = new BulkPreviewViewModel(() => "85");
        vm.Add(MakeEntry("A", "42", "85", conflict: false));
        vm.Add(MakeEntry("B", "50", "85", conflict: false));
        vm.Add(MakeEntry("C", "60", "85", conflict: false));

        vm.Summary.Should().Be("3 targets");
    }

    [Fact]
    public void Summary_HomogeneousButEmptyOriginal_FallsBackToCountTargets()
    {
        var vm = new BulkPreviewViewModel(() => "85");
        vm.Add(MakeEntry("A", "", "85", conflict: false));
        vm.Add(MakeEntry("B", "", "85", conflict: false));

        vm.Summary.Should().Be("2 targets");
    }

    [Fact]
    public void Summary_UsesCurrentTargetValueViaCallback()
    {
        string target = "85";
        var vm = new BulkPreviewViewModel(() => target);
        vm.Add(MakeEntry("A", "42", "85", conflict: false));

        vm.Summary.Should().EndWith("85");

        target = "99";
        vm.Summary.Should().EndWith("99");
    }

    [Fact]
    public void RaiseDerivedChanged_NotifiesAllDerivedProperties()
    {
        var vm = new BulkPreviewViewModel(() => "");
        var raised = new System.Collections.Generic.List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RaiseDerivedChanged();

        raised.Should().Contain(nameof(BulkPreviewViewModel.HasEntries));
        raised.Should().Contain(nameof(BulkPreviewViewModel.Count));
        raised.Should().Contain(nameof(BulkPreviewViewModel.Summary));
        raised.Should().Contain(nameof(BulkPreviewViewModel.ConflictCount));
        raised.Should().Contain(nameof(BulkPreviewViewModel.HasConflict));
        raised.Should().Contain(nameof(BulkPreviewViewModel.ConflictWarning));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fixtures
    // ─────────────────────────────────────────────────────────────────────

    private static BulkPreviewEntry MakeEntry(string name, string original, string preview, bool conflict)
    {
        var leaf = new MemberNode(name, "Int", original, $"DB.{name}", null, Array.Empty<MemberNode>());
        var leafVm = new MemberNodeViewModel(leaf, null);
        return new BulkPreviewEntry(leafVm, original, preview, conflict);
    }
}
