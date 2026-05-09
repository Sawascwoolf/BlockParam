using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using FluentAssertions;
using BlockParam.UI.Controls.PillMultiSelect;
using Xunit;

namespace BlockParam.Tests;

public class PillOverflowFormatterTests
{
    public PillOverflowFormatterTests()
    {
        // The "+N more" string is localized; pin to en-US so assertions
        // stay stable on a German developer machine where the resx falls
        // back to "+N weitere".
        var en = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = en;
        Thread.CurrentThread.CurrentUICulture = en;
    }

    private static PillMultiSelectItemViewModel Item(string display, string abbrev, bool selected = true)
    {
        var vm = new PillMultiSelectItemViewModel(display, abbrev);
        vm.IsSelected = selected;
        return vm;
    }

    private static IReadOnlyList<PillMultiSelectItemViewModel> Items(params (string Display, string Abbrev)[] data)
    {
        var list = new List<PillMultiSelectItemViewModel>();
        foreach (var (d, a) in data) list.Add(Item(d, a));
        return list;
    }

    [Fact]
    public void Empty_selection_yields_empty_string()
    {
        var result = PillOverflowFormatter.Format(Items(), new PillOverflowOptions());
        result.Should().BeEmpty();
    }

    [Fact]
    public void No_thresholds_set_renders_full_displays_joined()
    {
        var items = Items(
            ("DB_ProcessControl_HighPriority", "DB10"),
            ("DB_ConfigParams", "DB99"));
        var result = PillOverflowFormatter.Format(items, new PillOverflowOptions());
        result.Should().Be("DB_ProcessControl_HighPriority, DB_ConfigParams");
    }

    [Fact]
    public void Below_entries_threshold_renders_full_displays()
    {
        var items = Items(("DB_A", "DB1"), ("DB_B", "DB2"));
        var result = PillOverflowFormatter.Format(items, new PillOverflowOptions
        {
            AbbreviateAfterEntries = 2, // 2 is NOT > 2
        });
        result.Should().Be("DB_A, DB_B");
    }

    [Fact]
    public void Above_entries_threshold_switches_to_abbreviations()
    {
        var items = Items(("DB_A", "DB1"), ("DB_B", "DB2"), ("DB_C", "DB3"));
        var result = PillOverflowFormatter.Format(items, new PillOverflowOptions
        {
            AbbreviateAfterEntries = 2, // 3 > 2
        });
        result.Should().Be("DB1, DB2, DB3");
    }

    [Fact]
    public void Above_chars_threshold_switches_to_abbreviations()
    {
        // Joined full = "DB_ProcessControl_HighPriority, DB_ConfigParams" = 49 chars
        var items = Items(
            ("DB_ProcessControl_HighPriority", "DB10"),
            ("DB_ConfigParams", "DB99"));
        var result = PillOverflowFormatter.Format(items, new PillOverflowOptions
        {
            AbbreviateAfterChars = 30,
        });
        result.Should().Be("DB10, DB99");
    }

    [Fact]
    public void Below_chars_threshold_keeps_full_displays()
    {
        // Joined full = "A, B" = 4 chars
        var items = Items(("A", "X"), ("B", "Y"));
        var result = PillOverflowFormatter.Format(items, new PillOverflowOptions
        {
            AbbreviateAfterChars = 30,
        });
        result.Should().Be("A, B");
    }

    [Fact]
    public void Either_threshold_triggers_abbreviation_OR_semantics()
    {
        var items = Items(("DB_A", "DB1"), ("DB_B", "DB2"), ("DB_C", "DB3"));
        // Char threshold won't trip ("DB_A, DB_B, DB_C" = 16 chars), but
        // entries threshold will (3 > 2). Verifies OR semantics.
        var result = PillOverflowFormatter.Format(items, new PillOverflowOptions
        {
            AbbreviateAfterEntries = 2,
            AbbreviateAfterChars = 100,
        });
        result.Should().Be("DB1, DB2, DB3");
    }

    [Fact]
    public void Collapse_threshold_truncates_with_localized_more_suffix()
    {
        var items = Items(
            ("A", "X"), ("B", "Y"), ("C", "Z"), ("D", "W"), ("E", "V"));
        var result = PillOverflowFormatter.Format(items, new PillOverflowOptions
        {
            CollapseAfterEntries = 3,
        });
        result.Should().Be("A, B, C, +2 more");
    }

    [Fact]
    public void Collapse_does_not_trigger_when_count_equals_threshold()
    {
        var items = Items(("A", "X"), ("B", "Y"), ("C", "Z"));
        var result = PillOverflowFormatter.Format(items, new PillOverflowOptions
        {
            CollapseAfterEntries = 3, // 3 is NOT > 3
        });
        result.Should().Be("A, B, C");
    }

    [Fact]
    public void Abbreviation_and_collapse_can_both_apply_to_same_selection()
    {
        // 6 selected: count > 4 trips abbrev, count > 5 trips collapse.
        // Matches the 05_db_count_overflow.png screenshot scenario.
        var items = Items(
            ("DB_ProcessControl_HighPriority", "DB10"),
            ("DB_ProcessControl_LowPriority", "DB11"),
            ("DB_PumpStation_001", "DB42"),
            ("DB_ConfigParams", "DB99"),
            ("DB_DiagnosticData", "DB100"),
            ("DB_TankSettings_3", "DB200"));

        var result = PillOverflowFormatter.Format(items, PillOverflowOptions.DataBlockDefault());
        result.Should().Be("DB10, DB11, DB42, DB99, DB100, +1 more");
    }

    [Fact]
    public void DataBlockDefault_keeps_short_selection_as_full_names()
    {
        var items = Items(("DB_Short", "DB1"));
        var result = PillOverflowFormatter.Format(items, PillOverflowOptions.DataBlockDefault());
        result.Should().Be("DB_Short");
    }

    [Fact]
    public void DataBlockDefault_chars_threshold_trips_before_count_threshold()
    {
        // 3 long names: well under count=4 limit, but joined ~65 chars > 30.
        var items = Items(
            ("DB_ProcessControl_HighPriority", "DB10"),
            ("DB_PumpStation_001", "DB42"),
            ("DB_DiagnosticData", "DB100"));

        var result = PillOverflowFormatter.Format(items, PillOverflowOptions.DataBlockDefault());
        result.Should().Be("DB10, DB42, DB100");
    }

    [Fact]
    public void Char_threshold_uses_actual_separator_overhead()
    {
        // Three 5-char items joined = "AAAAA, BBBBB, CCCCC" = 5*3 + 2*2 = 19 chars.
        // Threshold of 18 should trip; threshold of 20 should not.
        var items = Items(("AAAAA", "1"), ("BBBBB", "2"), ("CCCCC", "3"));

        PillOverflowFormatter.Format(items, new PillOverflowOptions { AbbreviateAfterChars = 18 })
            .Should().Be("1, 2, 3");
        PillOverflowFormatter.Format(items, new PillOverflowOptions { AbbreviateAfterChars = 20 })
            .Should().Be("AAAAA, BBBBB, CCCCC");
    }
}
