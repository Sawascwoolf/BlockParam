using FluentAssertions;
using BlockParam.Services;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Covers the six TIA temporal datatype families consolidated under
/// <see cref="TemporalFormatParser"/> by issue #171. Each datatype has
/// at least: a canonical accepted form, the prefix variants the type-key
/// map recognizes, a malformed input, and a range-edge case.
/// </summary>
public class TemporalFormatParserTests
{
    // --- TryResolveType / IsTemporalType: key aliases ---

    [Theory]
    [InlineData("Time", TemporalDataType.Time)]
    [InlineData("LTime", TemporalDataType.LTime)]
    [InlineData("S5Time", TemporalDataType.S5Time)]
    [InlineData("Date", TemporalDataType.Date)]
    [InlineData("LDate", TemporalDataType.LDate)]
    [InlineData("Time_Of_Day", TemporalDataType.TimeOfDay)]
    [InlineData("TimeOfDay", TemporalDataType.TimeOfDay)]
    [InlineData("Tod", TemporalDataType.TimeOfDay)]
    [InlineData("LTime_Of_Day", TemporalDataType.LTimeOfDay)]
    [InlineData("LTimeOfDay", TemporalDataType.LTimeOfDay)]
    [InlineData("LTod", TemporalDataType.LTimeOfDay)]
    [InlineData("Date_And_Time", TemporalDataType.DateTime)]
    [InlineData("DateTime", TemporalDataType.DateTime)]
    [InlineData("DT", TemporalDataType.DateTime)]
    [InlineData("LDate_And_Time", TemporalDataType.LDateTime)]
    [InlineData("LDateTime", TemporalDataType.LDateTime)]
    [InlineData("LDT", TemporalDataType.LDateTime)]
    public void TryResolveType_RecognizesAllAliases(string key, TemporalDataType expected)
    {
        TemporalFormatParser.TryResolveType(key, out var resolved).Should().BeTrue();
        resolved.Should().Be(expected);
        TemporalFormatParser.IsTemporalType(key).Should().BeTrue();
    }

    [Theory]
    [InlineData("Int")]
    [InlineData("UDT_Foo")]
    [InlineData("")]
    [InlineData(null)]
    public void TryResolveType_RejectsNonTemporal(string? key)
    {
        TemporalFormatParser.TryResolveType(key, out _).Should().BeFalse();
        TemporalFormatParser.IsTemporalType(key).Should().BeFalse();
    }

    // --- Time ---

    [Theory]
    [InlineData("T#500ms", 500.0)]
    [InlineData("T#1s", 1_000.0)]
    [InlineData("T#2h30m", 2 * 3_600_000.0 + 30 * 60_000.0)]
    [InlineData("T#1d2h3m4s5ms", 86_400_000.0 + 2 * 3_600_000.0 + 3 * 60_000.0 + 4_000.0 + 5.0)]
    [InlineData("T#-500ms", -500.0)]
    public void TryParse_Time_AcceptedForms(string literal, double expectedMs)
    {
        TemporalFormatParser.TryParse(literal, TemporalDataType.Time, out var v).Should().BeTrue();
        v.NumericValue.Should().Be(expectedMs);
        v.Kind.Should().Be(TemporalDataType.Time);
    }

