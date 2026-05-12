using System.IO;
using BlockParam.Services.Storage;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests.Storage;

public class StoragePathTests
{
    [Fact]
    public void Slash_operator_composes_path_segments()
    {
        var p = StoragePath.FromAbsolute(@"C:\root") / "sub" / "leaf.json";

        p.FullPath.Should().Be(Path.Combine(@"C:\root", "sub", "leaf.json"));
    }

    [Fact]
    public void FileName_returns_terminal_segment()
    {
        var p = StoragePath.FromAbsolute(@"C:\root") / "config.json";
        p.FileName.Should().Be("config.json");
    }

    [Fact]
    public void Parent_returns_enclosing_directory()
    {
        var p = StoragePath.FromAbsolute(@"C:\root") / "sub" / "leaf.json";
        p.Parent.FullPath.Should().Be(Path.Combine(@"C:\root", "sub"));
    }

    [Fact]
    public void Equality_is_case_insensitive()
    {
        var a = StoragePath.FromAbsolute(@"C:\Foo\Bar.txt");
        var b = StoragePath.FromAbsolute(@"c:\foo\bar.txt");

        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Default_struct_is_empty()
    {
        default(StoragePath).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void AppData_root_points_at_AppDirectories_constant()
    {
        StoragePath.AppData.FullPath.Should().Be(BlockParam.Services.AppDirectories.AppData);
        StoragePath.Temp.FullPath.Should().Be(BlockParam.Services.AppDirectories.Temp);
        StoragePath.Logs.FullPath.Should().Be(BlockParam.Services.AppDirectories.LogsDir);
        StoragePath.ProgramData.FullPath.Should().Be(BlockParam.Services.AppDirectories.ProgramData);
    }
}
