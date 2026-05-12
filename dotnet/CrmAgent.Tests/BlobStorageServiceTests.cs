using CrmAgent.Services;

namespace CrmAgent.Tests;

public class BlobStorageServiceTests
{
    [Fact]
    public void BuildBlobNameFormatsCorrectly()
    {
        var timestamp = new DateTime(2026, 3, 13, 2, 30, 0, DateTimeKind.Utc);
        var result = BlobStorageService.BuildBlobName("pathway/names-individuals/snapshots/", timestamp);
        Assert.Equal("pathway/names-individuals/snapshots/2026-03-13T02-30-00Z.ndjson.gz", result);
    }

    [Fact]
    public void BuildBlobNameAddsTrailingSlash()
    {
        var timestamp = new DateTime(2026, 3, 13, 2, 30, 0, DateTimeKind.Utc);
        var result = BlobStorageService.BuildBlobName("pathway/snapshots", timestamp);
        Assert.Equal("pathway/snapshots/2026-03-13T02-30-00Z.ndjson.gz", result);
    }

    [Theory]
    [InlineData("../other-container/file")]
    [InlineData("jobs/../../secrets")]
    [InlineData("a/b/../c")]
    public void BuildBlobNameRejectsTraversalSegments(string badPath)
    {
        var timestamp = new DateTime(2026, 3, 13, 2, 30, 0, DateTimeKind.Utc);
        Assert.Throws<InvalidOperationException>(() => BlobStorageService.BuildBlobName(badPath, timestamp));
    }

    [Theory]
    [InlineData("jobs/%2e%2e/secrets")]        // %2e%2e  → ..
    [InlineData("jobs/%2E%2E/secrets")]        // uppercase encoding
    [InlineData("jobs/.%2e/secrets")]          // mixed .%2e → ..
    [InlineData("jobs/%2e./secrets")]          // mixed %2e. → ..
    public void BuildBlobNameRejectsUrlEncodedTraversalSegments(string badPath)
    {
        var timestamp = new DateTime(2026, 3, 13, 2, 30, 0, DateTimeKind.Utc);
        Assert.Throws<InvalidOperationException>(() => BlobStorageService.BuildBlobName(badPath, timestamp));
    }

    [Theory]
    [InlineData("/absolute/path")]
    [InlineData(@"\absolute\path")]       // backslash absolute — normalises to /absolute/path
    public void BuildBlobNameRejectsAbsolutePath(string badPath)
    {
        var timestamp = new DateTime(2026, 3, 13, 2, 30, 0, DateTimeKind.Utc);
        Assert.Throws<InvalidOperationException>(() => BlobStorageService.BuildBlobName(badPath, timestamp));
    }

    [Fact]
    public void BuildBlobNameRejectsEmptyPath()
    {
        var timestamp = new DateTime(2026, 3, 13, 2, 30, 0, DateTimeKind.Utc);
        Assert.Throws<InvalidOperationException>(() => BlobStorageService.BuildBlobName("", timestamp));
    }

    [Fact]
    public void BuildBlobNameNormalisesBackslashes()
    {
        var timestamp = new DateTime(2026, 3, 13, 2, 30, 0, DateTimeKind.Utc);
        var result = BlobStorageService.BuildBlobName(@"pathway\snapshots", timestamp);
        Assert.Equal("pathway/snapshots/2026-03-13T02-30-00Z.ndjson.gz", result);
    }
}
