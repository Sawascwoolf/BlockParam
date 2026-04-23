using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class ProjectScopeTests
{
    [Fact]
    public void DifferentPaths_ProduceDifferentScopes()
    {
        var a = ProjectScope.ForPath(@"C:\Projects\ProjectA\ProjectA.ap20");
        var b = ProjectScope.ForPath(@"C:\Projects\ProjectB\ProjectB.ap20");

        a.Should().NotBe(b);
    }

    [Fact]
    public void SamePath_ProducesStableScope()
    {
        var a1 = ProjectScope.ForPath(@"C:\Projects\ProjectA\ProjectA.ap20");
        var a2 = ProjectScope.ForPath(@"C:\Projects\ProjectA\ProjectA.ap20");

        a1.Should().Be(a2);
    }

    [Fact]
    public void PathComparison_IsCaseInsensitive()
    {
        var lower = ProjectScope.ForPath(@"c:\projects\proj\proj.ap20");
        var upper = ProjectScope.ForPath(@"C:\Projects\Proj\Proj.ap20");

        upper.Should().Be(lower);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_Empty_Whitespace_FallBackToDefault(string? path)
    {
        ProjectScope.ForPath(path).Should().Be("default");
    }

    [Fact]
    public void Scope_IsFilesystemSafe()
    {
        var scope = ProjectScope.ForPath(@"C:\Projects\has spaces & umlauts äöü\p.ap20");

        scope.Should().MatchRegex("^[a-f0-9]+$");
    }
}
