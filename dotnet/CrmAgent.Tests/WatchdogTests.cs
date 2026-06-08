using CrmAgent;
using CrmAgent.Services;

namespace CrmAgent.Tests;

public class WatchdogTests
{
    /// <summary>
    /// Controllable <see cref="TimeProvider"/> so tests can advance the clock deterministically
    /// without sleeping. <see cref="TimeProvider.GetElapsedTime"/> is computed from
    /// <see cref="GetTimestamp"/> and <see cref="TimestampFrequency"/>.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan delta) => _timestamp += delta.Ticks;
    }

    // -----------------------------------------------------------------------
    // AgentLiveness
    // -----------------------------------------------------------------------

    [Fact]
    public void Liveness_NoElapsed_WhenIdle()
    {
        var liveness = new AgentLiveness(new FakeTimeProvider());
        Assert.Null(liveness.ActiveJobId);
        Assert.Null(liveness.ActiveJobElapsed);
    }

    [Fact]
    public void Liveness_TracksElapsed_WhileJobActive()
    {
        var clock = new FakeTimeProvider();
        var liveness = new AgentLiveness(clock);

        liveness.BeginJob("job-1");
        clock.Advance(TimeSpan.FromSeconds(90));

        Assert.Equal("job-1", liveness.ActiveJobId);
        Assert.Equal(TimeSpan.FromSeconds(90), liveness.ActiveJobElapsed);
    }

    [Fact]
    public void Liveness_ClearsElapsed_AfterEndJob()
    {
        var clock = new FakeTimeProvider();
        var liveness = new AgentLiveness(clock);

        liveness.BeginJob("job-1");
        clock.Advance(TimeSpan.FromSeconds(10));
        liveness.EndJob();

        Assert.Null(liveness.ActiveJobId);
        Assert.Null(liveness.ActiveJobElapsed);
    }

    [Fact]
    public void Liveness_ResetsStartTime_OnNewJob()
    {
        var clock = new FakeTimeProvider();
        var liveness = new AgentLiveness(clock);

        liveness.BeginJob("job-1");
        clock.Advance(TimeSpan.FromSeconds(100));
        liveness.EndJob();

        liveness.BeginJob("job-2");
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal("job-2", liveness.ActiveJobId);
        Assert.Equal(TimeSpan.FromSeconds(5), liveness.ActiveJobElapsed);
    }

    // -----------------------------------------------------------------------
    // WatchdogService.ShouldForceRestart
    // -----------------------------------------------------------------------

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2100); // 1800 + 300 grace

    [Fact]
    public void Watchdog_DoesNotFire_WhenIdle()
    {
        Assert.False(WatchdogService.ShouldForceRestart(null, Limit));
    }

    [Fact]
    public void Watchdog_DoesNotFire_BelowLimit()
    {
        Assert.False(WatchdogService.ShouldForceRestart(TimeSpan.FromSeconds(2099), Limit));
    }

    [Fact]
    public void Watchdog_Fires_AtLimit()
    {
        Assert.True(WatchdogService.ShouldForceRestart(Limit, Limit));
    }

    [Fact]
    public void Watchdog_Fires_AboveLimit()
    {
        Assert.True(WatchdogService.ShouldForceRestart(TimeSpan.FromSeconds(5000), Limit));
    }

    [Fact]
    public void Watchdog_Limit_IsDeadlinePlusGrace()
    {
        var svc = new WatchdogService(
            new AgentLiveness(new FakeTimeProvider()),
            new AgentConfig
            {
                PortalUrl = "https://example.test",
                AgentApiKey = "k",
                AzureStorageConnectionString = "x",
                MaxJobDurationSeconds = 1800,
                WatchdogGraceSeconds = 300,
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WatchdogService>.Instance);

        Assert.Equal(TimeSpan.FromSeconds(2100), svc.WatchdogLimit);
    }
}
