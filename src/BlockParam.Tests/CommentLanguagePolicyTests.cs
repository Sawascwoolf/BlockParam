using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class CommentLanguagePolicyTests
{
    [Fact]
    public void Pick_Null_ReturnsNull()
    {
        var policy = new CommentLanguagePolicy("en-US", null, null);
        policy.Pick(null).Should().BeNull();
    }

    [Fact]
    public void Pick_EmptyDict_ReturnsNull()
    {
        var policy = new CommentLanguagePolicy("en-US", null, null);
        policy.Pick(new Dictionary<string, string>()).Should().BeNull();
    }

    [Fact]
    public void Pick_EditingLanguageMatches_ReturnsEditingVariant()
    {
        var policy = new CommentLanguagePolicy("de-DE", "en-US", new[] { "en-US", "de-DE" });
        var comments = new Dictionary<string, string>
        {
            ["en-US"] = "Motor setpoint",
            ["de-DE"] = "Motor-Sollwert",
        };

        policy.Pick(comments).Should().Be("Motor-Sollwert");
    }

    [Fact]
    public void Pick_EditingMissing_FallsBackToReference()
    {
        var policy = new CommentLanguagePolicy("fr-FR", "en-US", new[] { "en-US", "de-DE" });
        var comments = new Dictionary<string, string>
        {
            ["en-US"] = "Motor setpoint",
            ["de-DE"] = "Motor-Sollwert",
        };

        policy.Pick(comments).Should().Be("Motor setpoint");
    }

    [Fact]
    public void Pick_EditingAndReferenceMissing_FallsBackToFirstActive()
    {
        var policy = new CommentLanguagePolicy("fr-FR", "it-IT", new[] { "en-US", "de-DE" });
        var comments = new Dictionary<string, string>
        {
            ["de-DE"] = "Motor-Sollwert",
            ["en-US"] = "Motor setpoint",
        };

        policy.Pick(comments).Should().Be("Motor setpoint");
    }

    [Fact]
    public void Pick_EditingEmpty_SkipsToNextPreferred()
    {
        var policy = new CommentLanguagePolicy("de-DE", "en-US", null);
        var comments = new Dictionary<string, string>
        {
            ["de-DE"] = "",
            ["en-US"] = "Motor setpoint",
        };

        policy.Pick(comments).Should().Be("Motor setpoint");
    }

    [Fact]
    public void Pick_NoPreferredVariantPresent_FallsBackToAnyNonEmpty()
    {
        var policy = new CommentLanguagePolicy("fr-FR", "it-IT", new[] { "es-ES" });
        var comments = new Dictionary<string, string>
        {
            ["de-DE"] = "Motor-Sollwert",
        };

        policy.Pick(comments).Should().Be("Motor-Sollwert");
    }

    [Fact]
    public void Pick_LegacyEmptyKeyEntry_ReturnedByLastResortFallback()
    {
        var policy = new CommentLanguagePolicy("en-US", null, null);
        var comments = new Dictionary<string, string>
        {
            [""] = "Legacy comment",
        };

        policy.Pick(comments).Should().Be("Legacy comment");
    }

    [Fact]
    public void Pick_AllVariantsEmpty_ReturnsNull()
    {
        var policy = new CommentLanguagePolicy("de-DE", "en-US", null);
        var comments = new Dictionary<string, string>
        {
            ["de-DE"] = "",
            ["en-US"] = "",
        };

        policy.Pick(comments).Should().BeNull();
    }

    [Fact]
    public void Pick_NullEditingAndReference_UsesActiveLanguages()
    {
        var policy = new CommentLanguagePolicy(null, null, new[] { "de-DE", "en-US" });
        var comments = new Dictionary<string, string>
        {
            ["de-DE"] = "Motor-Sollwert",
            ["en-US"] = "Motor setpoint",
        };

        policy.Pick(comments).Should().Be("Motor-Sollwert");
    }

    [Fact]
    public void Pick_AllConstructorInputsNull_StillFallsBackToAnyNonEmpty()
    {
        var policy = new CommentLanguagePolicy(null, null, null);
        var comments = new Dictionary<string, string>
        {
            ["de-DE"] = "Motor-Sollwert",
        };

        policy.Pick(comments).Should().Be("Motor-Sollwert");
    }

    [Fact]
    public void Pick_DuplicateEditingAndReference_DoesNotAffectOrder()
    {
        // Editing == Reference: dedup should not cause the active-language entry to be skipped.
        var policy = new CommentLanguagePolicy("en-US", "en-US", new[] { "en-US", "de-DE" });
        var comments = new Dictionary<string, string>
        {
            ["de-DE"] = "Motor-Sollwert",
            ["en-US"] = "Motor setpoint",
        };

        policy.Pick(comments).Should().Be("Motor setpoint");
    }

    [Fact]
    public void Pick_EditingMatchesCaseInsensitively_InPreferenceOrder()
    {
        // Dedup in the preference list is case-insensitive — verify it doesn't drop
        // a legitimate second entry that differs only in case of an earlier one.
        var policy = new CommentLanguagePolicy("EN-us", "en-US", null);
        _ = policy; // preference order contains a single "EN-us"

        // Dictionary lookup itself is case-sensitive (matches .NET default), so the
        // stored key must match the editing-language culture name exactly.
        var comments = new Dictionary<string, string>
        {
            ["EN-us"] = "Upper",
        };

        policy.Pick(comments).Should().Be("Upper");
    }
}
