using System.Net;
using System.Text.Json;
using CrmAgent.Handlers;

namespace CrmAgent.Tests;

public class RestApiHandlerTests
{
    // ------------------------------------------------------------------
    // ValidateBaseUrl — blocks non-HTTP(S) schemes (SSRF protection)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("https://api.example.com/v1/data")]
    [InlineData("http://internal-api.local/data")]
    [InlineData("https://api.example.com:8443/path?foo=bar")]
    public void ValidateBaseUrl_AcceptsHttpAndHttps(string url)
    {
        // Should not throw
        RestApiHandler.ValidateBaseUrl(url);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://ftp.example.com/data")]
    [InlineData("ldap://dc.example.com")]
    [InlineData("//relative-url")]
    [InlineData("not-a-url-at-all")]
    [InlineData("")]
    public void ValidateBaseUrl_RejectsNonHttpSchemes(string url)
    {
        Assert.Throws<InvalidOperationException>(() => RestApiHandler.ValidateBaseUrl(url));
    }

    [Fact]
    public void ValidateBaseUrl_RedactsCredentialsInErrorMessage()
    {
        var url = "ftp://user:secret@ftp.example.com/data?token=abc123";
        var ex = Assert.Throws<InvalidOperationException>(() => RestApiHandler.ValidateBaseUrl(url));
        Assert.DoesNotContain("secret", ex.Message);
        Assert.DoesNotContain("token=abc123", ex.Message);
    }

    // ------------------------------------------------------------------
    // IsTransientError — retry classification
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]   // 429
    [InlineData(HttpStatusCode.InternalServerError, true)] // 500
    [InlineData(HttpStatusCode.BadGateway, true)]         // 502
    [InlineData(HttpStatusCode.ServiceUnavailable, true)] // 503
    [InlineData(HttpStatusCode.GatewayTimeout, true)]     // 504
    [InlineData(HttpStatusCode.BadRequest, false)]        // 400
    [InlineData(HttpStatusCode.Unauthorized, false)]      // 401
    [InlineData(HttpStatusCode.Forbidden, false)]         // 403
    [InlineData(HttpStatusCode.NotFound, false)]          // 404
    public void IsTransientError_ClassifiesByStatusCode(HttpStatusCode statusCode, bool expected)
    {
        var ex = new HttpRequestException("test", null, statusCode);
        Assert.Equal(expected, RestApiHandler.IsTransientError(ex));
    }

    [Fact]
    public void IsTransientError_TreatsNullStatusCodeAsTransient()
    {
        // Network errors have no status code
        var ex = new HttpRequestException("network failure");
        Assert.True(RestApiHandler.IsTransientError(ex));
    }

    // ------------------------------------------------------------------
    // RedactUrl — strips query parameters from URLs
    // ------------------------------------------------------------------

    [Fact]
    public void RedactUrl_StripsQueryString()
    {
        var result = RestApiHandler.RedactUrl("https://api.example.com/v1/data?apiKey=secret&token=abc");
        Assert.Equal("https://api.example.com/v1/data?[REDACTED]", result);
    }

    [Fact]
    public void RedactUrl_PreservesUrlWithoutQueryString()
    {
        var result = RestApiHandler.RedactUrl("https://api.example.com/v1/data");
        Assert.Equal("https://api.example.com/v1/data", result);
    }

    // ------------------------------------------------------------------
    // GetRecords — DataField extraction
    // ------------------------------------------------------------------

    [Fact]
    public void GetRecords_ReturnsRootArray()
    {
        using var doc = JsonDocument.Parse("[{\"id\":1},{\"id\":2}]");
        var records = RestApiHandler.GetRecords(doc.RootElement, null);
        Assert.Equal(JsonValueKind.Array, records.ValueKind);
        Assert.Equal(2, records.GetArrayLength());
    }

    [Fact]
    public void GetRecords_ExtractsNestedDataField()
    {
        using var doc = JsonDocument.Parse("{\"data\":{\"items\":[{\"id\":1},{\"id\":2}]}}");
        var records = RestApiHandler.GetRecords(doc.RootElement, "data.items");
        Assert.Equal(JsonValueKind.Array, records.ValueKind);
        Assert.Equal(2, records.GetArrayLength());
    }

    [Fact]
    public void GetRecords_ExtractsTopLevelDataField()
    {
        using var doc = JsonDocument.Parse("{\"results\":[{\"id\":1}]}");
        var records = RestApiHandler.GetRecords(doc.RootElement, "results");
        Assert.Equal(JsonValueKind.Array, records.ValueKind);
        Assert.Equal(1, records.GetArrayLength());
    }

    [Fact]
    public void GetRecords_ReturnsDefaultWhenPathMissing()
    {
        using var doc = JsonDocument.Parse("{\"other\":123}");
        var records = RestApiHandler.GetRecords(doc.RootElement, "data.items");
        Assert.Equal(JsonValueKind.Undefined, records.ValueKind);
    }

    [Fact]
    public void GetRecords_ReturnsDefaultWhenPathIsNotArray()
    {
        using var doc = JsonDocument.Parse("{\"data\":{\"items\":\"not-an-array\"}}");
        var records = RestApiHandler.GetRecords(doc.RootElement, "data.items");
        Assert.Equal(JsonValueKind.Undefined, records.ValueKind);
    }

    [Fact]
    public void GetRecords_WrapsSingleObjectAsArray()
    {
        using var doc = JsonDocument.Parse("{\"id\":1,\"name\":\"test\"}");
        var records = RestApiHandler.GetRecords(doc.RootElement, null);
        Assert.Equal(JsonValueKind.Array, records.ValueKind);
        Assert.Equal(1, records.GetArrayLength());
    }
}
