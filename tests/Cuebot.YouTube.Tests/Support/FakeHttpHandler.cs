using System.Net;
using System.Text;
using System.Text.Json;

namespace Cuebot.YouTube.Tests.Support;

/// <summary>A request as seen by <see cref="FakeHttpHandler"/>, with its body already read.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Bearer, string Body)
{
    public string Path => Uri.AbsolutePath;

    public string Query(string name)
    {
        foreach (var pair in Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]) == name)
                return eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return "";
    }

    /// <summary>A value of an application/x-www-form-urlencoded body.</summary>
    public string Form(string name)
    {
        foreach (var pair in Body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (Uri.UnescapeDataString(pair[..eq].Replace('+', ' ')) == name)
                return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return "";
    }

    public JsonElement Json => JsonSerializer.Deserialize<JsonElement>(Body);
}

/// <summary>In-memory HTTP: every request is recorded and answered by the supplied responder.</summary>
public sealed class FakeHttpHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_requests) return _requests.ToArray(); }
    }

    public IReadOnlyList<RecordedRequest> To(string path) => Requests.Where(r => r.Path.EndsWith(path, StringComparison.Ordinal)).ToArray();

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(HttpStatusCode status, string reason, string message = "error") =>
        Json($$$"""{"error":{"code":{{{(int)status}}},"message":"{{{message}}}","errors":[{"reason":"{{{reason}}}","message":"{{{message}}}"}]}}""", status);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!,
            request.Headers.Authorization?.Scheme == "Bearer" ? request.Headers.Authorization.Parameter : null, body);
        lock (_requests) _requests.Add(recorded);
        return respond(recorded);
    }
}
