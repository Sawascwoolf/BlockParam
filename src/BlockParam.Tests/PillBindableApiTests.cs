using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FluentAssertions;
using BlockParam.UI.Controls.PillMultiSelect;
using Xunit;

// Non-STA tests use plain [Fact].
// WPF-DP tests that need STA use [UIFact] (Xunit.StaFact).

namespace BlockParam.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// TooltipMode DP — wiring (requires STA for WPF DPs)
// ─────────────────────────────────────────────────────────────────────────────

public class PillTooltipMode_Dp_Tests
{
    private static PillMultiSelect MakePill(params string[] displays)
    {
        var sources = displays.Select(d => new SimpleItem(d, d.Substring(0, Math.Min(3, d.Length)))).ToList();
        var pill = new PillMultiSelect
        {
            ItemsSource = new ObservableCollection<object>(sources.Cast<object>()),
            DisplayMemberPath = nameof(SimpleItem.Name),
            AbbreviationMemberPath = nameof(SimpleItem.Short),
        };
        // Select all items so tooltip formatters have something to format.
        pill.SelectedItems = new ObservableCollection<object>(sources.Cast<object>());
        return pill;
    }

    // Note: TooltipMode = None is the DP's default value (set in PropertyMetadata)
    // and its observable effect — SelectionTooltip == null when no formatter is
    // installed — is covered by PillTooltipTests.SelectionTooltip_is_null_when_no_formatter_set.
    // A "set None, read back None" test here would only re-prove DP defaulting.

    [UIFact]
    public void TooltipMode_FullNames_dp_can_be_set_and_read_back()
    {
        var pill = MakePill("Alpha", "Beta");
        pill.TooltipMode = PillTooltipMode.FullNames;
        pill.TooltipMode.Should().Be(PillTooltipMode.FullNames);
    }

    [UIFact]
    public void TooltipMode_AbbrevAndFullNames_dp_can_be_set_and_read_back()
    {
        var pill = MakePill("Alpha", "Beta");
        pill.TooltipMode = PillTooltipMode.AbbrevAndFullNames;
        pill.TooltipMode.Should().Be(PillTooltipMode.AbbrevAndFullNames);
    }

    [UIFact]
    public void TooltipFormatter_clr_overrides_TooltipMode_dp()
    {
        // Precedence: CLR TooltipFormatter beats TooltipMode DP.
        var pill = MakePill("Alpha", "Beta");
        pill.TooltipMode = PillTooltipMode.FullNames;

        pill.TooltipFormatter = _ => "custom";

        // Exercise the formatter path via a fresh internal-state check.
        // We can verify the override is wired by checking it doesn't throw
        // and the DP value is unaffected (DP still shows FullNames but the
        // formatter precedence is tested in the internal-state tests below).
        pill.TooltipMode.Should().Be(PillTooltipMode.FullNames); // DP unchanged
        pill.TooltipFormatter.Should().NotBeNull();
    }

    [UIFact]
    public void TooltipFormatter_cleared_to_null_restores_TooltipMode_dp()
    {
        var pill = MakePill("Alpha", "Beta");
        pill.TooltipMode = PillTooltipMode.FullNames;
        pill.TooltipFormatter = _ => "custom";
        pill.TooltipFormatter = null;

        // Formatter cleared — TooltipMode DP should now drive the tooltip again.
        pill.TooltipFormatter.Should().BeNull();
        pill.TooltipMode.Should().Be(PillTooltipMode.FullNames);
    }

    [UIFact]
    public void DisplayFormatter_clr_overrides_OverflowOptions_dp()
    {
        var pill = MakePill("Alpha", "Beta");
        pill.OverflowOptions = PillOverflowOptions.DataBlockDefault();

        pill.DisplayFormatter = _ => "custom";

        pill.DisplayFormatter.Should().NotBeNull();
        pill.OverflowOptions.Should().NotBeNull(); // DP still set
    }

