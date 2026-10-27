using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Chat;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Chat.Pronouns;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.UiTests.Chat;

/// <summary>
/// A chat page wired to fakes: a 7TV-like provider with a few emotes, an image server that generates small PNGs (404
/// for "missing"), alejo.io pronouns, a Twitch moderator/announcer and an action engine whose actions record their args.
/// </summary>
internal sealed class ChatPageFixture : IDisposable
{
    public readonly string DataDir = Directory.CreateTempSubdirectory("cuebot-ui-chat-").FullName;
    public readonly AppLog Log = new();
    public readonly EventBus Bus;
    public readonly ChatHub Hub;
    public readonly FakeSender Sender = new("Twitch");
    public readonly EmoteService Emotes;
    public readonly ImageServer Images = new();
    public readonly PronounServer PronounHttp = new();
    public readonly FakeModerator Moderator = new();
    public readonly FakeAnnouncer Announcer = new();
    public readonly ProfileService Profile;
    public readonly ActionEngine Engine;
    public readonly RecordingSubAction Recorder = new();
    public readonly JsonFile<ChatPageSettings> Settings;
    public readonly JsonFile<EmoteSettings> EmoteSettings;

    public ChatPageFixture(bool withModeration = true)
    {
        Bus = new EventBus(Log);
        Hub = new ChatHub(Bus, Log, [new FakeAdapter("Twitch"), new FakeAdapter("Kick")], [Sender]);
        EmoteSettings = new JsonFile<EmoteSettings>(Path.Combine(DataDir, "emotes.json"), Log);
        Emotes = new EmoteService(Bus, Log, new EmoteDiskCache(Path.Combine(DataDir, "cache"), Log), () => EmoteSettings.Value,
            [new FakeEmoteProvider()]);
        Emotes.RefreshAsync().GetAwaiter().GetResult();

        Profile = new ProfileService(null, Log);
        foreach (var name in new[] { "Shoutout", "Say Hello" })
            Profile.Profile.Actions.Add(new ActionDefinition { Name = name, SubActions = [new SubActionDefinition { Type = RecordingSubAction.Id }] });
        Profile.NotifyChanged();
        Engine = new ActionEngine(Bus, Profile, new Registry([Recorder], []), new GlobalVariables(null, Log), Log);

        Settings = new JsonFile<ChatPageSettings>(Path.Combine(DataDir, "chat-page.json"), Log);
        Settings.Value.Highlights.MentionNames = ["streamer"];
        Settings.Value.Highlights.Keywords = ["giveaway"];
        Settings.Value.QuickActions =
        [
            new QuickActionDefinition { Label = "Shout out", ActionName = "Shoutout", Scope = QuickActionScope.User },
            new QuickActionDefinition { Label = "Hello", ActionName = "Say Hello", Scope = QuickActionScope.Global },
        ];

        Services = new ChatPageServices
        {
            Emotes = Emotes,
            EmoteSettings = EmoteSettings,
            Images = new EmoteImageLoader(new HttpClient(Images)),
            Pronouns = new PronounService(new HttpClient(PronounHttp), Log),
            Moderation = withModeration ? new ChatModerationService([Moderator], [Announcer]) : new ChatModerationService([], []),
            Engine = Engine,
            ActionNames = () => Profile.Snapshot.Actions.Select(a => a.Name),
            Settings = Settings,
        };
    }

    public ChatPageServices Services { get; }

    public ChatViewModel CreateViewModel() => new(Hub, Services);

    /// <summary>Publishes a Twitch-module-style chat event (with emote ranges and first-message flag when given).</summary>
    public void Chat(string user, string text, string? emotes = null, bool firstMessage = false, string platform = "Twitch", string? id = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["messageId"] = id ?? Guid.NewGuid().ToString("N"),
            ["user"] = user,
            ["userName"] = user.ToLowerInvariant(),
            ["userId"] = "id-" + user.ToLowerInvariant(),
            ["message"] = text,
        };
        if (emotes is not null) args["emotes"] = emotes;
        if (firstMessage) args["isFirstMessage"] = true;
        Bus.Publish(new BotEvent($"{platform.ToLowerInvariant()}.chatMessage", args));
    }

    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pumps the UI thread until the condition holds (images and lookups finish on the thread pool).</summary>
    public static void Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met");
            Thread.Sleep(10);
            Pump();
        }
    }

    public void Dispose()
    {
        Engine.Dispose();
        Emotes.Dispose();
        Hub.Dispose();
        Settings.Dispose();
        EmoteSettings.Dispose();
        Log.Dispose();
        try { Directory.Delete(DataDir, recursive: true); }
        catch (IOException) { }
    }
}

internal sealed class FakeAdapter(string platform) : IChatEventAdapter
{
    public string Platform => platform;

    public ChatMessage? ToChatMessage(BotEvent e)
    {
        if (e.Type != $"{platform.ToLowerInvariant()}.chatMessage") return null;
        string S(string k) => e.Args.GetValueOrDefault(k) as string ?? "";
        return new ChatMessage(platform, S("messageId"), S("user"), S("userId"), S("message"), e.Time)
        {
            Color = platform == "Twitch" ? "#FF7F50" : null,
            Roles = S("user") == "Mod" ? ChatRoles.Moderator : ChatRoles.None,
        };
    }

