using CrmAgent.Services;
using Serilog;

namespace CrmAgent;

/// <summary>
/// Last-resort liveness watchdog. Runs alongside <see cref="AgentWorker"/> and watches the
/// elapsed time of the in-flight job via <see cref="AgentLiveness"/>.
///
/// The per-job deadline in <see cref="AgentWorker"/> handles the common case by tripping the
/// job's cancellation token, which every SQL/blob/HTTP call observes. This watchdog exists for
/// the pathological case where a job is genuinely <em>wedged</em> — blocked in native/driver code
/// that ignores cancellation — so cooperative cancellation never takes effect and the poll loop
/// can never return. When that happens the only way to recover is to exit the process and let the
/// service manager start a fresh one:
///   • Linux  — systemd <c>Restart=always</c> (see install-linux.sh)
///   • Windows — <c>sc failure … actions= restart/…</c> (see Setup.bat)
/// Both treat a non-zero / abnormal exit as a failure, which <see cref="Environment.FailFast"/>
/// guarantees. This is what removes the need to phone the customer to restart the service.
/// </summary>
public sealed class WatchdogService : BackgroundService
{
    private readonly AgentLiveness _liveness;
    private readonly AgentConfig _config;
    private readonly ILogger<WatchdogService> _logger;

    public WatchdogService(AgentLiveness liveness, AgentConfig config, ILogger<WatchdogService> logger)
    {
        _liveness = liveness;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// The point past a job's start at which a still-running job is considered wedged: the
    /// cooperative deadline plus the grace period that allows in-flight cancellation to unwind.
    /// </summary>
    internal TimeSpan WatchdogLimit =>
        TimeSpan.FromSeconds(_config.MaxJobDurationSeconds + _config.WatchdogGraceSeconds);

    /// <summary>
    /// Pure decision function, kept separate from <see cref="Environment.FailFast"/> so it can be
    /// unit-tested. Returns <c>true</c> when a job is active and has exceeded the watchdog limit.
    /// </summary>
    internal static bool ShouldForceRestart(TimeSpan? activeJobElapsed, TimeSpan limit)
        => activeJobElapsed is { } elapsed && elapsed >= limit;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A grace of 0 (or a disabled per-job deadline) disables the hard watchdog. Cooperative
        // cancellation still applies; we just never force-kill the process.
        if (_config.WatchdogGraceSeconds <= 0 || _config.MaxJobDurationSeconds <= 0)
        {
            _logger.LogInformation("Hard watchdog disabled (MaxJobDurationSeconds={Max}, WatchdogGraceSeconds={Grace})",
                _config.MaxJobDurationSeconds, _config.WatchdogGraceSeconds);
            return;
        }

        var limit = WatchdogLimit;
        // Check often enough to react promptly without busy-spinning: ~6 checks per limit window,
        // clamped to a sane 5s–60s range.
        var checkInterval = TimeSpan.FromSeconds(Math.Clamp(limit.TotalSeconds / 6, 5, 60));

        _logger.LogInformation(
            "Watchdog started (limit={LimitSec}s, checkInterval={CheckSec}s)",
            (int)limit.TotalSeconds, (int)checkInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(checkInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // graceful shutdown
            }

            var elapsed = _liveness.ActiveJobElapsed;
            if (!ShouldForceRestart(elapsed, limit))
                continue;

            var jobId = _liveness.ActiveJobId ?? "(unknown)";
            _logger.LogCritical(
                "WATCHDOG: job {JobId} has run {ElapsedSec}s (limit {LimitSec}s) and did not honour cancellation — " +
                "forcing process termination so the service manager restarts a clean agent",
                jobId, (int)(elapsed?.TotalSeconds ?? 0), (int)limit.TotalSeconds);

            // Flush logs so the reason for the restart survives in agent.log / journald.
            await Log.CloseAndFlushAsync();

            // Abnormal exit → systemd/SCM treats it as a failure and restarts per the configured policy.
            Environment.FailFast(
                $"Watchdog: job {jobId} wedged for {(int)(elapsed?.TotalSeconds ?? 0)}s without honouring cancellation.");
        }

        _logger.LogInformation("Watchdog stopped");
    }
}
