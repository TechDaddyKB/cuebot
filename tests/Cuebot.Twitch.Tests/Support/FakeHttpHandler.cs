using System.Net;
using System.Text;

namespace Cuebot.Twitch.Tests.Support;

/// <summary>A request as seen by <see cref="FakeHttpHandler"/>, with its body already read.</summary>
public sealed record RecordedRequest(HttpMethod Method, string Path, string? Authorization, string? ClientId, string Body)
{
    public IReadOnlyDictionary<string, string> Form =>
        Body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.Length > 1 ? p[1] : ""));
}

/// <summary>In-memory HTTP: every request is recorded and answered by the supplied responder.</summary>
public sealed class FakeHttpHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_requests) return _requests.ToArray(); }
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("Client-Id", out var ids) ? ids.FirstOrDefault() : null,
            body);
        lock (_requests) _requests.Add(recorded);
        return respond(recorded);
    }
}
