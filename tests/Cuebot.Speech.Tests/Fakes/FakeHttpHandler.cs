using System.Net;
using System.Text;

namespace Cuebot.Speech.Tests.Fakes;

/// <summary>Records requests (with their bodies) and answers them from a callback.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests) return _requests.ToArray();
        }
    }

    public RecordedRequest Last => Requests[^1];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    public HttpClient Client() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, headers,
            request.Content?.Headers.ContentType?.ToString(), body is null ? null : Encoding.UTF8.GetString(body));
        lock (_requests) _requests.Add(recorded);
        return respond(request);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers,
    string? ContentType, string? Body)
{
    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : "";
}
