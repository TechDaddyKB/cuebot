using System.Collections.Concurrent;
using System.Diagnostics;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.YouTube;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in tests against the real YouTube Data API with the user's own Google Cloud OAuth client. The read-only test
/// signs in (device code, approved once) and connects; the chat test runs only while a broadcast is live and
/// CUEBOT_LIVE_YOUTUBE_CHAT=1, because it posts a "dev testing" message (50 quota units). See docs/LIVE_TESTING.md.
/// </summary>
[Collection(YouTubeLiveCollection.Name)]
public sealed class YouTubeLiveTests(YouTubeLiveBot bot)
{
    private static readonly TimeSpan ChatTimeout = TimeSpan.FromMinutes(1);

    [LiveYouTubeFact]
    public void Signs_in_connects_and_reads_the_channel()
    {
        Assert.Equal(ConnectionState.Connected, bot.YouTube.State);
        Assert.StartsWith("UC", bot.YouTube.Settings.Broadcaster.ChannelId);
        Assert.False(string.IsNullOrWhiteSpace(bot.YouTube.Settings.Broadcaster.ChannelTitle));
        Assert.True(bot.YouTube.QuotaUsedToday > 0);
    }

    [LiveYouTubeChatFact]
    public async Task Sent_chat_message_comes_back_through_the_chat_poll()
    {
        await YouTubeLiveBot.WaitUntilAsync(() => bot.YouTube.IsLive, TimeSpan.FromMinutes(3), "a live YouTube broadcast to be monitored");
        var since = DateTimeOffset.Now;
        var text = $"dev testing: Cuebot YouTube live test {Guid.NewGuid():N}"[..48];

        await bot.YouTube.SendChatMessageAsync(text);

        await bot.WaitForEventAsync(YouTubeEventTypes.ChatMessage, since, ChatTimeout, e => e.Args["message"] as string == text);
    }
}

/// <summary>Settings of the YouTube live tests, from the environment or the live.env file.</summary>
internal static class YouTubeLiveEnvironment
{
    /// <summary>OAuth client of type "TVs and Limited Input devices" from your Google Cloud project.</summary>
    public static string? ClientId => Get("CUEBOT_LIVE_YOUTUBE_CLIENT_ID");

    public static string? ClientSecret => Get("CUEBOT_LIVE_YOUTUBE_CLIENT_SECRET");

    /// <summary>Posting to chat is visible to viewers, so it needs CUEBOT_LIVE_YOUTUBE_CHAT=1.</summary>
    public static bool ChatAllowed => Get("CUEBOT_LIVE_YOUTUBE_CHAT") == "1";

    private static string? Get(string name)
    {
        _ = LiveEnvironment.RunRequested;   // loads the live.env file
        return Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
    }
}

public sealed class LiveYouTubeFactAttribute : LiveFactAttribute
{
    public LiveYouTubeFactAttribute()
    {
        if (Skip is not null) return;
        if (YouTubeLiveEnvironment.ClientId is null || YouTubeLiveEnvironment.ClientSecret is null)
            Skip = "Set CUEBOT_LIVE_YOUTUBE_CLIENT_ID and CUEBOT_LIVE_YOUTUBE_CLIENT_SECRET (see docs/LIVE_TESTING.md)";
    }
}

public sealed class LiveYouTubeChatFactAttribute : LiveFactAttribute
{
    public LiveYouTubeChatFactAttribute()
    {
        if (Skip is not null) return;
        if (YouTubeLiveEnvironment.ClientId is null || YouTubeLiveEnvironment.ClientSecret is null)
            Skip = "Set CUEBOT_LIVE_YOUTUBE_CLIENT_ID and CUEBOT_LIVE_YOUTUBE_CLIENT_SECRET (see docs/LIVE_TESTING.md)";
        else if (!YouTubeLiveEnvironment.ChatAllowed)
            Skip = "Posting to YouTube chat is visible: set CUEBOT_LIVE_YOUTUBE_CHAT=1 and be live to run it";
    }
}

/// <summary>A real Cuebot with the YouTube module, shared by the YouTube live tests.</summary>
public sealed class YouTubeLiveBot : IAsyncLifetime
{
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private readonly ConcurrentQueue<BotEvent> _events = new();
    private ServiceProvider? _services;

    public YouTubeService YouTube =>
        (_services ?? throw new InvalidOperationException("YouTube live tests are not configured")).GetRequiredService<YouTubeService>();

    public async Task InitializeAsync()
    {
        if (!LiveEnvironment.RunRequested || YouTubeLiveEnvironment.ClientId is not { } clientId
            || YouTubeLiveEnvironment.ClientSecret is not { } secret) return;   // every test skips

        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, "youtube");
        Directory.CreateDirectory(dataDirectory);
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddYouTube().BuildServiceProvider();
        _services.GetRequiredService<EventBus>().Subscribe(_events.Enqueue);

        YouTube.Settings.ClientId = clientId;
        YouTube.Settings.ClientSecret = secret;
        YouTube.Settings.AutoConnect = false;
        YouTube.Settings.PollNewSubscribers = false;
        if (!YouTube.IsBroadcasterSignedIn) await SignInAsync(dataDirectory);
        else await YouTube.ConnectAsync();
        await WaitUntilAsync(() => YouTube.State == ConnectionState.Connected, TimeSpan.FromSeconds(30), "the YouTube connection");
    }

    private async Task SignInAsync(string dataDirectory)
    {
        var auth = await YouTube.BeginDeviceSignInAsync(YouTubeAccountKind.Broadcaster);
        var message = $"Approve Cuebot's live tests on YouTube: {auth.VerificationUrl} (code {auth.UserCode})";
        Console.Error.WriteLine(message);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "sign-in.txt"), message);
        try { Process.Start(new ProcessStartInfo(auth.VerificationUrl) { UseShellExecute = true }); }
        catch { /* headless: use the printed link or sign-in.txt */ }

        using var timeout = new CancellationTokenSource(SignInTimeout);
        await YouTube.CompleteDeviceSignInAsync(auth, timeout.Token);   // connects afterwards
    }

    public async Task<BotEvent> WaitForEventAsync(string type, DateTimeOffset since, TimeSpan timeout, Func<BotEvent, bool>? match = null)
    {
        BotEvent? hit = null;
        await WaitUntilAsync(() => (hit = _events.FirstOrDefault(e => e.Type == type && e.Time >= since && (match?.Invoke(e) ?? true))) is not null,
            timeout, type);
        return hit!;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(250);
        }
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class YouTubeLiveCollection : ICollectionFixture<YouTubeLiveBot>
{
    public const string Name = "Live YouTube";
}
