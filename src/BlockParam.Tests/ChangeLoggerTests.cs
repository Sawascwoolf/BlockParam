using FluentAssertions;
using BlockParam.Services;
using Xunit;

namespace BlockParam.Tests;

public class ChangeLoggerTests
{
    [Fact]
    public void Log_SingleChange_EntryCreated()
    {
        var logger = new ChangeLogger();

        logger.Log(CreateEntry("Speed", "1500", "2000"));

        logger.Entries.Should().HaveCount(1);
    }

    [Fact]
    public void Log_BulkChange_AllEntriesRecorded()
    {
        var logger = new ChangeLogger();

        logger.Log(CreateEntry("ModuleId", "0", "42", "Drive1.Msg1.ModuleId"));
        logger.Log(CreateEntry("ModuleId", "0", "42", "Drive1.Msg2.ModuleId"));
        logger.Log(CreateEntry("ModuleId", "0", "42", "Sensor1.Msg1.ModuleId"));

        logger.Entries.Should().HaveCount(3);
    }

    [Fact]
    public void Log_Format_Readable()
    {
        var logger = new ChangeLogger();
        logger.Log(CreateEntry("Speed", "1500", "2000"));

        var formatted = logger.FormatLog();

        formatted.Should().Contain("Speed");
        formatted.Should().Contain("1500");
        formatted.Should().Contain("2000");
        formatted.Should().Contain("TestDB");
    }

    [Fact]
    public void Log_ContainsOldAndNewValue()
    {
        var logger = new ChangeLogger();
        logger.Log(CreateEntry("Speed", "1500", "2000"));

        var entry = logger.Entries[0];
        entry.OldValue.Should().Be("1500");
        entry.NewValue.Should().Be("2000");
        entry.MemberPath.Should().Be("Speed");
        entry.DbName.Should().Be("TestDB");
    }

    [Fact]
    public void Log_Sink_CalledForEachEntry()
    {
        var sinkEntries = new List<ChangeLogEntry>();
        var logger = new ChangeLogger(entry => sinkEntries.Add(entry));

        logger.Log(CreateEntry("A", "0", "1"));
        logger.Log(CreateEntry("B", "0", "2"));

        sinkEntries.Should().HaveCount(2);
    }

    private static ChangeLogEntry CreateEntry(
        string member, string oldVal, string newVal,
        string? path = null)
    {
        return new ChangeLogEntry(
            DateTime.UtcNow, "TestDB", path ?? member, "Int", oldVal, newVal, "DB Root");
    }
}
