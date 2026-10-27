using System.Net;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.SystemTools;

namespace Cuebot.Tests.SystemTools;

public sealed class HttpRequestSubActionTests : IDisposable
{
    private readonly FakeHandler _handler = new();
    private readonly SystemTestHarness _h;

    public HttpRequestSubActionTests() => _h = new SystemTestHarness(new HttpRequestSubAction(new HttpClient(_handler)));

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Sends_method_headers_and_body_and_stores_the_response()
    {
        _handler.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"id":42}""") };
            response.Headers.Add("X-Rate", "10");
            return response;
        };

        var args = await _h.RunAsync("core.httpRequest",
            ("method", "post"), ("url", "https://api.example.test/items?x=1"),
            ("headers", "Authorization: Bearer secret-token\n\n# comment\nX-Custom:  a:b \r\nContent-Language: de"),
            ("body", """{"name":"x"}"""), ("contentType", "application/json; charset=utf-8"));

        var request = _handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.example.test/items?x=1", request.Uri);
        Assert.Equal("Bearer secret-token", request.Headers["Authorization"]);
        Assert.Equal("a:b", request.Headers["X-Custom"]);
        Assert.Equal("de", request.Headers["Content-Language"]);
        Assert.Equal("application/json; charset=utf-8", request.Headers["Content-Type"]);
        Assert.Equal("""{"name":"x"}""", request.Body);

        Assert.Equal("""{"id":42}""", args!["httpResponse"]);
        Assert.Equal(201, args["httpStatusCode"]);
        Assert.Equal(true, args["httpSuccess"]);
        Assert.Contains("X-Rate: 10", (string)args["httpHeaders"]!);
        Assert.DoesNotContain(_h.Log.Recent(), e => e.Message.Contains("secret-token"));
    }

    [Fact]
    public async Task Get_without_body_sends_no_content()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("missing") };
        var args = await _h.RunAsync("core.httpRequest", ("url", "http://localhost:1/x"), ("variable", "page"));

        Assert.Null(_handler.Requests.Single().Body);
        Assert.Equal(HttpMethod.Get, _handler.Requests.Single().Method);
        Assert.Equal("missing", args!["page"]);
        Assert.Equal(404, args["httpStatusCode"]);
        Assert.Equal(false, args["httpSuccess"]);
    }

    [Fact]
    public async Task Network_failures_and_timeouts_set_error_arguments()
    {
        _handler.Respond = _ => throw new HttpRequestException("connection refused");
        var failed = await _h.RunAsync("core.httpRequest", ("url", "https://down.example.test/"));
        Assert.Equal(false, failed!["httpSuccess"]);
        Assert.Equal(0, failed["httpStatusCode"]);
        Assert.Equal("connection refused", failed["httpError"]);

        _handler.Delay = TimeSpan.FromSeconds(30);
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
        var slow = await _h.RunAsync("core.httpRequest", ("url", "https://slow.example.test/"), ("timeout", "1"));
        Assert.Equal(false, slow!["httpSuccess"]);
        Assert.StartsWith("Timed out", (string)slow["httpError"]!);
    }

    [Theory]
    [InlineData("ftp://example.test/")]
    [InlineData("not a url")]
    public async Task Rejects_non_http_urls(string url) =>
        Assert.Null(await _h.RunAsync("core.httpRequest", ("url", url)));

    [Fact]
    public async Task Bad_header_lines_fail_without_revealing_the_line()
    {
        Assert.Null(await _h.RunAsync("core.httpRequest", ("url", "https://example.test/"), ("headers", "Authorization Bearer hunter2")));
        Assert.Empty(_handler.Requests);
        Assert.DoesNotContain(_h.Log.Recent(), e => e.Message.Contains("hunter2"));
    }

    [Fact]
    public void Header_lines_parse_and_flag_sensitive_names()
    {
        var headers = HttpHeaderLines.Parse("A: 1\r\n  B :two words  \n#C: no\n");
        Assert.Equal([new("A", "1"), new KeyValuePair<string, string>("B", "two words")], headers);
        Assert.Throws<FormatException>(() => HttpHeaderLines.Parse(": value"));
        Assert.Throws<FormatException>(() => HttpHeaderLines.Parse("Bad Name: x"));
        Assert.True(HttpHeaderLines.IsSensitive("authorization"));
        Assert.False(HttpHeaderLines.IsSensitive("Accept"));
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, Dictionary<string, string> Headers, string? Body);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public TimeSpan Delay { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.ToString(), headers, body));
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return Respond(request);
        }
    }
}
