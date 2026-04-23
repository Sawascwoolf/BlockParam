using FluentAssertions;
using BlockParam.Config;
using BlockParam.Models;
using BlockParam.Services;
using BlockParam.SimaticML;
using Xunit;

namespace BlockParam.Tests;

public class PathPatternMatcherTests
{
    private readonly SimaticMLParser _parser = new();

    // Helper: build a member chain manually for unit tests
    private static MemberNode MakeMember(string name, string datatype, MemberNode? parent = null)
    {
        var path = parent != null ? $"{parent.Path}.{name}" : name;
        return new MemberNode(name, datatype, null, path, parent, new List<MemberNode>(), false);
    }

    [Fact]
    public void Match_RegexModuleIdAnywhere()
    {
        // drive1.communicationError.moduleId
        var drive = MakeMember("drive1", "\"driveConfig_UDT\"");
        var comm = MakeMember("communicationError", "\"messageConfig_UDT\"", drive);
        var moduleId = MakeMember("moduleId", "Int", comm);

        PathPatternMatcher.IsMatch(moduleId, @".*\.moduleId$").Should().BeTrue();
    }

    [Fact]
    public void Match_UdtToken_CorrectType()
    {
        var drive = MakeMember("drive1", "\"driveConfig_UDT\"");
        var comm = MakeMember("communicationError", "\"messageConfig_UDT\"", drive);
        var moduleId = MakeMember("moduleId", "Int", comm);

        PathPatternMatcher.IsMatch(moduleId, @".*{udt:messageConfig_UDT}\.moduleId$")
            .Should().BeTrue();
    }

    [Fact]
    public void Match_UdtToken_WrongType_NoMatch()
    {
        var drive = MakeMember("drive1", "\"driveConfig_UDT\"");
        var comm = MakeMember("communicationError", "\"messageConfig_UDT\"", drive);
        var moduleId = MakeMember("moduleId", "Int", comm);

        PathPatternMatcher.IsMatch(moduleId, @".*{udt:wrongType_UDT}\.moduleId$")
            .Should().BeFalse();
    }

    [Fact]
    public void Match_UdtToken_QuotedDatatype_Stripped()
    {
        // Datatype is "\"messageConfig_UDT\"" (with quotes)
        var comm = MakeMember("comm", "\"messageConfig_UDT\"");
        var moduleId = MakeMember("moduleId", "Int", comm);

        // Pattern uses unquoted type name
        PathPatternMatcher.IsMatch(moduleId, @".*{udt:messageConfig_UDT}\.moduleId$")
            .Should().BeTrue();
    }

    [Fact]
    public void Match_ExactPath()
    {
        var drive = MakeMember("drive1", "Struct");
        var comm = MakeMember("communicationError", "\"messageConfig_UDT\"", drive);
        var moduleId = MakeMember("moduleId", "Int", comm);

        PathPatternMatcher.IsMatch(moduleId, @"^drive1\.communicationError\.moduleId$")
            .Should().BeTrue();
    }

    [Fact]
    public void Match_RegexCharacterClass()
    {
        var drive1 = MakeMember("drive1", "Struct");
        var comm1 = MakeMember("comm", "\"msgCfg\"", drive1);
        var mod1 = MakeMember("moduleId", "Int", comm1);

        var drive2 = MakeMember("drive2", "Struct");
        var comm2 = MakeMember("comm", "\"msgCfg\"", drive2);
        var mod2 = MakeMember("moduleId", "Int", comm2);

        var drive3 = MakeMember("sensor1", "Struct");
        var comm3 = MakeMember("comm", "\"msgCfg\"", drive3);
        var mod3 = MakeMember("moduleId", "Int", comm3);

        var pattern = @"^drive[12]\..*\.moduleId$";
        PathPatternMatcher.IsMatch(mod1, pattern).Should().BeTrue();
        PathPatternMatcher.IsMatch(mod2, pattern).Should().BeTrue();
        PathPatternMatcher.IsMatch(mod3, pattern).Should().BeFalse();
    }

    [Fact]
    public void Match_NestedUdts()
    {
        var drive = MakeMember("drive1", "\"driveConfig_UDT\"");
        var comm = MakeMember("communicationError", "\"messageConfig_UDT\"", drive);
        var moduleId = MakeMember("moduleId", "Int", comm);

        PathPatternMatcher.IsMatch(moduleId,
            @".*{udt:driveConfig_UDT}\.{udt:messageConfig_UDT}\.moduleId$")
            .Should().BeTrue();
    }


