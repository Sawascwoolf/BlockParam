using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class DbExportCacheDecisionTests
{
    // Decide(forceRefresh, tokenReadable, matchingEntryExists, anyEntryExists)

    [Theory]
    // Token unreadable -> Disabled regardless of everything else.
    [InlineData(false, false, false, false, DbCacheOutcome.Disabled)]
    [InlineData(true, false, true, true, DbCacheOutcome.Disabled)]
    [InlineData(false, false, true, true, DbCacheOutcome.Disabled)]
    // Forced wins over any cached entry (token readable).
    [InlineData(true, true, true, true, DbCacheOutcome.Forced)]
    [InlineData(true, true, false, false, DbCacheOutcome.Forced)]
    // Matching fresh entry -> Hit.
    [InlineData(false, true, true, true, DbCacheOutcome.Hit)]
    // Entry exists but token moved -> Stale.
    [InlineData(false, true, false, true, DbCacheOutcome.Stale)]
    // Nothing cached -> Miss.
    [InlineData(false, true, false, false, DbCacheOutcome.Miss)]
    public void Decide_FollowsPrecedence(
        bool forceRefresh, bool tokenReadable,
        bool matching, bool anyEntry, DbCacheOutcome expected)
    {
        DbExportCacheDecision
            .Decide(forceRefresh, tokenReadable, matching, anyEntry)
            .Should().Be(expected);
    }

    [Fact]
    public void Decide_DisabledTakesPrecedenceOverForced()
    {
        // Both forceRefresh and !tokenReadable true: Disabled must win, because
        // an unreadable token must never let the cache be trusted/written.
        DbExportCacheDecision.Decide(
            forceRefresh: true, tokenReadable: false,
            matchingEntryExists: true, anyEntryExists: true)
            .Should().Be(DbCacheOutcome.Disabled);
    }

    [Theory]
    [InlineData(DbCacheOutcome.Hit, "cache=hit")]
    [InlineData(DbCacheOutcome.Miss, "cache=miss")]
    [InlineData(DbCacheOutcome.Stale, "cache=stale")]
    [InlineData(DbCacheOutcome.Disabled, "cache=disabled")]
    [InlineData(DbCacheOutcome.Forced, "cache=forced")]
    public void Predictor_MapsEachOutcome(DbCacheOutcome outcome, string expected)
    {
        DbExportCacheDecision.Predictor(outcome).Should().Be(expected);
    }
}
