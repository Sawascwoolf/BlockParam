using BlockParam.Config;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class InlineRuleParserTests
{
    [Fact]
    public void Returns_null_when_no_tokens_present()
    {
        InlineRuleParser.Parse("just a normal comment").Should().BeNull();
        InlineRuleParser.Parse((string?)null).Should().BeNull();
        InlineRuleParser.Parse("").Should().BeNull();
    }

    [Fact]
    public void Extracts_var_table()
    {
        var rule = InlineRuleParser.Parse("Module id {bp_varTable=MOD_}");
        rule.Should().NotBeNull();
        rule!.VarTable.Should().Be("MOD_");
    }

    [Fact]
    public void Extracts_min_max()
    {
        var rule = InlineRuleParser.Parse("temp {bp_min=0}{bp_max=100}");
        rule.Should().NotBeNull();
        rule!.Min.Should().Be("0");
        rule.Max.Should().Be("100");
    }

    [Fact]
    public void Extracts_allowed_values_csv()
    {
        var rule = InlineRuleParser.Parse("mode {bp_allowed=AUTO, MANUAL,OFF}");
        rule.Should().NotBeNull();
        rule!.AllowedValues.Should().BeEquivalentTo(new[] { "AUTO", "MANUAL", "OFF" });
    }

    [Theory]
    [InlineData("{bp_exclude=true}", true)]
    [InlineData("{bp_exclude=TRUE}", true)]
    [InlineData("{bp_exclude=1}", true)]
    [InlineData("{bp_exclude=yes}", true)]
    [InlineData("{bp_exclude=false}", false)]
    [InlineData("{bp_exclude=no}", false)]
    public void Parses_exclude_as_boolean(string comment, bool expected)
    {
        var rule = InlineRuleParser.Parse(comment);
        rule!.Exclude.Should().Be(expected);
    }

    [Fact]
    public void Extracts_comment_template()
    {
        var rule = InlineRuleParser.Parse("{bp_comment=Set by operator on {date}}");
        // Only matches up to the first closing brace — the template ends at '}'.
        // Callers wanting braces in templates should avoid nested braces or escape.
        rule!.CommentTemplate.Should().Be("Set by operator on {date");
    }

    [Fact]
    public void Ignores_unknown_properties()
    {
        var rule = InlineRuleParser.Parse("{bp_nonsense=42}{bp_varTable=MOD_}");
        rule.Should().NotBeNull();
        rule!.VarTable.Should().Be("MOD_");
    }

    [Fact]
    public void Returns_null_when_only_unknown_properties_present()
    {
        InlineRuleParser.Parse("{bp_nonsense=42}").Should().BeNull();
        InlineRuleParser.Parse("{bp_foo=a}{bp_bar=b}").Should().BeNull();
    }

    [Fact]
    public void Merges_multiple_tokens_in_one_comment()
    {
        var rule = InlineRuleParser.Parse("{bp_varTable=MOD_} - allowed: {bp_allowed=A,B}");
        rule!.VarTable.Should().Be("MOD_");
        rule.AllowedValues.Should().BeEquivalentTo(new[] { "A", "B" });
    }

    [Fact]
    public void Merges_rules_across_language_variants()
    {
        var comments = new Dictionary<string, string>
        {
            ["de-DE"] = "Modul-ID {bp_varTable=MOD_}",
            ["en-GB"] = "Module id (no rule here)",
        };
        var rule = InlineRuleParser.Parse(comments);
        rule.Should().NotBeNull();
        rule!.VarTable.Should().Be("MOD_");
    }

    [Fact]
    public void First_language_wins_on_conflict()
    {
        var comments = new Dictionary<string, string>
        {
            ["de-DE"] = "{bp_min=0}",
            ["en-GB"] = "{bp_min=5}",
        };
        var rule = InlineRuleParser.Parse(comments);
        rule.Should().NotBeNull();
        rule!.Min.Should().Be("0"); // de-DE sorts alphabetically before en-GB
    }

    [Fact]
    public void Language_iteration_is_deterministic_regardless_of_insertion_order()
    {
        // Same languages and values, but inserted in different orders —
        // the alphabetically-first culture must always win.
        var enFirst = new Dictionary<string, string>
        {
            ["en-GB"] = "{bp_min=5}",
            ["de-DE"] = "{bp_min=0}",
        };
        var deFirst = new Dictionary<string, string>
        {
            ["de-DE"] = "{bp_min=0}",
            ["en-GB"] = "{bp_min=5}",
        };
        InlineRuleParser.Parse(enFirst)!.Min.Should().Be("0");
        InlineRuleParser.Parse(deFirst)!.Min.Should().Be("0");
    }

    [Fact]
    public void Same_value_across_languages_is_not_a_conflict()
    {
        // Both languages set bp_min=0 — merged rule should reflect the shared
        // value without treating it as a conflict. (Regression: earlier impl
        // logged a conflict warning any time two languages set the same key,
        // even with identical values.)
        var comments = new Dictionary<string, string>
        {
            ["de-DE"] = "{bp_min=0}",
            ["en-GB"] = "{bp_min=0}",
        };
        var rule = InlineRuleParser.Parse(comments);
        rule!.Min.Should().Be("0");
    }

    [Fact]
    public void Unions_non_conflicting_properties_across_languages()
    {
        var comments = new Dictionary<string, string>
        {
            ["de-DE"] = "Modul-ID {bp_varTable=MOD_}",
            ["en-GB"] = "Module id {bp_exclude=true}",
        };
        var rule = InlineRuleParser.Parse(comments);
        rule!.VarTable.Should().Be("MOD_");
        rule.Exclude.Should().Be(true);
    }

    [Fact]
    public void Returns_null_when_no_language_variant_has_tokens()
    {
        var comments = new Dictionary<string, string>
        {
            ["de-DE"] = "Modul-ID",
            ["en-GB"] = "Module id",
        };
        InlineRuleParser.Parse(comments).Should().BeNull();
    }

    [Fact]
    public void ToMemberRule_sets_exact_path_pattern_and_inline_source()
    {
        var inline = new InlineCommentRule { VarTable = "MOD_" };
        var rule = InlineRuleParser.ToMemberRule(inline, "drives[1].moduleId");

        rule.Source.Should().Be(RuleSource.Inline);
        rule.PathPattern.Should().Be(@"^drives\[1]\.moduleId$");
        rule.TagTableReference.Should().NotBeNull();
        rule.TagTableReference!.TableName.Should().Be("MOD_");
    }

    [Fact]
    public void ToMemberRule_emits_constraints_when_min_max_or_allowed_present()
    {
        var inline = new InlineCommentRule
        {
            Min = "0",
            Max = "100",
            AllowedValues = new List<string> { "A", "B" },
        };
        var rule = InlineRuleParser.ToMemberRule(inline, "x");
        rule.Constraints.Should().NotBeNull();
        rule.Constraints!.Min.Should().Be("0");
        rule.Constraints.Max.Should().Be("100");
        rule.Constraints.AllowedValues.Should().BeEquivalentTo(new object[] { "A", "B" });
    }

    [Fact]
    public void ToMemberRule_omits_constraints_when_no_range_or_allowed()
    {
        var inline = new InlineCommentRule { VarTable = "MOD_" };
        var rule = InlineRuleParser.ToMemberRule(inline, "x");
        rule.Constraints.Should().BeNull();
    }

    [Fact]
    public void ToMemberRule_array_pattern_matches_container_and_indexed_elements()
    {
        var inline = new InlineCommentRule { VarTable = "MOD_" };
        var rule = InlineRuleParser.ToMemberRule(inline, "drives", isArrayMember: true);

        var regex = new System.Text.RegularExpressions.Regex(rule.PathPattern);
        regex.IsMatch("drives").Should().BeTrue();
        regex.IsMatch("drives[0]").Should().BeTrue();
        regex.IsMatch("drives[0,1]").Should().BeTrue();
        regex.IsMatch("drives[0].moduleId").Should().BeFalse();
        regex.IsMatch("otherDrives").Should().BeFalse();
    }
}
