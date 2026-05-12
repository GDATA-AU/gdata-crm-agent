using CrmAgent.Services;

namespace CrmAgent.Tests;

public class BackoffStrategyTests
{
    private static BackoffStrategy CreateAuthBackoff(TimeSpan? baseInterval = null) => new(
        baseInterval ?? TimeSpan.FromSeconds(5),
        attemptsBeforeBackoff: 5,
        [
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1),
        ]);

    private static BackoffStrategy CreateTransientBackoff(TimeSpan? baseInterval = null) => new(
        baseInterval ?? TimeSpan.FromSeconds(5),
        attemptsBeforeBackoff: 0,
        [
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromMinutes(5),
        ]);

    // -----------------------------------------------------------------------
    // Auth backoff
    // -----------------------------------------------------------------------

    [Fact]
    public void Auth_ReturnsBaseInterval_DuringInitialAttempts()
    {
        var backoff = CreateAuthBackoff();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(TimeSpan.FromSeconds(5), backoff.RecordFailure());
        }
    }

    [Fact]
    public void Auth_EscalatesThroughTiers_AfterThreshold()
    {
        var backoff = CreateAuthBackoff();

        // Burn through the 5 initial attempts
        for (var i = 0; i < 5; i++) backoff.RecordFailure();

        Assert.Equal(TimeSpan.FromMinutes(1), backoff.RecordFailure());
        Assert.Equal(TimeSpan.FromMinutes(10), backoff.RecordFailure());
        Assert.Equal(TimeSpan.FromMinutes(30), backoff.RecordFailure());
        Assert.Equal(TimeSpan.FromHours(1), backoff.RecordFailure());
    }

    [Fact]
    public void Auth_CapsAtLastTier()
    {
        var backoff = CreateAuthBackoff();

        // 5 initial + 4 tiers = 9, then further attempts stay at cap
        for (var i = 0; i < 9; i++) backoff.RecordFailure();

        Assert.Equal(TimeSpan.FromHours(1), backoff.RecordFailure());
        Assert.Equal(TimeSpan.FromHours(1), backoff.RecordFailure());
        Assert.Equal(TimeSpan.FromHours(1), backoff.RecordFailure());
    }

    // -----------------------------------------------------------------------
    // Transient backoff
    // -----------------------------------------------------------------------

    [Fact]
    public void Transient_EscalatesImmediately()
    {
        var backoff = CreateTransientBackoff();

        Assert.Equal(TimeSpan.FromSeconds(5), backoff.RecordFailure());
        Assert.Equal(TimeSpan.FromSeconds(10), backoff.RecordFailure());
        Assert.Equal(TimeSpan.FromSeconds(20), backoff.RecordFailure());
        Assert.Equal(TimeSpan.FromSeconds(40), backoff.RecordFailure());
        Assert.Equal(TimeSpan.FromSeconds(60), backoff.RecordFailure());
        Assert.Equal(TimeSpan.FromMinutes(5), backoff.RecordFailure());
    }

    [Fact]
    public void Transient_CapsAtLastTier()
    {
        var backoff = CreateTransientBackoff();

        // Walk through all 6 tiers
        for (var i = 0; i < 6; i++) backoff.RecordFailure();

        Assert.Equal(TimeSpan.FromMinutes(5), backoff.RecordFailure());
        Assert.Equal(TimeSpan.FromMinutes(5), backoff.RecordFailure());
    }

    // -----------------------------------------------------------------------
    // Reset
    // -----------------------------------------------------------------------

    [Fact]
    public void Reset_ReturnsToInitialState()
    {
        var backoff = CreateAuthBackoff();

        // Escalate past threshold
        for (var i = 0; i < 7; i++) backoff.RecordFailure();
        Assert.True(backoff.ConsecutiveFailures > 0);

        backoff.Reset();

        Assert.Equal(0, backoff.ConsecutiveFailures);
        // First failure after reset should return base interval
        Assert.Equal(TimeSpan.FromSeconds(5), backoff.RecordFailure());
    }

    // -----------------------------------------------------------------------
    // ConsecutiveFailures tracking
    // -----------------------------------------------------------------------

    [Fact]
    public void ConsecutiveFailures_IncrementsOnEachFailure()
    {
        var backoff = CreateTransientBackoff();

        Assert.Equal(0, backoff.ConsecutiveFailures);
        backoff.RecordFailure();
        Assert.Equal(1, backoff.ConsecutiveFailures);
        backoff.RecordFailure();
        Assert.Equal(2, backoff.ConsecutiveFailures);
    }

    // -----------------------------------------------------------------------
    // Constructor validation
    // -----------------------------------------------------------------------

    [Fact]
    public void Constructor_ThrowsWhenTiersEmpty()
    {
        Assert.Throws<ArgumentException>(() =>
            new BackoffStrategy(TimeSpan.FromSeconds(5), 0, []));
    }

    [Fact]
    public void Constructor_ThrowsWhenBaseIntervalNotPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BackoffStrategy(TimeSpan.Zero, 0, [TimeSpan.FromSeconds(5)]));
    }

    [Fact]
    public void Constructor_ThrowsWhenAttemptsBeforeBackoffNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BackoffStrategy(TimeSpan.FromSeconds(5), -1, [TimeSpan.FromSeconds(5)]));
    }
}
