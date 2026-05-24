using System.Collections.Generic;
using System.ComponentModel;
using FluentAssertions;
using BlockParam.UI.Controls.PillMultiSelect;
using Xunit;

namespace BlockParam.Tests;

public class PillItemsLifecycleTests
{
    private static MultiSelectRowViewModel Item(string display, string abbrev, bool selected = false)
        => new(new object(), display, abbrev) { IsSelected = selected };

    [Fact]
    public void RemoveItem_unsubscribes_so_changes_no_longer_affect_aggregates()
    {
        var vm = new MultiSelectInternalState();
        var a = Item("A", "1", selected: true);
        vm.AddItem(a);
        vm.SelectedCount.Should().Be(1);

        vm.RemoveItem(a).Should().BeTrue();
        vm.SelectedCount.Should().Be(0);

        // Mutating the orphaned item must not raise SelectedCount notifications
        // on the VM — that would be a leaked PropertyChanged subscription.
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        a.IsSelected = false;
        notifications.Should().NotContain(nameof(MultiSelectInternalState.SelectedCount));
    }

    [Fact]
    public void ClearItems_empties_collection_and_unsubscribes_from_remaining_items()
    {
        var vm = new MultiSelectInternalState();
        var a = Item("A", "1", selected: true);
        var b = Item("B", "2", selected: true);
        vm.AddItem(a);
        vm.AddItem(b);
        vm.SelectedCount.Should().Be(2);

        vm.ClearItems();
        vm.Items.Should().BeEmpty();
        vm.SelectedCount.Should().Be(0);

        // Toggling either previously-held item must not fire VM-level
        // notifications. A leaked subscription would resurrect SelectedCount
        // updates against an empty collection.
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        a.IsSelected = false;
        b.IsSelected = false;
        notifications.Should().NotContain(nameof(MultiSelectInternalState.SelectedCount));
    }

    [Fact]
    public void Items_property_is_read_only_view_of_internal_collection()
    {
        var vm = new MultiSelectInternalState();
        vm.AddItem(Item("A", "1"));
        vm.AddItem(Item("B", "2"));
        vm.Items.Count.Should().Be(2);
        // Compile-time guarantee: vm.Items is IReadOnlyList<...> so
        // callers can't bypass AddItem/RemoveItem/ClearItems and corrupt
        // the PropertyChanged subscription bookkeeping.
        vm.Items.Should().BeAssignableTo<IReadOnlyList<MultiSelectRowViewModel>>();
    }
}

public class PillConfigDefaultsTests
{
    [Fact]
    public void Defaults_match_reference_design()
    {
        var vm = new MultiSelectInternalState();
        vm.PopupWidth.Should().Be(280);
        vm.PopupMaxListHeight.Should().Be(280);
        vm.ShowSearchBox.Should().BeTrue();
        vm.ShowFooterActions.Should().BeTrue();
    }

    [Theory]
    [InlineData(nameof(MultiSelectInternalState.PopupWidth))]
    [InlineData(nameof(MultiSelectInternalState.PopupMaxListHeight))]
    [InlineData(nameof(MultiSelectInternalState.ShowSearchBox))]
    [InlineData(nameof(MultiSelectInternalState.ShowFooterActions))]
    public void Config_setters_raise_property_changed(string propertyName)
    {
        var vm = new MultiSelectInternalState();
        var fired = new List<string?>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        switch (propertyName)
        {
            case nameof(MultiSelectInternalState.PopupWidth):
                vm.PopupWidth = 400; break;
            case nameof(MultiSelectInternalState.PopupMaxListHeight):
                vm.PopupMaxListHeight = 400; break;
            case nameof(MultiSelectInternalState.ShowSearchBox):
                vm.ShowSearchBox = false; break;
            case nameof(MultiSelectInternalState.ShowFooterActions):
                vm.ShowFooterActions = false; break;
        }

        fired.Should().Contain(propertyName);
    }

    [Fact]
    public void Setting_same_value_skips_redundant_notification()
    {
        var vm = new MultiSelectInternalState { PopupWidth = 400 };
        var fired = new List<string?>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        vm.PopupWidth = 400;

        fired.Should().NotContain(nameof(MultiSelectInternalState.PopupWidth));
    }
}

public class PillFormatterSetterTests
{
    [Fact]
    public void DisplayFormatter_setter_uses_equality_check_to_skip_redundant_updates()
    {
        var vm = new MultiSelectInternalState();
        vm.AddItem(new MultiSelectRowViewModel("src", "A", "1") { IsSelected = true });

        Func<IReadOnlyList<MultiSelectRowViewModel>, string> fmt = _ => "x";
        vm.DisplayFormatter = fmt;

        var fired = new List<string?>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        vm.DisplayFormatter = fmt;

        fired.Should().NotContain(nameof(MultiSelectInternalState.DisplayFormatter));
        fired.Should().NotContain(nameof(MultiSelectInternalState.SelectedAbbreviationsText));
    }

    [Fact]
    public void TooltipFormatter_setter_raises_both_formatter_and_derived_property_changes()
    {
        var vm = new MultiSelectInternalState();
        vm.AddItem(new MultiSelectRowViewModel("src", "A", "1") { IsSelected = true });

        var fired = new List<string?>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        vm.TooltipFormatter = rows => PillTooltipFormatters.FullNamesRows(rows);

        fired.Should().Contain(nameof(MultiSelectInternalState.TooltipFormatter));
        fired.Should().Contain(nameof(MultiSelectInternalState.SelectionTooltip));
    }
}

public class PillOverridableStringsTests
{
    public PillOverridableStringsTests()
    {
        var en = new System.Globalization.CultureInfo("en-US");
        System.Threading.Thread.CurrentThread.CurrentCulture = en;
        System.Threading.Thread.CurrentThread.CurrentUICulture = en;
    }

    [Fact]
    public void Default_strings_resolve_to_resx_values()
    {
        var vm = new MultiSelectInternalState();
        vm.SearchPlaceholder.Should().Be("Search...");
        vm.ClearTooltip.Should().Be("Clear");
        vm.SelectAllText.Should().Be("Select all");
        vm.ResetText.Should().Be("Reset");
    }

    [Fact]
    public void Setter_overrides_resx_default_so_host_apps_can_skip_the_localization_dependency()
    {
        var vm = new MultiSelectInternalState
        {
            SearchPlaceholder = "Find tag…",
            ClearTooltip = "Wipe",
            SelectAllText = "All",
            ResetText = "None",
        };

        vm.SearchPlaceholder.Should().Be("Find tag…");
        vm.ClearTooltip.Should().Be("Wipe");
        vm.SelectAllText.Should().Be("All");
        vm.ResetText.Should().Be("None");
    }

    [Fact]
    public void PlusMoreFormat_default_pulls_from_resx()
    {
        var options = new PillOverflowOptions();
        options.PlusMoreFormat.Should().Be("+{0} more");
    }

    [Fact]
    public void PlusMoreFormat_override_changes_collapse_suffix()
    {
        var items = new List<MultiSelectRowViewModel>
        {
            new("src1", "A", "1") { IsSelected = true },
            new("src2", "B", "2") { IsSelected = true },
            new("src3", "C", "3") { IsSelected = true },
            new("src4", "D", "4") { IsSelected = true },
        };
        var options = new PillOverflowOptions
        {
            CollapseAfterEntries = 2,
            PlusMoreFormat = "and {0} others",
        };

        var result = PillOverflowFormatter.Format(items, r => r.Display, r => r.Abbreviation, options);

        result.Should().Be("A, B, and 2 others");
    }
}
