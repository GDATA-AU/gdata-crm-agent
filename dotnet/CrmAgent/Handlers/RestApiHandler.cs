using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrmAgent.Models;
using CrmAgent.Services;

namespace CrmAgent.Handlers;

/// <summary>
/// Executes REST API extraction jobs with support for Bearer/OAuth2 auth
/// and offset/cursor/link-header pagination.
/// </summary>
public sealed partial class RestApiHandler : IJobHandler
{
    private static readonly int[] RetryDelaysMs = [1000, 3000, 9000];

    private readonly IBlobStorage _blob;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<RestApiHandler> _logger;

    public RestApiHandler(IBlobStorage blob, IHttpClientFactory httpFactory, AgentConfig agentConfig, ILogger<RestApiHandler> logger)
    {
        _blob = blob;
        _httpFactory = httpFactory;
        _agentConfig = agentConfig;
        _logger = logger;
    }

    public async Task<HandlerResult> ExecuteAsync(Job job, Action<JobProgress> onProgress, CancellationToken ct)
    {
        var config = job.Config.ToRestApiConfig(job);

        ValidateBaseUrl(config.BaseUrl);

        var auth = config.Auth;
        if (auth?.Type == AuthType.Encrypted)
        {
            _logger.LogDebug("Decrypting encrypted auth block for job {JobId}", job.Id);
            auth = AuthDecryptionService.Decrypt(auth, _agentConfig.AgentApiKey);
        }

        var token = await ResolveTokenAsync(auth, ct);

        // Preview mode: fetch up to IJobHandler.PreviewRowLimit rows inline (no blob, no hashing).
        if (job.Preview)
        {
            _logger.LogInformation("Starting REST API preview for job {JobId} url={BaseUrl} (limit={Limit})",
                job.Id, config.BaseUrl, IJobHandler.PreviewRowLimit);

            // For date-range configs, scope to the first window only.
            var previewDateRange = config.DateRange ?? TryAutoDetectDateRange(config);
            var previewConfig = previewDateRange is not null
                ? ApplyFirstDateWindow(config, previewDateRange)
                : config;

            var rows = await FetchPreviewPagesAsync(previewConfig, token, ct);

            _logger.LogInformation("REST API preview complete for job {JobId}: {Rows} rows", job.Id, rows.Count);
            return new HandlerResult { BlobName = null, ProcessedRows = rows.Count, PreviewRows = rows };
        }

        var timestamp = DateTime.UtcNow;
        var blobName = BlobStorageService.BuildBlobName(config.BlobPath, timestamp);

        _logger.LogInformation("Starting REST API extraction for job {JobId} url={BaseUrl} blob={BlobName}",
            job.Id, config.BaseUrl, blobName);

        // Check if date-range chunking is required (explicit config or auto-detected)
        var dateRange = config.DateRange ?? TryAutoDetectDateRange(config);
        if (dateRange is not null)
        {
            var chunkedConfig = config with { DateRange = dateRange };
            return await ExecuteWithDateChunkingAsync(chunkedConfig, token, blobName, onProgress, ct);
        }

        int processedRows;

        try
        {
            await using var blobStream = await _blob.OpenWriteStreamAsync(blobName, ct);
            await using (var writer = new NdjsonGzipWriter(blobStream, leaveOpen: true))
            {
                processedRows = await FetchAllPagesAsync(config, token, writer, onProgress, ct);
            }

            _logger.LogInformation("REST API extraction complete for job {JobId}: {Rows} rows → {BlobName}",
                job.Id, processedRows, blobName);

            return new HandlerResult { BlobName = blobName, ProcessedRows = processedRows };
        }
        catch (HttpRequestException ex) when (IsDateRangeError(ex))
        {
            // Clean up partial blob before retrying with chunking
            try { await _blob.DeleteBlobIfExistsAsync(blobName, CancellationToken.None); }
            catch { /* best-effort cleanup */ }

            // API rejected the date range — auto-detect date params and retry with chunking
            var fallbackDateRange = TryAutoDetectDateRange(config, force: true);
            if (fallbackDateRange is null)
                throw; // Can't determine date params, rethrow original error

            _logger.LogWarning(
                "API returned date-range error, auto-chunking with detected params '{StartParam}' and '{EndParam}'",
                fallbackDateRange.StartParam, fallbackDateRange.EndParam);

            var chunkedConfig = config with { DateRange = fallbackDateRange };
            return await ExecuteWithDateChunkingAsync(chunkedConfig, token, blobName, onProgress, ct);
        }
        catch
        {
            // Clean up partial/corrupt blob on failure
            try { await _blob.DeleteBlobIfExistsAsync(blobName, CancellationToken.None); }
            catch { /* best-effort cleanup */ }
            throw;
        }
    }

