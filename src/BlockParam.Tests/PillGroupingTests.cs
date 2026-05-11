using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using FluentAssertions;
using BlockParam.UI.Controls.PillMultiSelect;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Test source item with a Department property used as the group key and an
/// Name display field. Implements INPC so we can also cover live source
/// mutations in a follow-up if needed.
/// </summary>
file sealed class TeamMember : INotifyPropertyChanged
{
    private string _department;
    public TeamMember(string name, string department)
    {
        Name = name;
        _department = department;
    }
    public string Name { get; }
    public string Department
    {
        get => _department;
        set
        {
            if (_department == value) return;
            _department = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Department)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Test fixture wiring up the same surface as <see cref="PillMultiSelect"/>
/// (internal state + item source) so grouping behaviour can be exercised
/// without instantiating the WPF UserControl.
/// </summary>
file sealed class GroupingFixture
{
    internal PillMultiSelectInternalState State { get; }
    internal PillItemSource ItemSource { get; }
    internal MemberPathResolver Resolver { get; }
    internal ObservableCollection<TeamMember> Sources { get; }

    internal GroupingFixture()
    {
        State = new PillMultiSelectInternalState();
        Resolver = new MemberPathResolver();
        ItemSource = new PillItemSource(State, Resolver);

        Sources = new ObservableCollection<TeamMember>();
        ItemSource.ItemsSource = Sources;
        ItemSource.DisplayMemberPath = nameof(TeamMember.Name);
    }

    internal void Add(TeamMember member) => Sources.Add(member);
}

// ─────────────────────────────────────────────────────────────────────────────
// Group VM creation & assignment
// ─────────────────────────────────────────────────────────────────────────────

public class PillGroupVm_Lifecycle_Tests
{
    [Fact]
    public void Setting_group_key_member_path_creates_one_group_per_distinct_value()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Eng"));
        fx.Add(new TeamMember("Carol", "Sales"));

        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        fx.State.Groups.Should().HaveCount(2);
        fx.State.Groups.Keys.Should().BeEquivalentTo(new[] { "Eng", "Sales" });
    }

    [Fact]
    public void Each_row_carries_a_back_reference_to_its_owning_group()
    {
        var fx = new GroupingFixture();
        var alice = new TeamMember("Alice", "Eng");
        fx.Add(alice);
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        var row = fx.State.Items.Single();
        row.OwningGroup.Should().NotBeNull();
        row.OwningGroup!.Key.Should().Be("Eng");
        row.OwningGroup.Children.Should().Contain(row);
    }

    [Fact]
    public void Clearing_group_key_member_path_drops_all_groups()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);
        fx.State.Groups.Should().NotBeEmpty();

        fx.ItemSource.GroupKeyMemberPath = null;

        fx.State.Groups.Should().BeEmpty();
        fx.State.Items.Single().OwningGroup.Should().BeNull();
        fx.State.IsExplicitGroupingActive.Should().BeFalse();
    }

    [Fact]
    public void GroupKeySelector_overrides_member_path()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Sales"));

        // Bucket every member into the same "All" group via the selector.
        fx.ItemSource.GroupKeyOverride = _ => "All";

        fx.State.Groups.Should().HaveCount(1);
        fx.State.Groups.Single().Key.Should().Be("All");
    }

    [Fact]
    public void Removing_last_row_of_a_group_removes_the_group_vm()
    {
        var fx = new GroupingFixture();
        var alice = new TeamMember("Alice", "Eng");
        var bob = new TeamMember("Bob", "Sales");
        fx.Add(alice);
        fx.Add(bob);
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);
        fx.State.Groups.Should().HaveCount(2);

        fx.Sources.Remove(alice);

        fx.State.Groups.Should().HaveCount(1);
        fx.State.Groups.Keys.Should().BeEquivalentTo(new[] { "Sales" });
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tri-state aggregation on group headers
// ─────────────────────────────────────────────────────────────────────────────

