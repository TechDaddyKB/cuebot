using System.Net;
using System.Text;

namespace Cuebot.Integrations.Tests.Support;

/// <summary>A request as seen by <see cref="FakeHttpHandler"/>, with its body already read.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Bearer, string Body)
{
    public string Path => Uri.AbsolutePath;

    public string Query(string name) => Values(Uri.Query.TrimStart('?')).GetValueOrDefault(name, "");

    /// <summary>A value of an application/x-www-form-urlencoded body.</summary>
    public string Form(string name) => Values(Body).GetValueOrDefault(name, "");

    private static Dictionary<string, string> Values(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            result[key] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return result;
    }
}

/// <summary>In-memory HTTP: every request is recorded and answered by the supplied responder.</summary>
internal sealed class FakeHttpHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_requests) return _requests.ToArray(); }
    }

    public IReadOnlyList<RecordedRequest> To(string path) => Requests.Where(r => r.Path.EndsWith(path, StringComparison.Ordinal)).ToArray();

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Empty(HttpStatusCode status = HttpStatusCode.NoContent) => new(status) { Content = new ByteArrayContent([]) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!,
            request.Headers.Authorization?.Scheme == "Bearer" ? request.Headers.Authorization.Parameter : null, body);
        lock (_requests) _requests.Add(recorded);
        return respond(recorded);
    }
}
