using System.Threading.Channels;
using Cuebot.Core.Logging;
using Cuebot.Twitch.EventSub;
using Cuebot.Twitch.Tests.Support;

namespace Cuebot.Twitch.Tests;

public sealed class EventSubClientTests : IAsyncLifetime
{
    private static readonly EventSubTimeouts FastTimeouts = new()
    {
        KeepaliveSlack = TimeSpan.FromMilliseconds(300),
        InitialRetryDelay = TimeSpan.FromMilliseconds(50),
        MaxRetryDelay = TimeSpan.FromMilliseconds(200),
    };

    private readonly FakeEventSubServer _server = new();
    private readonly RecordingListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _run = Task.CompletedTask;

    public Task InitializeAsync()
    {
        var client = new EventSubClient(_server.Url(), _listener, new AppLog(), FastTimeouts);
        _run = client.RunAsync(_stop.Token);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _stop.CancelAsync();
        await _run.WaitAsync(FakeEventSubServer.Timeout);
        await _server.DisposeAsync();
    }

    private async Task<FakeEventSubConnection> ConnectAndWelcomeAsync(string sessionId)
    {
        var connection = await _server.NextConnectionAsync();
        await connection.SendAsync(EventSubMessages.Welcome(sessionId));
        Assert.Equal(sessionId, await _listener.SessionsStarted.NextAsync());
        return connection;
    }

    [Fact]
    public async Task Welcome_invokes_the_subscribe_callback_with_the_session_id()
    {
        var connection = await _server.NextConnectionAsync();

        Assert.Equal("/ws", connection.Path);
        await connection.SendAsync(EventSubMessages.Welcome("session-1"));
        Assert.Equal("session-1", await _listener.SessionsStarted.NextAsync());
    }

    [Fact]
    public async Task Notifications_are_dispatched_once_per_message_id()
    {
        var connection = await ConnectAndWelcomeAsync("session-1");

        await connection.SendAsync(EventSubMessages.Notification("n1", "channel.follow", EventSubMessages.FollowEvent));
        await connection.SendAsync(EventSubMessages.Keepalive());
        await connection.SendAsync(EventSubMessages.Notification("n1", "channel.follow", EventSubMessages.FollowEvent));
        await connection.SendAsync(EventSubMessages.Notification("n2", "channel.follow", EventSubMessages.FollowEvent));

        var first = await _listener.Notifications.NextAsync();
        Assert.Equal("n1", first.MessageId);
        Assert.Equal("channel.follow", first.SubscriptionType);
        Assert.Equal("Cool_User", first.Event.GetProperty("user_name").GetString());
        Assert.Equal("n2", (await _listener.Notifications.NextAsync()).MessageId);
        Assert.False(_listener.Notifications.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Messages_split_across_frames_are_reassembled()
    {
        var connection = await ConnectAndWelcomeAsync("session-1");

        await connection.SendFragmentedAsync(EventSubMessages.Notification("n1", "channel.follow", EventSubMessages.FollowEvent), 4);

        Assert.Equal("n1", (await _listener.Notifications.NextAsync()).MessageId);
    }

    [Fact]
    public async Task Session_reconnect_swaps_connections_without_resubscribing()
    {
        var old = await ConnectAndWelcomeAsync("session-1");

        await old.SendAsync(EventSubMessages.Reconnect("session-1", _server.Url("reconnect")));
        var replacement = await _server.NextConnectionAsync();
        Assert.Equal("/reconnect", replacement.Path);

        // The old connection keeps delivering until the new one is welcomed.
        await old.SendAsync(EventSubMessages.Notification("before-swap", "channel.follow", EventSubMessages.FollowEvent));
        Assert.Equal("before-swap", (await _listener.Notifications.NextAsync()).MessageId);

        await replacement.SendAsync(EventSubMessages.Welcome("session-1"));
        Assert.True(await old.WaitForClientCloseAsync());

        await replacement.SendAsync(EventSubMessages.Notification("after-swap", "channel.follow", EventSubMessages.FollowEvent));
        Assert.Equal("after-swap", (await _listener.Notifications.NextAsync()).MessageId);
        Assert.False(_listener.SessionsStarted.Reader.TryRead(out _));
        Assert.False(_listener.ConnectionsLost.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Missing_keepalives_trigger_a_fresh_reconnect_and_resubscribe()
    {
        var first = await _server.NextConnectionAsync();
        await first.SendAsync(EventSubMessages.Welcome("session-1", keepaliveSeconds: 1));
        Assert.Equal("session-1", await _listener.SessionsStarted.NextAsync());

        Assert.Contains("no message", await _listener.ConnectionsLost.NextAsync());
        await ConnectAndWelcomeAsync("session-2");
    }

    [Fact]
    public async Task Unexpected_close_reconnects_fresh()
    {
        var first = await ConnectAndWelcomeAsync("session-1");

        await first.CloseAsync();

        Assert.Contains("closed", await _listener.ConnectionsLost.NextAsync());
        await ConnectAndWelcomeAsync("session-2");
    }

    [Fact]
    public async Task Revocations_are_reported()
    {
        var connection = await ConnectAndWelcomeAsync("session-1");

        await connection.SendAsync(EventSubMessages.Revocation("channel.follow", "authorization_revoked"));

        Assert.Equal(("channel.follow", "authorization_revoked"), await _listener.Revocations.NextAsync());
    }

    private sealed class RecordingListener : IEventSubListener
    {
        public Channel<string> SessionsStarted { get; } = Channel.CreateUnbounded<string>();
        public Channel<EventSubNotification> Notifications { get; } = Channel.CreateUnbounded<EventSubNotification>();
        public Channel<(string, string)> Revocations { get; } = Channel.CreateUnbounded<(string, string)>();
        public Channel<string> ConnectionsLost { get; } = Channel.CreateUnbounded<string>();

        public Task OnSessionStartedAsync(string sessionId, CancellationToken ct) =>
            SessionsStarted.Writer.WriteAsync(sessionId, ct).AsTask();

        public void OnNotification(EventSubNotification notification) => Notifications.Writer.TryWrite(notification);

        public void OnRevocation(string subscriptionType, string status) => Revocations.Writer.TryWrite((subscriptionType, status));

        public void OnConnectionLost(string reason, TimeSpan retryIn) => ConnectionsLost.Writer.TryWrite(reason);
    }
}

internal static class ChannelTestExtensions
{
    public static async Task<T> NextAsync<T>(this Channel<T> channel)
    {
        using var timeout = new CancellationTokenSource(FakeEventSubServer.Timeout);
        return await channel.Reader.ReadAsync(timeout.Token);
    }
}
