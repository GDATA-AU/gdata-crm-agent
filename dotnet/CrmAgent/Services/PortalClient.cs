using System.Net.Http.Json;
using System.Text.Json;
using CrmAgent.Models;

namespace CrmAgent.Services;

/// <summary>
/// HTTP client for the GDATA Customer Portal API.
/// Registered as a typed HttpClient via DI.
/// </summary>
public sealed class PortalClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger<PortalClient> _logger;

    public PortalClient(HttpClient http, ILogger<PortalClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Poll the portal for a pending job.
    /// Returns a <see cref="PollResult"/> with the job (if available), the HTTP
    /// status code, and an optional Retry-After duration.
    /// Only throws for network failures, deserialization errors, or unexpected HTTP
    /// status codes (e.g. 400, 404) — expected error codes (401, 403, 429, 5xx) are
    /// returned in the result.
    /// </summary>
    public async Task<PollResult> PollForJobAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("api/agent/jobs", ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return new PollResult(null, response.StatusCode);

        // Auth failures, rate-limiting, and server errors are returned as results
        // so the caller can apply appropriate backoff strategies.
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden
            or System.Net.HttpStatusCode.TooManyRequests
            || (int)response.StatusCode >= 500)
        {
            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is DateTimeOffset date
                    ? date - DateTimeOffset.UtcNow
                    : null);

            // Clamp negative Retry-After values to zero.
            if (retryAfter < TimeSpan.Zero)
                retryAfter = TimeSpan.Zero;

            return new PollResult(null, response.StatusCode, retryAfter);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Poll failed: {(int)response.StatusCode} {response.ReasonPhrase} — {body}");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Poll returned unexpected content-type '{contentType}' (expected application/json). Body: {body}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("Poll response: {Body}", json);

        var envelope = JsonSerializer.Deserialize<PollResponse>(json, JsonOptions);
        return new PollResult(envelope?.Job, response.StatusCode);
    }

    /// <summary>
    /// Report a status update for an active job back to the portal.
    /// Does NOT throw on errors — logs and returns silently so the agent
    /// can continue running even when the portal is temporarily unreachable.
    /// </summary>
    public async Task ReportJobStatusAsync(string jobId, JobStatusUpdate update, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/agent/jobs/{Uri.EscapeDataString(jobId)}";
            var response = await _http.PatchAsJsonAsync(url, update, JsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Portal responded with {StatusCode} to status update for job {JobId}: {Body}",
                    (int)response.StatusCode, jobId, body);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to report job status for {JobId}", jobId);
        }
    }
}
