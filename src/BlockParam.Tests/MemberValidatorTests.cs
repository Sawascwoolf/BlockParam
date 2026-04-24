using FluentAssertions;
using NSubstitute;
using BlockParam.Config;
using BlockParam.Models;
using BlockParam.Services;
using Xunit;

namespace BlockParam.Tests;

public class MemberValidatorTests
{
    private static MemberNode IntMember(string path = "db.moduleId") =>
        new("moduleId", "Int", "0", path, null, Array.Empty<MemberNode>());

    private static TagTableCache CacheWithMod()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.GetTagTableNames().Returns(new[] { "MOD_Halle1" });
        reader.ReadTagTable("MOD_Halle1").Returns(new[]
        {
            new TagTableEntry("MOD_TP310", "42", "Int", ""),
            new TagTableEntry("MOD_TP311", "43", "Int", ""),
        });
        return new TagTableCache(reader);
    }

    private static BulkChangeConfig ConfigWithModRule() => new()
    {
        Rules = new List<MemberRule>
        {
            new()
            {
                PathPattern = @".*\.moduleId$",
                TagTableReference = new TagTableReference { TableName = "MOD_*" },
                Constraints = new ValueConstraint { RequireTagTableValue = true },
            },
        },
    };

    [Fact]
    public void EmptyValue_ReturnsNull()
    {
        var v = new MemberValidator(null, null);
        v.Validate(IntMember(), null).Should().BeNull();
        v.Validate(IntMember(), "").Should().BeNull();
    }

    [Fact]
    public void NoRule_InRangeInt_ReturnsNull()
    {
        var v = new MemberValidator(null, null);
        v.Validate(IntMember(), "42").Should().BeNull();
    }

    [Fact]
    public void NoRule_OutOfRangeInt_ReturnsTypeError()
    {
        var v = new MemberValidator(null, null);
        v.Validate(IntMember(), "99999").Should().NotBeNull();
    }

    [Fact]
    public void TagTableRequired_ValidConstantName_ReturnsNull()
    {
        var v = new MemberValidator(ConfigWithModRule(), CacheWithMod());
        v.Validate(IntMember(), "MOD_TP310").Should().BeNull();
    }

    [Fact]
    public void TagTableRequired_ConstantValue_ReturnsNull()
    {
        // The validator also accepts the tag's value literal (not just the name).
        var v = new MemberValidator(ConfigWithModRule(), CacheWithMod());
        v.Validate(IntMember(), "42").Should().BeNull();
    }

    [Fact]
    public void TagTableRequired_UnknownSymbolicName_ReturnsTagTableError()
    {
        // Regression guard: typing a name-like value that isn't in the table
        // must report the tag-table error, not the datatype format error. If
        // the validator ordering regresses, this flips to "Invalid Int value".
        var v = new MemberValidator(ConfigWithModRule(), CacheWithMod());
        var error = v.Validate(IntMember(), "MOD_TP999");
        error.Should().NotBeNull();
        error.Should().Contain("MOD_*");
    }

    [Fact]
    public void TagTableRequired_NumericNotInTable_ReturnsTagTableError()
    {
        var v = new MemberValidator(ConfigWithModRule(), CacheWithMod());
        var error = v.Validate(IntMember(), "9999");
        error.Should().NotBeNull();
        error.Should().Contain("MOD_*");
    }

    [Fact]
    public void GetHint_Int_NoRule_ReturnsDatatypeFallback()
    {
        var v = new MemberValidator(null, null);
        var hint = v.GetHint(IntMember());
        hint.Should().NotBeNull();
        hint.Should().Contain("Int");
        hint.Should().Contain("-32768");
        hint.Should().Contain("32767");
    }

    [Fact]
    public void GetHint_UsesRuleConstraintOverDatatype()
    {
        var config = new BulkChangeConfig
        {
            Rules = new List<MemberRule>
            {
                new()
                {
                    PathPattern = @".*\.moduleId$",
                    Constraints = new ValueConstraint { Min = 0, Max = 100 },
                },
            },
        };
        var v = new MemberValidator(config, null);
        var hint = v.GetHint(IntMember());
        hint.Should().NotBeNull();
        hint.Should().Contain("0");
        hint.Should().Contain("100");
    }
}
