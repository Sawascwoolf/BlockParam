using FluentAssertions;
using BlockParam.Config;
using Xunit;

namespace BlockParam.Tests;

public class ConfigLoaderTests
{
    [Fact]
    public void Load_ValidConfig_ParsesCorrectly()
    {
        var json = @"{
            ""version"": ""1.0"",
            ""rules"": [
                {
                    ""pathPattern"": ""ModuleId"",
                    ""datatype"": ""Int"",
                    ""constraints"": { ""min"": 0, ""max"": 9999 }
                }
            ]
        }";

        var config = ConfigLoader.Deserialize(json);

        config.Should().NotBeNull();
        config!.Version.Should().Be("1.0");
        config.Rules.Should().HaveCount(1);
        config.Rules[0].PathPattern.Should().Be("ModuleId");
        config.Rules[0].Constraints!.Min.Should().Be(0);
        config.Rules[0].Constraints!.Max.Should().Be(9999);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyConfig()
    {
        var loader = new ConfigLoader("/nonexistent/path/config.json");

        var config = loader.GetConfig();
        config.Should().NotBeNull();
        config!.Rules.Should().BeEmpty();
    }

    [Fact]
    public void Load_EmptyString_ReturnsNull()
    {
        var config = ConfigLoader.Deserialize("");

        config.Should().BeNull();
    }

    [Fact]
    public void Load_InvalidJson_ThrowsMeaningfulError()
    {
        var act = () => ConfigLoader.Deserialize("{invalid json!!}");

        act.Should().Throw<Newtonsoft.Json.JsonException>();
    }

    [Fact]
    public void Load_RuleWithCommentTemplate_ParsesCorrectly()
    {
        var json = @"{
            ""version"": ""1.0"",
            ""rules"": [{
                ""pathPattern"": "".*{udt:messageConfig_UDT}$"",
                ""commentTemplate"": ""{db}.{parent}"",
                ""commentLanguage"": ""en-GB""
            }]
        }";

        var config = ConfigLoader.Deserialize(json);

        config!.Rules.Should().HaveCount(1);
        config.Rules[0].CommentTemplate.Should().Be("{db}.{parent}");
    }

    [Fact]
    public void Load_ConfigWithTagTableRef_Parsed()
    {
        var json = @"{
            ""version"": ""1.0"",
            ""rules"": [{
                ""pathPattern"": ""ModuleId"",
                ""datatype"": ""Int"",
                ""tagTableReference"": {
                    ""tableName"": ""Constants_Modules"",
                    ""description"": ""Module ID constants""
                }
            }]
        }";

        var config = ConfigLoader.Deserialize(json);

        config!.Rules[0].TagTableReference.Should().NotBeNull();
        config.Rules[0].TagTableReference!.TableName.Should().Be("Constants_Modules");
    }

    [Fact]
    public void GetRule_MatchingRule_ReturnsConstraint()
    {
        var json = @"{
            ""rules"": [{
                ""pathPattern"": ""Speed"",
                ""datatype"": ""Int"",
                ""constraints"": { ""min"": 0, ""max"": 3000 }
            }]
        }";

        var config = ConfigLoader.Deserialize(json)!;
        var member = new Models.MemberNode("Speed", "Int", null, "Speed", null, new List<Models.MemberNode>(), false);
        var rule = config.GetRule(member);

        rule.Should().NotBeNull();
        rule!.Constraints!.Min.Should().Be(0);
        rule.Constraints.Max.Should().Be(3000);
    }

    [Fact]
    public void GetRule_NoMatchingRule_ReturnsNull()
    {
        var json = @"{ ""rules"": [{ ""pathPattern"": ""Speed"", ""datatype"": ""Int"" }] }";

        var config = ConfigLoader.Deserialize(json)!;
        var member = new Models.MemberNode("Temperature", "Real", null, "Temperature", null, new List<Models.MemberNode>(), false);
        var rule = config.GetRule(member);

        rule.Should().BeNull();
    }
}

public class ValueConstraintTests
{
    [Fact]
    public void Constraint_ValueInRange_Accepted()
    {
        var constraint = new ValueConstraint { Min = 0, Max = 100 };

        constraint.Validate("50").Should().BeNull();
    }

    [Fact]
    public void Constraint_ValueOutOfRange_Rejected()
    {
        var constraint = new ValueConstraint { Min = 0, Max = 100 };

        constraint.Validate("150").Should().NotBeNull();
        constraint.Validate("-1").Should().NotBeNull();
    }

    [Fact]
    public void Constraint_AllowedValues_Accepted()
    {
        var constraint = new ValueConstraint
        {
            AllowedValues = new List<object> { 1, 2, 3, 42 }
        };

        constraint.Validate("42").Should().BeNull();
    }

    [Fact]
    public void Constraint_DisallowedValue_Rejected()
    {
        var constraint = new ValueConstraint
        {
            AllowedValues = new List<object> { 1, 2, 3 }
        };

        constraint.Validate("99").Should().NotBeNull();
    }

    [Fact]
    public void Constraint_NoLimits_EverythingAllowed()
    {
        var constraint = new ValueConstraint();

        constraint.Validate("anything").Should().BeNull();
    }
}
