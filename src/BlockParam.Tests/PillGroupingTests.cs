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

    [Fact]
    public void Swapping_ItemsSource_detaches_children_from_old_groups()
    {
        // When the host swaps ItemsSource, RebuildRows discards the old
        // group VMs. ClearGroups must detach each child first so stale
        // PropertyChanged subscriptions against the old group's
        // OnChildPropertyChanged unwind cleanly - the row could still be
        // alive through SelectedItems, and we don't want a dead group to
        // be reachable through its event handlers.
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Eng"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);
        var oldEng = fx.State.Groups["Eng"];
        oldEng.Children.Count.Should().Be(2);
        var aliceRow = fx.State.Items.First(r => r.Display == "Alice");

        // Trigger a Reset by swapping ItemsSource.
        fx.ItemSource.ItemsSource = new ObservableCollection<TeamMember>
        {
            new("Carol", "Sales"),
        };

        // Old group must have no remaining children and the surviving row
        // must no longer point at the dead group.
        oldEng.Children.Should().BeEmpty();
        aliceRow.OwningGroup.Should().BeNull();
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
    public void Mid_search_collapse_persists_after_search_clears()
    {
        // User starts with an expanded group; types a search that matches a
        // child, so the policy is a no-op for that group. The user then
        // collapses the group while search is active. Clearing the search
        // should leave the group collapsed - the most recent user intent.
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Sales"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);
        // Eng starts expanded (default).
        fx.State.SearchText = "Alice";  // search active; Eng has a match
        fx.State.Groups["Eng"].IsExpanded.Should().BeTrue();

        // User collapses Eng mid-search.
        fx.State.Groups["Eng"].IsExpanded = false;

        fx.State.SearchText = "";

        // Restore must honour the mid-search write, not the pre-search value.
        fx.State.Groups["Eng"].IsExpanded.Should().BeFalse();
    }

    [Fact]
    public void Subsequent_keystroke_does_not_reexpand_user_collapsed_group()
    {
        // User collapses Eng mid-search; typing more characters that still
        // match should not re-expand Eng on every keystroke.
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Sales"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);
        fx.State.SearchText = "A";
        fx.State.Groups["Eng"].IsExpanded.Should().BeTrue();

        // User collapses Eng even though it has a match.
        fx.State.Groups["Eng"].IsExpanded = false;

        // User refines the search; Eng still matches "Al".
        fx.State.SearchText = "Al";

        // The collapse must stick - the user already expressed a preference.
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

// ─────────────────────────────────────────────────────────────────────────────
// Trigger token bundling: fully-selected groups collapse into one token
// ─────────────────────────────────────────────────────────────────────────────

public class PillTriggerTokenBundling_Tests
{
    [Fact]
    public void No_grouping_emits_one_token_per_selected_row()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Eng"));
        // Grouping NOT configured.

        fx.State.Items[0].IsSelected = true;
        fx.State.Items[1].IsSelected = true;

        var summary = fx.State.SelectedAbbreviationsText;
        // Without grouping, OwningGroup is null and bundling is a no-op.
        summary.Should().Be("Alice, Bob");
    }

    [Fact]
    public void Fully_selected_single_group_collapses_into_one_token()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Eng"));
        fx.Add(new TeamMember("Carol", "Sales"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        // Select all Engineering, leave Sales alone.
        foreach (var row in fx.State.Items)
            if (row.GroupKey?.ToString() == "Eng") row.IsSelected = true;

        fx.State.SelectedAbbreviationsText.Should().Be("Eng");
    }

    [Fact]
    public void Two_fully_selected_groups_emit_two_group_tokens()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Sales"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        foreach (var row in fx.State.Items) row.IsSelected = true;

        fx.State.SelectedAbbreviationsText.Should().Be("Eng, Sales");
    }

    [Fact]
    public void Partial_group_emits_individual_row_tokens()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Eng"));
        fx.Add(new TeamMember("Carol", "Eng"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        fx.State.Items[0].IsSelected = true;
        fx.State.Items[2].IsSelected = true;

        // 2 of 3 selected → group is partial, no bundling.
        fx.State.SelectedAbbreviationsText.Should().Be("Alice, Carol");
    }

    [Fact]
    public void Mixed_full_and_partial_emits_group_then_individuals()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Eng"));
        fx.Add(new TeamMember("Carol", "Sales"));
        fx.Add(new TeamMember("Dave", "Sales"));
        fx.Add(new TeamMember("Eve", "Sales"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        // Engineering fully selected; Sales partial (Carol + Dave only).
        fx.State.Items[0].IsSelected = true;
        fx.State.Items[1].IsSelected = true;
        fx.State.Items[2].IsSelected = true;
        fx.State.Items[3].IsSelected = true;

        fx.State.SelectedAbbreviationsText.Should().Be("Eng, Carol, Dave");
    }

    [Fact]
    public void Group_token_appears_at_position_of_first_selected_member()
    {
        var fx = new GroupingFixture();
        // Source order interleaves the two groups.
        fx.Add(new TeamMember("Alice", "Eng"));    // Eng
        fx.Add(new TeamMember("Bob", "Sales"));    // Sales
        fx.Add(new TeamMember("Carol", "Eng"));    // Eng
        fx.Add(new TeamMember("Dave", "Sales"));   // Sales
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        foreach (var row in fx.State.Items) row.IsSelected = true;

        // Both groups fully checked; group token appears at the FIRST selected
        // member's slot. Source order: Alice(Eng), Bob(Sales), Carol(Eng), Dave(Sales)
        // → tokens: Eng (at Alice), Sales (at Bob); subsequent Eng/Sales members absorbed.
        fx.State.SelectedAbbreviationsText.Should().Be("Eng, Sales");
    }

    [Fact]
    public void Indeterminate_group_does_not_bundle()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Eng"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);

        // Mark one as indeterminate, the other true. Group aggregate is
        // null (any-null rule), so it should NOT bundle.
        fx.State.Items[0].IsSelected = null;
        fx.State.Items[1].IsSelected = true;

        // Only Bob is fully checked; bundling skips Eng (group.IsSelected != true);
        // Alice is null so not included in the summary at all.
        fx.State.SelectedAbbreviationsText.Should().Be("Bob");
    }

    [Fact]
    public void Builder_emits_group_count_for_tooltip_use()
    {
        var fx = new GroupingFixture();
        fx.Add(new TeamMember("Alice", "Eng"));
        fx.Add(new TeamMember("Bob", "Eng"));
        fx.Add(new TeamMember("Carol", "Eng"));
        fx.ItemSource.GroupKeyMemberPath = nameof(TeamMember.Department);
        foreach (var row in fx.State.Items) row.IsSelected = true;

        var selected = fx.State.Items.Where(r => r.IsCheckedTrue).ToList();
        var tokens = PillTriggerTokenBuilder.Build(selected);

        tokens.Should().ContainSingle()
            .Which.GroupMemberCount.Should().Be(3);
    }
}

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
