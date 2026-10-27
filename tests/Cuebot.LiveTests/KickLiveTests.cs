using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Kick;
using Cuebot.Kick.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in tests against the real Kick API and real-time chat with your own Kick app. The first run opens the
/// browser once for the OAuth sign-in; the tokens are kept in the live-test data directory. See docs/LIVE_TESTING.md.
/// </summary>
[Collection(KickLiveCollection.Name)]
public sealed class KickLiveTests(KickLiveBot bot)
{
    private static readonly TimeSpan ChatTimeout = TimeSpan.FromSeconds(30);

    [LiveKickFact]
    public void Connects_and_reads_the_channel()
    {
        Assert.Equal(ConnectionState.Connected, bot.Kick.State);
        Assert.False(string.IsNullOrWhiteSpace(bot.Kick.Settings.ChannelSlug));
        Assert.NotNull(bot.Kick.Channel);
        Assert.True(bot.Kick.Settings.Broadcaster.UserId > 0);
    }

    [LiveKickFact]
    public async Task Sent_message_comes_back_through_realtime_chat()
    {
        await KickLiveBot.WaitUntilAsync(() => bot.Kick.IsRealtimeConnected, ChatTimeout,
            "real-time chat to connect (if kick.com blocks the id lookup, set CUEBOT_LIVE_KICK_CHATROOM_ID and CUEBOT_LIVE_KICK_CHANNEL_ID)");
        var text = $"dev testing {Guid.NewGuid():N}"[..24];
        var since = DateTimeOffset.Now;
        var id = await bot.Kick.SendChatMessageAsync(text);
        Assert.False(string.IsNullOrEmpty(id));
        var e = await bot.WaitForEventAsync(KickEventTypes.ChatMessage, since, ChatTimeout, x => x.Args["message"] as string == text);
        Assert.Equal(bot.Kick.Settings.Broadcaster.UserName, e.Args["user"]);
        Assert.Equal(true, e.Args["isBroadcaster"]);
    }
}

[CollectionDefinition(Name)]
public sealed class KickLiveCollection : ICollectionFixture<KickLiveBot>
{
    public const string Name = "Live Kick";
}

public sealed class KickLiveBot : IAsyncLifetime
{
    private readonly ConcurrentQueue<BotEvent> _events = new();
    private ServiceProvider? _services;

    public KickService Kick => (_services ?? throw new InvalidOperationException("Kick live tests are not configured")).GetRequiredService<KickService>();

    public async Task InitializeAsync()
    {
        if (!LiveEnvironment.RunRequested || KickLiveEnvironment.ClientId is not { } clientId || KickLiveEnvironment.ClientSecret is not { } secret)
            return;   // every test skips
        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, "kick");
        Directory.CreateDirectory(dataDirectory);
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddKick().BuildServiceProvider();
        _services.GetRequiredService<EventBus>().Subscribe(_events.Enqueue);

        var s = Kick.Settings;
        s.ClientId = clientId;
        s.ClientSecret = secret;
        s.AutoConnect = false;
        if (KickLiveEnvironment.RedirectPort is { } port) s.RedirectPort = port;
        if (KickLiveEnvironment.ChannelId is { } channelId) s.ChannelId = channelId;
        if (KickLiveEnvironment.ChatroomId is { } chatroomId) s.ChatroomId = chatroomId;
        if (!Kick.IsBroadcasterSignedIn)
            await Kick.SignInAsync(KickAccount.Broadcaster, url => Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true }));
        await Kick.ConnectAsync();
        Kick.SaveSettings();
    }

    public async Task<BotEvent> WaitForEventAsync(string type, DateTimeOffset since, TimeSpan timeout, Func<BotEvent, bool>? match = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = _events.FirstOrDefault(e => e.Type == type && e.Time >= since && (match?.Invoke(e) ?? true));
            if (found is not null) return found;
            await Task.Delay(200);
        }
        throw new TimeoutException($"No {type} event within {timeout.TotalSeconds:0}s");
    }

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(200);
        }
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
    }
}

internal static class KickLiveEnvironment
{
    public static string? ClientId => Get("CUEBOT_LIVE_KICK_CLIENT_ID");

    /// <summary>Kick requires the client secret for token requests. Never printed.</summary>
    public static string? ClientSecret => Get("CUEBOT_LIVE_KICK_CLIENT_SECRET");

    public static int? RedirectPort => int.TryParse(Get("CUEBOT_LIVE_KICK_REDIRECT_PORT"), out var p) ? p : null;
    public static long? ChannelId => long.TryParse(Get("CUEBOT_LIVE_KICK_CHANNEL_ID"), out var id) ? id : null;
    public static long? ChatroomId => long.TryParse(Get("CUEBOT_LIVE_KICK_CHATROOM_ID"), out var id) ? id : null;

    private static string? Get(string name)
    {
        // LiveEnvironment's static constructor loads live.env into the environment.
        RuntimeHelpers.RunClassConstructor(typeof(LiveEnvironment).TypeHandle);
        return Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
    }
}

/// <summary>Runs only with CUEBOT_LIVE_KICK_CLIENT_ID and _CLIENT_SECRET set. Posts a "dev testing" message to your own chat.</summary>
public sealed class LiveKickFactAttribute : LiveFactAttribute
{
    public LiveKickFactAttribute()
    {
        if (Skip is not null) return;
        if (KickLiveEnvironment.ClientId is null || KickLiveEnvironment.ClientSecret is null)
            Skip = "Set CUEBOT_LIVE_KICK_CLIENT_ID and CUEBOT_LIVE_KICK_CLIENT_SECRET to run against the real Kick API (see docs/LIVE_TESTING.md)";
    }
}