public class PillGroupVm_TriState_Tests
{
    [Fact]
    public void Header_is_false_when_no_child_is_selected()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Eng"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        var group = fx.State.Groups["Eng"];
        group.IsSelected.Should().Be(false);
    }

    [Fact]
    public void Header_is_true_when_every_child_is_selected()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Eng"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        foreach (var row in fx.State.Items) row.IsSelected = true;

        fx.State.Groups["Eng"].IsSelected.Should().Be(true);
    }

    [Fact]
    public void Header_is_null_when_children_are_mixed()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Eng"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        fx.State.Items[0].IsSelected = true;

        fx.State.Groups["Eng"].IsSelected.Should().BeNull();
    }

    [Fact]
    public void Header_is_null_when_any_child_is_indeterminate()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Eng"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        fx.State.Items[0].IsSelected = null;

        fx.State.Groups["Eng"].IsSelected.Should().BeNull();
    }

    [Fact]
    public void Setting_header_to_true_selects_every_child()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Eng"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        fx.State.Groups["Eng"].IsSelected = true;

        fx.State.Items.Should().AllSatisfy(r => r.IsSelected.Should().Be(true));
    }

    [Fact]
    public void Setting_header_to_false_clears_every_child()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Eng"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);
        foreach (var row in fx.State.Items) row.IsSelected = true;

        fx.State.Groups["Eng"].IsSelected = false;

        fx.State.Items.Should().AllSatisfy(r => r.IsSelected.Should().Be(false));
    }

    [Fact]
    public void SelectedCount_and_TotalCount_track_children()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Eng"));
        fx.Add(new TeamMember("Carol", "Eng"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);
        var group = fx.State.Groups["Eng"];

        group.TotalCount.Should().Be(3);
        group.SelectedCount.Should().Be(0);

        fx.State.Items[0].IsSelected = true;
        fx.State.Items[2].IsSelected = true;

        group.SelectedCount.Should().Be(2);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Expand/collapse
// ─────────────────────────────────────────────────────────────────────────────

public class PillGroupVm_Expansion_Tests
{
    [Fact]
    public void Groups_default_to_expanded()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        fx.State.Groups["Eng"].IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void Toggle_expanded_command_flips_the_value()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);
        var group = fx.State.Groups["Eng"];

        group.ToggleExpandedCommand.Execute(null);
        group.IsExpanded.Should().BeFalse();

        group.ToggleExpandedCommand.Execute(null);
        group.IsExpanded.Should().BeTrue();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Search-into-collapsed
// ─────────────────────────────────────────────────────────────────────────────

public class PillSearch_ExpansionPolicy_Tests
{
    [Fact]
    public void Search_with_match_inside_collapsed_group_forces_expansion()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Sales"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        // User collapses Eng manually.
        fx.State.Groups["Eng"].IsExpanded = false;

        // Type a search that matches Alice (in collapsed Eng group).
        fx.State.SearchText = "Alice";

        fx.State.Groups["Eng"].IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void Search_with_no_match_in_collapsed_group_leaves_it_collapsed()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Sales"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);
        fx.State.Groups["Eng"].IsExpanded = false;

        // Search matches Bob in Sales, no match in Eng.
        fx.State.SearchText = "Bob";

        fx.State.Groups["Eng"].IsExpanded.Should().BeFalse();
        fx.State.Groups["Sales"].IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void Clearing_search_restores_user_collapsed_state()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Sales"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);
        fx.State.Groups["Eng"].IsExpanded = false;
        fx.State.SearchText = "Alice";  // forces Eng open
        fx.State.Groups["Eng"].IsExpanded.Should().BeTrue();

        fx.State.SearchText = "";

        fx.State.Groups["Eng"].IsExpanded.Should().BeFalse();
    }

    [Fact]
    public void Search_expansion_policy_is_noop_when_grouping_not_active()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        // No GroupKeyMemberPath set — flat list.

        fx.State.SearchText = "Alice";

        // No groups exist; nothing to expand. Just verify SearchText sticks.
        fx.State.SearchText.Should().Be("Alice");
        fx.State.Groups.Should().BeEmpty();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CollectionView wiring (GroupDescriptions / SortDescriptions)
// ─────────────────────────────────────────────────────────────────────────────

public class PillGrouping_CollectionView_Tests
{
    [Fact]
    public void Explicit_grouping_attaches_one_group_description()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Sales"));

        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        fx.State.FilteredItems.GroupDescriptions.Should().HaveCount(1);
        fx.State.FilteredItems.SortDescriptions.Should().BeEmpty();
    }

    [Fact]
    public void Explicit_grouping_supersedes_selected_first_when_popup_opens()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Sales"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        // Mix selection so selected-first WOULD attach an extra grouping.
        fx.State.Items[0].IsSelected = true;
        fx.State.IsOpen = true;

        // Still exactly one (explicit) group description, not the selected-first one.
        fx.State.FilteredItems.GroupDescriptions.Should().HaveCount(1);
    }

    [Fact]
    public void CollectionView_group_name_is_the_PillGroupViewModel()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Sales"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        var view = (ListCollectionView)fx.State.FilteredItems;
        var groupNames = view.Groups!
            .OfType<CollectionViewGroup>()
            .Select(g => g.Name)
            .ToList();

        groupNames.Should().AllBeOfType<PillGroupViewModel>();
        groupNames.OfType<PillGroupViewModel>()
            .Select(g => g.Key)
            .Should().BeEquivalentTo(new[] { "Eng", "Sales" });
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tri-state row + SelectedItems contract
// ─────────────────────────────────────────────────────────────────────────────

public class PillRow_TriState_Tests
{
    [Fact]
    public void Row_IsSelected_defaults_to_false()
    {
        var row = new PillRowViewModel(new object(), "A", "1");
        row.IsSelected.Should().Be(false);
    }

    [Fact]
    public void Row_IsSelected_can_be_set_to_null()
    {
        var row = new PillRowViewModel(new object(), "A", "1");
        row.IsSelected = null;
        row.IsSelected.Should().BeNull();
        row.IsCheckedTrue.Should().BeFalse();
    }

    [Fact]
    public void Indeterminate_row_is_not_added_to_SelectedItems()
    {
        var state = new PillMultiSelectInternalState();
        var resolver = new MemberPathResolver();
        var itemSource = new PillItemSource(state, resolver);
        var sync = new PillSelectionSync(state, itemSource, resolver);

        var sources = new ObservableCollection<object>();
        var selectedItems = new ObservableCollection<object>();
        sync.SetSelectedItems(selectedItems);
        itemSource.ItemsSource = sources;

        var src = new object();
        sources.Add(src);
        var row = state.Items.Single();

        row.IsSelected = null;

        selectedItems.Should().BeEmpty();
    }

    [Fact]
    public void Transition_from_true_to_null_removes_from_SelectedItems()
    {
        var state = new PillMultiSelectInternalState();
        var resolver = new MemberPathResolver();
        var itemSource = new PillItemSource(state, resolver);
        var sync = new PillSelectionSync(state, itemSource, resolver);

        var sources = new ObservableCollection<object>();
        var selectedItems = new ObservableCollection<object>();
        sync.SetSelectedItems(selectedItems);
        itemSource.ItemsSource = sources;

        var src = new object();
        sources.Add(src);
        var row = state.Items.Single();
        row.IsSelected = true;
        selectedItems.Should().ContainSingle().Which.Should().BeSameAs(src);

        row.IsSelected = null;

        selectedItems.Should().BeEmpty();
    }
}
