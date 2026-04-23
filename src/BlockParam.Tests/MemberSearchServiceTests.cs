using FluentAssertions;
using BlockParam.Services;
using BlockParam.SimaticML;
using Xunit;

namespace BlockParam.Tests;

public class MemberSearchServiceTests
{
    private readonly SimaticMLParser _parser = new();
    private readonly MemberSearchService _search = new();

    [Fact]
    public void Search_ByName_FindsMatches()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var result = _search.Search(db, "ModuleId");

        result.HitCount.Should().Be(4);
        result.Matches.Should().OnlyContain(m => m.Name == "ModuleId");
    }

    [Fact]
    public void Search_ByValue_FindsMatches()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var result = _search.Search(db, "42");

        result.Matches.Should().Contain(m => m.StartValue == "42");
    }

    [Fact]
    public void Search_ByPath_FindsMatches()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var result = _search.Search(db, "Drive1.Msg");

        result.Matches.Should().OnlyContain(m => m.Path.Contains("Drive1.Msg"));
    }

    [Fact]
    public void Search_CaseInsensitive()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var upper = _search.Search(db, "MODULEID");
        var lower = _search.Search(db, "moduleid");

        upper.HitCount.Should().Be(lower.HitCount);
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsAll()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var result = _search.Search(db, "");

        result.IsEmpty.Should().BeTrue();
        result.HitCount.Should().Be(db.AllMembers().Count());
    }

    [Fact]
    public void Search_NoMatches_ReturnsEmpty()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var result = _search.Search(db, "xyz_nonexistent_123");

        result.HitCount.Should().Be(0);
    }

    [Fact]
    public void Search_ByDatatype_FindsMatches()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("mixed-types-db.xml"));
        var result = _search.Search(db, "Real");

        result.Matches.Should().Contain(m => m.Datatype == "Real");
    }
}
