using System.Net;
using System.Text.Json;
using CrmAgent;
using CrmAgent.Handlers;
using CrmAgent.Models;
using CrmAgent.Services;
using CrmAgent.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrmAgent.Tests;

/// <summary>
/// Characterization tests pinning the current fetch/pagination/chunking behavior of
/// <see cref="RestApiHandler"/> before it is restructured. They drive the public
/// <see cref="RestApiHandler.ExecuteAsync"/> against a scripted HTTP stub and an
/// in-memory blob store, asserting against behavior as it exists today.
///
/// The retry path is intentionally not tested here: its back-off delays are the
/// hardcoded 1s/3s/9s in RetryDelaysMs and would make the suite sleep.
/// </summary>
public class RestApiHandlerFetchTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static AgentConfig Config(int maxPages = 100_000, int timeoutSeconds = 300) => new()
    {
        PortalUrl = "https://portal.example.com",
        AgentApiKey = "test-key",
        AzureStorageConnectionString = "UseDevelopmentStorage=true",
        RestApiTimeoutSeconds = timeoutSeconds,
        MaxRestApiPages = maxPages,
    };

    private static RestApiHandler Handler(FakeBlobStorage blob, StubHttpMessageHandler stub, AgentConfig config) =>
        new(blob, new StubHttpClientFactory(stub), config, NullLogger<RestApiHandler>.Instance);

    private static Job Job(JobConfig config, bool preview = false) => new()
    {
        Id = "job-1",
        Type = JobType.RestApi,
        Preview = preview,
        Config = config,
    };

    /// <summary>JSON array of <paramref name="count"/> objects with sequential ids starting at <paramref name="startId"/>.</summary>
    private static string RowsJson(int count, int startId = 0) =>
        "[" + string.Join(",", Enumerable.Range(startId, count).Select(i => $"{{\"id\":{i},\"name\":\"row{i}\"}}")) + "]";

    private static void AssertEveryLineHasRowHash(string[] lines)
    {
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.True(doc.RootElement.TryGetProperty("_rowHash", out _), $"line missing _rowHash: {line}");
        }
    }

    // ------------------------------------------------------------------
    // Offset pagination
    // ------------------------------------------------------------------

    [Fact]
    public async Task Offset_TwoFullPagesThenShortPage_WritesAllRows()
    {
        var stub = new StubHttpMessageHandler()
            .EnqueueJson(RowsJson(2, 0))   // full page
            .EnqueueJson(RowsJson(2, 2))   // full page
            .EnqueueJson(RowsJson(1, 4));  // short page → terminates
        var blob = new FakeBlobStorage();
        var handler = Handler(blob, stub, Config());

        var job = Job(new JobConfig
        {
            BaseUrl = "https://api.example.com/data",
            Pagination = new RestApiPagination { Type = PaginationType.Offset, PageSize = 2 },
            BlobPath = "jobs/test",
            HashFields = ["id"],
        });

        var result = await handler.ExecuteAsync(job, _ => { }, CancellationToken.None);

        Assert.Equal(5, result.ProcessedRows);
        Assert.Equal(3, stub.RequestedUrls.Count);
        Assert.Contains("top=2&skip=0", stub.RequestedUrls[0]);
        Assert.Contains("top=2&skip=2", stub.RequestedUrls[1]);
        Assert.Contains("top=2&skip=4", stub.RequestedUrls[2]);

        var lines = TestGzip.DecompressToLines(blob.Blobs[result.BlobName!]);
        Assert.Equal(5, lines.Length);
        AssertEveryLineHasRowHash(lines);
    }

    // ------------------------------------------------------------------
    // Cursor pagination
    // ------------------------------------------------------------------

    [Fact]
    public async Task Cursor_ChainsUntilNullCursor()
    {
        var stub = new StubHttpMessageHandler()
            .EnqueueJson("{\"data\":" + RowsJson(2, 0) + ",\"nextCursor\":\"c1\"}")
            .EnqueueJson("{\"data\":" + RowsJson(2, 2) + ",\"nextCursor\":\"c2\"}")
            .EnqueueJson("{\"data\":" + RowsJson(1, 4) + ",\"nextCursor\":null}");
        var blob = new FakeBlobStorage();
        var handler = Handler(blob, stub, Config());

        var job = Job(new JobConfig
        {
            BaseUrl = "https://api.example.com/data",
            Pagination = new RestApiPagination { Type = PaginationType.Cursor, PageSize = 2 },
            DataField = "data",
            BlobPath = "jobs/test",
            HashFields = ["id"],
        });

        var result = await handler.ExecuteAsync(job, _ => { }, CancellationToken.None);

        Assert.Equal(5, result.ProcessedRows);
        Assert.Equal(3, stub.RequestedUrls.Count);
        Assert.DoesNotContain("cursor=", stub.RequestedUrls[0]);   // first request has no cursor
        Assert.Contains("cursor=c1", stub.RequestedUrls[1]);
        Assert.Contains("cursor=c2", stub.RequestedUrls[2]);

        var lines = TestGzip.DecompressToLines(blob.Blobs[result.BlobName!]);
        Assert.Equal(5, lines.Length);
        AssertEveryLineHasRowHash(lines);
    }

    // ------------------------------------------------------------------
    // Link-header pagination
    // ------------------------------------------------------------------

    [Fact]
    public async Task LinkHeader_FollowsNextUntilAbsent()
    {
        var stub = new StubHttpMessageHandler()
            .EnqueueJson(RowsJson(2, 0), linkHeader: "<https://api.example.com/data?page=2>; rel=\"next\"")
            .EnqueueJson(RowsJson(2, 2)); // no Link header → stop
        var blob = new FakeBlobStorage();
        var handler = Handler(blob, stub, Config());

        var job = Job(new JobConfig
        {
            BaseUrl = "https://api.example.com/data",
            Pagination = new RestApiPagination { Type = PaginationType.LinkHeader },
            BlobPath = "jobs/test",
            HashFields = ["id"],
        });

        var result = await handler.ExecuteAsync(job, _ => { }, CancellationToken.None);

        Assert.Equal(4, result.ProcessedRows);
        Assert.Equal(2, stub.RequestedUrls.Count);
        Assert.Equal("https://api.example.com/data", stub.RequestedUrls[0]);
        Assert.Equal("https://api.example.com/data?page=2", stub.RequestedUrls[1]);

        var lines = TestGzip.DecompressToLines(blob.Blobs[result.BlobName!]);
        Assert.Equal(4, lines.Length);
        AssertEveryLineHasRowHash(lines);
    }

    // ------------------------------------------------------------------
    // No pagination — single request with nested dataField
    // ------------------------------------------------------------------

    [Fact]
    public async Task NoPagination_SingleRequest_ExtractsNestedDataField()
    {
        var stub = new StubHttpMessageHandler()
            .EnqueueJson("{\"data\":{\"items\":" + RowsJson(3, 0) + "}}");
        var blob = new FakeBlobStorage();
        var handler = Handler(blob, stub, Config());

        var job = Job(new JobConfig
        {
            BaseUrl = "https://api.example.com/data",
            DataField = "data.items",
            BlobPath = "jobs/test",
            HashFields = ["id"],
        });

        var result = await handler.ExecuteAsync(job, _ => { }, CancellationToken.None);

        Assert.Equal(3, result.ProcessedRows);
        Assert.Single(stub.RequestedUrls);

        var lines = TestGzip.DecompressToLines(blob.Blobs[result.BlobName!]);
        Assert.Equal(3, lines.Length);
        AssertEveryLineHasRowHash(lines);
    }

    // ------------------------------------------------------------------
    // Page cap
    // ------------------------------------------------------------------

    [Fact]
    public async Task PageCap_AlwaysFullPages_ThrowsAndDeletesPartialBlob()
    {
        var stub = new StubHttpMessageHandler()
            .EnqueueJson(RowsJson(2, 0))
            .EnqueueJson(RowsJson(2, 2))
            .EnqueueJson(RowsJson(2, 4)); // 3 full pages consumed before the cap trips on page 4
        var blob = new FakeBlobStorage();
        var handler = Handler(blob, stub, Config(maxPages: 3));

        var job = Job(new JobConfig
        {
            BaseUrl = "https://api.example.com/data",
            Pagination = new RestApiPagination { Type = PaginationType.Offset, PageSize = 2 },
            BlobPath = "jobs/test",
            HashFields = ["id"],
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.ExecuteAsync(job, _ => { }, CancellationToken.None));

        Assert.Equal(3, stub.RequestedUrls.Count);
        Assert.NotEmpty(blob.DeletedBlobs);
    }

    // ------------------------------------------------------------------
    // Non-2xx mid-extraction
    // ------------------------------------------------------------------

    [Fact]
    public async Task Non2xxMidExtraction_ThrowsAndDeletesPartialBlob()
    {
        var stub = new StubHttpMessageHandler()
            .EnqueueJson(RowsJson(2, 0))
            .EnqueueJson("{\"error\":\"bad request\"}", HttpStatusCode.BadRequest);
        var blob = new FakeBlobStorage();
        var handler = Handler(blob, stub, Config());

        var job = Job(new JobConfig
        {
            BaseUrl = "https://api.example.com/data",
            Pagination = new RestApiPagination { Type = PaginationType.Offset, PageSize = 2 },
            BlobPath = "jobs/test",
            HashFields = ["id"],
        });

        await Assert.ThrowsAsync<HttpRequestException>(
            () => handler.ExecuteAsync(job, _ => { }, CancellationToken.None));

        Assert.Equal(2, stub.RequestedUrls.Count);
        Assert.NotEmpty(blob.DeletedBlobs);
    }

    // ------------------------------------------------------------------
    // Date chunking (explicit DateRange)
    // ------------------------------------------------------------------

    [Fact]
    public async Task DateChunking_Explicit_SplitsRangeIntoWindows()
    {
        // ~62-day span → two 31-day windows.
        var stub = new StubHttpMessageHandler()
            .EnqueueJson(RowsJson(2, 0))
            .EnqueueJson(RowsJson(3, 2));
        var blob = new FakeBlobStorage();
        var handler = Handler(blob, stub, Config());

        var job = Job(new JobConfig
        {
            BaseUrl = "https://api.example.com/data",
            Params = new Dictionary<string, string>
            {
                ["startDate"] = "2026-01-01T00:00:00.000Z",
                ["endDate"] = "2026-03-04T00:00:00.000Z",
            },
            DateRange = new RestApiDateRange { StartParam = "startDate", EndParam = "endDate", MaxDays = 31 },
            BlobPath = "jobs/test",
            HashFields = ["id"],
        });

        var result = await handler.ExecuteAsync(job, _ => { }, CancellationToken.None);

        Assert.Equal(5, result.ProcessedRows);
        Assert.Equal(2, stub.RequestedUrls.Count);

        var decoded = stub.DecodedRequestedUrls;
        // Non-final chunk: end is windowStart + 31d minus 1ms.
        Assert.Contains("startDate=2026-01-01T00:00:00.000Z", decoded[0]);
        Assert.Contains("endDate=2026-01-31T23:59:59.999Z", decoded[0]);
        // Final chunk: ends at the real range end.
        Assert.Contains("startDate=2026-02-01T00:00:00.000Z", decoded[1]);
        Assert.Contains("endDate=2026-03-04T00:00:00.000Z", decoded[1]);

        var lines = TestGzip.DecompressToLines(blob.Blobs[result.BlobName!]);
        Assert.Equal(5, lines.Length);
    }

    // ------------------------------------------------------------------
    // Date chunking (auto-detected, no DateRange config)
    // ------------------------------------------------------------------

    [Fact]
    public async Task DateChunking_AutoDetected_ChunksWhenSpanExceeds31Days()
    {
        var stub = new StubHttpMessageHandler()
            .EnqueueJson(RowsJson(2, 0))
            .EnqueueJson(RowsJson(2, 2));
        var blob = new FakeBlobStorage();
        var handler = Handler(blob, stub, Config());

        var job = Job(new JobConfig
        {
            BaseUrl = "https://api.example.com/data",
            Params = new Dictionary<string, string>
            {
                ["startDate"] = "2026-01-01T00:00:00.000Z",
                ["endDate"] = "2026-03-04T00:00:00.000Z", // > 31 days apart
            },
            // No DateRange configured — must be auto-detected.
            BlobPath = "jobs/test",
            HashFields = ["id"],
        });

        var result = await handler.ExecuteAsync(job, _ => { }, CancellationToken.None);

        Assert.Equal(4, result.ProcessedRows);
        Assert.Equal(2, stub.RequestedUrls.Count); // chunking happened despite no explicit config
    }

    // ------------------------------------------------------------------
    // Preview mode — 100-row cap, no blob writes
    // ------------------------------------------------------------------

    [Fact]
    public async Task Preview_Offset_StopsAt100Rows_NoBlob()
    {
        var stub = new StubHttpMessageHandler()
            .EnqueueJson(RowsJson(60, 0))
            .EnqueueJson(RowsJson(60, 60));
        var blob = new FakeBlobStorage();
        var handler = Handler(blob, stub, Config());

        var job = Job(new JobConfig
        {
            BaseUrl = "https://api.example.com/data",
            Pagination = new RestApiPagination { Type = PaginationType.Offset, PageSize = 60 },
            BlobPath = "jobs/test",
            HashFields = ["id"],
        }, preview: true);

        var result = await handler.ExecuteAsync(job, _ => { }, CancellationToken.None);

        Assert.Null(result.BlobName);
        Assert.NotNull(result.PreviewRows);
        Assert.Equal(100, result.PreviewRows!.Count);
        Assert.Equal(100, result.ProcessedRows);
        Assert.Empty(blob.Blobs);
        Assert.Equal(2, stub.RequestedUrls.Count);
    }

    [Fact]
    public async Task Preview_SinglePage_CapsAt100Rows_NoBlob()
    {
        var stub = new StubHttpMessageHandler()
            .EnqueueJson(RowsJson(150, 0)); // more than the preview limit in one page
        var blob = new FakeBlobStorage();
        var handler = Handler(blob, stub, Config());

        var job = Job(new JobConfig
        {
            BaseUrl = "https://api.example.com/data",
            BlobPath = "jobs/test",
            HashFields = ["id"],
        }, preview: true);

        var result = await handler.ExecuteAsync(job, _ => { }, CancellationToken.None);

        Assert.Null(result.BlobName);
        Assert.Equal(100, result.PreviewRows!.Count);
        Assert.Empty(blob.Blobs);
        Assert.Single(stub.RequestedUrls);
    }

    [Fact]
    public async Task Preview_Cursor_CapsAt100Rows_NoBlob()
    {
        var stub = new StubHttpMessageHandler()
            .EnqueueJson("{\"data\":" + RowsJson(60, 0) + ",\"nextCursor\":\"c1\"}")
            .EnqueueJson("{\"data\":" + RowsJson(60, 60) + ",\"nextCursor\":\"c2\"}");
        var blob = new FakeBlobStorage();
        var handler = Handler(blob, stub, Config());

        var job = Job(new JobConfig
        {
            BaseUrl = "https://api.example.com/data",
            Pagination = new RestApiPagination { Type = PaginationType.Cursor, PageSize = 60 },
            DataField = "data",
            BlobPath = "jobs/test",
            HashFields = ["id"],
        }, preview: true);

        var result = await handler.ExecuteAsync(job, _ => { }, CancellationToken.None);

        Assert.Null(result.BlobName);
        Assert.Equal(100, result.PreviewRows!.Count);
        Assert.Empty(blob.Blobs);
        Assert.Equal(2, stub.RequestedUrls.Count);
    }

    // ------------------------------------------------------------------
    // "single" pagination type (portal contract gap — see commit message)
    // ------------------------------------------------------------------

    [Fact]
    public void Deserialize_SinglePaginationType_Succeeds()
    {
        // Regression test: the portal's config type union permits "single"; deserializing
        // it must not throw (previously JsonStringEnumConverter<PaginationType> did).
        const string json = "{\"baseUrl\":\"https://api.example.com/data\",\"pagination\":{\"type\":\"single\"}}";
        var config = JsonSerializer.Deserialize<JobConfig>(json, JsonDefaults.CamelCase);

        Assert.NotNull(config);
        Assert.NotNull(config!.Pagination);
        Assert.Equal(PaginationType.Single, config.Pagination!.Type);
    }

    [Fact]
    public async Task Single_IssuesOneRequest_WritesRows()
    {
        var stub = new StubHttpMessageHandler()
            .EnqueueJson("{\"data\":" + RowsJson(3, 0) + "}");
        var blob = new FakeBlobStorage();
        var handler = Handler(blob, stub, Config());

        var job = Job(new JobConfig
        {
            BaseUrl = "https://api.example.com/data",
            Pagination = new RestApiPagination { Type = PaginationType.Single },
            DataField = "data",
            BlobPath = "jobs/test",
            HashFields = ["id"],
        });

        var result = await handler.ExecuteAsync(job, _ => { }, CancellationToken.None);

        Assert.Equal(3, result.ProcessedRows);
        Assert.Single(stub.RequestedUrls);
        Assert.DoesNotContain("skip=", stub.RequestedUrls[0]);
        Assert.DoesNotContain("top=", stub.RequestedUrls[0]);
        Assert.DoesNotContain("cursor=", stub.RequestedUrls[0]);

        var lines = TestGzip.DecompressToLines(blob.Blobs[result.BlobName!]);
        Assert.Equal(3, lines.Length);
        AssertEveryLineHasRowHash(lines);
    }

    [Fact]
    public async Task Preview_Single_CapsAt100Rows_NoBlob()
    {
        var stub = new StubHttpMessageHandler()
            .EnqueueJson("{\"data\":" + RowsJson(150, 0) + "}");
        var blob = new FakeBlobStorage();
        var handler = Handler(blob, stub, Config());

        var job = Job(new JobConfig
        {
            BaseUrl = "https://api.example.com/data",
            Pagination = new RestApiPagination { Type = PaginationType.Single },
            DataField = "data",
            BlobPath = "jobs/test",
            HashFields = ["id"],
        }, preview: true);

        var result = await handler.ExecuteAsync(job, _ => { }, CancellationToken.None);

        Assert.Null(result.BlobName);
        Assert.Equal(100, result.PreviewRows!.Count);
        Assert.Empty(blob.Blobs);
        Assert.Single(stub.RequestedUrls);
    }
}
