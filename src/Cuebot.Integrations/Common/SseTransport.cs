using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Cuebot.Integrations.Common;

/// <summary>One server-sent event (https://html.spec.whatwg.org/multipage/server-sent-events.html).</summary>
internal sealed record SseEvent(string Type, string Data, string? Id);

/// <summary>
/// Incremental text/event-stream parser: feed it lines, get complete events. Comment lines (":" keep-alives) and events
/// without data yield nothing, as in a browser's EventSource.
/// </summary>
internal sealed class SseParser
{
    private readonly StringBuilder _data = new();
    private string _type = "";
    private string? _id;
    private bool _hasData;

    /// <summary>Processes one line (without its line break); returns the event an empty line completes, else null.</summary>
    public SseEvent? Line(string line)
    {
        if (line.Length == 0)
        {
            if (!_hasData)
            {
                _type = "";
                return null;
            }
            var e = new SseEvent(_type.Length > 0 ? _type : "message", _data.ToString(), _id);
            _data.Clear();
            _hasData = false;
            _type = "";
            return e;
        }
        if (line[0] == ':') return null;
        var colon = line.IndexOf(':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? "" : line[(colon + 1)..];
        if (value.StartsWith(' ')) value = value[1..];
        switch (field)
        {
            case "data":
                if (_hasData) _data.Append('\n');
                _data.Append(value);
                _hasData = true;
                break;
            case "event":
                _type = value;
                break;
            case "id" when !value.Contains('\0'):
                _id = value;
                break;
        }
        return null;
    }
}

/// <summary>
/// A server-sent-events stream behind the <see cref="IWebSocketTransport"/> shape, so SSE services reuse the shared
/// connect/reconnect loop: <see cref="ReceiveAsync"/> returns the data of each "message" event. A stream that sends
/// nothing (not even a keep-alive comment) for <c>idleTimeout</c> is treated as dead.
/// </summary>
internal sealed class SseTransport(HttpClient http, TimeSpan idleTimeout) : IWebSocketTransport
{
    /// <summary>Shared client without an overall timeout (streams stay open for hours).</summary>
    public static readonly HttpClient SharedClient = CreateClient();

    private readonly SseParser _parser = new();
    private HttpResponseMessage? _response;
    private StreamReader? _reader;

    public string? CloseReason { get; private set; }

    public async Task ConnectAsync(Uri url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(name, value);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"The server answered HTTP {(int)status} ({status})", null, status);
        }
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            response.Dispose();
            throw new HttpRequestException($"The server answered with {(mediaType.Length > 0 ? mediaType : "no content type")}, not an event stream",
                null, HttpStatusCode.UnsupportedMediaType);
        }
        _response = response;
        _reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), Encoding.UTF8);
    }

    public Task SendAsync(string text, CancellationToken ct) => throw new NotSupportedException("An event stream is receive-only.");

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var reader = _reader ?? throw new InvalidOperationException("Not connected");
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(idleTimeout);
            string? line;
            try
            {
                line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                CloseReason = $"no data or keep-alive for {idleTimeout.TotalSeconds:0} s";
                throw new TimeoutException(CloseReason);
            }
            if (line is null)
            {
                CloseReason = "the server ended the event stream";
                return null;
            }
            if (_parser.Line(line) is { Type: "message" } e) return e.Data;
        }
    }

    public ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        _response?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Cuebot");
        return client;
    }
}