    public FeedItem? ToFeedItem(BotEvent e) => null;
}

internal sealed class FakeSender(string platform) : IChatSender
{
    public List<string> Sent { get; } = [];
    public string Platform => platform;
    public bool CanSend => true;

    public Task SendAsync(string text, CancellationToken ct = default)
    {
        Sent.Add(text);
        return Task.CompletedTask;
    }
}

internal sealed class FakeEmoteProvider : IEmoteProvider
{
    public string Name => EmoteProviders.SevenTv;
    public bool SupportsPlatform(string platform) => true;

    public Task<IReadOnlyList<Emote>> FetchGlobalAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Emote>>(
    [
        new Emote("KEKW", "1", Name, "https://img.test/kekw.png"),
        new Emote("KEKBye", "2", Name, "https://img.test/kekbye.png"),
        new Emote("catJAM", "3", Name, "https://img.test/catjam.png") { IsAnimated = true },
        new Emote("Missing", "4", Name, "https://img.test/missing.png"),
        new Emote("RainTime", "5", Name, "https://img.test/rain.png") { IsZeroWidth = true },
    ]);

    public Task<IReadOnlyList<Emote>> FetchChannelAsync(EmoteChannel channel, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Emote>>([]);
}

/// <summary>Serves a generated 28x28 PNG for every URL except ones containing "missing" (404).</summary>
internal sealed class ImageServer : HttpMessageHandler
{
    private static readonly Lazy<byte[]> Png = new(CreatePng);
    private int _requests;

    public int Requests => _requests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        if (request.RequestUri!.ToString().Contains("missing", StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png.Value) });
    }

    private static byte[] CreatePng()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(28, 28), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = bitmap.Lock())
        {
            var row = new byte[buffer.RowBytes];
            for (var y = 0; y < 28; y++)
            {
                for (var x = 0; x < 28; x++)
                {
                    var inside = (x - 14) * (x - 14) + (y - 14) * (y - 14) < 13 * 13;
                    row[x * 4 + 0] = (byte)(inside ? 40 : 0);    // B
                    row[x * 4 + 1] = (byte)(inside ? 200 : 0);   // G
                    row[x * 4 + 2] = (byte)(inside ? 250 : 0);   // R
                    row[x * 4 + 3] = (byte)(inside ? 255 : 0);   // A
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
            }
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }
}

internal sealed class PronounServer : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        string? body = path switch
        {
            "/v1/pronouns" => """{"theythem":{"name":"theythem","subject":"They","object":"Them","singular":false},"hehim":{"name":"hehim","subject":"He","object":"Him","singular":false}}""",
            "/v1/users/alice" => """{"channel_id":"1","channel_login":"alice","pronoun_id":"theythem","alt_pronoun_id":null}""",
            "/v1/users/bob" => """{"channel_id":"2","channel_login":"bob","pronoun_id":"hehim","alt_pronoun_id":"theythem"}""",
            _ => null,
        };
        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

internal sealed class FakeModerator : IChatModerator
{
    public List<string> Calls { get; } = [];
    public string Platform => "Twitch";
    public ChatModerationActions Available { get; set; } = ChatModerationActions.DeleteMessage | ChatModerationActions.Timeout | ChatModerationActions.Ban;

    public Task DeleteMessageAsync(string messageId, CancellationToken ct = default)
    {
        Calls.Add($"delete {messageId}");
        return Task.CompletedTask;
    }

    public Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default)
    {
        Calls.Add($"timeout {userId} {userName} {duration.TotalSeconds}");
        return Task.CompletedTask;
    }

    public Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default)
    {
        Calls.Add($"ban {userId} {userName}");
        return Task.CompletedTask;
    }
}

internal sealed class FakeAnnouncer : IChatAnnouncer
{
    public List<(string Text, ChatAnnouncementKind Kind)> Posts { get; } = [];
    public string Platform => "Twitch";
    public bool Supports(ChatAnnouncementKind kind) => kind == ChatAnnouncementKind.Announcement;

    public Task AnnounceAsync(string text, ChatAnnouncementKind kind, CancellationToken ct = default)
    {
        Posts.Add((text, kind));
        return Task.CompletedTask;
    }
}

/// <summary>A sub-action that records the arguments of each run.</summary>
internal sealed class RecordingSubAction : ISubActionHandler
{
    public const string Id = "test.record";
    private readonly List<(string Action, Dictionary<string, object?> Args)> _runs = [];

    public IReadOnlyList<(string Action, Dictionary<string, object?> Args)> Runs
    {
        get { lock (_runs) return _runs.ToArray(); }
    }

    public string TypeId => Id;
    public string Category => "Test";
    public string DisplayName => "Record";
    public IReadOnlyList<ParameterDescriptor> Parameters => [];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        lock (_runs) _runs.Add((context.Action.Name, new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase)));
        return Task.FromResult(SubActionResult.Next);
    }
}
