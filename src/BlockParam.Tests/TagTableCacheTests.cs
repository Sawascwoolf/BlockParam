using FluentAssertions;
using NSubstitute;
using BlockParam.Models;
using BlockParam.Services;
using Xunit;

namespace BlockParam.Tests;

public class TagTableCacheTests
{
    [Fact]
    public void GetEntries_ReturnsFromReader()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.ReadTagTable("Modules").Returns(new[]
        {
            new TagTableEntry("MOD_1", "42", "Int", "Module 1")
        });
        var cache = new TagTableCache(reader);

        var entries = cache.GetEntries("Modules");

        entries.Should().HaveCount(1);
        entries[0].Name.Should().Be("MOD_1");
        entries[0].Value.Should().Be("42");
    }

    [Fact]
    public void GetEntries_NonExistent_ReturnsEmpty()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.ReadTagTable("NonExistent").Returns(Array.Empty<TagTableEntry>());
        var cache = new TagTableCache(reader);

        cache.GetEntries("NonExistent").Should().BeEmpty();
    }

    [Fact]
    public void Cache_SecondCall_NoReread()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.ReadTagTable("Modules").Returns(new[]
        {
            new TagTableEntry("MOD_1", "42", "Int")
        });
        var cache = new TagTableCache(reader);

        cache.GetEntries("Modules");
        cache.GetEntries("Modules");

        reader.Received(1).ReadTagTable("Modules");
    }

    [Fact]
    public void Cache_Invalidate_ForcesReread()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.ReadTagTable("Modules").Returns(new[]
        {
            new TagTableEntry("MOD_1", "42", "Int")
        });
        var cache = new TagTableCache(reader);

        cache.GetEntries("Modules");
        cache.Invalidate();
        cache.GetEntries("Modules");

        reader.Received(2).ReadTagTable("Modules");
    }

    [Fact]
    public void GetTableNames_ReturnsFromReader()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.GetTagTableNames().Returns(new[] { "Modules", "Elements", "Messages" });
        var cache = new TagTableCache(reader);

        var names = cache.GetTableNames();

        names.Should().HaveCount(3);
        names.Should().Contain("Modules");
    }

    [Fact]
    public void GetEntriesByPattern_Wildcard_AggregatesMultipleTables()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.GetTagTableNames().Returns(new[] { "MOD_Halle1", "MOD_Halle2", "ELE_Drives" });
        reader.ReadTagTable("MOD_Halle1").Returns(new[]
        {
            new TagTableEntry("MOD_1", "1", "Int")
        });
        reader.ReadTagTable("MOD_Halle2").Returns(new[]
        {
            new TagTableEntry("MOD_2", "2", "Int")
        });
        var cache = new TagTableCache(reader);

        var entries = cache.GetEntriesByPattern("MOD_*");

        entries.Should().HaveCount(2);
        entries.Should().Contain(e => e.Name == "MOD_1");
        entries.Should().Contain(e => e.Name == "MOD_2");
    }

    [Fact]
    public void GetEntriesByPattern_ExactName_StillWorks()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.GetTagTableNames().Returns(new[] { "Constants" });
        reader.ReadTagTable("Constants").Returns(new[]
        {
            new TagTableEntry("C1", "10", "Int")
        });
        var cache = new TagTableCache(reader);

        cache.GetEntriesByPattern("Constants").Should().HaveCount(1);
    }

    [Fact]
    public void GetEntriesByPattern_NoMatch_ReturnsEmpty()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.GetTagTableNames().Returns(new[] { "MOD_Halle1" });
        var cache = new TagTableCache(reader);

        cache.GetEntriesByPattern("XYZ_*").Should().BeEmpty();
    }

    [Fact]
    public void FindConstant_ReturnsEntryFromAnyTable()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.GetTagTableNames().Returns(new[] { "MOD_", "ELE_" });
        reader.ReadTagTable("MOD_").Returns(new[] { new TagTableEntry("MOD_1", "1", "Int") });
        reader.ReadTagTable("ELE_").Returns(new[] { new TagTableEntry("MAX_VALVES", "42", "Int") });
        var cache = new TagTableCache(reader);

        var entry = cache.FindConstant("MAX_VALVES");

        entry.Should().NotBeNull();
        entry!.Value.Should().Be("42");
    }

    [Fact]
    public void FindConstant_UnknownName_ReturnsNull()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.GetTagTableNames().Returns(Array.Empty<string>());
        var cache = new TagTableCache(reader);

        cache.FindConstant("NOPE").Should().BeNull();
    }

    [Fact]
    public void TryGetConstantValue_DecimalValue_Parsed()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.GetTagTableNames().Returns(new[] { "C" });
        reader.ReadTagTable("C").Returns(new[] { new TagTableEntry("MAX", "10", "Int") });
        var cache = new TagTableCache(reader);

        cache.TryGetConstantValue("MAX", out var value).Should().BeTrue();
        value.Should().Be(10);
    }

    [Fact]
    public void TryGetConstantValue_HexValue_Parsed()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.GetTagTableNames().Returns(new[] { "C" });
        reader.ReadTagTable("C").Returns(new[] { new TagTableEntry("MASK", "16#2A", "Int") });
        var cache = new TagTableCache(reader);

        cache.TryGetConstantValue("MASK", out var value).Should().BeTrue();
        value.Should().Be(42);
    }

    [Fact]
    public void TryGetConstantValue_Missing_ReturnsFalse()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.GetTagTableNames().Returns(Array.Empty<string>());
        var cache = new TagTableCache(reader);

        cache.TryGetConstantValue("MAX", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseIntLiteral_SupportsBinaryAndOctal()
    {
        TagTableCache.TryParseIntLiteral("2#101010", out var bin).Should().BeTrue();
        bin.Should().Be(42);

        TagTableCache.TryParseIntLiteral("8#52", out var oct).Should().BeTrue();
        oct.Should().Be(42);
    }

    [Fact]
    public void TryParseIntLiteral_MalformedHex_ReturnsFalse()
    {
        TagTableCache.TryParseIntLiteral("16#ZZ", out _).Should().BeFalse();
    }

    [Fact]
    public void Invalidate_ClearsConstantIndex()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.GetTagTableNames().Returns(new[] { "C" });
        reader.ReadTagTable("C").Returns(new[] { new TagTableEntry("MAX", "10", "Int") });
        var cache = new TagTableCache(reader);

        cache.TryGetConstantValue("MAX", out _).Should().BeTrue();
        cache.Invalidate();

        reader.GetTagTableNames().Returns(new[] { "C" });
        reader.ReadTagTable("C").Returns(new[] { new TagTableEntry("MAX", "99", "Int") });
        cache.TryGetConstantValue("MAX", out var value).Should().BeTrue();
        value.Should().Be(99);
    }
}
