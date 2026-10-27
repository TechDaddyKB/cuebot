using System.Net;
using System.Text;

namespace Cuebot.Kick.Tests.Support;

/// <summary>A request as seen by <see cref="FakeHttpHandler"/>, with its body already read.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? ContentType, string Body,
    IReadOnlyDictionary<string, string> Headers)
{
    public string Path => Uri.AbsolutePath;
    public string Query => Uri.Query;
}

/// <summary>In-memory HTTP: every request is recorded and answered by the supplied responder.</summary>
public sealed class FakeHttpHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_requests) return _requests.ToArray(); }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentType?.MediaType, body, headers);
        lock (_requests) _requests.Add(recorded);
        return respond(recorded);
    }
}
