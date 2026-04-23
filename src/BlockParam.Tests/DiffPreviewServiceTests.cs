using FluentAssertions;
using BlockParam.Services;
using BlockParam.SimaticML;
using Xunit;

namespace BlockParam.Tests;

public class DiffPreviewServiceTests
{
    private readonly SimaticMLParser _parser = new();
    private readonly DiffPreviewService _service = new();

    [Fact]
    public void ComputeDiff_AllDifferent_AllMarkedChanged()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var members = db.AllMembers().Where(m => m.Name == "ModuleId").ToList();

        var diff = _service.ComputeDiff(db, members, "99");

        diff.Should().HaveCount(4);
        diff.Where(d => d.OldValue == "42").Should().OnlyContain(d => d.IsChanged);
    }

    [Fact]
    public void ComputeDiff_SomeAlreadyCorrect_MarkedUnchanged()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var members = db.AllMembers().Where(m => m.Name == "ModuleId").ToList();

        // Set to same value that already exists (42)
        var diff = _service.ComputeDiff(db, members, "42");

        diff.Should().OnlyContain(d => !d.IsChanged);
    }

    [Fact]
    public void ComputeDiff_NoneChanged_ZeroChangeCount()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var members = db.AllMembers().Where(m => m.Name == "ModuleId").ToList();

        var diff = _service.ComputeDiff(db, members, "42");

        _service.CountChanges(diff).Should().Be(0);
    }

    [Fact]
    public void ComputeDiff_PreservesPath()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        var members = db.AllMembers().Where(m => m.Name == "ModuleId").ToList();

        var diff = _service.ComputeDiff(db, members, "99");

        diff.Should().Contain(d => d.MemberPath == "Drive1.Msg_CommError.ModuleId");
        diff.Should().Contain(d => d.MemberPath == "Sensor1.Msg_Overtemp.ModuleId");
    }

    [Fact]
    public void ComputeDiff_MixedChanges_CorrectCount()
    {
        var db = _parser.Parse(TestFixtures.LoadXml("udt-instances-db.xml"));
        // ElementId: Drive1 members have "1", Sensor1 members have "2"
        var members = db.AllMembers().Where(m => m.Name == "ElementId").ToList();

        var diff = _service.ComputeDiff(db, members, "1");

        // 2 already "1" (unchanged), 2 are "2" (changed)
        _service.CountChanges(diff).Should().Be(2);
    }
}
