using BlockParam.DevLauncher;
using FluentAssertions;

namespace BlockParam.Tests;

/// <summary>
/// Tests for <see cref="Program.EnumerateDevLauncherDbs"/> and
/// <see cref="Program.ResolveFixturePath"/> — the peer-fixture seeding
/// infrastructure in the DevLauncher (issue #182, item 4).
/// </summary>
public sealed class DevLauncherDbEnumerationTests : IDisposable
{
    private readonly string _tempDir;

    public DevLauncherDbEnumerationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BlockParam_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ───────────── EnumerateDevLauncherDbs ─────────────

    [Fact]
    public void NonExistent_directory_returns_empty()
    {
        var result = Program.EnumerateDevLauncherDbs(
            Path.Combine(_tempDir, "does_not_exist"), anchorPlc: null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void GlobalDB_fixture_is_parsed()
    {
        WriteFixture("MyDB.xml", globalDb: true, number: 42);

        var result = Program.EnumerateDevLauncherDbs(_tempDir, anchorPlc: null);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("MyDB");
        result[0].Number.Should().Be(42);
        result[0].IsInstanceDb.Should().BeFalse();
    }

    [Fact]
    public void InstanceDB_fixture_is_parsed()
    {
        WriteFixture("InstDB.xml", globalDb: false, number: 7);

        var result = Program.EnumerateDevLauncherDbs(_tempDir, anchorPlc: null);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("InstDB");
        result[0].IsInstanceDb.Should().BeTrue();
    }

    [Fact]
    public void PlcPrefix_double_underscore_splits_into_PlcName_and_DbName()
    {
        WriteFixture("Plant_A__DB_Valves.xml", globalDb: true, number: 10);

        var result = Program.EnumerateDevLauncherDbs(_tempDir, anchorPlc: null);

        result.Should().HaveCount(1);
        result[0].PlcName.Should().Be("Plant_A");
        result[0].Name.Should().Be("DB_Valves");
    }

    [Fact]
    public void Single_underscore_in_name_is_not_treated_as_separator()
    {
        WriteFixture("DB_Stash_Test.xml", globalDb: true, number: 1);

        var result = Program.EnumerateDevLauncherDbs(_tempDir, anchorPlc: null);

        result.Should().HaveCount(1);
        result[0].PlcName.Should().BeEmpty();
        result[0].Name.Should().Be("DB_Stash_Test");
    }

    [Fact]
    public void Unprefixed_file_inherits_anchorPlc()
    {
        WriteFixture("DB_Unit.xml", globalDb: true, number: 3);

        var result = Program.EnumerateDevLauncherDbs(_tempDir, anchorPlc: "PLC_1");

        result.Should().HaveCount(1);
        result[0].PlcName.Should().Be("PLC_1");
        result[0].Name.Should().Be("DB_Unit");
    }

    [Fact]
    public void PlcPrefixed_file_ignores_anchorPlc()
    {
        WriteFixture("Plant_B__DB_Motor.xml", globalDb: true, number: 5);

        var result = Program.EnumerateDevLauncherDbs(_tempDir, anchorPlc: "PLC_1");

        result.Should().HaveCount(1);
        result[0].PlcName.Should().Be("Plant_B");
    }

    [Fact]
    public void Modified_xml_artifacts_are_excluded()
    {
        WriteFixture("DB_Real.xml", globalDb: true, number: 1);
        WriteFixture("DB_Real_modified.xml", globalDb: true, number: 1);

        var result = Program.EnumerateDevLauncherDbs(_tempDir, anchorPlc: null);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("DB_Real");
    }

    [Fact]
    public void NonDb_xml_file_is_skipped()
    {
        File.WriteAllText(
            Path.Combine(_tempDir, "NotADb.xml"),
            "<?xml version=\"1.0\"?><Root><Something/></Root>");

        var result = Program.EnumerateDevLauncherDbs(_tempDir, anchorPlc: null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void BlockNumber_missing_from_xml_yields_null_number()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <SW.Blocks.GlobalDB ID=""0"">
    <AttributeList>
      <Name>NoNumber</Name>
    </AttributeList>
  </SW.Blocks.GlobalDB>
</Document>";
        File.WriteAllText(Path.Combine(_tempDir, "NoNumber.xml"), xml);

        var result = Program.EnumerateDevLauncherDbs(_tempDir, anchorPlc: null);

        result.Should().HaveCount(1);
        result[0].Number.Should().BeNull();
    }

    [Fact]
    public void Multiple_fixtures_all_enumerated()
    {
        WriteFixture("DB_A.xml", globalDb: true, number: 1);
        WriteFixture("DB_B.xml", globalDb: true, number: 2);
        WriteFixture("Plant_X__DB_C.xml", globalDb: false, number: 3);

        var result = Program.EnumerateDevLauncherDbs(_tempDir, anchorPlc: "Anchor");

        result.Should().HaveCount(3);
        result.Select(r => r.Name).Should().BeEquivalentTo("DB_A", "DB_B", "DB_C");
    }

    // ───────────── ResolveFixturePath ─────────────

    [Fact]
    public void ResolveFixturePath_prefers_prefixed_file()
    {
        WriteFixture("Plant_A__DB_X.xml", globalDb: true, number: 1);
        WriteFixture("DB_X.xml", globalDb: true, number: 1);

        var summary = new Models.DataBlockSummary("DB_X", "", plcName: "Plant_A");
        var result = Program.ResolveFixturePath(_tempDir, summary);

        result.Should().NotBeNull();
        Path.GetFileName(result!).Should().Be("Plant_A__DB_X.xml");
    }

    [Fact]
    public void ResolveFixturePath_falls_back_to_bare_name()
    {
        WriteFixture("DB_Y.xml", globalDb: true, number: 1);

        var summary = new Models.DataBlockSummary("DB_Y", "", plcName: "Plant_B");
        var result = Program.ResolveFixturePath(_tempDir, summary);

        result.Should().NotBeNull();
        Path.GetFileName(result!).Should().Be("DB_Y.xml");
    }

    [Fact]
    public void ResolveFixturePath_returns_null_when_not_found()
    {
        var summary = new Models.DataBlockSummary("DB_Ghost", "", plcName: "PLC");
        var result = Program.ResolveFixturePath(_tempDir, summary);

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveFixturePath_bare_name_when_no_plc()
    {
        WriteFixture("DB_Bare.xml", globalDb: true, number: 1);

        var summary = new Models.DataBlockSummary("DB_Bare", "", plcName: "");
        var result = Program.ResolveFixturePath(_tempDir, summary);

        result.Should().NotBeNull();
        Path.GetFileName(result!).Should().Be("DB_Bare.xml");
    }

    // ───────────── helpers ─────────────

    private void WriteFixture(string fileName, bool globalDb, int number)
    {
        var blockTag = globalDb ? "SW.Blocks.GlobalDB" : "SW.Blocks.InstanceDB";
        var xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <{blockTag} ID=""0"">
    <AttributeList>
      <Name>{Path.GetFileNameWithoutExtension(fileName)}</Name>
      <Number>{number}</Number>
    </AttributeList>
  </{blockTag}>
</Document>";
        File.WriteAllText(Path.Combine(_tempDir, fileName), xml);
    }
}
