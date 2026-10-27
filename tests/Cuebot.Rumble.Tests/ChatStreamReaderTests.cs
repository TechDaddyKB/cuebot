using System.Collections.Concurrent;
using System.Net;
using Cuebot.Core.Logging;
using Cuebot.Rumble.Chat;
using Cuebot.Rumble.Tests.Support;

namespace Cuebot.Rumble.Tests;

public sealed class ChatStreamReaderTests : IAsyncDisposable
{
    private const long ChatId = 285005514;
    private readonly ConcurrentQueue<Func<HttpResponseMessage>> _responses = new();
    private readonly FakeHttpHandler _http;
    private readonly Listener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public ChatStreamReaderTests() => _http = new FakeHttpHandler(_ =>
        _responses.TryDequeue(out var next) ? next() : new HttpResponseMessage(HttpStatusCode.NoContent));

    private Task RunAsync(DateTimeOffset publishSince) =>
        new ChatStreamReader(new HttpClient(_http), RumbleEndpoints.Production, TestTimings.Fast, TimeProvider.System, new AppLog())
            .RunAsync(ChatId, publishSince, _listener, _stop.Token);

    private FakeEventStream Connection()
    {
        var stream = new FakeEventStream();
        _responses.Enqueue(stream.Response);
        return stream;
    }

    private static string Event(string type, params (string Id, string Text)[] messages)
    {
        var items = messages.Select(m => $$"""{"id":"{{m.Id}}","user_id":"1","text":"{{m.Text}}","time":"2026-01-01T12:00:00+0000"}""");
        return $$"""{"type":"{{type}}","data":{"users":[{"id":"1","username":"Bob","badges":[]}],"messages":[""" + string.Join(",", items) + "]}}";
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _stop.Dispose();
    }

    [Fact]
    public async Task First_init_seeds_history_then_new_messages_are_delivered()
    {
        var stream = Connection();
        var run = RunAsync(DateTimeOffset.MaxValue);

        stream.Push(": \n\n");
        stream.SendData(Event("init", ("1", "old"), ("2", "older")));
        stream.SendData(Event("messages", ("3", "new")));
        await Wait.UntilAsync(() => _listener.Messages.Count == 1, "new message");

        Assert.Equal("new", _listener.Messages.Single().Text);
        Assert.True(_listener.Attached);
        Assert.Equal(ChatId.ToString(), _http.Requests[0].Path.Split('/')[^2]);
        Assert.EndsWith("/stream", _http.Requests[0].Path);

        stream.End();                                  // next connection gets 204: chat over
        await run.WaitAsync(Wait.Timeout);
        Assert.False(_listener.Attached);
        Assert.Contains("closed the chat", _listener.DetachReasons.Last());
    }

    [Fact]
    public async Task Reconnects_and_publishes_only_messages_missed_while_disconnected()
    {
        var first = Connection();
        var second = Connection();
        var run = RunAsync(DateTimeOffset.MaxValue);

        first.SendData(Event("init", ("1", "old")));
        first.SendData(Event("messages", ("2", "live")));
        await Wait.UntilAsync(() => _listener.Messages.Count == 1);
        first.End();

        second.SendData(Event("init", ("1", "old"), ("2", "live"), ("3", "missed")));
        await Wait.UntilAsync(() => _listener.Messages.Count == 2, "missed message after reconnect");

        Assert.Equal(["live", "missed"], _listener.Messages.Select(m => m.Text));
        Assert.Equal(2, _listener.AttachCount);
        second.End();
        await run.WaitAsync(Wait.Timeout);
    }

    [Fact]
    public async Task A_stream_that_just_started_publishes_init_messages_newer_than_its_start()
    {
        var stream = Connection();
        var run = RunAsync(new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero));

        stream.SendData(Event("init", ("1", "since the stream started")));
        await Wait.UntilAsync(() => _listener.Messages.Count == 1);

        stream.End();
        await run.WaitAsync(Wait.Timeout);
    }

    [Fact]
    public async Task Server_errors_are_retried_and_cancellation_ends_quietly()
    {
        _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var stream = Connection();
        var run = RunAsync(DateTimeOffset.MaxValue);

        stream.SendData(Event("init"));
        await Wait.UntilAsync(() => _listener.Attached, "attach after a failed attempt");
        await _stop.CancelAsync();

        await run.WaitAsync(Wait.Timeout);
        Assert.Equal(2, _http.Requests.Count);
    }

    private sealed class Listener : IChatStreamListener
    {
        private readonly ConcurrentQueue<ChatStreamMessage> _messages = new();
        private readonly ConcurrentQueue<string> _detached = new();
        private int _attachCount;

        public IReadOnlyCollection<ChatStreamMessage> Messages => _messages;
        public IReadOnlyCollection<string> DetachReasons => _detached;
        public int AttachCount => _attachCount;
        public bool Attached { get; private set; }

        public void OnAttached()
        {
            Interlocked.Increment(ref _attachCount);
            Attached = true;
        }

        public void OnDetached(string reason)
        {
            _detached.Enqueue(reason);
            Attached = false;
        }

        public void OnMessage(ChatStreamMessage message) => _messages.Enqueue(message);
    }
}
