using System.Text.RegularExpressions;
using CrmAgent.Handlers;
using CrmAgent.Models;
using CrmAgent.Services;

namespace CrmAgent;

/// <summary>
/// The main agent poll loop, implemented as a <see cref="BackgroundService"/>.
/// Polls the portal for jobs, executes them, and reports results.
/// Runs until the host requests a graceful shutdown via the cancellation token.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private readonly AgentConfig _config;
    private readonly PortalClient _portal;
    private readonly HandlerFactory _handlers;
    private readonly ILogger<AgentWorker> _logger;

    public AgentWorker(
        AgentConfig config,
        PortalClient portal,
        HandlerFactory handlers,
        ILogger<AgentWorker> logger)
    {
        _config = config;
        _portal = portal;
        _handlers = handlers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agent poll loop started (interval={PollIntervalMs}ms)", _config.PollIntervalMs);

        // Safety net: if the inner loop ever exits unexpectedly (without the
        // stoppingToken being cancelled), restart it automatically.  This
        // guards against future bugs that could leave the agent in a zombie
        // state (process alive but not polling).
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPollLoopAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Poll loop crashed unexpectedly — restarting in 10s");
                await WaitAsync(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("Agent poll loop stopped");
    }

    private async Task RunPollLoopAsync(CancellationToken stoppingToken)
    {
        var baseInterval = TimeSpan.FromMilliseconds(_config.PollIntervalMs);

        // Auth failures (401/403): 5 attempts at base interval, then 1m → 10m → 30m → 1h cap
        var authBackoff = new BackoffStrategy(baseInterval, 5, [
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1),
        ]);

        // Transient errors (5xx/network): immediate escalation 5s → 10s → 20s → 40s → 60s → 5m cap
        var transientBackoff = new BackoffStrategy(baseInterval, 0, [
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromMinutes(5),
        ]);

        while (!stoppingToken.IsCancellationRequested)
        {
            // ---------------------------------------------------------------
            // Poll for a job
            // ---------------------------------------------------------------
            PollResult pollResult;
            try
            {
                pollResult = await _portal.PollForJobAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var delay = transientBackoff.RecordFailure();
                _logger.LogWarning(ex,
                    "Failed to poll for job (attempt {Attempt}, next retry in {DelaySec}s)",
                    transientBackoff.ConsecutiveFailures, (int)delay.TotalSeconds);
                await WaitAsync(delay, stoppingToken);
                continue;
            }

            // ---------------------------------------------------------------
            // Handle non-success status codes with appropriate backoff
            // ---------------------------------------------------------------
            if (pollResult.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
            {
                var delay = authBackoff.RecordFailure();
                _logger.LogWarning(
                    "API key rejected by server (HTTP {StatusCode}). Agent may have been deregistered or deactivated. " +
                    "Attempt {Attempt}, next retry in {DelaySec}s",
                    (int)pollResult.StatusCode, authBackoff.ConsecutiveFailures, (int)delay.TotalSeconds);
                await WaitAsync(delay, stoppingToken);
                continue;
            }

            if (pollResult.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var delay = pollResult.RetryAfter ?? transientBackoff.RecordFailure();
                _logger.LogWarning(
                    "Rate-limited by server (HTTP 429). Next retry in {DelaySec}s",
                    (int)delay.TotalSeconds);
                if (pollResult.RetryAfter is not null)
                {
                    // Server told us when to retry — don't escalate
                    transientBackoff.Reset();
                }
                await WaitAsync(delay, stoppingToken);
                continue;
            }

            if ((int)pollResult.StatusCode >= 500)
            {
                var delay = transientBackoff.RecordFailure();
                _logger.LogWarning(
                    "Server error (HTTP {StatusCode}). Attempt {Attempt}, next retry in {DelaySec}s",
                    (int)pollResult.StatusCode, transientBackoff.ConsecutiveFailures, (int)delay.TotalSeconds);
                await WaitAsync(delay, stoppingToken);
                continue;
            }

            // ---------------------------------------------------------------
            // Success — reset both backoff counters
            // ---------------------------------------------------------------
            if (authBackoff.ConsecutiveFailures > 0 || transientBackoff.ConsecutiveFailures > 0)
            {
                _logger.LogInformation("Connection restored — resuming normal polling");
            }
            authBackoff.Reset();
            transientBackoff.Reset();

            var job = pollResult.Job;

            if (job is null)
            {
                _logger.LogDebug("No job available — sleeping");
                await WaitAsync(baseInterval, stoppingToken);
                continue;
            }

            _logger.LogInformation("Job received: {JobId} type={JobType} blobPath={BlobPath}",
                job.Id, job.Type, job.Config.BlobPath ?? job.BlobPath);
            // Log full config at Debug only — params/auth details must not appear in production Info logs.
            _logger.LogDebug(
                "Job config: {JobId} baseUrl={BaseUrl} method={Method} authType={AuthType} " +
                "paramKeys={ParamKeys} paginationType={PaginationType} pageSize={PageSize} dataField={DataField} " +
                "pageParam={PageParam} pageSizeParam={PageSizeParam} hashFields={HashFields} blobPath={BlobPath}",
                job.Id,
                job.Config.BaseUrl,
                job.Config.Method,
                job.Config.Auth?.Type,
                job.Config.Params is not null ? string.Join(", ", job.Config.Params.Keys) : null,
                job.Config.Pagination?.Type,
                job.Config.Pagination?.PageSize,
                job.Config.Pagination?.DataField,
                job.Config.Pagination?.PageParam,
                job.Config.Pagination?.PageSizeParam,
                job.Config.HashFields,
                job.Config.BlobPath);

            // Ping jobs are heartbeat checks from the portal. They are marked
            // completed immediately with no handler execution or blob output.
            if (job.Type == JobType.Ping)
            {
                _logger.LogInformation("Ping job received: {JobId} - completing immediately", job.Id);

                await _portal.ReportJobStatusAsync(job.Id, new JobStatusUpdate
                {
                    Status = JobStatus.Completed,
                }, stoppingToken);

                _logger.LogInformation("Ping job completed: {JobId}", job.Id);
                continue;
            }

            // ---------------------------------------------------------------
            // Report "running"
            // ---------------------------------------------------------------
            await _portal.ReportJobStatusAsync(job.Id, new JobStatusUpdate { Status = JobStatus.Running }, stoppingToken);

            if (job.Preview)
            {
                // Preview mode: no heartbeat, return rows inline.
                try
                {
                    var handler = _handlers.GetHandler(job);
                    var result = await handler.ExecuteAsync(job, _ => { }, stoppingToken);

                    await _portal.ReportJobStatusAsync(job.Id, new JobStatusUpdate
                    {
                        Status = JobStatus.Completed,
                        Progress = new JobProgress
                        {
                            ProcessedRows = result.ProcessedRows,
                            PreviewData = result.PreviewRows,
                        },
                    }, stoppingToken);

                    _logger.LogInformation("Preview job completed: {JobId} rows={Rows}", job.Id, result.ProcessedRows);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Preview job {JobId} cancelled due to shutdown", job.Id);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Preview job failed: {JobId}", job.Id);

                    await _portal.ReportJobStatusAsync(job.Id, new JobStatusUpdate
                    {
                        Status = JobStatus.Failed,
                        Error = SanitizeErrorMessage(ex),
                    }, CancellationToken.None);
                }

                await WaitAsync(baseInterval, stoppingToken);
                continue;
            }

            // ---------------------------------------------------------------
            // Start heartbeat timer
            // ---------------------------------------------------------------
            var progressLock = new object();
            var lastProgress = new JobProgress { ProcessedRows = 0 };
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var heartbeatTask = RunHeartbeatAsync(job.Id, () => { lock (progressLock) { return lastProgress; } }, heartbeatCts.Token);

            // ---------------------------------------------------------------
            // Execute the handler
            // ---------------------------------------------------------------
            try
            {
                var handler = _handlers.GetHandler(job);

                var result = await handler.ExecuteAsync(job, progress =>
                {
                    lock (progressLock) { lastProgress = progress; }
                }, stoppingToken);

                // Stop heartbeat
                await heartbeatCts.CancelAsync();
                await AwaitHeartbeat(heartbeatTask);

                // Report completion
                if (string.IsNullOrEmpty(result.BlobName))
                    throw new InvalidOperationException($"Handler returned no BlobName for non-preview job {job.Id}.");

                await _portal.ReportJobStatusAsync(job.Id, new JobStatusUpdate
                {
                    Status = JobStatus.Completed,
                    Progress = new JobProgress { ProcessedRows = result.ProcessedRows },
                    BlobName = result.BlobName,
                }, stoppingToken);

                _logger.LogInformation("Job completed: {JobId} rows={Rows} blob={BlobName}",
                    job.Id, result.ProcessedRows, result.BlobName);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await heartbeatCts.CancelAsync();
                await AwaitHeartbeat(heartbeatTask);
                _logger.LogInformation("Job {JobId} cancelled due to shutdown", job.Id);
                break;
            }
            catch (Exception ex)
            {
                await heartbeatCts.CancelAsync();
                await AwaitHeartbeat(heartbeatTask);

                _logger.LogError(ex, "Job failed: {JobId}", job.Id);

                await _portal.ReportJobStatusAsync(job.Id, new JobStatusUpdate
                {
                    Status = JobStatus.Failed,
                    Error = SanitizeErrorMessage(ex),
                }, CancellationToken.None);
            }

            // Sleep before next poll
            await WaitAsync(baseInterval, stoppingToken);
        }

    }

    /// <summary>
    /// Truncates and sanitises exception messages before sending them to the
    /// portal.  SQL and HTTP exceptions can embed server names, full URLs or
    /// connection strings — redact common sensitive patterns and limit to 500 chars.
    /// </summary>
    private static string SanitizeErrorMessage(Exception ex)
    {
        var msg = ex.GetType().Name + ": " + ex.Message;

        // Redact connection-string-like values (key=value pairs with sensitive keys)
        msg = Regex.Replace(msg,
            @"(Password|Pwd|User\s*Id|UID|Secret|Token|Api[_\-]?Key)\s*=\s*[^;""'\s]+",
            "$1=[REDACTED]",
            RegexOptions.IgnoreCase);

        // Redact full URLs (may contain tokens/keys in query strings)
        msg = Regex.Replace(msg,
            @"https?://[^\s""'<>]+",
            m =>
            {
                var idx = m.Value.IndexOf('?');
                return idx >= 0 ? m.Value[..idx] + "?[REDACTED]" : m.Value;
            },
            RegexOptions.IgnoreCase);

        const int maxLength = 500;
        return msg.Length > maxLength ? msg[..maxLength] + "…" : msg;
    }

    private static async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown — return immediately.
        }
    }

    private async Task RunHeartbeatAsync(string jobId, Func<JobProgress> getProgress, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_config.HeartbeatIntervalMs, ct);
                await _portal.ReportJobStatusAsync(jobId, new JobStatusUpdate
                {
                    Status = JobStatus.Running,
                    Progress = getProgress(),
                }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the heartbeat is stopped.
        }
    }

    private static async Task AwaitHeartbeat(Task heartbeatTask)
    {
        try { await heartbeatTask; }
        catch (OperationCanceledException) { }
    }
}
