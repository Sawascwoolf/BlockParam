using FluentAssertions;
using BlockParam.Config;
using BlockParam.Services;
using Xunit;

namespace BlockParam.Tests;

public class RuleHintFormatterTests
{
    [Fact]
    public void NullRule_NoDatatype_ReturnsNull()
    {
        RuleHintFormatter.Format(null, null).Should().BeNull();
    }

    [Fact]
    public void NullRule_WithIntDatatype_ReturnsDatatypeFallback()
    {
        var hint = RuleHintFormatter.Format(null, "Int");
        hint.Should().NotBeNull();
        hint.Should().Contain("Int");
        hint.Should().Contain("-32768");
        hint.Should().Contain("32767");
    }

    [Fact]
    public void NullRule_WithUnknownDatatype_ReturnsNull()
    {
        RuleHintFormatter.Format(null, "SomeUdt").Should().BeNull();
    }

    [Fact]
    public void RuleRange_OverridesDatatypeFallback()
    {
        var rule = new MemberRule
        {
            PathPattern = ".*",
            Constraints = new ValueConstraint { Min = 0L, Max = 100L },
        };
        var hint = RuleHintFormatter.Format(rule, "Int");
        hint.Should().NotBeNull();
        hint.Should().Contain("0");
        hint.Should().Contain("100");
        // Datatype fallback MUST NOT appear alongside an explicit rule range.
        hint.Should().NotContain("32767");
    }

    [Fact]
    public void AllowedValues_DoesNotAppendDatatypeFallback()
    {
        // Regression guard: a datatype fallback appended after "One of:" reads
        // as "allowed values OR any Int", which contradicts the validator.
        var rule = new MemberRule
        {
            PathPattern = ".*",
            Constraints = new ValueConstraint
            {
                AllowedValues = new List<object> { "OPEN", "CLOSED" },
            },
        };
        var hint = RuleHintFormatter.Format(rule, "Int");
        hint.Should().NotBeNull();
        hint.Should().Contain("OPEN");
        hint.Should().Contain("CLOSED");
        hint.Should().NotContain("32767");
    }

    [Fact]
    public void RequireTagTable_DoesNotAppendDatatypeFallback()
    {
        var rule = new MemberRule
        {
            PathPattern = ".*",
            TagTableReference = new TagTableReference { TableName = "MOD_*" },
            Constraints = new ValueConstraint { RequireTagTableValue = true },
        };
        var hint = RuleHintFormatter.Format(rule, "Int");
        hint.Should().NotBeNull();
        hint.Should().Contain("MOD_*");
        hint.Should().NotContain("32767");
    }

    [Fact]
    public void EmptyConstraints_ReturnsDatatypeFallback()
    {
        // Rule exists but has no user-visible constraint parts — the datatype
        // fallback should still provide something useful.
        var rule = new MemberRule
        {
            PathPattern = ".*",
            Constraints = new ValueConstraint(),
        };
        var hint = RuleHintFormatter.Format(rule, "Int");
        hint.Should().NotBeNull();
        hint.Should().Contain("Int");
    }

    [Fact]
    public void IntRangeHint_UsesInvariantCultureFormatting()
    {
        // TIA parser accepts InvariantCulture literals ("-32768"), not culture
        // grouping ("-32.768"). The hint must match so users don't type invalid
        // values by copying the hint verbatim.
        var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture =
            new System.Globalization.CultureInfo("de-DE");
        try
        {
            var hint = RuleHintFormatter.Format(null, "Int");
            hint.Should().NotBeNull();
            hint.Should().Contain("-32768");
            hint.Should().Contain("32767");
            hint.Should().NotContain("32.767");
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = prev;
        }
    }

    [Fact]
    public void BoolDatatype_ReturnsBoolHint()
    {
        var hint = RuleHintFormatter.Format(null, "Bool");
        hint.Should().NotBeNull();
        hint.Should().Contain("Bool");
    }

    [Fact]
    public void RealDatatype_ReturnsFloatHint()
    {
        var hint = RuleHintFormatter.Format(null, "Real");
        hint.Should().NotBeNull();
        hint.Should().Contain("Real");
    }
}
