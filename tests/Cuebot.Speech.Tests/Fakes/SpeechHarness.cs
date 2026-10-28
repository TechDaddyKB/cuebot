using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Speech.Tests.Fakes;

/// <summary>A started <see cref="SpeechService"/> wired to fakes, with settings in a temporary folder.</summary>
internal sealed class SpeechHarness : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cuebot-speech-tests", Guid.NewGuid().ToString("N"));
    private readonly JsonFile<SpeechSettings> _settings;
    private readonly JsonFile<SpeechUserStore> _users;

    public SpeechHarness(params ITtsEngine[] engines) : this(input: null, engines)
    {
    }

    public SpeechHarness(IAudioInput? input, params ITtsEngine[] engines)
    {
        Directory.CreateDirectory(_directory);
        _settings = new JsonFile<SpeechSettings>(Path.Combine(_directory, "speech.json"), Log);
        _users = new JsonFile<SpeechUserStore>(UsersFile, Log);
        Engine = engines.OfType<FakeTtsEngine>().FirstOrDefault() ?? new FakeTtsEngine(voices: "default");
        Bus = new EventBus(Log);
        Users = new SpeechUserDirectory(_users);
        Service = new SpeechService(_settings, engines.Length == 0 ? [Engine] : engines, Player, Bus, Log, Users, Chat,
            input, [Emotes], Time, new Random(1234), [Roles]);
        Settings.Aliases.Add(new VoiceAlias { Name = "Main", EngineId = Engine.Id, VoiceId = "v1" });
        Settings.Commands.ViewerCooldownSeconds = 0;
    }

    public AppLog Log { get; } = new();
    public EventBus Bus { get; }
    public FakeTtsEngine Engine { get; }
    public FakeAudioPlayer Player { get; } = new();
    public FakeChatReplier Chat { get; } = new();
    public FakeEmotes Emotes { get; } = new();
    public FakeChannelRoles Roles { get; } = new();
    public ManualTime Time { get; } = new();
    public SpeechUserDirectory Users { get; }
    public SpeechService Service { get; }
    public SpeechSettings Settings => Service.Settings;
    public string Folder => _directory;
    public string UsersFile => Path.Combine(_directory, "speech-users.json");

    public static async Task<SpeechHarness> StartAsync(params ITtsEngine[] engines)
    {
        var harness = new SpeechHarness(engines);
        await harness.Service.StartAsync();
        return harness;
    }

    public void FlushUsers() => _users.SaveNow();

    /// <summary>Handles a chat event the way the bus would, waiting for any command reply.</summary>
    public Task SayAsync(BotEvent chat) => Service.HandleChatAsync(ChatContext.FromEvent(chat)!);

    /// <summary>Speaks a marker with the lowest priority and waits, so everything queued before it has been spoken.</summary>
    public Task DrainAsync() =>
        Service.SpeakAsync("marker", new SpeakOptions { Alias = "Main", Priority = int.MinValue }, wait: true)
            .WaitAsync(TimeSpan.FromSeconds(5));

    /// <summary>Texts spoken so far, without drain markers.</summary>
    public IReadOnlyList<string> Spoken => Engine.History.Select(c => c.Text).Where(t => t != "marker").ToArray();

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        _settings.Dispose();
        _users.Dispose();
        Log.Dispose();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>Twitch moderators by user id, as the Twitch module's Helix lookup would answer.</summary>
internal sealed class FakeChannelRoles : Cuebot.Core.Chat.Moderation.IChannelRoles
{
    public HashSet<string> Moderators { get; } = new(StringComparer.Ordinal);
    public List<string> Lookups { get; } = [];
    public string Platform => "Twitch";

    public Task<bool> IsModeratorAsync(string userId, CancellationToken ct = default)
    {
        lock (Lookups) Lookups.Add(userId);
        return Task.FromResult(Moderators.Contains(userId));
    }
}

/// <summary>An emote provider with a settable name list.</summary>
internal sealed class FakeEmotes : IEmoteNameProvider
{
    public string Source => "Fake";
    public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> GetEmoteNames(string platform) => Names;
}
