namespace CrmAgent;

/// <summary>
/// Strongly-typed configuration loaded from environment variables / appsettings.
/// </summary>
public sealed class AgentConfig
{
    public required string PortalUrl { get; init; }
    public required string AgentApiKey { get; init; }
    public required string AzureStorageConnectionString { get; init; }
    public int PollIntervalMs { get; init; } = 5_000;
    public int HeartbeatIntervalMs { get; init; } = 30_000;

    /// <summary>
    /// When <c>true</c> the MSSQL driver skips TLS certificate validation for on-premise
    /// servers that use self-signed certificates.  Defaults to <c>true</c> because most
    /// on-premises SQL Server instances use self-signed certificates.
    /// </summary>
    public bool SqlTrustServerCertificate { get; init; } = true;

    /// <summary>
    /// Timeout in seconds for individual outbound REST API page requests.
    /// Set to 300 seconds (5 minutes) to accommodate APIs that serve large pages
    /// or enforce server-side query timeouts.  Reduce this value if faster failure
    /// detection is more important than completing slow page fetches.
    /// </summary>
    public int RestApiTimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// Timeout in seconds for SQL command execution. If a query exceeds this limit it is
    /// cancelled and the job fails with a clear error rather than hanging the agent
    /// indefinitely. Defaults to 300 seconds (5 minutes) — comfortably above any expected
    /// extraction, so it only catches genuinely stuck commands. The per-job deadline and the
    /// hard watchdog are the primary hang protection. Set to 0 to disable (not recommended).
    /// </summary>
    public int SqlCommandTimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// Timeout in seconds for opening a SQL connection. Bounds the time spent waiting for
    /// an unreachable or overloaded SQL Server before the job fails. Defaults to 15 seconds
    /// (the SqlClient default), surfaced here so operators can tune it.
    /// </summary>
    public int SqlConnectTimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// Maximum wall-clock duration in seconds for a single job. When exceeded the job's
    /// cancellation token is tripped so every SQL/blob/HTTP call cooperatively aborts and
    /// the job is reported as failed — the agent keeps polling rather than hanging.
    /// Defaults to 1800 (30 minutes). Set to 0 to disable the per-job deadline (not recommended).
    /// </summary>
    public int MaxJobDurationSeconds { get; init; } = 1800;

    /// <summary>
    /// Grace period in seconds added on top of <see cref="MaxJobDurationSeconds"/> before the
    /// hard watchdog force-terminates the process. Cooperative cancellation (the per-job
    /// deadline) is tried first; if a wedged job ignores its cancellation token and is still
    /// running this long after starting, the watchdog calls <c>Environment.FailFast</c> so the
    /// service manager (systemd / Windows SCM) restarts a clean process. Defaults to 300
    /// (5 minutes). Set to 0 to disable the hard watchdog (not recommended).
    /// </summary>
    public int WatchdogGraceSeconds { get; init; } = 300;

    /// <summary>
    /// Upper bound on the number of pages a single REST API extraction may fetch. Guards
    /// against a misconfigured cursor/offset that never terminates and would otherwise loop
    /// forever. Defaults to 100,000. Must be at least 1.
    /// </summary>
    public int MaxRestApiPages { get; init; } = 100_000;
}