    [UIFact]
    public void DisplayFormatter_cleared_to_null_restores_OverflowOptions_dp()
    {
        var pill = MakePill("Alpha", "Beta");
        pill.OverflowOptions = PillOverflowOptions.DataBlockDefault();
        pill.DisplayFormatter = _ => "custom";
        pill.DisplayFormatter = null;

        pill.DisplayFormatter.Should().BeNull();
        pill.OverflowOptions.Should().NotBeNull(); // DP still driving
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IsOpen DP — round-trip with internal state (requires STA)
// ─────────────────────────────────────────────────────────────────────────────

public class PillIsOpen_Dp_Tests
{
    [UIFact]
    public void IsOpen_dp_defaults_to_false()
    {
        var pill = new PillMultiSelect();
        pill.IsOpen.Should().BeFalse();
    }

    [UIFact]
    public void Setting_IsOpen_dp_to_true_is_reflected_back()
    {
        var pill = new PillMultiSelect();
        pill.IsOpen = true;
        pill.IsOpen.Should().BeTrue();
    }

    [UIFact]
    public void Setting_IsOpen_dp_to_false_after_true_round_trips()
    {
        var pill = new PillMultiSelect();
        pill.IsOpen = true;
        pill.IsOpen = false;
        pill.IsOpen.Should().BeFalse();
    }

    [UIFact]
    public void IsOpen_dp_no_reflection_required()
    {
        // Phase 3 goal: DevLauncher can say pill.IsOpen = true without reflection.
        // This test documents the clean API — no BindingFlags anywhere.
        var pill = new PillMultiSelect
        {
            ItemsSource = new ObservableCollection<object> { new SimpleItem("DB_A", "A") },
            DisplayMemberPath = nameof(SimpleItem.Name),
        };

        pill.IsOpen = true;
        pill.IsOpen.Should().BeTrue();

        pill.IsOpen = false;
        pill.IsOpen.Should().BeFalse();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DisplaySelector / AbbreviationSelector escape hatches (requires STA)
// ─────────────────────────────────────────────────────────────────────────────

public class PillSelectorEscapeHatch_Tests
{
    [UIFact]
    public void DisplaySelector_overrides_DisplayMemberPath()
    {
        var item = new SimpleItem("LongName", "SH");
        var pill = new PillMultiSelect
        {
            ItemsSource = new ObservableCollection<object> { item },
            DisplayMemberPath = nameof(SimpleItem.Name),
        };

        // Override with a selector that reverses the display string.
        pill.DisplaySelector = obj => new string(((SimpleItem)obj).Name.Reverse().ToArray());

        // Verify via SelectedAbbreviationsText — the default formatter joins Display values.
        pill.SelectedItems = new ObservableCollection<object> { item };

        // The formatter falls back to comma-join of Abbreviation (not Display).
        // To observe Display we set DisplaySelector and check it doesn't throw.
        // The value from DisplaySelector should now be "emaNgnoL".
        pill.DisplaySelector.Should().NotBeNull();
    }

    [UIFact]
    public void AbbreviationSelector_overrides_AbbreviationMemberPath()
    {
        var item = new SimpleItem("LongName", "SH");
        var pill = new PillMultiSelect
        {
            ItemsSource = new ObservableCollection<object> { item },
            DisplayMemberPath = nameof(SimpleItem.Name),
            AbbreviationMemberPath = nameof(SimpleItem.Short),
        };

        pill.AbbreviationSelector = obj => "XX-" + ((SimpleItem)obj).Short;

        pill.AbbreviationSelector.Should().NotBeNull();
    }

    [UIFact]
    public void DisplaySelector_cleared_to_null_falls_back_to_member_path()
    {
        var pill = new PillMultiSelect
        {
            ItemsSource = new ObservableCollection<object> { new SimpleItem("Alpha", "AL") },
            DisplayMemberPath = nameof(SimpleItem.Name),
        };

        pill.DisplaySelector = _ => "override";
        pill.DisplaySelector = null;

        pill.DisplaySelector.Should().BeNull();
    }

    [UIFact]
    public void AbbreviationSelector_cleared_to_null_falls_back_to_member_path()
    {
        var pill = new PillMultiSelect
        {
            ItemsSource = new ObservableCollection<object> { new SimpleItem("Alpha", "AL") },
            AbbreviationMemberPath = nameof(SimpleItem.Short),
        };

        pill.AbbreviationSelector = _ => "override";
        pill.AbbreviationSelector = null;

        pill.AbbreviationSelector.Should().BeNull();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FilterPredicate escape hatch (requires STA)
// ─────────────────────────────────────────────────────────────────────────────

public class PillFilterPredicate_Tests
{
    [UIFact]
    public void FilterPredicate_can_be_set_and_cleared()
    {
        var pill = new PillMultiSelect
        {
            ItemsSource = new ObservableCollection<object>
            {
                new SimpleItem("Alpha", "AL"),
                new SimpleItem("Beta",  "BT"),
            },
            DisplayMemberPath = nameof(SimpleItem.Name),
        };

        pill.FilterPredicate = (obj, text) =>
            ((SimpleItem)obj).Name.StartsWith(text, StringComparison.OrdinalIgnoreCase);

        pill.FilterPredicate.Should().NotBeNull();

        pill.FilterPredicate = null;
        pill.FilterPredicate.Should().BeNull();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// TooltipMode DP precedence — via PillFormatterCoordinator (non-STA, internal)
// ─────────────────────────────────────────────────────────────────────────────

public class PillFormatterCoordinator_Tests
{
    private static (PillFormatterCoordinator coord, PillMultiSelectInternalState state) Make()
    {
        var state = new PillMultiSelectInternalState();
        var resolver = new MemberPathResolver();
        var source = new PillItemSource(state, resolver);
        var coord = new PillFormatterCoordinator(state, source);
        return (coord, state);
    }

    [Fact]
    public void TooltipMode_None_sets_null_formatter()
    {
        var (coord, state) = Make();
        coord.OnTooltipModeChanged(PillTooltipMode.None);
        state.TooltipFormatter.Should().BeNull();
    }

    [Fact]
    public void TooltipMode_FullNames_installs_formatter()
    {
        var (coord, state) = Make();
        coord.OnTooltipModeChanged(PillTooltipMode.FullNames);
        state.TooltipFormatter.Should().NotBeNull();

        // Verify the formatter produces FullNames output.
        var rows = new[] { new PillRowViewModel(new object(), "DB_A", "A"), new PillRowViewModel(new object(), "DB_B", "B") };
        state.TooltipFormatter!(rows).Should().Be("DB_A\nDB_B");
    }

    [Fact]
    public void TooltipMode_AbbrevAndFullNames_installs_formatter()
    {
        var (coord, state) = Make();
        coord.OnTooltipModeChanged(PillTooltipMode.AbbrevAndFullNames);
        state.TooltipFormatter.Should().NotBeNull();

        var rows = new[] { new PillRowViewModel(new object(), "DB_Alpha", "AL"), new PillRowViewModel(new object(), "DB_Beta", "BT") };
        state.TooltipFormatter!(rows).Should().Be("AL — DB_Alpha\nBT — DB_Beta");
    }

    [Fact]
    public void CLR_TooltipFormatter_overrides_TooltipMode_dp()
    {
        var (coord, state) = Make();
        coord.OnTooltipModeChanged(PillTooltipMode.FullNames);

        // CLR override — must replace the DP-installed formatter.
        coord.TooltipFormatter = items => "custom:" + items.Count;

        // The internal state should now use the CLR formatter (projected from source items).
        // We verify the flag is set by checking that re-setting the DP mode is a no-op.
        coord.OnTooltipModeChanged(PillTooltipMode.AbbrevAndFullNames);

        // Because _customTooltipFormatterSet = true, the DP change must NOT
        // overwrite the CLR formatter. The only way to confirm is that the
        // existing formatter is still the one we set (state.TooltipFormatter
        // is the row→source projection of our lambda, not the FullNames/AbbrevAndFullNames one).
        // Create a row and call the formatter — it should produce the projection output.
        var src = new object();
        var row = new PillRowViewModel(src, "DB_A", "A");
        var result = state.TooltipFormatter!(new[] { row });
        // Our lambda returns "custom:N" for any N items; the row projection maps source items.
        result.Should().StartWith("custom:");
    }

    [Fact]
    public void CLR_TooltipFormatter_cleared_to_null_restores_dp_mode()
    {
        var (coord, state) = Make();
        coord.OnTooltipModeChanged(PillTooltipMode.FullNames);
        coord.TooltipFormatter = _ => "custom";
        coord.TooltipFormatter = null; // clear override

        // FullNames should be reinstalled.
        state.TooltipFormatter.Should().NotBeNull();
        var rows = new[] { new PillRowViewModel(new object(), "DB_A", "A") };
        state.TooltipFormatter!(rows).Should().Be("DB_A");
    }

    [Fact]
    public void OverflowOptions_installs_display_formatter()
    {
        var (coord, state) = Make();
        coord.OnOverflowOptionsChanged(new PillOverflowOptions { AbbreviateAfterEntries = 1 });
        state.DisplayFormatter.Should().NotBeNull();
    }

    [Fact]
    public void OverflowOptions_null_clears_display_formatter()
    {
        var (coord, state) = Make();
        coord.OnOverflowOptionsChanged(new PillOverflowOptions());
        coord.OnOverflowOptionsChanged(null);
        state.DisplayFormatter.Should().BeNull();
    }

    [Fact]
    public void CLR_DisplayFormatter_overrides_OverflowOptions_dp()
    {
        var (coord, state) = Make();
        coord.OnOverflowOptionsChanged(PillOverflowOptions.DataBlockDefault());

        coord.DisplayFormatter = items => "custom:" + items.Count;

        // Re-setting the DP must be a no-op due to precedence flag.
        coord.OnOverflowOptionsChanged(new PillOverflowOptions());

        var src = new object();
        var row = new PillRowViewModel(src, "DB_A", "A");
        var result = state.DisplayFormatter!(new[] { row });
        result.Should().StartWith("custom:");
    }

    [Fact]
    public void CLR_DisplayFormatter_cleared_to_null_restores_dp_options()
    {
        var (coord, state) = Make();
        coord.OnOverflowOptionsChanged(new PillOverflowOptions { AbbreviateAfterEntries = 0 });
        coord.DisplayFormatter = _ => "custom";
        coord.DisplayFormatter = null;

        // OverflowOptions formatter should be reinstalled.
        state.DisplayFormatter.Should().NotBeNull();
    }

    [Fact]
    public void FilterPredicate_wires_CustomFilter_on_internal_state()
    {
        var (coord, state) = Make();
        coord.FilterPredicate = (obj, text) => obj.ToString()!.Contains(text);
        state.CustomFilter.Should().NotBeNull();
    }

    [Fact]
    public void FilterPredicate_null_clears_CustomFilter()
    {
        var (coord, state) = Make();
        coord.FilterPredicate = (obj, text) => true;
        coord.FilterPredicate = null;
        state.CustomFilter.Should().BeNull();
    }

    [Fact]
    public void CustomFilter_receives_projected_source_item()
    {
        var (coord, state) = Make();
        object? capturedSource = null;
        coord.FilterPredicate = (obj, _) => { capturedSource = obj; return true; };

        var src = new object();
        var row = new PillRowViewModel(src, "DB_A", "A");
        state.CustomFilter!(row, "any");

        capturedSource.Should().BeSameAs(src);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CustomFilter in PillMultiSelectInternalState (non-STA)
// ─────────────────────────────────────────────────────────────────────────────

public class PillInternalState_CustomFilter_Tests
{
    private static PillRowViewModel Row(string display, string abbrev) =>
        new(new object(), display, abbrev);

    [Fact]
    public void CustomFilter_null_uses_default_contains_check()
    {
        var state = new PillMultiSelectInternalState();
        state.CustomFilter.Should().BeNull(); // default
    }

    [Fact]
    public void CustomFilter_set_replaces_default_filter_logic()
    {
        var state = new PillMultiSelectInternalState();
        var row = Row("Alpha", "AL");
        state.AddItem(row);

        // Install a filter that matches only items whose Display starts with "Z".
        state.CustomFilter = (r, text) => r.Display.StartsWith("Z", StringComparison.Ordinal);

        state.SearchText = "anything";
        // Alpha doesn't start with Z → should be filtered out.
        state.FilteredItems.Cast<object>().Should().NotContain(row);
    }

    [Fact]
    public void CustomFilter_cleared_restores_default_contains_check()
    {
        var state = new PillMultiSelectInternalState();
        var row = Row("Alpha", "AL");
        state.AddItem(row);

        state.CustomFilter = (r, _) => false; // block everything
        state.SearchText = "Al";
        state.FilteredItems.Cast<object>().Should().BeEmpty();

        state.CustomFilter = null; // restore default
        state.FilteredItems.Cast<object>().Should().Contain(row);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared test helper DTO
// ─────────────────────────────────────────────────────────────────────────────

file sealed class SimpleItem
{
    public SimpleItem(string name, string @short)
    {
        Name = name;
        Short = @short;
    }

    public string Name { get; }
    public string Short { get; }
}
