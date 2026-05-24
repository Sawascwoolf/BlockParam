using System.Collections.Generic;
using FluentAssertions;
using BlockParam.UI.Controls.PillMultiSelect;
using Xunit;

namespace BlockParam.Tests;

public class PillTooltipTests
{
    private static MultiSelectRowViewModel Item(string display, string abbrev, bool selected = true)
        => new(new object(), display, abbrev) { IsSelected = selected };

    // ── PillTooltipFormatters generic API ────────────────────────────────────

    [Fact]
    public void FullNames_generic_joins_displays_one_per_line()
    {
        var items = new[] { "DB_A", "DB_B", "DB_C" };
        PillTooltipFormatters.FullNames(items, x => x)
            .Should().Be("DB_A\nDB_B\nDB_C");
    }

    [Fact]
    public void AbbrevAndFullNames_generic_renders_mapping_per_line()
    {
        // Arbitrary source type — demonstrates generic API is not coupled to MultiSelectRowViewModel.
        var items = new[] { (Display: "DB_ProcessControl_HighPriority", Abbrev: "DB10"),
                            (Display: "DB_ConfigParams", Abbrev: "DB99") };
        PillTooltipFormatters.AbbrevAndFullNames(items, t => t.Display, t => t.Abbrev)
            .Should().Be("DB10 — DB_ProcessControl_HighPriority\nDB99 — DB_ConfigParams");
    }

    // ── Internal row-adapted helpers (used by UserControl for TooltipMode DP) ─

    [Fact]
    public void FullNamesRows_joins_row_displays_one_per_line()
    {
        var rows = new[] { Item("DB_A", "DB1"), Item("DB_B", "DB2"), Item("DB_C", "DB3") };
        PillTooltipFormatters.FullNamesRows(rows).Should().Be("DB_A\nDB_B\nDB_C");
    }

    [Fact]
    public void AbbrevAndFullNamesRows_renders_mapping_per_line()
    {
        var rows = new[] { Item("DB_ProcessControl_HighPriority", "DB10"), Item("DB_ConfigParams", "DB99") };
        PillTooltipFormatters.AbbrevAndFullNamesRows(rows)
            .Should().Be("DB10 — DB_ProcessControl_HighPriority\nDB99 — DB_ConfigParams");
    }

    // ── MultiSelectInternalState.SelectionTooltip (internal state wiring) ─

    [Fact]
    public void SelectionTooltip_is_null_when_no_formatter_set()
    {
        var vm = new MultiSelectInternalState();
        vm.AddItem(Item("DB_A", "DB1"));
        vm.SelectionTooltip.Should().BeNull();
    }

    [Fact]
    public void SelectionTooltip_is_null_when_nothing_selected()
    {
        var vm = new MultiSelectInternalState
        {
            TooltipFormatter = rows => PillTooltipFormatters.FullNamesRows(rows)
        };
        vm.AddItem(Item("DB_A", "DB1", selected: false));
        vm.AddItem(Item("DB_B", "DB2", selected: false));
        vm.SelectionTooltip.Should().BeNull();
    }

    [Fact]
    public void SelectionTooltip_runs_formatter_and_excludes_unselected_items()
    {
        var vm = new MultiSelectInternalState
        {
            TooltipFormatter = rows => PillTooltipFormatters.FullNamesRows(rows)
        };
        vm.AddItem(Item("DB_A", "DB1", selected: true));
        vm.AddItem(Item("DB_B", "DB2", selected: false));
        vm.AddItem(Item("DB_C", "DB3", selected: true));
        vm.SelectionTooltip.Should().Be("DB_A\nDB_C");
    }

    [Fact]
    public void SelectionTooltip_updates_when_selection_changes()
    {
        var vm = new MultiSelectInternalState
        {
            TooltipFormatter = rows => PillTooltipFormatters.FullNamesRows(rows)
        };
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