    [Fact]
    public void GetRule_MostSpecificWins_UdtPatternOverGeneric()
    {
        var config = new BulkChangeConfig
        {
            Rules = new List<MemberRule>
            {
                new() { PathPattern = @".*\.moduleId$", Constraints = new ValueConstraint { Min = 0 } },
                new() { PathPattern = @".*{udt:messageConfig_UDT}\.moduleId$",
                        Constraints = new ValueConstraint { Min = 100 } }
            }
        };

        var comm = MakeMember("comm", "\"messageConfig_UDT\"");
        var moduleId = MakeMember("moduleId", "Int", comm);

        var rule = config.GetRule(moduleId);
        rule.Should().NotBeNull();
        rule!.Constraints!.Min.Should().Be(100); // UDT pattern is more specific
    }

    [Fact]
    public void GetRule_NoMatch_ReturnsNull()
    {
        var config = new BulkChangeConfig
        {
            Rules = new List<MemberRule>
            {
                new() { PathPattern = @".*\.elementId$" }
            }
        };

        var moduleId = MakeMember("moduleId", "Int");

        config.GetRule(moduleId).Should().BeNull();
    }

    [Fact]
    public void Match_SameUdtType_PositionalResolution()
    {
        // outer (myUDT) → inner (myUDT) → field
        var outer = MakeMember("outer", "\"myUDT\"");
        var inner = MakeMember("inner", "\"myUDT\"", outer);
        var field = MakeMember("field", "Int", inner);

        // Pattern requires two UDT segments — should resolve positionally
        PathPatternMatcher.IsMatch(field, @"{udt:myUDT}\.{udt:myUDT}\.field$")
            .Should().BeTrue();
    }

    [Fact]
    public void Match_RootLevelMember_SimplePattern()
    {
        var root = MakeMember("moduleId", "Int");

        // Simple name as pathPattern should match root-level member
        PathPatternMatcher.IsMatch(root, "moduleId").Should().BeTrue();
    }

    [Fact]
    public void GetRule_DatatypeFilter_QuoteStripping()
    {
        var config = new BulkChangeConfig
        {
            Rules = new List<MemberRule>
            {
                new() { PathPattern = "drive1", Datatype = "driveConfig_UDT" }
            }
        };

        // Member has quoted datatype
        var drive = MakeMember("drive1", "\"driveConfig_UDT\"");

        config.GetRule(drive).Should().NotBeNull();
    }

    [Fact]
    public void ValidatePattern_ValidRegex_ReturnsNull()
    {
        PathPatternMatcher.ValidatePattern(@".*\.moduleId$").Should().BeNull();
    }

    [Fact]
    public void ValidatePattern_InvalidRegex_ReturnsError()
    {
        PathPatternMatcher.ValidatePattern(@"[invalid").Should().NotBeNull();
    }

    [Fact]
    public void Match_V20_RealFixture()
    {
        var xml = TestFixtures.LoadXml("v20-tp307.xml");
        var db = _parser.Parse(xml);

        // drive1.communicationError.moduleId — communicationError is "messageConfig_UDT"
        var moduleId = db.AllMembers().First(m => m.Path == "drive1.communicationError.moduleId");

        PathPatternMatcher.IsMatch(moduleId, @".*{udt:messageConfig_UDT}\.moduleId$")
            .Should().BeTrue();

        // drive2 is Struct, not UDT — its children should NOT match UDT pattern
        // (unless they themselves have the UDT parent)
        var drive2ModuleId = db.AllMembers().First(m => m.Path == "drive2.blocked.moduleId");
        // drive2.blocked is "messageConfig_UDT" so it should match
        PathPatternMatcher.IsMatch(drive2ModuleId, @".*{udt:messageConfig_UDT}\.moduleId$")
            .Should().BeTrue();
    }

    // ===== Specificity tests =====

    [Fact]
    public void Specificity_ExactPath_HighestScore()
    {
        var exact = PathPatternMatcher.CalculateSpecificity(
            @"^drive1\.communicationError\.moduleId$", null, "Int");
        var udtPattern = PathPatternMatcher.CalculateSpecificity(
            @".*{udt:messageConfig_UDT}\.moduleId$", null, "Int");
        var wildcard = PathPatternMatcher.CalculateSpecificity(
            @".*\.moduleId$", null, null);

        exact.Should().BeGreaterThan(udtPattern);
        udtPattern.Should().BeGreaterThan(wildcard);
    }

    [Fact]
    public void Specificity_UdtToken_MoreSpecificThanWildcard()
    {
        var withUdt = PathPatternMatcher.CalculateSpecificity(
            @".*{udt:messageConfig_UDT}\.moduleId$", null, null);
        var withoutUdt = PathPatternMatcher.CalculateSpecificity(
            @".*\.moduleId$", null, null);

        withUdt.Should().BeGreaterThan(withoutUdt);
    }

