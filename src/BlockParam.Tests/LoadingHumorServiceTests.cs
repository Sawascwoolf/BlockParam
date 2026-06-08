using System.Text.RegularExpressions;
using BlockParam.Localization;
using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class LoadingHumorServiceTests
{
    [Fact]
    public void Catalog_is_tight_and_non_empty()
    {
        // Issue #127 hard rule: ≤15 entries, every line is a translation cost.
        LoadingHumorService.Keys.Should().NotBeEmpty();
        LoadingHumorService.Keys.Count.Should().BeLessThanOrEqualTo(15);
    }

    [Fact]
    public void Catalog_keys_are_unique()
    {
        LoadingHumorService.Keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_catalog_key_resolves_to_a_real_resx_string()
    {
        // Res.Get returns "[key]" when a key is missing. Guards against a key
        // in the catalog with no matching <data> entry in Strings.resx.
        foreach (var key in LoadingHumorService.Keys)
        {
            var text = Res.Get(key);
            text.Should().NotBe($"[{key}]", "every quip key must exist in Strings.resx");
            text.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void No_quip_names_a_platform_owner_or_competitor()
    {
        // Marketplace rule: the joke is about loading screens in general.
        // Whole-word match so "negoTIAting" / "Simatic" inside ordinary words
        // doesn't false-positive — we're banning the names, not the letters.
        string[] banned = { "TIA", "Siemens", "Openness", "SimaticML", "Simatic" };
        foreach (var key in LoadingHumorService.Keys)
        {
            var text = Res.Get(key);
            foreach (var word in banned)
            {
                Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase)
                    .Should().BeFalse($"quip '{key}' must not mention {word}");
            }
        }
    }

    [Fact]
    public void PickKey_always_returns_a_catalog_key()
    {
        for (var i = 0; i < 200; i++)
        {
            LoadingHumorService.Keys.Should().Contain(LoadingHumorService.PickKey());
        }
    }
}
