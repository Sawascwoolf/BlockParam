using FluentAssertions;
using BlockParam.Updates;
using Xunit;

namespace BlockParam.Tests;

public class GitHubReleaseFetcherTests
{
    [Fact]
    public void ParseRelease_HappyPath()
    {
        var json = @"{
            ""tag_name"": ""v0.4.0"",
            ""name"": ""v0.4.0 — fancy update"",
            ""html_url"": ""https://github.com/Sawascwoolf/BlockParam/releases/tag/v0.4.0"",
            ""body"": ""- Added thing\n- Fixed thing"",
            ""prerelease"": false,
            ""published_at"": ""2026-05-02T08:00:00Z""
        }";

        var info = GitHubReleaseFetcher.ParseRelease(json);

        info.Should().NotBeNull();
        info!.TagName.Should().Be("v0.4.0");
        info.Name.Should().Be("v0.4.0 — fancy update");
        info.HtmlUrl.Should().Be("https://github.com/Sawascwoolf/BlockParam/releases/tag/v0.4.0");
        info.Body.Should().Contain("Added thing");
        info.PreRelease.Should().BeFalse();
        info.PublishedAt.Should().NotBeNull();
    }

    [Fact]
    public void ParseRelease_PreRelease_True()
    {
        var json = @"{ ""tag_name"": ""v0.4.0-rc1"", ""prerelease"": true }";
        var info = GitHubReleaseFetcher.ParseRelease(json);
        info.Should().NotBeNull();
        info!.PreRelease.Should().BeTrue();
    }

    [Fact]
    public void ParseRelease_MissingTag_ReturnsNull()
    {
        // GitHub never returns a release without tag_name, but if they ever
        // did the service must reject it rather than show "v(empty)".
        var json = @"{ ""name"": ""untitled"" }";
        GitHubReleaseFetcher.ParseRelease(json).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]                // unexpected array root
    [InlineData("{}")]                // valid JSON but no tag_name
    [InlineData("null")]
    public void ParseRelease_GarbageInput_ReturnsNull(string json)
    {
        GitHubReleaseFetcher.ParseRelease(json).Should().BeNull();
    }
}
