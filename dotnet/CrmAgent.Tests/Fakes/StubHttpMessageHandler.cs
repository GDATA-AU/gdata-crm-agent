using System.Net;
using System.Text;

namespace CrmAgent.Tests.Fakes;

/// <summary>
/// Scripted <see cref="HttpMessageHandler"/> for handler tests. Responses are
/// dequeued in the order they were enqueued and every requested absolute URL is
/// recorded in <see cref="RequestedUrls"/>. Since every pagination strategy issues
/// its requests strictly sequentially, a FIFO queue is sufficient.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    public List<string> RequestedUrls { get; } = new();
    private readonly Queue<HttpResponseMessage> _responses = new();

    public StubHttpMessageHandler EnqueueJson(
        string json, HttpStatusCode status = HttpStatusCode.OK, string? linkHeader = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (linkHeader is not null)
            response.Headers.TryAddWithoutValidation("Link", linkHeader);
        _responses.Enqueue(response);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestedUrls.Add(request.RequestUri!.ToString());
        if (_responses.Count == 0)
            throw new InvalidOperationException(
                $"StubHttpMessageHandler: no queued response for request #{RequestedUrls.Count} to {request.RequestUri}");
        return Task.FromResult(_responses.Dequeue());
    }

    /// <summary>Requested URLs with percent-encoding decoded, for readable assertions on query params.</summary>
    public IReadOnlyList<string> DecodedRequestedUrls =>
        RequestedUrls.Select(Uri.UnescapeDataString).ToList();
}

/// <summary>
/// <see cref="IHttpClientFactory"/> that hands out clients wired to a single stub
/// handler. <c>disposeHandler: false</c> keeps the shared handler alive across the
/// handler's per-request <c>using var http</c> disposals.
/// </summary>
public sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