    [Fact]
    public void Specificity_DatatypeFilter_AddsScore()
    {
        var withType = PathPatternMatcher.CalculateSpecificity(
            @".*\.moduleId$", null, "Int");
        var withoutType = PathPatternMatcher.CalculateSpecificity(
            @".*\.moduleId$", null, null);

        withType.Should().BeGreaterThan(withoutType);
    }

    [Fact]
    public void Specificity_SimplePattern_HasScore()
    {
        var score = PathPatternMatcher.CalculateSpecificity("moduleId", null, null);
        score.Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetRule_MostSpecificWins()
    {
        var config = new BulkChangeConfig
        {
            Rules = new List<MemberRule>
            {
                // General rule: any moduleId
                new() { PathPattern = "moduleId",
                        Constraints = new ValueConstraint { Max = 9999 } },
                // Specific rule: moduleId in messageConfig_UDT
                new() { PathPattern = @".*{udt:messageConfig_UDT}\.moduleId$",
                        Constraints = new ValueConstraint { Max = 100 } },
            }
        };

        var comm = MakeMember("comm", "\"messageConfig_UDT\"");
        var moduleId = MakeMember("moduleId", "Int", comm);

        var rule = config.GetRule(moduleId);
        rule.Should().NotBeNull();
        // The UDT-specific rule should win (higher specificity)
        rule!.Constraints!.Max.Should().Be(100);
    }

    [Fact]
    public void GetRule_FallbackToGeneralWhenSpecificDoesNotMatch()
    {
        var config = new BulkChangeConfig
        {
            Rules = new List<MemberRule>
            {
                new() { PathPattern = "moduleId",
                        Constraints = new ValueConstraint { Max = 9999 } },
                new() { PathPattern = @".*{udt:otherConfig_UDT}\.moduleId$",
                        Constraints = new ValueConstraint { Max = 100 } },
            }
        };

        // Parent is NOT otherConfig_UDT
        var comm = MakeMember("comm", "\"messageConfig_UDT\"");
        var moduleId = MakeMember("moduleId", "Int", comm);

        var rule = config.GetRule(moduleId);
        rule.Should().NotBeNull();
        // General rule wins (specific one doesn't match)
        rule!.Constraints!.Max.Should().Be(9999);
    }

    // ===== includeSelf tests =====

    [Fact]
    public void IsMatch_WithIncludeSelf_MatchesUdtInstanceOnSelf()
    {
        // The UDT instance IS messageConfig_UDT — with includeSelf it should match
        var drive = MakeMember("drive1", "\"driveConfig_UDT\"");
        var comm = MakeMember("communicationError", "\"messageConfig_UDT\"", drive);

        PathPatternMatcher.IsMatch(comm, @".*{udt:messageConfig_UDT}$", includeSelf: true)
            .Should().BeTrue();
    }

    [Fact]
    public void IsMatch_WithoutIncludeSelf_DoesNotMatchSelf()
    {
        // Same pattern, but default includeSelf=false — self is excluded from ancestor chain
        var drive = MakeMember("drive1", "\"driveConfig_UDT\"");
        var comm = MakeMember("communicationError", "\"messageConfig_UDT\"", drive);

        PathPatternMatcher.IsMatch(comm, @".*{udt:messageConfig_UDT}$")
            .Should().BeFalse();
    }

    [Fact]
    public void GetCommentRule_MatchesUdtInstance()
    {
        var config = new BulkChangeConfig
        {
            Rules = new List<MemberRule>
            {
                new() { PathPattern = @".*{udt:messageConfig_UDT}$",
                        CommentTemplate = "{db}.{parent}" }
            }
        };

        var drive = MakeMember("drive1", "\"driveConfig_UDT\"");
        var comm = MakeMember("communicationError", "\"messageConfig_UDT\"", drive);

        var rule = config.GetCommentRule(comm);
        rule.Should().NotBeNull();
        rule!.CommentTemplate.Should().Be("{db}.{parent}");
    }

    [Fact]
    public void GetCommentRule_IgnoresRulesWithoutTemplate()
    {
        var config = new BulkChangeConfig
        {
            Rules = new List<MemberRule>
            {
                new() { PathPattern = @".*{udt:messageConfig_UDT}$" } // no commentTemplate
            }
        };

        var comm = MakeMember("communicationError", "\"messageConfig_UDT\"");
        config.GetCommentRule(comm).Should().BeNull();
    }
}
