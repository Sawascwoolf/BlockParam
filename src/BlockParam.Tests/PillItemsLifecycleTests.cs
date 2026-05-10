using System.Collections.Generic;
using System.ComponentModel;
using FluentAssertions;
using BlockParam.UI.Controls.PillMultiSelect;
using Xunit;

namespace BlockParam.Tests;

public class PillItemsLifecycleTests
{
    private static PillMultiSelectItemViewModel Item(string display, string abbrev, bool selected = false)
        => new(display, abbrev) { IsSelected = selected };

    [Fact]
    public void RemoveItem_unsubscribes_so_changes_no_longer_affect_aggregates()
    {
        var vm = new PillMultiSelectViewModel();
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
        notifications.Should().NotContain(nameof(PillMultiSelectViewModel.SelectedCount));
    }

    [Fact]
    public void ClearItems_empties_collection_and_unsubscribes_from_remaining_items()
    {
        var vm = new PillMultiSelectViewModel();
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
        notifications.Should().NotContain(nameof(PillMultiSelectViewModel.SelectedCount));
    }

    [Fact]
    public void Items_property_is_read_only_view_of_internal_collection()
    {
        var vm = new PillMultiSelectViewModel();
        vm.AddItem(Item("A", "1"));
        vm.AddItem(Item("B", "2"));
        vm.Items.Count.Should().Be(2);
        // Compile-time guarantee: vm.Items is IReadOnlyList<...> so
        // callers can't bypass AddItem/RemoveItem/ClearItems and corrupt
        // the PropertyChanged subscription bookkeeping.
        vm.Items.Should().BeAssignableTo<IReadOnlyList<PillMultiSelectItemViewModel>>();
    }
}

public class PillConfigDefaultsTests
{
    [Fact]
    public void Defaults_match_reference_design()
    {
        var vm = new PillMultiSelectViewModel();
        vm.PopupWidth.Should().Be(280);
        vm.PopupMaxListHeight.Should().Be(280);
        vm.ShowSearchBox.Should().BeTrue();
        vm.ShowFooterActions.Should().BeTrue();
    }

    [Theory]
    [InlineData(nameof(PillMultiSelectViewModel.PopupWidth))]
    [InlineData(nameof(PillMultiSelectViewModel.PopupMaxListHeight))]
    [InlineData(nameof(PillMultiSelectViewModel.ShowSearchBox))]
    [InlineData(nameof(PillMultiSelectViewModel.ShowFooterActions))]
    public void Config_setters_raise_property_changed(string propertyName)
    {
        var vm = new PillMultiSelectViewModel();
        var fired = new List<string?>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        switch (propertyName)
        {
            case nameof(PillMultiSelectViewModel.PopupWidth):
                vm.PopupWidth = 400; break;
            case nameof(PillMultiSelectViewModel.PopupMaxListHeight):
                vm.PopupMaxListHeight = 400; break;
            case nameof(PillMultiSelectViewModel.ShowSearchBox):
                vm.ShowSearchBox = false; break;
            case nameof(PillMultiSelectViewModel.ShowFooterActions):
                vm.ShowFooterActions = false; break;
        }

        fired.Should().Contain(propertyName);
    }

    [Fact]
    public void Setting_same_value_skips_redundant_notification()
    {
        var vm = new PillMultiSelectViewModel { PopupWidth = 400 };
        var fired = new List<string?>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        vm.PopupWidth = 400;

        fired.Should().NotContain(nameof(PillMultiSelectViewModel.PopupWidth));
    }
}

public class PillFormatterSetterTests
{
    [Fact]
    public void DisplayFormatter_setter_uses_equality_check_to_skip_redundant_updates()
    {
        var vm = new PillMultiSelectViewModel();
        vm.AddItem(new PillMultiSelectItemViewModel("A", "1") { IsSelected = true });

        Func<IReadOnlyList<PillMultiSelectItemViewModel>, string> fmt = _ => "x";
        vm.DisplayFormatter = fmt;

        var fired = new List<string?>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        vm.DisplayFormatter = fmt;

        fired.Should().NotContain(nameof(PillMultiSelectViewModel.DisplayFormatter));
        fired.Should().NotContain(nameof(PillMultiSelectViewModel.SelectedAbbreviationsText));
    }

    [Fact]
    public void TooltipFormatter_setter_raises_both_formatter_and_derived_property_changes()
    {
        var vm = new PillMultiSelectViewModel();
        vm.AddItem(new PillMultiSelectItemViewModel("A", "1") { IsSelected = true });

        var fired = new List<string?>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        vm.TooltipFormatter = PillTooltipFormatters.FullNames;

        fired.Should().Contain(nameof(PillMultiSelectViewModel.TooltipFormatter));
        fired.Should().Contain(nameof(PillMultiSelectViewModel.SelectionTooltip));
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
        var vm = new PillMultiSelectViewModel();
        vm.SearchPlaceholder.Should().Be("Search...");
        vm.ClearTooltip.Should().Be("Clear");
        vm.SelectAllText.Should().Be("Select all");
        vm.ResetText.Should().Be("Reset");
    }

    [Fact]
    public void Setter_overrides_resx_default_so_host_apps_can_skip_the_localization_dependency()
    {
        var vm = new PillMultiSelectViewModel
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
        var items = new List<PillMultiSelectItemViewModel>
        {
            new("A", "1") { IsSelected = true },
            new("B", "2") { IsSelected = true },
            new("C", "3") { IsSelected = true },
            new("D", "4") { IsSelected = true },
        };
        var options = new PillOverflowOptions
        {
            CollapseAfterEntries = 2,
            PlusMoreFormat = "and {0} others",
        };

        var result = PillOverflowFormatter.Format(items, options);

        result.Should().Be("A, B, and 2 others");
    }
}
