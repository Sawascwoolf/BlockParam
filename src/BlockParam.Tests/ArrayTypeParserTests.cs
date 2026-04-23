using FluentAssertions;
using BlockParam.SimaticML;
using Xunit;

namespace BlockParam.Tests;

public class ArrayTypeParserTests
{
    [Fact]
    public void TryParse_SimpleIntArray_ReturnsBoundsAndElement()
    {
        ArrayTypeParser.TryParse("Array[0..4] of Int", out var info).Should().BeTrue();
        info!.Dimensions.Should().HaveCount(1);
        info!.Dimensions[0].LowerBoundToken.Should().Be("0");
        info!.Dimensions[0].UpperBoundToken.Should().Be("4");
        info!.ElementType.Should().Be("Int");
        info!.IsMultiDimensional.Should().BeFalse();
    }

    [Fact]
    public void TryParse_NonZeroLowerBound_ParsedCorrectly()
    {
        ArrayTypeParser.TryParse("Array[5..17] of Real", out var info).Should().BeTrue();
        info!.Dimensions[0].LowerBoundToken.Should().Be("5");
        info!.Dimensions[0].UpperBoundToken.Should().Be("17");
        info!.Dimensions[0].LowerIsLiteral.Should().BeTrue();
        info!.Dimensions[0].UpperIsLiteral.Should().BeTrue();
        info!.ElementType.Should().Be("Real");
    }

    [Fact]
    public void TryParse_NegativeLowerBound_ParsedCorrectly()
    {
        ArrayTypeParser.TryParse("Array[-3..3] of Int", out var info).Should().BeTrue();
        info!.Dimensions[0].LowerBoundToken.Should().Be("-3");
        info!.Dimensions[0].UpperBoundToken.Should().Be("3");
        info!.Dimensions[0].LowerIsLiteral.Should().BeTrue();
    }

    [Fact]
    public void TryParse_SymbolicUpperBound_FlaggedAsNonLiteral()
    {
        ArrayTypeParser.TryParse("Array[1..MAX_VALVES] of Int", out var info).Should().BeTrue();
        info!.Dimensions[0].LowerIsLiteral.Should().BeTrue();
        info!.Dimensions[0].UpperBoundToken.Should().Be("MAX_VALVES");
        info!.Dimensions[0].UpperIsLiteral.Should().BeFalse();
    }

    [Fact]
    public void TryParse_QuotedUdtElement_PreservesQuotes()
    {
        ArrayTypeParser.TryParse("Array[1..3] of \"UDT_Motor\"", out var info).Should().BeTrue();
        info!.ElementType.Should().Be("\"UDT_Motor\"");
    }

    [Fact]
    public void TryParse_StructElement_PreservedAsIs()
    {
        ArrayTypeParser.TryParse("Array[0..2] of Struct", out var info).Should().BeTrue();
        info!.ElementType.Should().Be("Struct");
    }

    [Fact]
    public void TryParse_MultiDim_ReturnsAllDimensions()
    {
        ArrayTypeParser.TryParse("Array[0..2, 0..1] of Int", out var info).Should().BeTrue();
        info!.Dimensions.Should().HaveCount(2);
        info!.Dimensions[0].LowerBoundToken.Should().Be("0");
        info!.Dimensions[0].UpperBoundToken.Should().Be("2");
        info!.Dimensions[1].LowerBoundToken.Should().Be("0");
        info!.Dimensions[1].UpperBoundToken.Should().Be("1");
        info!.IsMultiDimensional.Should().BeTrue();
    }

    [Fact]
    public void TryParse_ThreeDim_Works()
    {
        ArrayTypeParser.TryParse("Array[0..1, 0..1, 0..1] of Bool", out var info).Should().BeTrue();
        info!.Dimensions.Should().HaveCount(3);
        info!.IsMultiDimensional.Should().BeTrue();
    }

    [Fact]
    public void TryParse_CaseInsensitive_Works()
    {
        ArrayTypeParser.TryParse("ARRAY[0..4] OF Int", out var info).Should().BeTrue();
        info!.Dimensions[0].UpperBoundToken.Should().Be("4");
    }

    [Fact]
    public void TryParse_NonArray_ReturnsFalse()
    {
        ArrayTypeParser.TryParse("Int", out _).Should().BeFalse();
        ArrayTypeParser.TryParse("Struct", out _).Should().BeFalse();
        ArrayTypeParser.TryParse("\"UDT_Motor\"", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_MalformedBounds_ReturnsFalse()
    {
        ArrayTypeParser.TryParse("Array[no range] of Int", out _).Should().BeFalse();
        ArrayTypeParser.TryParse("Array[] of Int", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_QuotedSymbolicBound_StripsQuotesForResolverLookup()
    {
        // TIA V20 exports surround constant names with quotes:
        //   Array["MOD_TP307"..2, 1..4] of Struct
        // The cache stores names without quotes, so the token must be unquoted.
        ArrayTypeParser.TryParse("Array[\"MOD_TP307\"..2, 1..4] of Struct", out var info)
            .Should().BeTrue();
        info!.Dimensions[0].LowerBoundToken.Should().Be("MOD_TP307");
        info!.Dimensions[0].LowerIsLiteral.Should().BeFalse();
        info!.Dimensions[0].UpperBoundToken.Should().Be("2");
        info!.Dimensions[1].LowerBoundToken.Should().Be("1");
        info!.Dimensions[1].UpperBoundToken.Should().Be("4");
    }

    [Fact]
    public void TryParse_EmptyOrNull_ReturnsFalse()
    {
        ArrayTypeParser.TryParse("", out _).Should().BeFalse();
        ArrayTypeParser.TryParse("   ", out _).Should().BeFalse();
    }
}
