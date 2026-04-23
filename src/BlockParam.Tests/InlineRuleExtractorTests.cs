using BlockParam.Config;
using BlockParam.Models;
using BlockParam.SimaticML;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class InlineRuleExtractorTests
{
    private static (DataBlockInfo Db, BulkChangeConfig Config) ParseFixture()
    {
        var xml = TestFixtures.LoadXml("inline-rules-db.xml");
        var parser = new SimaticMLParser();
        var db = parser.Parse(xml);
        var config = new BulkChangeConfig();
        return (db, config);
    }

    [Fact]
    public void Extracts_rules_from_db_member_comments()
    {
        var (db, config) = ParseFixture();
        var count = InlineRuleExtractor.ApplyTo(config, db);

        // moduleId (varTable), temperature (min+max), debug (exclude) → 3 members
        count.Should().Be(3);
        config.Rules.Should().HaveCount(3);
        config.Rules.Should().OnlyContain(r => r.Source == RuleSource.Inline);
    }

    [Fact]
    public void Applies_inline_rule_via_GetRule()
    {
        var (db, config) = ParseFixture();
        InlineRuleExtractor.ApplyTo(config, db);

        var moduleId = db.Members.Single(m => m.Name == "moduleId");
        var rule = config.GetRule(moduleId);

        rule.Should().NotBeNull();
        rule!.Source.Should().Be(RuleSource.Inline);
        rule.TagTableReference.Should().NotBeNull();
        rule.TagTableReference!.TableName.Should().Be("MOD_");
    }

    [Fact]
    public void Inline_rule_overrides_config_rule()
    {
        var (db, config) = ParseFixture();

        // A config rule for the same path, with a different tag table
        config.Rules.Add(new MemberRule
        {
            PathPattern = "^moduleId$",
            TagTableReference = new TagTableReference { TableName = "LOSES_TO_INLINE_" },
            Source = RuleSource.TiaProject,
        });

        InlineRuleExtractor.ApplyTo(config, db);

        var moduleId = db.Members.Single(m => m.Name == "moduleId");
        var rule = config.GetRule(moduleId);
        rule!.TagTableReference!.TableName.Should().Be("MOD_");
    }

    [Fact]
    public void ApplyTo_clears_previous_inline_rules_before_adding_new_ones()
    {
        var (db, config) = ParseFixture();

        // First apply leaves 3 inline rules in the config
        InlineRuleExtractor.ApplyTo(config, db);
        config.Rules.Count(r => r.Source == RuleSource.Inline).Should().Be(3);

        // Second apply with same DB must not accumulate duplicates
        InlineRuleExtractor.ApplyTo(config, db);
        config.Rules.Count(r => r.Source == RuleSource.Inline).Should().Be(3);
    }

    [Fact]
    public void Handles_null_config_or_db_safely()
    {
        InlineRuleExtractor.ApplyTo(null, null).Should().Be(0);
        InlineRuleExtractor.ApplyTo(new BulkChangeConfig(), null).Should().Be(0);

        var (db, _) = ParseFixture();
        InlineRuleExtractor.ApplyTo(null, db).Should().Be(0);
    }

    [Fact]
    public void Extracted_constraints_have_min_and_max()
    {
        var (db, config) = ParseFixture();
        InlineRuleExtractor.ApplyTo(config, db);

        var temperature = db.Members.Single(m => m.Name == "temperature");
        var rule = config.GetRule(temperature);

        rule!.Constraints.Should().NotBeNull();
        rule.Constraints!.Min.Should().Be("0");
        rule.Constraints.Max.Should().Be("100");
    }

    [Fact]
    public void Extracted_exclude_flag_is_applied()
    {
        var (db, config) = ParseFixture();
        InlineRuleExtractor.ApplyTo(config, db);

        var debug = db.Members.Single(m => m.Name == "debug");
        var rule = config.GetRule(debug);
        rule!.ExcludeFromSetpoints.Should().BeTrue();
    }
}
