using BlockParam.Models;
using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class DataBlockListFilterTests
{
    private static IReadOnlyList<DataBlockSummary> SampleProjectTree() => new[]
    {
        new DataBlockSummary("DB_Unit_C", "Recipe"),
        new DataBlockSummary("DB_Unit_A", ""),
        new DataBlockSummary("DB_Unit_B", "Recipe"),
        new DataBlockSummary("DB_FB_Pump1", "InstanceDBs", "InstanceDB", isInstanceDb: true),
        new DataBlockSummary("DB_Sensors", "Hardware/IO"),
    };

    [Fact]
    public void Sort_OrdersByFolderThenName()
    {
        var sorted = DataBlockListFilter.Sort(SampleProjectTree());

        sorted.Select(b => b.Name).Should().Equal(
            "DB_Unit_A",      // root (empty folder) first
            "DB_Sensors",     // Hardware/IO
            "DB_FB_Pump1",    // InstanceDBs
            "DB_Unit_B",      // Recipe
            "DB_Unit_C");     // Recipe
    }

    [Fact]
    public void Filter_EmptyQuery_ReturnsAll()
    {
        var sorted = DataBlockListFilter.Sort(SampleProjectTree());

        DataBlockListFilter.Filter(sorted, "").Should().HaveCount(5);
        DataBlockListFilter.Filter(sorted, "   ").Should().HaveCount(5);
        DataBlockListFilter.Filter(sorted, null).Should().HaveCount(5);
    }

    [Fact]
    public void Filter_MatchesNameCaseInsensitive()
    {
        var sorted = DataBlockListFilter.Sort(SampleProjectTree());

        DataBlockListFilter.Filter(sorted, "unit").Select(b => b.Name).Should().BeEquivalentTo(
            new[] { "DB_Unit_A", "DB_Unit_B", "DB_Unit_C" });
    }

    [Fact]
    public void Filter_DoesNotMatchFolderPath()
    {
        // Folder paths are deliberately not searched. PLC programmers rarely
        // type fragments of the project tree, and matching path noise hurts
        // the common name-or-number case.
        var sorted = DataBlockListFilter.Sort(SampleProjectTree());
        DataBlockListFilter.Filter(sorted, "Recipe").Should().BeEmpty();
    }

    [Fact]
    public void Filter_MatchesDbNumberFormatted()
    {
        // PLC programmers reference DBs as "DB17". The filter must surface
        // the right block whether the user types the number alone or with
        // the "DB" prefix.
        var blocks = new[]
        {
            new DataBlockSummary("DB_Foo", "", number: 1),
            new DataBlockSummary("DB_Recipe", "", number: 17),
            new DataBlockSummary("DB_Misc", "", number: 23),
        };

        DataBlockListFilter.Filter(blocks, "DB17").Select(b => b.Name).Should().Equal("DB_Recipe");
        DataBlockListFilter.Filter(blocks, "db17").Select(b => b.Name).Should().Equal("DB_Recipe");
        DataBlockListFilter.Filter(blocks, "17").Select(b => b.Name).Should().Equal("DB_Recipe");
    }

    [Fact]
    public void Filter_BareDigitOnlyMatchesNumber_NotNameSubstring()
    {
        // A name like "DB_Tank1" must not be a hit for the query "1" — the
        // bare-digit form is reserved for numeric matches so a single key
        // press doesn't drown the picker in spurious hits.
        var blocks = new[]
        {
            new DataBlockSummary("DB_Tank1", "", number: 5),
            new DataBlockSummary("DB_Other", "", number: 1),
        };

        DataBlockListFilter.Filter(blocks, "1").Select(b => b.Name).Should().Equal("DB_Other");
    }
}
