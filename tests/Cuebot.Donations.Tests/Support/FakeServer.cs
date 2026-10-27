using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Cuebot.Donations.Transport;

namespace Cuebot.Donations.Tests.Support;

/// <summary>In-memory websocket server: every transport the client creates shows up in <see cref="NextAsync"/>.</summary>
internal sealed class FakeServer
{
    private readonly Channel<FakeSocket> _accepted = Channel.CreateUnbounded<FakeSocket>();
    private readonly List<Uri> _urls = [];

    /// <summary>Return true to fail the connection (like an HTTP 400 on the upgrade).</summary>
    public Func<Uri, bool> Reject { get; set; } = _ => false;

    public IReadOnlyList<Uri> Urls
    {
        get { lock (_urls) return _urls.ToArray(); }
    }

    public IWebSocketTransport Create() => new FakeSocket(this);

    internal void Accept(FakeSocket socket, Uri url)
    {
        lock (_urls) _urls.Add(url);
        _accepted.Writer.TryWrite(socket);
    }

    public async Task<FakeSocket> NextAsync()
    {
        using var cts = new CancellationTokenSource(Wait.Timeout);
        return await _accepted.Reader.ReadAsync(cts.Token);
    }
}

internal sealed class FakeSocket(FakeServer server) : IWebSocketTransport
{
    private readonly Channel<string?> _toClient = Channel.CreateUnbounded<string?>();
    private readonly Channel<string> _fromClient = Channel.CreateUnbounded<string>();

    public Uri? Url { get; private set; }
    public bool Disposed { get; private set; }
    public string? CloseReason { get; private set; }

    public Task ConnectAsync(Uri url, CancellationToken ct)
    {
        if (server.Reject(url))
        {
            throw new WebSocketException($"Unable to connect to {url}: status code '400'");
        }
        Url = url;
        server.Accept(this, url);
        return Task.CompletedTask;
    }

    public Task SendAsync(string text, CancellationToken ct)
    {
        _fromClient.Writer.TryWrite(text);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        try
        {
            return await _toClient.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>Server → client text frame.</summary>
    public void Push(string text) => _toClient.Writer.TryWrite(text);

    /// <summary>Server closes the websocket.</summary>
    public void Close(string reason = "server closed the connection (1000)")
    {
        CloseReason = reason;
        _toClient.Writer.TryWrite(null);
    }

    /// <summary>The next frame the client sent.</summary>
    public async Task<string> NextSentAsync()
    {
        using var cts = new CancellationTokenSource(Wait.Timeout);
        return await _fromClient.Reader.ReadAsync(cts.Token);
    }

    /// <summary>True when the client sends nothing for <paramref name="time"/>.</summary>
    public async Task<bool> SilentForAsync(TimeSpan time)
    {
        using var cts = new CancellationTokenSource(time);
        try
        {
            await _fromClient.Reader.WaitToReadAsync(cts.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal static class Wait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task UntilAsync(Func<bool> condition, string what = "condition")
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(10);
        }
    }
}

internal static class TestTimings
{
    public static readonly DonationsTimings Fast = new(
        ConnectTimeout: TimeSpan.FromSeconds(2),
        OpenTimeout: TimeSpan.FromSeconds(2),
        ReconnectDelay: TimeSpan.FromMilliseconds(20),
        MaxReconnectDelay: TimeSpan.FromMilliseconds(200),
        KeepAlive: TimeSpan.FromSeconds(30));
}

/// <summary>A request as seen by <see cref="FakeHttpHandler"/>.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body);

/// <summary>In-memory HTTP: every request is recorded and answered by the supplied responder.</summary>
internal sealed class FakeHttpHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
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
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body);
        lock (_requests) _requests.Add(recorded);
        return respond(recorded);
    }
}
