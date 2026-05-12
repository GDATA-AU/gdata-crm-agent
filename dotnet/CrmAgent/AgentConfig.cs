namespace CrmAgent;

/// <summary>
/// Strongly-typed configuration loaded from environment variables / appsettings.
/// </summary>
public sealed class AgentConfig
{
    public const string SectionName = "Agent";

    public required string PortalUrl { get; init; }
    public required string AgentApiKey { get; init; }
    public required string AzureStorageConnectionString { get; init; }
    public int PollIntervalMs { get; init; } = 5_000;
    public int HeartbeatIntervalMs { get; init; } = 30_000;
    public string LogLevel { get; init; } = "Information";

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
}
