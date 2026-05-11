using System.Linq;
using FluentAssertions;
using BlockParam.UI.Controls.PillMultiSelect;
using Xunit;

namespace BlockParam.Tests;

public class PillSnapshotOrderingTests
{
    private static PillRowViewModel Item(string display, string abbrev, bool selected = false)
        => new(new object(), display, abbrev) { IsSelected = selected };

    private static PillMultiSelectInternalState BuildVm(params PillRowViewModel[] items)
    {
        var vm = new PillMultiSelectInternalState();
        foreach (var item in items) vm.AddItem(item);
        return vm;
    }

    private static string[] ViewOrder(PillMultiSelectInternalState vm)
        => vm.FilteredItems.Cast<PillRowViewModel>().Select(r => r.Display).ToArray();

    [Fact]
    public void SortSelectedFirst_defaults_to_true()
    {
        var vm = new PillMultiSelectInternalState();
        vm.SortSelectedFirst.Should().BeTrue();
    }

    [Fact]
    public void Popup_open_orders_selected_items_first_in_view()
    {
        var vm = BuildVm(
            Item("A", "1", selected: true),
            Item("B", "2", selected: false),
            Item("C", "3", selected: true));

        vm.IsOpen = true;

        ViewOrder(vm).Should().Equal("A", "C", "B");
    }

    [Fact]
    public void Toggling_selection_while_open_does_not_reshuffle_view()
    {
        var a = Item("A", "1", selected: true);
        var b = Item("B", "2", selected: false);
        var c = Item("C", "3", selected: true);
        var vm = BuildVm(a, b, c);
        vm.IsOpen = true;
        var openingOrder = ViewOrder(vm);

        // User toggles inside the open popup.
        a.IsSelected = false;
        b.IsSelected = true;

        ViewOrder(vm).Should().Equal(openingOrder);
    }

    [Fact]
    public void Re_opening_popup_re_snapshots_ordering()
    {
        var a = Item("A", "1", selected: true);
        var b = Item("B", "2", selected: false);
        var vm = BuildVm(a, b);

        vm.IsOpen = true;
        ViewOrder(vm).Should().Equal("A", "B");  // selected (A) first

        // User toggles, then closes and reopens.
        a.IsSelected = false;
        b.IsSelected = true;
        vm.IsOpen = false;
        vm.IsOpen = true;

        ViewOrder(vm).Should().Equal("B", "A");  // selected (B) now first
    }

    [Fact]
    public void SortSelectedFirst_false_disables_view_grouping()
    {
        var vm = BuildVm(
            Item("A", "1", selected: true),
            Item("B", "2", selected: false));
        vm.IsOpen = true;
        vm.FilteredItems.GroupDescriptions.Should().HaveCount(1);

        vm.SortSelectedFirst = false;

        vm.FilteredItems.GroupDescriptions.Should().BeEmpty();
        vm.FilteredItems.SortDescriptions.Should().BeEmpty();
    }

    [Fact]
    public void Mixed_selection_attaches_group_and_sort_descriptions()
    {
        var vm = BuildVm(
            Item("A", "1", selected: true),
            Item("B", "2", selected: false));
        vm.IsOpen = true;
        vm.FilteredItems.GroupDescriptions.Should().HaveCount(1);
        vm.FilteredItems.SortDescriptions.Should().HaveCount(1);
    }

    [Fact]
    public void All_selected_skips_grouping_no_divider_needed()
    {
        var vm = BuildVm(
            Item("A", "1", selected: true),
            Item("B", "2", selected: true));
        vm.IsOpen = true;
        vm.FilteredItems.GroupDescriptions.Should().BeEmpty();
    }

    [Fact]
    public void Nothing_selected_skips_grouping_no_divider_needed()
    {
        var vm = BuildVm(
            Item("A", "1", selected: false),
            Item("B", "2", selected: false));
        vm.IsOpen = true;
        vm.FilteredItems.GroupDescriptions.Should().BeEmpty();
    }

    [Fact]
    public void Active_search_disables_grouping()
    {
        var vm = BuildVm(
            Item("Alpha", "1", selected: true),
            Item("Beta", "2", selected: false));
        vm.IsOpen = true;
        vm.FilteredItems.GroupDescriptions.Should().HaveCount(1);

        vm.SearchText = "a";
        vm.FilteredItems.GroupDescriptions.Should().BeEmpty();
    }

    [Fact]
    public void Clearing_search_restores_grouping()
    {
        var vm = BuildVm(
            Item("Alpha", "1", selected: true),
            Item("Beta", "2", selected: false));
        vm.IsOpen = true;
        vm.SearchText = "a";
        vm.FilteredItems.GroupDescriptions.Should().BeEmpty();

        vm.SearchText = "";
        vm.FilteredItems.GroupDescriptions.Should().HaveCount(1);
    }
}
