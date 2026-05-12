using System.Text.Json.Serialization;

namespace CrmAgent.Models;

// ---------------------------------------------------------------------------
// Job types returned by the portal poll endpoint
// ---------------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter<JobType>))]
public enum JobType
{
    [JsonStringEnumMemberName("sql")]
    Sql,

    [JsonStringEnumMemberName("rest-api")]
    RestApi,

    [JsonStringEnumMemberName("ping")]
    Ping,
}

[JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))]
public enum JobStatus
{
    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("completed")]
    Completed,

    [JsonStringEnumMemberName("failed")]
    Failed,
}

[JsonConverter(typeof(JsonStringEnumConverter<PaginationType>))]
public enum PaginationType
{
    [JsonStringEnumMemberName("offset")]
    Offset,

    [JsonStringEnumMemberName("cursor")]
    Cursor,

    [JsonStringEnumMemberName("link-header")]
    LinkHeader,
}

[JsonConverter(typeof(JsonStringEnumConverter<AuthType>))]
public enum AuthType
{
    [JsonStringEnumMemberName("bearer")]
    Bearer,

    [JsonStringEnumMemberName("oauth2-client-credentials")]
    OAuth2ClientCredentials,

    [JsonStringEnumMemberName("oauth2-password")]
    OAuth2Password,

    [JsonStringEnumMemberName("encrypted")]
    Encrypted,
}

// ---------------------------------------------------------------------------
// Job configuration
// ---------------------------------------------------------------------------

public sealed class RestApiPagination
{
    public required PaginationType Type { get; init; }
    public string? PageParam { get; init; }
    public string? PageSizeParam { get; init; }
    public int? PageSize { get; init; }
    public string? CursorField { get; init; }
    public string? DataField { get; init; }
    public string? TotalField { get; init; }
}

/// <summary>
/// Configures automatic date-range chunking for APIs that enforce a max range
/// (e.g. CXOne's 31-day limit). The handler splits the range into windows of
/// <see cref="MaxDays"/> days each and concatenates results into a single blob.
/// </summary>
public sealed class RestApiDateRange
{
    /// <summary>Query parameter name for the start date (e.g. "startDate").</summary>
    public required string StartParam { get; init; }

    /// <summary>Query parameter name for the end date (e.g. "endDate").</summary>
    public required string EndParam { get; init; }

    /// <summary>
    /// Date format string used to parse/format the param values.
    /// Defaults to ISO 8601 (yyyy-MM-ddTHH:mm:ss.fffZ) if not specified.
    /// </summary>
    public string? Format { get; init; }

    /// <summary>Maximum days per API call. Defaults to 31.</summary>
    public int? MaxDays { get; init; }
}

public sealed class RestApiAuth
{
    public required AuthType Type { get; init; }
    public string? Token { get; init; }
    public string? TokenUrl { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? Scope { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }

    // Encrypted auth envelope fields
    public string? Payload { get; init; }
    public string? Iv { get; init; }
    public string? AuthTag { get; init; }
}

public sealed class SqlJobConfig
{
    public required string Server { get; init; }
    public required string Database { get; init; }
    public required string Query { get; init; }
    public required string BlobPath { get; init; }
    public required string[] HashFields { get; init; }
}

public sealed class RestApiJobConfig
{
    public required string BaseUrl { get; init; }
    public required string Method { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public RestApiAuth? Auth { get; init; }
    public RestApiPagination? Pagination { get; init; }
    public Dictionary<string, string>? Params { get; init; }
    public RestApiDateRange? DateRange { get; init; }
    public string? DataField { get; init; }
    public required string BlobPath { get; init; }
    public required string[] HashFields { get; init; }
}

/// <summary>
/// Union job config — deserialized from the portal, then cast to the
/// specific type based on <see cref="Job.Type"/>.
/// </summary>
public sealed class JobConfig
{
    public string? Type { get; init; }

    // SQL fields
    public string? Server { get; init; }
    public string? Database { get; init; }
    public string? Query { get; init; }

    // REST API fields
    public string? BaseUrl { get; init; }
    public string? Method { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public RestApiAuth? Auth { get; init; }
    public RestApiPagination? Pagination { get; init; }
    public Dictionary<string, string>? Params { get; init; }
    public RestApiDateRange? DateRange { get; init; }
    public string? DataField { get; init; }

    // Shared
    public string? BlobPath { get; init; }
    public string[]? HashFields { get; init; }

    public SqlJobConfig ToSqlConfig(Job job) => new()
    {
        Server = Server ?? throw new InvalidOperationException("SQL job config missing 'server'"),
        Database = Database ?? throw new InvalidOperationException("SQL job config missing 'database'"),
        Query = Query ?? throw new InvalidOperationException("SQL job config missing 'query'"),
        BlobPath = BlobPath ?? job.BlobPath ?? $"jobs/{job.Id}",
        HashFields = HashFields ?? job.HashFields ?? [],
    };

    public RestApiJobConfig ToRestApiConfig(Job job) => new()
    {
        BaseUrl = BaseUrl ?? throw new InvalidOperationException("REST API job config missing 'baseUrl'"),
        Method = Method ?? "GET",
        Headers = Headers,
        Auth = Auth,
        Pagination = Pagination,
        Params = Params,
        DateRange = DateRange,
        DataField = DataField ?? Pagination?.DataField,
        BlobPath = BlobPath ?? job.BlobPath ?? $"jobs/{job.Id}",
        HashFields = HashFields ?? job.HashFields ?? [],
    };
}

// ---------------------------------------------------------------------------
// Job envelope
// ---------------------------------------------------------------------------

public sealed class Job
{
    public required string Id { get; init; }
    public required JobType Type { get; init; }
    public required JobConfig Config { get; init; }
    public bool Preview { get; init; }
    public string? BlobPath { get; init; }
    public string[]? HashFields { get; init; }
}

public sealed class PollResponse
{
    public Job? Job { get; init; }
}

// ---------------------------------------------------------------------------
// Status reporting
// ---------------------------------------------------------------------------

public sealed class JobProgress
{
    public required int ProcessedRows { get; init; }
    public int? TotalRows { get; init; }
    public string? Message { get; init; }
    public List<Dictionary<string, object?>>? PreviewData { get; init; }
}

public sealed class JobStatusUpdate
{
    public required JobStatus Status { get; init; }
    public JobProgress? Progress { get; init; }
    public string? Error { get; init; }
    public string? BlobName { get; init; }
}

// ---------------------------------------------------------------------------
// Poll result
// ---------------------------------------------------------------------------

/// <summary>
/// Result of a portal poll attempt. Carries the job (if available), the HTTP
/// status code, and an optional Retry-After duration from the server.
/// Only used for expected HTTP responses (2xx, 401, 403, 429, 5xx).
/// Network failures and deserialization errors still throw.
/// </summary>
public sealed record PollResult(
    Job? Job,
    System.Net.HttpStatusCode StatusCode,
    TimeSpan? RetryAfter = null);

// ---------------------------------------------------------------------------
// Handler result
// ---------------------------------------------------------------------------

public sealed class HandlerResult
{
    public string? BlobName { get; init; }
    public required int ProcessedRows { get; init; }
    public List<Dictionary<string, object?>>? PreviewRows { get; init; }
}