    // -----------------------------------------------------------------------
    // Date-range chunking
    // -----------------------------------------------------------------------

    private async Task<HandlerResult> ExecuteWithDateChunkingAsync(
        RestApiJobConfig config, string? token, string blobName,
        Action<JobProgress> onProgress, CancellationToken ct)
    {
        var dr = config.DateRange!;
        var maxDays = dr.MaxDays ?? 31;
        var format = dr.Format ?? "yyyy-MM-ddTHH:mm:ss.fffZ";

        // Parse start/end dates from Params
        if (config.Params is null ||
            !config.Params.TryGetValue(dr.StartParam, out var startStr) ||
            !config.Params.TryGetValue(dr.EndParam, out var endStr))
        {
            throw new InvalidOperationException(
                $"DateRange chunking requires '{dr.StartParam}' and '{dr.EndParam}' in params");
        }

        var rangeStart = DateTime.Parse(startStr, null, System.Globalization.DateTimeStyles.RoundtripKind);
        var rangeEnd = DateTime.Parse(endStr, null, System.Globalization.DateTimeStyles.RoundtripKind);

        if (rangeEnd <= rangeStart)
            throw new InvalidOperationException("DateRange: endDate must be after startDate");

        var totalDays = (rangeEnd - rangeStart).TotalDays;
        var totalProcessedRows = 0;

        _logger.LogInformation(
            "Date range spans {TotalDays:F1} days, chunking into {MaxDays}-day windows",
            totalDays, maxDays);

        try
        {
            await using var blobStream = await _blob.OpenWriteStreamAsync(blobName, ct);
            await using (var writer = new NdjsonGzipWriter(blobStream, leaveOpen: true))
            {
                var chunkStart = rangeStart;
                var chunkIndex = 0;

                while (chunkStart < rangeEnd)
                {
                    ct.ThrowIfCancellationRequested();

                    // Treat range windows as [start, endExclusive) internally. For APIs that
                    // interpret both start/end query params as inclusive, we send endExclusive-1ms
                    // for non-final chunks so each request stays strictly under maxDays.
                    var chunkEndExclusive = chunkStart.AddDays(maxDays);
                    if (chunkEndExclusive > rangeEnd)
                        chunkEndExclusive = rangeEnd;

                    var chunkEndForRequest = chunkEndExclusive < rangeEnd
                        ? chunkEndExclusive.AddMilliseconds(-1)
                        : chunkEndExclusive;

                    _logger.LogInformation(
                        "Fetching date chunk {ChunkIndex}: {ChunkStart} → {ChunkEnd}",
                        chunkIndex, chunkStart.ToString(format), chunkEndForRequest.ToString(format));

                    // Build a config with overridden date params for this chunk
                    var chunkParams = new Dictionary<string, string>(config.Params!);
                    chunkParams[dr.StartParam] = chunkStart.ToString(format);
                    chunkParams[dr.EndParam] = chunkEndForRequest.ToString(format);

                    // DateRange = null prevents recursion back into chunking.
                    var chunkConfig = config with { Params = chunkParams, DateRange = null };

                    var chunkRows = await FetchAllPagesAsync(chunkConfig, token, writer, onProgress, ct);
                    totalProcessedRows += chunkRows;

                    _logger.LogInformation(
                        "Date chunk {ChunkIndex} complete: {Rows} rows (total so far: {Total})",
                        chunkIndex, chunkRows, totalProcessedRows);

                    onProgress(new JobProgress
                    {
                        ProcessedRows = totalProcessedRows,
                        Message = $"Chunk {chunkIndex + 1} done ({chunkStart:yyyy-MM-dd} → {chunkEndForRequest:yyyy-MM-dd}), {totalProcessedRows} total rows",
                    });

                    chunkStart = chunkEndExclusive;
                    chunkIndex++;
                }
            }
        }
        catch
        {
            // Clean up partial/corrupt blob on failure
            try { await _blob.DeleteBlobIfExistsAsync(blobName, CancellationToken.None); }
            catch { /* best-effort cleanup */ }
            throw;
        }

        _logger.LogInformation(
            "REST API date-chunked extraction complete: {Rows} total rows across {Chunks} chunks → {BlobName}",
            totalProcessedRows,
            (int)Math.Ceiling(totalDays / maxDays),
            blobName);

        return new HandlerResult { BlobName = blobName, ProcessedRows = totalProcessedRows };
    }