    [Theory]
    [InlineData("500ms")]                  // missing prefix
    [InlineData("T#")]                     // empty body
    [InlineData("T#abc")]                  // junk
    [InlineData("T#1x")]                   // unknown unit
    [InlineData("LT#500ms")]               // wrong prefix for Time
    public void TryParse_Time_RejectsMalformed(string literal)
    {
        TemporalFormatParser.TryParse(literal, TemporalDataType.Time, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_Time_AcceptsS5TPrefix()
    {
        TemporalFormatParser.TryParse("S5T#2s", TemporalDataType.Time, out var v).Should().BeTrue();
        v.NumericValue.Should().Be(2_000.0);
    }

    // --- LTime (only LT# accepted) ---

    [Fact]
    public void TryParse_LTime_AcceptsLTPrefix()
    {
        TemporalFormatParser.TryParse("LT#1us", TemporalDataType.LTime, out var v).Should().BeTrue();
        v.NumericValue.Should().BeApproximately(0.001, 1e-9);
    }

    [Theory]
    [InlineData("T#500ms")]   // T# isn't valid for LTime
    [InlineData("LT#")]
    [InlineData("LT#xyz")]
    public void TryParse_LTime_RejectsMalformed(string literal)
    {
        TemporalFormatParser.TryParse(literal, TemporalDataType.LTime, out _).Should().BeFalse();
    }

    // --- S5Time ---

    [Fact]
    public void TryParse_S5Time_AcceptsBothS5TAndT()
    {
        // S5Time accepts both T# and S5T# (mirrors the validator's behaviour).
        TemporalFormatParser.TryParse("S5T#500ms", TemporalDataType.S5Time, out var v1).Should().BeTrue();
        v1.NumericValue.Should().Be(500.0);

        TemporalFormatParser.TryParse("T#2s", TemporalDataType.S5Time, out var v2).Should().BeTrue();
        v2.NumericValue.Should().Be(2_000.0);
    }

    [Fact]
    public void TryParse_S5Time_RejectsMalformed()
    {
        TemporalFormatParser.TryParse("S5T#nope", TemporalDataType.S5Time, out _).Should().BeFalse();
    }

    // --- Date ---

    [Theory]
    [InlineData("D#2024-01-15")]
    [InlineData("D#1990-01-01")]
    public void TryParse_Date_AcceptedForms(string literal)
    {
        TemporalFormatParser.TryParse(literal, TemporalDataType.Date, out var v).Should().BeTrue();
        v.NumericValue.Should().BeGreaterThan(0); // ticks
    }

    [Fact]
    public void TryParse_Date_RangeEdge_MonthDayBoundary()
    {
        TemporalFormatParser.TryParse("D#2024-12-31", TemporalDataType.Date, out _).Should().BeTrue();
        TemporalFormatParser.TryParse("D#2024-13-01", TemporalDataType.Date, out _).Should().BeFalse();
        TemporalFormatParser.TryParse("D#2024-00-15", TemporalDataType.Date, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("2024-01-15")]    // missing prefix
    [InlineData("D#24-01-15")]    // 2-digit year
    [InlineData("D#nope")]
    [InlineData("D#")]
    public void TryParse_Date_RejectsMalformed(string literal)
    {
        TemporalFormatParser.TryParse(literal, TemporalDataType.Date, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_LDate_UsesDHashPrefix()
    {
        // LDate shares D# with Date (per the existing validator mapping).
        TemporalFormatParser.TryParse("D#2024-06-01", TemporalDataType.LDate, out var v).Should().BeTrue();
        v.Kind.Should().Be(TemporalDataType.LDate);
    }

    // --- TimeOfDay ---

    [Theory]
    [InlineData("TOD#00:00:00", 0.0)]
    [InlineData("TOD#12:30:00", (12 * 3600.0 + 30 * 60.0) * 1000.0)]
    [InlineData("TOD#23:59:59", (23 * 3600.0 + 59 * 60.0 + 59.0) * 1000.0)]
    public void TryParse_TimeOfDay_AcceptedForms(string literal, double expectedMs)
    {
        TemporalFormatParser.TryParse(literal, TemporalDataType.TimeOfDay, out var v).Should().BeTrue();
        v.NumericValue.Should().BeApproximately(expectedMs, 1e-6);
    }

    [Theory]
    [InlineData("12:30:00")]          // missing prefix
    [InlineData("TOD#24:00:00")]      // out-of-range hour rejected by TimeSpan parser
    [InlineData("TOD#12:60:00")]      // out-of-range minute
    [InlineData("TOD#nope")]
    public void TryParse_TimeOfDay_RejectsMalformed(string literal)
    {
        TemporalFormatParser.TryParse(literal, TemporalDataType.TimeOfDay, out _).Should().BeFalse();
    }

    // --- LTimeOfDay ---

    [Fact]
    public void TryParse_LTimeOfDay_AcceptsFractionalAndPlain()
    {
        TemporalFormatParser.TryParse("LTOD#12:30:00.123", TemporalDataType.LTimeOfDay, out var v1).Should().BeTrue();
        v1.NumericValue.Should().BeApproximately((12 * 3600.0 + 30 * 60.0) * 1000.0 + 123.0, 1e-6);

        // The LTOD# body validator also accepts the plain TOD-body form.
        TemporalFormatParser.TryParse("LTOD#12:30:00", TemporalDataType.LTimeOfDay, out var v2).Should().BeTrue();
        v2.NumericValue.Should().BeApproximately((12 * 3600.0 + 30 * 60.0) * 1000.0, 1e-6);
    }

    [Theory]
    [InlineData("TOD#12:30:00")]      // wrong prefix
    [InlineData("LTOD#24:00:00")]     // out-of-range hour
    [InlineData("LTOD#nope")]
    public void TryParse_LTimeOfDay_RejectsMalformed(string literal)
    {
        TemporalFormatParser.TryParse(literal, TemporalDataType.LTimeOfDay, out _).Should().BeFalse();
    }

    // --- DateTime ---

    [Fact]
    public void TryParse_DateTime_AcceptedForms()
    {
        TemporalFormatParser.TryParse("DT#2024-01-15-12:30:00", TemporalDataType.DateTime, out var v).Should().BeTrue();
        v.NumericValue.Should().BeGreaterThan(0);
        v.Kind.Should().Be(TemporalDataType.DateTime);
    }

    [Fact]
    public void TryParse_DateTime_RangeEdge_HourBoundary()
    {
        TemporalFormatParser.TryParse("DT#2024-01-15-23:59:59", TemporalDataType.DateTime, out _).Should().BeTrue();
        TemporalFormatParser.TryParse("DT#2024-01-15-24:00:00", TemporalDataType.DateTime, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("2024-01-15-12:30:00")]    // missing prefix
    [InlineData("DT#2024-01-15 12:30:00")] // space separator (TIA uses dash)
    [InlineData("DT#nope")]
    public void TryParse_DateTime_RejectsMalformed(string literal)
    {
        TemporalFormatParser.TryParse(literal, TemporalDataType.DateTime, out _).Should().BeFalse();
    }

    // --- LDateTime ---

    [Fact]
    public void TryParse_LDateTime_AcceptsFractionalAndPlain()
    {
        TemporalFormatParser.TryParse("LDT#2024-01-15-12:30:00.1234567", TemporalDataType.LDateTime, out var v1).Should().BeTrue();
        v1.NumericValue.Should().BeGreaterThan(0);

        TemporalFormatParser.TryParse("LDT#2024-01-15-12:30:00", TemporalDataType.LDateTime, out var v2).Should().BeTrue();
        v2.NumericValue.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("DT#2024-01-15-12:30:00")]   // wrong prefix
    [InlineData("LDT#2024-01-15-24:00:00")]  // hour overflow
    public void TryParse_LDateTime_RejectsMalformed(string literal)
    {
        TemporalFormatParser.TryParse(literal, TemporalDataType.LDateTime, out _).Should().BeFalse();
    }

    // --- Body-only validation paths used by the validator ---

    [Theory]
    [InlineData("500ms", true)]
    [InlineData("2h30m", true)]
    [InlineData("-1s", true)]
    [InlineData("", false)]
    [InlineData("-", false)]
    [InlineData("abc", false)]
    public void IsValidTimeBody_Matches(string body, bool expected)
        => TemporalFormatParser.IsValidTimeBody(body).Should().Be(expected);

    [Theory]
    [InlineData("2024-01-15", true)]
    [InlineData("24-01-15", false)]
    [InlineData("nope", false)]
    public void IsValidDateBody_Matches(string body, bool expected)
        => TemporalFormatParser.IsValidDateBody(body).Should().Be(expected);

    [Theory]
    [InlineData("12:30:00", true)]
    [InlineData("12:30:00.123", false)]  // plain TOD body rejects fractional
    [InlineData("nope", false)]
    public void IsValidTimeOfDayBody_Matches(string body, bool expected)
        => TemporalFormatParser.IsValidTimeOfDayBody(body).Should().Be(expected);

    [Theory]
    [InlineData("12:30:00", true)]
    [InlineData("12:30:00.123", true)]
    [InlineData("nope", false)]
    public void IsValidLTimeOfDayBody_AcceptsBoth(string body, bool expected)
        => TemporalFormatParser.IsValidLTimeOfDayBody(body).Should().Be(expected);

    [Theory]
    [InlineData("2024-01-15-12:30:00", true)]
    [InlineData("2024-01-15-12:30:00.123", false)]
    [InlineData("nope", false)]
    public void IsValidDateTimeBody_Matches(string body, bool expected)
        => TemporalFormatParser.IsValidDateTimeBody(body).Should().Be(expected);

    [Theory]
    [InlineData("2024-01-15-12:30:00", true)]
    [InlineData("2024-01-15-12:30:00.1234567", true)]
    [InlineData("nope", false)]
    public void IsValidLDateTimeBody_AcceptsBoth(string body, bool expected)
        => TemporalFormatParser.IsValidLDateTimeBody(body).Should().Be(expected);

    // --- Empty / null literal handling ---

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_EmptyLiteral_ReturnsFalse(string? literal)
    {
        TemporalFormatParser.TryParse(literal, TemporalDataType.Time, out _).Should().BeFalse();
    }
}
