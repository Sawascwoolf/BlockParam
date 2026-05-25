using BlockParam.Services;
using BlockParam.Services.Storage;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests.Storage;

/// <summary>
/// Regression tests for <see cref="TagTableDirectoryProbe"/>, the helper
/// that pulled the three tag-table-cache <c>System.IO</c> calls out of
/// <see cref="UI.BulkChangeViewModel"/>. Exercises the in-memory storage
/// path so the BulkChangeViewModel hotspot can't silently regress (#85).
/// </summary>
public class TagTableDirectoryProbeTests
{
    private static StoragePath TagDir =>
        StoragePath.FromAbsolute(@"C:\bp\temp") / "TagTables";

    [Fact]
    public void Exists_false_when_path_null_empty_or_whitespace()
    {
        var probe = new TagTableDirectoryProbe(new InMemoryBlockParamStorage());

        probe.Exists(null).Should().BeFalse();
        probe.Exists("").Should().BeFalse();
        probe.Exists("   ").Should().BeFalse();
    }

    [Fact]
    public void Exists_reflects_storage_state()
    {
        var fs = new InMemoryBlockParamStorage();
        var probe = new TagTableDirectoryProbe(fs);

        probe.Exists(TagDir.FullPath).Should().BeFalse();
        fs.EnsureDirectory(TagDir);
        probe.Exists(TagDir.FullPath).Should().BeTrue();
    }

    [Fact]
    public void GetNewestXmlWriteTime_null_when_directory_missing()
    {
        var probe = new TagTableDirectoryProbe(new InMemoryBlockParamStorage());
        probe.GetNewestXmlWriteTime(TagDir.FullPath).Should().BeNull();
    }

    [Fact]
    public void GetNewestXmlWriteTime_null_when_directory_empty()
    {
        var fs = new InMemoryBlockParamStorage();
        fs.EnsureDirectory(TagDir);

        var probe = new TagTableDirectoryProbe(fs);
        probe.GetNewestXmlWriteTime(TagDir.FullPath).Should().BeNull();
    }

    [Fact]
    public void GetNewestXmlWriteTime_returns_most_recent_xml_write_only()
    {
        var fs = new InMemoryBlockParamStorage();
        fs.WriteAllText(TagDir / "old.xml", "");
        fs.SetLastWriteTime(TagDir / "old.xml", new DateTime(2020, 1, 1));
        fs.WriteAllText(TagDir / "new.xml", "");
        fs.SetLastWriteTime(TagDir / "new.xml", new DateTime(2026, 5, 24));
        // Non-xml file must be ignored even if newer.
        fs.WriteAllText(TagDir / "ignore.txt", "");
        fs.SetLastWriteTime(TagDir / "ignore.txt", new DateTime(2030, 1, 1));

        var probe = new TagTableDirectoryProbe(fs);
        probe.GetNewestXmlWriteTime(TagDir.FullPath)
            .Should().Be(new DateTime(2026, 5, 24));
    }
}