    /// <summary>
    /// Fetches all pages for a given config (single page or paginated) and writes to the provided writer,
    /// reporting progress after each page.
    /// </summary>
    private async Task<int> FetchAllPagesAsync(
        RestApiJobConfig config, string? token,
        NdjsonGzipWriter writer, Action<JobProgress> onProgress, CancellationToken ct)
    {
        var processedRows = 0;
        await foreach (var records in FetchRecordPagesAsync(config, token, ct))
        {
            processedRows += await WriteRecordsAsync(records, config.HashFields, writer);
            onProgress(new JobProgress { ProcessedRows = processedRows, Message = $"Processed {processedRows} records..." });
        }
        return processedRows;
    }

    // -----------------------------------------------------------------------
    // Auth
    // -----------------------------------------------------------------------

    private async Task<string?> ResolveTokenAsync(RestApiAuth? auth, CancellationToken ct)
    {
        if (auth is null) return null;

        return auth.Type switch
        {
            AuthType.Bearer => auth.Token,
            AuthType.OAuth2ClientCredentials => await FetchOAuth2ClientCredentialsTokenAsync(auth, ct),
            AuthType.OAuth2Password => await FetchOAuth2PasswordTokenAsync(auth, ct),
            _ => null,
        };
    }

    private async Task<string> FetchOAuth2ClientCredentialsTokenAsync(RestApiAuth auth, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(auth.TokenUrl) || string.IsNullOrEmpty(auth.ClientId) || string.IsNullOrEmpty(auth.ClientSecret))
            throw new InvalidOperationException("OAuth2 client-credentials auth requires tokenUrl, clientId, and clientSecret");

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(_agentConfig.RestApiTimeoutSeconds);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = auth.ClientId,
            ["client_secret"] = auth.ClientSecret,
        };
        if (!string.IsNullOrEmpty(auth.Scope))
            form["scope"] = auth.Scope;

        return await PostTokenRequestAsync(http, auth.TokenUrl, form, ct);
    }

    private async Task<string> FetchOAuth2PasswordTokenAsync(RestApiAuth auth, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(auth.TokenUrl) || string.IsNullOrEmpty(auth.ClientId) || string.IsNullOrEmpty(auth.ClientSecret))
            throw new InvalidOperationException("OAuth2 password auth requires tokenUrl, clientId, and clientSecret");
        if (string.IsNullOrEmpty(auth.Username) || string.IsNullOrEmpty(auth.Password))
            throw new InvalidOperationException("OAuth2 password auth requires username and password");

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(_agentConfig.RestApiTimeoutSeconds);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = auth.ClientId,
            ["client_secret"] = auth.ClientSecret,
            ["username"] = auth.Username,
            ["password"] = auth.Password,
        };
        if (!string.IsNullOrEmpty(auth.Scope))
            form["scope"] = auth.Scope;

        return await PostTokenRequestAsync(http, auth.TokenUrl, form, ct);
    }

    private static async Task<string> PostTokenRequestAsync(
        HttpClient http, string tokenUrl, Dictionary<string, string> form, CancellationToken ct)
    {
        var response = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(form), ct);

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"OAuth2 token request failed: {(int)response.StatusCode} — {text}");
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("OAuth2 response missing access_token");
    }

    // -----------------------------------------------------------------------
    // HTTP helpers
    // -----------------------------------------------------------------------

    private HttpClient CreateApiClient(RestApiJobConfig config, string? token)
    {
        var http = _httpFactory.CreateClient();

        http.Timeout = TimeSpan.FromSeconds(_agentConfig.RestApiTimeoutSeconds);

        // Always request JSON — many APIs (e.g. CXone) return XML/text otherwise.
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        if (config.Headers is not null)
        {
            foreach (var (key, value) in config.Headers)
                http.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }

        if (token is not null)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return http;
    }

    /// <summary>
    /// Build the effective base URL by appending any static query params from the config.
    /// </summary>
    private static string BuildBaseUrl(RestApiJobConfig config)
    {
        if (config.Params is null || config.Params.Count == 0)
            return config.BaseUrl;

        var separator = config.BaseUrl.Contains('?') ? "&" : "?";
        var queryParams = string.Join("&", config.Params.Select(
            kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{config.BaseUrl}{separator}{queryParams}";
    }

    private static async Task<(JsonElement Body, HttpResponseHeaders Headers)> FetchPageCoreAsync(
        HttpClient http, string url, string method, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        var response = await http.SendAsync(request, ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var preview = body.Length > 200 ? body[..200] + "…" : body;
            throw new HttpRequestException(
                $"REST API request failed: {(int)response.StatusCode} {response.ReasonPhrase} — {preview}",
                inner: null,
                response.StatusCode);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            var preview = body.Length > 500 ? body[..500] + "…" : body;
            throw new InvalidOperationException(
                $"REST API returned non-JSON response: {preview}", ex);
        }

        using (doc)
        {
            return (doc.RootElement.Clone(), response.Headers);
        }
    }

    /// <summary>
    /// Wraps <see cref="FetchPageCoreAsync"/> with retry + exponential back-off
    /// for transient HTTP errors (429, 5xx, network failures).
    /// </summary>
    private async Task<(JsonElement Body, HttpResponseHeaders Headers)> FetchPageAsync(
        HttpClient http, string url, string method, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await FetchPageCoreAsync(http, url, method, ct);
            }
            catch (HttpRequestException ex) when (attempt < RetryDelaysMs.Length && IsTransientError(ex))
            {
                _logger.LogWarning(
                    "Transient HTTP error on attempt {Attempt} for {Url}: {StatusCode} — retrying in {Delay}ms",
                    attempt + 1, Redaction.RedactUrl(url), (int?)ex.StatusCode, RetryDelaysMs[attempt]);
                await Task.Delay(RetryDelaysMs[attempt], ct);
            }
        }
    }

    /// <summary>
    /// Increments the page counter and throws once the configured page cap is exceeded.
    /// Guards every pagination loop against a misbehaving API (e.g. a cursor that never goes
    /// null, or pages that never shrink) that would otherwise loop forever and hang the agent.
    /// </summary>
    private void EnsurePageLimit(ref int pageCount)
    {
        if (++pageCount > _agentConfig.MaxRestApiPages)
        {
            throw new InvalidOperationException(
                $"REST API pagination exceeded the limit of {_agentConfig.MaxRestApiPages} pages — " +
                "aborting to prevent an unbounded loop. Check the pagination configuration for this job.");
        }
    }

    internal static bool IsTransientError(HttpRequestException ex)
    {
        if (ex.StatusCode is null) return true; // Network error
        var code = (int)ex.StatusCode;
        return code == 429 || code >= 500;
    }

    /// <summary>
    /// Resolve a dot-notation path into a JSON value.
    /// </summary>
    private static JsonElement? GetNestedValue(JsonElement root, string path)
    {
        var current = root;
        foreach (var key in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out var next))
                return null;
            current = next;
        }
        return current;
    }

    /// <summary>
    /// Materialise a JSON record object to a dictionary, mapping JSON value kinds to
    /// CLR types (nested objects/arrays are kept as their raw JSON text).
    /// </summary>
    private static Dictionary<string, object?> ToRow(JsonElement record)
    {
        var row = new Dictionary<string, object?>();
        foreach (var prop in record.EnumerateObject())
        {
            row[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.GetDecimal(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.GetRawText(),
            };
        }
        return row;
    }

    /// <summary>
    /// Write an array of JSON records as NDJSON rows with hashes.
    /// Returns the number of records written.
    /// </summary>
    private static async Task<int> WriteRecordsAsync(
        JsonElement records, string[] hashFields, NdjsonGzipWriter writer)
    {
        var count = 0;
        foreach (var record in records.EnumerateArray())
        {
            var row = ToRow(record);
            row["_rowHash"] = HashService.ComputeRowHash(record, hashFields);
            await writer.WriteRowAsync(row);
            count++;
        }
        return count;
    }

    internal static JsonElement GetRecords(JsonElement body, string? dataField)
    {
        if (dataField is not null)
        {
            var nested = GetNestedValue(body, dataField);
            if (nested is null || nested.Value.ValueKind != JsonValueKind.Array)
                return default;
            return nested.Value;
        }

        // A single root object is treated as a one-element array.
        if (body.ValueKind == JsonValueKind.Array)
            return body;

        using var doc = JsonDocument.Parse($"[{body.GetRawText()}]");
        return doc.RootElement.Clone();
    }

    // -----------------------------------------------------------------------
    // Unified pagination
    // -----------------------------------------------------------------------

    /// <summary>
    /// Yields the records array (post-<see cref="GetRecords"/>) of each page for any
    /// pagination strategy. Creates its own HTTP client and enforces the page cap on
    /// every request in the paginated strategies. Termination semantics per strategy
    /// exactly match the behavior pinned by the characterization tests.
    /// </summary>
    private async IAsyncEnumerable<JsonElement> FetchRecordPagesAsync(
        RestApiJobConfig config, string? token,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var http = CreateApiClient(config, token);

        switch (config.Pagination)
        {
            case null:
            case { Type: PaginationType.Single }:
            {
                // Single fetch, no page-limit guard. Yield only if the payload is an array.
                var url = BuildBaseUrl(config);
                var (body, _) = await FetchPageAsync(http, url, config.Method, ct);
                var records = GetRecords(body, config.DataField);
                if (records.ValueKind == JsonValueKind.Array)
                    yield return records;
                yield break;
            }

            case { Type: PaginationType.LinkHeader }:
            {
                var pageCount = 0;
                string? nextUrl = BuildBaseUrl(config);
                while (nextUrl is not null)
                {
                    EnsurePageLimit(ref pageCount);
                    var (body, headers) = await FetchPageAsync(http, nextUrl, config.Method, ct);
                    var records = GetRecords(body, config.DataField);
                    // Keep following links even when a page's records aren't an array.
                    if (records.ValueKind == JsonValueKind.Array)
                        yield return records;
                    nextUrl = ParseLinkHeaderNext(headers);
                }
                yield break;
            }

            case { Type: PaginationType.Offset } pagination:
            {
                var pageSize = pagination.PageSize ?? 100;
                var pageParam = pagination.PageParam ?? "skip";
                var pageSizeParam = pagination.PageSizeParam ?? "top";
                var offset = 0;
                var pageCount = 0;
                var baseUrl = BuildBaseUrl(config);

                while (true)
                {
                    EnsurePageLimit(ref pageCount);
                    var separator = baseUrl.Contains('?') ? "&" : "?";
                    var url = $"{baseUrl}{separator}{pageSizeParam}={pageSize}&{pageParam}={offset}";
                    var (body, _) = await FetchPageAsync(http, url, config.Method, ct);
                    var records = GetRecords(body, config.DataField);

                    if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() == 0)
                        yield break;

                    yield return records;

                    if (records.GetArrayLength() < pageSize) yield break;
                    offset += pageSize;
                }
            }

            case { Type: PaginationType.Cursor } pagination:
            {
                var pageSize = pagination.PageSize ?? 100;
                var pageSizeParam = pagination.PageSizeParam ?? "pageSize";
                var cursorField = pagination.CursorField ?? "nextCursor";
                var pageParam = pagination.PageParam ?? "cursor";
                var pageCount = 0;
                string? cursor = null;
                var baseUrl = BuildBaseUrl(config);

                while (true)
                {
                    EnsurePageLimit(ref pageCount);
                    var separator = baseUrl.Contains('?') ? "&" : "?";
                    var url = $"{baseUrl}{separator}{pageSizeParam}={pageSize}";
                    if (cursor is not null)
                        url += $"&{pageParam}={Uri.EscapeDataString(cursor)}";

                    var (body, _) = await FetchPageAsync(http, url, config.Method, ct);
                    var records = GetRecords(body, config.DataField);

                    if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() == 0)
                        yield break;

                    yield return records;

                    var nextCursor = GetNestedValue(body, cursorField);
                    if (nextCursor is null ||
                        nextCursor.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        yield break;

                    cursor = nextCursor.Value.ToString();
                }
            }

            default:
                throw new InvalidOperationException($"Unsupported pagination type: {config.Pagination.Type}");
        }
    }

    // -----------------------------------------------------------------------
    // Preview helpers
    // -----------------------------------------------------------------------

    private async Task<List<Dictionary<string, object?>>> FetchPreviewPagesAsync(
        RestApiJobConfig config, string? token, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();

        // Clamp the page size so a paginated preview never fetches more than the preview
        // limit per page. The single/link-header paths send no page-size param, so "single"
        // (like no pagination) is left unchanged and takes the single-fetch path.
        var previewConfig = config.Pagination is { Type: not PaginationType.Single } pagination
            ? config with { Pagination = pagination with { PageSize = Math.Min(pagination.PageSize ?? 100, IJobHandler.PreviewRowLimit) } }
            : config;

        await foreach (var records in FetchRecordPagesAsync(previewConfig, token, ct))
        {
            CollectRecordsForPreview(records, rows, IJobHandler.PreviewRowLimit - rows.Count);
            if (rows.Count >= IJobHandler.PreviewRowLimit)
                break;
        }

        return rows;
    }

    /// <summary>
    /// Collects JSON records into a list of dictionaries for preview (no hashing, no NDJSON).
    /// Stops after <paramref name="limit"/> records.
    /// </summary>
    private static void CollectRecordsForPreview(
        JsonElement records, List<Dictionary<string, object?>> target, int limit)
    {
        foreach (var record in records.EnumerateArray())
        {
            if (limit <= 0) break;
            target.Add(ToRow(record));
            limit--;
        }
    }

    // -----------------------------------------------------------------------
    // Link header parsing
    // -----------------------------------------------------------------------

    private static string? ParseLinkHeaderNext(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var values))
            return null;

        var link = string.Join(", ", values);
        var match = LinkHeaderNextRegex().Match(link);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"<([^>]+)>;\s*rel=""next""")]
    private static partial Regex LinkHeaderNextRegex();

    // -----------------------------------------------------------------------
    // Auto date-range detection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Well-known date parameter names used by common APIs (e.g. CXOne).
    /// </summary>
    private static readonly string[][] KnownDateParamPairs =
    [
        ["startDate", "endDate"],
        ["start_date", "end_date"],
        ["from", "to"],
        ["fromDate", "toDate"],
        ["from_date", "to_date"],
        ["updatedSince", "updatedBefore"],
        ["startTime", "endTime"],
        ["start_time", "end_time"],
    ];

    /// <summary>
    /// Attempts to detect date-range params in the config. Returns a DateRange config if
    /// two date params are found and the span exceeds 31 days. When <paramref name="force"/>
    /// is true, returns a DateRange even if the span is ≤ 31 days (used after API rejection).
    /// </summary>
    private RestApiDateRange? TryAutoDetectDateRange(RestApiJobConfig config, bool force = false)
    {
        if (config.Params is null || config.Params.Count < 2)
            return null;

        // Try well-known param names first
        foreach (var pair in KnownDateParamPairs)
        {
            if (config.Params.TryGetValue(pair[0], out var startStr) &&
                config.Params.TryGetValue(pair[1], out var endStr))
            {
                var result = TryBuildDateRange(pair[0], startStr, pair[1], endStr, force);
                if (result is not null)
                    return result;
            }
        }

        // Fallback: find any two params whose values parse as dates
        var dateCandidates = new List<(string Key, DateTime Value)>();
        foreach (var (key, value) in config.Params)
        {
            if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                dateCandidates.Add((key, dt));
        }

        if (dateCandidates.Count >= 2)
        {
            // Sort by date value and pick the earliest as start, latest as end
            dateCandidates.Sort((a, b) => a.Value.CompareTo(b.Value));
            var start = dateCandidates[0];
            var end = dateCandidates[^1];
            return TryBuildDateRange(start.Key, config.Params[start.Key], end.Key, config.Params[end.Key], force);
        }

        return null;
    }

    private RestApiDateRange? TryBuildDateRange(
        string startParam, string startStr, string endParam, string endStr, bool force)
    {
        if (!DateTime.TryParse(startStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var startDt) ||
            !DateTime.TryParse(endStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var endDt))
            return null;

        var span = (endDt - startDt).TotalDays;
        if (!force && span <= 31)
            return null;

        _logger.LogInformation(
            "Auto-detected date range params '{StartParam}'→'{EndParam}' spanning {Days:F0} days",
            startParam, endParam, span);

        return new RestApiDateRange
        {
            StartParam = startParam,
            EndParam = endParam,
            MaxDays = 31,
        };
    }

    /// <summary>
    /// Checks whether an HttpRequestException indicates a date-range validation error.
    /// </summary>
    private static bool IsDateRangeError(HttpRequestException ex)
    {
        var msg = ex.Message;
        return ex.StatusCode == System.Net.HttpStatusCode.BadRequest &&
               (msg.Contains("InvalidDateRange", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("invalid date range", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("max 31 days", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scopes a config to the first date window (used for previews of date-range jobs).
    /// Returns the config unchanged when the start/end params are missing; otherwise clamps
    /// the end param to start + MaxDays (bounded by the real range end) and clears DateRange.
    /// </summary>
    private static RestApiJobConfig ApplyFirstDateWindow(RestApiJobConfig config, RestApiDateRange dr)
    {
        if (config.Params is null ||
            !config.Params.TryGetValue(dr.StartParam, out var startStr) ||
            !config.Params.TryGetValue(dr.EndParam, out var endStr))
            return config;

        var maxDays = dr.MaxDays ?? 31;
        var format = dr.Format ?? "yyyy-MM-ddTHH:mm:ss.fffZ";

        var rangeStart = DateTime.Parse(startStr, null, System.Globalization.DateTimeStyles.RoundtripKind);
        var rangeEnd = DateTime.Parse(endStr, null, System.Globalization.DateTimeStyles.RoundtripKind);
        var firstWindowEnd = rangeStart.AddDays(maxDays);
        if (firstWindowEnd > rangeEnd) firstWindowEnd = rangeEnd;

        var windowParams = new Dictionary<string, string>(config.Params)
        {
            [dr.EndParam] = firstWindowEnd.ToString(format),
        };

        return config with { Params = windowParams, DateRange = null };
    }

    /// <summary>
    /// Rejects non-HTTP(S) base URLs to prevent SSRF via file://, ftp://, etc.
    /// Note: this check does not block requests to private/internal IP ranges
    /// (e.g. 10.x, 172.16.x, 192.168.x, 127.x, 169.254.169.254).  The agent
    /// is trusted on-premises software whose jobs originate from the portal;
    /// network-level egress controls (firewall rules) should restrict outbound
    /// traffic to the specific external hosts required by each integration.
    /// </summary>
    internal static void ValidateBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            // Redact the full URL to avoid leaking credentials that may be embedded
            // in query strings or userinfo components.
            var redacted = uri is not null ? $"{uri.Scheme}://{uri.Host}" : "(invalid)";
            throw new InvalidOperationException(
                $"REST API baseUrl must use http or https scheme: '{redacted}'");
        }
    }
}
