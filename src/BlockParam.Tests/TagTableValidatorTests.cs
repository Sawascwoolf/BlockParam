using FluentAssertions;
using NSubstitute;
using BlockParam.Config;
using BlockParam.Models;
using BlockParam.Services;
using Xunit;

namespace BlockParam.Tests;

public class TagTableValidatorTests
{
    private static TagTableCache CreateCache()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.GetTagTableNames().Returns(new[] { "MOD_Halle1" });
        reader.ReadTagTable("MOD_Halle1").Returns(new[]
        {
            new TagTableEntry("MOD_FOERDERER", "42", "Int", "Förderer"),
            new TagTableEntry("MOD_VERPACKUNG", "43", "Int", "Verpackung"),
        });
        return new TagTableCache(reader);
    }

    [Fact]
    public void RequireTagTable_ValidConstant_Accepted()
    {
        var validator = new TagTableValidator(CreateCache());
        var rule = new MemberRule
        {
            PathPattern = @".*\\\.moduleId",
            TagTableReference = new TagTableReference { TableName = "MOD_*" },
            Constraints = new ValueConstraint { RequireTagTableValue = true }
        };

        validator.Validate("42", rule).Should().BeNull();
    }

    [Fact]
    public void RequireTagTable_InvalidValue_Rejected()
    {
        var validator = new TagTableValidator(CreateCache());
        var rule = new MemberRule
        {
            PathPattern = @".*\\\.moduleId",
            TagTableReference = new TagTableReference { TableName = "MOD_*" },
            Constraints = new ValueConstraint { RequireTagTableValue = true }
        };

        validator.Validate("9999", rule).Should().NotBeNull();
        validator.Validate("9999", rule).Should().Contain("9999");
    }

    [Fact]
    public void RequireTagTable_FlagFalse_AnyValueOk()
    {
        var validator = new TagTableValidator(CreateCache());
        var rule = new MemberRule
        {
            PathPattern = @".*\\\.moduleId",
            TagTableReference = new TagTableReference { TableName = "MOD_*" },
            Constraints = new ValueConstraint { RequireTagTableValue = false }
        };

        validator.Validate("9999", rule).Should().BeNull();
    }

    [Fact]
    public void RequireTagTable_NoTagTableRef_Ignored()
    {
        var validator = new TagTableValidator(CreateCache());
        var rule = new MemberRule
        {
            PathPattern = @".*\\\.moduleId",
            Constraints = new ValueConstraint { RequireTagTableValue = true }
        };

        validator.Validate("9999", rule).Should().BeNull();
    }

    [Fact]
    public void RequireTagTable_NoConstraints_Ignored()
    {
        var validator = new TagTableValidator(CreateCache());
        var rule = new MemberRule { PathPattern = @".*\\\.moduleId" };

        validator.Validate("anything", rule).Should().BeNull();
    }
}
