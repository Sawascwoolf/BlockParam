using FluentAssertions;
using BlockParam.UI.Controls.PillMultiSelect;
using Xunit;

namespace BlockParam.Tests;

public class PillTooltipTests
{
    private static PillRowViewModel Item(string display, string abbrev, bool selected = true)
        => new(new object(), display, abbrev) { IsSelected = selected };

    [Fact]
    public void FullNames_joins_displays_one_per_line()
    {
        var items = new[] { Item("DB_A", "DB1"), Item("DB_B", "DB2"), Item("DB_C", "DB3") };
        PillTooltipFormatters.FullNames(items).Should().Be("DB_A\nDB_B\nDB_C");
    }

    [Fact]
    public void AbbrevAndFullNames_renders_mapping_per_line()
    {
        var items = new[] { Item("DB_ProcessControl_HighPriority", "DB10"), Item("DB_ConfigParams", "DB99") };
        PillTooltipFormatters.AbbrevAndFullNames(items)
            .Should().Be("DB10 — DB_ProcessControl_HighPriority\nDB99 — DB_ConfigParams");
    }

    [Fact]
    public void SelectionTooltip_is_null_when_no_formatter_set()
    {
        var vm = new PillMultiSelectInternalState();
        vm.AddItem(Item("DB_A", "DB1"));
        vm.SelectionTooltip.Should().BeNull();
    }

    [Fact]
    public void SelectionTooltip_is_null_when_nothing_selected()
    {
        var vm = new PillMultiSelectInternalState { TooltipFormatter = PillTooltipFormatters.FullNames };
        vm.AddItem(Item("DB_A", "DB1", selected: false));
        vm.AddItem(Item("DB_B", "DB2", selected: false));
        vm.SelectionTooltip.Should().BeNull();
    }

    [Fact]
    public void SelectionTooltip_runs_formatter_and_excludes_unselected_items()
    {
        var vm = new PillMultiSelectInternalState { TooltipFormatter = PillTooltipFormatters.FullNames };
        vm.AddItem(Item("DB_A", "DB1", selected: true));
        vm.AddItem(Item("DB_B", "DB2", selected: false));
        vm.AddItem(Item("DB_C", "DB3", selected: true));
        vm.SelectionTooltip.Should().Be("DB_A\nDB_C");
    }

    [Fact]
    public void SelectionTooltip_updates_when_selection_changes()
    {
        var vm = new PillMultiSelectInternalState { TooltipFormatter = PillTooltipFormatters.FullNames };
        var a = Item("DB_A", "DB1", selected: true);
        var b = Item("DB_B", "DB2", selected: false);
        vm.AddItem(a);
        vm.AddItem(b);

        vm.SelectionTooltip.Should().Be("DB_A");

        b.IsSelected = true;
        vm.SelectionTooltip.Should().Be("DB_A\nDB_B");

        a.IsSelected = false;
        vm.SelectionTooltip.Should().Be("DB_B");
    }
}
