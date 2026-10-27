using System.Net;
using System.Text;

namespace Cuebot.Rumble.Tests.Support;

/// <summary>A request as seen by <see cref="FakeHttpHandler"/>, with its body already read.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Cookie, string Body)
{
    public string Path => Uri.AbsolutePath;
}

/// <summary>In-memory HTTP: every request is recorded and answered by the supplied responder.</summary>
public sealed class FakeHttpHandler(Func<RecordedRequest, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];

    public FakeHttpHandler(Func<RecordedRequest, HttpResponseMessage> respond) : this(r => Task.FromResult(respond(r)))
    {
    }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_requests) return _requests.ToArray(); }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!,
            request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null, body);
        lock (_requests) _requests.Add(recorded);
        return await respond(recorded).WaitAsync(ct);
    }
}
