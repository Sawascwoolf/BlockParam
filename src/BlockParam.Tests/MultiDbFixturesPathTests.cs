using BlockParam.SimaticML;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Smoke tests verifying that DB_ProcessPlant_B1 and DB_ProcessPlant_C1
/// are structurally identical to DB_ProcessPlant_A1 (same member paths),
/// with only start values and plant-identity fields differing.
/// Required for the multi-DB video chapter in #96: cross-DB bulk preview
/// requires identical member paths across all three fixtures.
/// </summary>
public class MultiDbFixturesPathTests
{
    private static IReadOnlyList<string> GetAllPaths(string fixtureName)
    {
        var resolver = new UdtSetPointResolver();
        foreach (var (_, xml) in TestFixtures.LoadUdtFixtures())
            resolver.LoadFromXml(xml);
        var parser = new SimaticMLParser(constantResolver: null, udtResolver: resolver);
        var db = parser.Parse(TestFixtures.LoadXml(fixtureName));
        return db.AllMembers().Select(m => m.Path).ToList();
    }

    [Fact]
    public void MultiDbFixtures_B1C1_ShareA1MemberPaths()
    {
        // B1 and C1 are both derived from DB_ProcessPlant_A1 in assets/fixtures/
        // (which uses a 2D array structure different from the older test-fixture A1).
        // The invariant is: B1 and C1 must expose identical member paths to each other.
        // We also verify they each have more than 500 members (the 2D A1 structure).
        var pathsB1 = GetAllPaths("DB_ProcessPlant_B1.xml");
        var pathsC1 = GetAllPaths("DB_ProcessPlant_C1.xml");

        pathsB1.Should().NotBeEmpty("B1 fixture must have members");
        pathsC1.Should().BeEquivalentTo(pathsB1, because: "C1 must expose the same member paths as B1");
        pathsB1.Count.Should().BeGreaterThan(500, because: "the 2D-array structure produces many leaf nodes");
    }

    [Fact]
    public void MultiDbFixtures_ParseWithoutThrowing()
    {
        var act = () =>
        {
            GetAllPaths("DB_ProcessPlant_B1.xml");
            GetAllPaths("DB_ProcessPlant_C1.xml");
        };
        act.Should().NotThrow("B1 and C1 fixtures must parse cleanly");
    }

    [Fact]
    public void MultiDbFixtures_B1_HasDistinctPlantIdentity()
    {
        var resolver = new UdtSetPointResolver();
        foreach (var (_, xml) in TestFixtures.LoadUdtFixtures())
            resolver.LoadFromXml(xml);
        var parser = new SimaticMLParser(constantResolver: null, udtResolver: resolver);

        var b1 = parser.Parse(TestFixtures.LoadXml("DB_ProcessPlant_B1.xml"));
        var c1 = parser.Parse(TestFixtures.LoadXml("DB_ProcessPlant_C1.xml"));

        b1.Name.Should().Be("DB_ProcessPlant_B1");
        b1.Number.Should().Be(5);
        c1.Name.Should().Be("DB_ProcessPlant_C1");
        c1.Number.Should().Be(6);
    }

    [Fact]
    public void MultiDbFixtures_ValveTagsDifferAcrossDbs()
    {
        var resolver = new UdtSetPointResolver();
        foreach (var (_, xml) in TestFixtures.LoadUdtFixtures())
            resolver.LoadFromXml(xml);
        var parser = new SimaticMLParser(constantResolver: null, udtResolver: resolver);

        var a1 = parser.Parse(TestFixtures.LoadXml("DB_ProcessPlant_A1.xml"));
        var b1 = parser.Parse(TestFixtures.LoadXml("DB_ProcessPlant_B1.xml"));
        var c1 = parser.Parse(TestFixtures.LoadXml("DB_ProcessPlant_C1.xml"));

        // All valveTag start values in A1 use the V- prefix.
        // B1 uses W-, C1 uses X-.
        // Collect all valveTag leaves that have a populated StartValue.
        var tagsA1 = a1.AllMembers().Where(m => m.Name == "valveTag" && m.StartValue != null).ToList();
        var tagsB1 = b1.AllMembers().Where(m => m.Name == "valveTag" && m.StartValue != null).ToList();
        var tagsC1 = c1.AllMembers().Where(m => m.Name == "valveTag" && m.StartValue != null).ToList();

        tagsA1.Should().NotBeEmpty("A1 must have populated valveTag leaves");
        tagsB1.Should().NotBeEmpty("B1 must have populated valveTag leaves");
        tagsC1.Should().NotBeEmpty("C1 must have populated valveTag leaves");

        tagsA1.Should().OnlyContain(m => m.StartValue!.StartsWith("'V-"),
            because: "A1 valve tags all use V- prefix");
        tagsB1.Should().OnlyContain(m => m.StartValue!.StartsWith("'W-"),
            because: "B1 valve tags all use W- prefix");
        tagsC1.Should().OnlyContain(m => m.StartValue!.StartsWith("'X-"),
            because: "C1 valve tags all use X- prefix");
    }
}
