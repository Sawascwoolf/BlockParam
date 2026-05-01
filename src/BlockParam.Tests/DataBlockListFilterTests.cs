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
    public void Filter_MatchesFolderPath()
    {
        var sorted = DataBlockListFilter.Sort(SampleProjectTree());

        // Folder "Recipe" surfaces both DBs in that folder.
        DataBlockListFilter.Filter(sorted, "Recipe").Select(b => b.Name).Should().BeEquivalentTo(
            new[] { "DB_Unit_B", "DB_Unit_C" });
    }
}
