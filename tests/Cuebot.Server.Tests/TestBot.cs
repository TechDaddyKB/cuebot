using System.Collections.Concurrent;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Users;
using Cuebot.Core.Variables;
using Cuebot.Scripting;
using Cuebot.Scripting.Compat;
using Cuebot.Server.SpeakerBot;

namespace Cuebot.Server.Tests;

/// <summary>Real core services (engine, variables, users, commands, chat) with fakes for chat sending and speech.</summary>
internal sealed class TestBot : IAsyncDisposable
{
    public TestBot(ServerSettings? settings = null, TestBotOptions? options = null)
    {
        options ??= new TestBotOptions();
        Bus = new EventBus(Log);
        Profile = new ProfileService(null, Log);
        Globals = new GlobalVariables(null, Log);
        UserVariables = new UserVariables(null, Log);
        Users = new UserRegistry(null, Bus, Log);
        Groups = new GroupStore(null, Log, Bus);
        Commands = new CommandStore(null, Log);
        Chat = new ChatHub(Bus, Log, [], [Sender]);
        CodeTriggers = new CodeTriggerRegistry();

        Api = new ScriptApi([new CoreScriptFunctions(), new VariableScriptFunctions(UserVariables), Functions, .. options.Functions]);
        Scripts = new CSharpScriptEngine(Api, new CphServices(new LateProvider(this)));
        Registry = new Registry([Capture, new CSharpCodeSubAction(Scripts), new LogSubAction(), new IfSubAction(), new DelaySubAction()],
            [.. PlatformTriggers, .. options.Triggers], [new CodeTriggerProvider(CodeTriggers), new CommandTriggers(Commands), new CoreTriggers(Profile)]);
        Timers = new TimerService(Profile, Bus);
        Transfer = new Cuebot.Core.Transfer.TransferService(Profile, Commands, Globals, () => Registry);
        Engine = new ActionEngine(Bus, Profile, Registry, Globals, Log);
        Engine.Start();
        Engine.RunCompleted += r => Runs.Enqueue(r);

        Services = new ServerServices
        {
            Engine = Engine, Profile = Profile, Globals = Globals, Bus = Bus, Log = Log, UserVariables = UserVariables,
            Users = Users, Commands = Commands, Chat = Chat, CodeTriggers = CodeTriggers, Speaker = Speaker,
            Credits = Credits, Registry = Registry, Timers = Timers, Transfer = Transfer, Scripts = Api,
            Connections = [new FakeConnection("Twitch", ConnectionState.Connected), new FakeConnection("YouTube", ConnectionState.Disconnected)],
            Pronouns = options.Pronouns, Emotes = options.Emotes?.Invoke(Bus, Log),
            Overlays = Overlays, OverlayAssets = OverlayAssets, OverlayFeed = OverlayFeed, SessionData = SessionData,
            Widgets = Widgets, WidgetCode = WidgetCode, WidgetStore = WidgetStore, OverlayBridge = OverlayBridge, Elements = Elements,
            AlertQueue = AlertQueue,
        };
        Emotes = Services.Emotes;
        Manager = new ServerManager(null, Services);
        if (settings is not null) Copy(settings, Manager.Settings);
        Bus.Subscribe(e => Events.Enqueue(e));
    }

    /// <summary>Stand-ins for some platform modules' triggers, so GetEvents lists their events as in the app.</summary>
    internal static readonly ITriggerType[] PlatformTriggers =
    [
        .. new[]
        {
            "twitch.chatMessage", "twitch.follow", "twitch.sub", "twitch.resub", "twitch.subGift", "twitch.giftBomb",
            "twitch.cheer", "twitch.raid", "twitch.rewardRedemption", "twitch.emoteModeOn", "youtube.chatMessage",
            "youtube.superChat", "youtube.newSponsor", "kick.chatMessage", "kick.subscription", "users.firstWords",
            "slobs.streamStarted", "obs.streamStarted", "tits.itemHit", "pulsoid.heartRate",
        }.Select(t => (ITriggerType)new EventTriggerType(t, "Test", t, t)),
    ];

    public AppLog Log { get; } = new();
    public Cuebot.Core.Overlays.OverlayStore Overlays { get; } = new(null, new AppLog());
    public Cuebot.Core.Overlays.AssetStore OverlayAssets { get; } = new(null);
    public Cuebot.Core.Overlays.ElementStore Elements { get; } = new(null, new AppLog());
    public Cuebot.Core.Overlays.Widgets.WidgetCodeStore WidgetCode { get; } = new(null);
    public Cuebot.Core.Overlays.Widgets.WidgetKeyValueStore WidgetStore { get; } = new(null, new AppLog());
    public Cuebot.Core.Overlays.Widgets.WidgetLibrary Widgets => _widgets ??= new(Overlays, Elements, WidgetCode);
    private Cuebot.Core.Overlays.Widgets.WidgetLibrary? _widgets;
    public Cuebot.Core.Overlays.Events.OverlayEventBridge OverlayBridge => _overlayBridge ??= new(Bus, OverlayFeed, [], Log);
    private Cuebot.Core.Overlays.Events.OverlayEventBridge? _overlayBridge;
    public Cuebot.Core.Overlays.OverlayAlertQueue AlertQueue => _alertQueue ??= new(OverlayFeed);
    private Cuebot.Core.Overlays.OverlayAlertQueue? _alertQueue;
    public Cuebot.Core.Overlays.OverlayFeed OverlayFeed => _overlayFeed ??= new Cuebot.Core.Overlays.OverlayFeed(Log);
    private Cuebot.Core.Overlays.OverlayFeed? _overlayFeed;
    public Cuebot.Core.Overlays.Session.SessionDataStore SessionData => _sessionData ??= new(null, Log, Bus, OverlayFeed, null);
    private Cuebot.Core.Overlays.Session.SessionDataStore? _sessionData;
    public Cuebot.Core.Chat.Emotes.EmoteService? Emotes { get; }
    public Cuebot.Core.Credits.CreditsCollector Credits => _credits ??= new Cuebot.Core.Credits.CreditsCollector(null, Bus, Log);
    private Cuebot.Core.Credits.CreditsCollector? _credits;
    public EventBus Bus { get; }
    public ProfileService Profile { get; }
    public GlobalVariables Globals { get; }
    public UserVariables UserVariables { get; }
    public UserRegistry Users { get; }
    public GroupStore Groups { get; }
    public CommandStore Commands { get; }
    public ChatHub Chat { get; }
    public CodeTriggerRegistry CodeTriggers { get; }
    public ScriptApi Api { get; }
    public CSharpScriptEngine Scripts { get; }
    public Registry Registry { get; }
    public TimerService Timers { get; }
    public Cuebot.Core.Transfer.TransferService Transfer { get; }
    public ActionEngine Engine { get; }
    public ServerServices Services { get; }
    public ServerManager Manager { get; }
    public FakeSender Sender { get; } = new();
    public FakeSpeaker Speaker { get; } = new();
    public FakeFunctions Functions { get; } = new();
    public CaptureSubAction Capture { get; } = new();
    public ConcurrentQueue<BotEvent> Events { get; } = new();
    public ConcurrentQueue<ActionRunRecord> Runs { get; } = new();

    private static void Copy(ServerSettings from, ServerSettings to)
    {
        to.AllowLan = from.AllowLan;
        to.AllowedOrigins = from.AllowedOrigins;
        to.WebSocket = from.WebSocket;
        to.Http = from.Http;
        to.Udp = from.Udp;
        to.SpeakerBot = from.SpeakerBot;
        to.CustomServers = from.CustomServers;
        to.CustomClients = from.CustomClients;
        to.Api = from.Api;
        to.Mcp = from.Mcp;
        to.Overlays = from.Overlays;
    }

    /// <summary>Adds an action that ends with a capture step (optionally after a C# step and with triggers).</summary>
    public ActionDefinition AddAction(string name, string? csharp = null, params TriggerDefinition[] triggers)
    {
        var action = new ActionDefinition { Name = name, Group = "Tests", Concurrent = true };
        action.Triggers.AddRange(triggers);
        if (csharp is not null)
        {
            action.SubActions.Add(new SubActionDefinition { Type = "code.csharp", Parameters = { ["code"] = csharp, ["timeoutSeconds"] = "30" } });
        }
        action.SubActions.Add(new SubActionDefinition { Type = CaptureSubAction.Id });
        Profile.Profile.Actions.Add(action);
        Profile.NotifyChanged();
        return action;
    }

    public async Task<ActionRunRecord> WaitForRunAsync(string actionName, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            var run = Runs.FirstOrDefault(r => r.ActionName == actionName);
            if (run is not null) return run;
            await Task.Delay(20);
        }
        throw new TimeoutException($"'{actionName}' did not run");
    }

    public async Task<BotEvent> WaitForEventAsync(string type, Func<BotEvent, bool>? match = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var e = Events.FirstOrDefault(x => x.Type == type && (match?.Invoke(x) ?? true));
            if (e is not null) return e;
            await Task.Delay(20);
        }
        throw new TimeoutException($"No '{type}' event");
    }

    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        Emotes?.Dispose();
        Overlays.Dispose();
        OverlayAssets.Dispose();
        Elements.Dispose();
        WidgetCode.Dispose();
        WidgetStore.Dispose();
        _sessionData?.Dispose();
        _overlayBridge?.Dispose();
        Scripts.Dispose();
        Engine.Dispose();
        Timers.Dispose();
        Chat.Dispose();
        Users.Dispose();
        Profile.Dispose();
        Globals.Dispose();
        Log.Dispose();
    }

    /// <summary>A tiny service provider so CphServices resolves the engine lazily, like in the app.</summary>
    private sealed class LateProvider(TestBot bot) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ActionEngine) ? bot.Engine
            : serviceType == typeof(ProfileService) ? bot.Profile
            : serviceType == typeof(Registry) ? bot.Registry
            : serviceType == typeof(EventBus) ? bot.Bus
            : serviceType == typeof(GlobalVariables) ? bot.Globals
            : serviceType == typeof(UserVariables) ? bot.UserVariables
            : serviceType == typeof(UserRegistry) ? bot.Users
            : serviceType == typeof(GroupStore) ? bot.Groups
            : serviceType == typeof(CommandStore) ? bot.Commands
            : serviceType == typeof(ChatHub) ? bot.Chat
            : serviceType == typeof(AppLog) ? bot.Log
            : serviceType == typeof(CodeTriggerRegistry) ? bot.CodeTriggers
            : null;
    }
}

/// <summary>Optional extras: more script functions and trigger types, pronoun and emote services.</summary>
internal sealed class TestBotOptions
{
    public IReadOnlyList<IScriptFunctionProvider> Functions { get; init; } = [];
    public IReadOnlyList<ITriggerType> Triggers { get; init; } = [];
    public Cuebot.Core.Chat.Pronouns.PronounService? Pronouns { get; init; }
    public Func<EventBus, AppLog, Cuebot.Core.Chat.Emotes.EmoteService>? Emotes { get; init; }
}

internal sealed class CaptureSubAction : ISubActionHandler
{
    public const string Id = "test.capture";
    public string TypeId => Id;
    public string Category => "Test";
    public string DisplayName => "Capture";
    public IReadOnlyList<ParameterDescriptor> Parameters => [];
    public ConcurrentDictionary<string, Dictionary<string, object?>> ByAction { get; } = new();

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        ByAction[context.Action.Name] = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(SubActionResult.Next);
    }
}

internal sealed class FakeSender : IChatSender
{
    public string Platform => "Twitch";
    public bool CanSend => true;
    public ConcurrentQueue<string> Sent { get; } = new();
    public ConcurrentQueue<(string Text, ChatSendOptions Options)> SentWithOptions { get; } = new();

    public Task SendAsync(string text, CancellationToken ct = default)
    {
        Sent.Enqueue(text);
        return Task.CompletedTask;
    }

    public Task SendAsync(string text, ChatSendOptions options, CancellationToken ct = default)
    {
        SentWithOptions.Enqueue((text, options));
        return SendAsync(text, ct);
    }
}

internal sealed class FakeConnection(string name, ConnectionState state) : IConnectionStatus
{
    public string Name => name;
    public ConnectionState State => state;
    public string StatusMessage => state.ToString();
    public event Action? StatusChanged { add { } remove { } }
}

/// <summary>Script functions standing in for platform modules (obs.request, speech.speak).</summary>
internal sealed class FakeFunctions : IScriptFunctionProvider
{
    public ConcurrentQueue<(string Name, string Args)> Calls { get; } = new();

    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("obs.request", "fake", ["requestType", "requestData"], (_, a, _) =>
        {
            Calls.Enqueue(("obs.request", a.GetRawText()));
            object? result = a.GetString("requestType") switch
            {
                "GetCurrentProgramScene" => new { currentProgramSceneName = "Main" },
                "GetSceneItemId" => new { sceneItemId = 7 },
                _ => new { },
            };
            return Task.FromResult<object?>(result);
        }),
        new("speech.speak", "fake", ["text", "alias"], (_, a, _) =>
        {
            Calls.Enqueue(("speech.speak", a.GetRawText()));
            return Task.FromResult<object?>(null);
        }),
    ];
}

internal sealed class FakeSpeaker : ISpeakerBackend
{
    public bool Enabled { get; set; } = true;
    public bool Paused { get; set; }
    public bool EventsEnabled { get; set; } = true;
    public int QueueLength => 0;
    public string? CurrentText => null;
    public IReadOnlyList<string> Aliases => ["Narrator", "Robot"];
    public ConcurrentQueue<string> Log { get; } = new();

    public Task<SpeakerSpeakResult> SpeakAsync(string text, string? alias, bool badWordFilter, bool wait, bool silent, CancellationToken ct)
    {
        Log.Enqueue($"speak:{alias}:{text}:{badWordFilter}:{wait}:{silent}");
        return Task.FromResult(new SpeakerSpeakResult(true, wait ? 1500 : 0, silent ? "speech.wav" : ""));
    }

    public void Stop() => Log.Enqueue("stop");

    public bool SpeakNext()
    {
        Log.Enqueue("next");
        return true;
    }
    public void Clear() => Log.Enqueue("clear");
    public bool SetMode(string mode) => mode is "all" or "command";
    public bool ActivateVoiceGateProfile(string profile) => profile == "Mic";
    public bool ActivateIgnoreProfile(string profile) => true;
    public void SetNickname(string platform, string userName, string nickname) => Log.Enqueue($"nick:{userName}:{nickname}");
    public void SetUserVoice(string platform, string userId, string userName, string alias) => Log.Enqueue($"voice:{userName}:{alias}");
    public void SetRegular(string platform, string userId, string userName, bool regular) => Log.Enqueue($"reg:{userName}:{regular}");
    public bool StickyRandom { get; set; } = true;
    public ConcurrentDictionary<string, bool> EventStates { get; } = new(StringComparer.OrdinalIgnoreCase) { ["Follow"] = true, ["Merch"] = true };
    public string? AssignLastVoice(string platform, string userName)
    {
        Log.Enqueue($"assign:{userName}");
        return userName.Equals("ann", StringComparison.OrdinalIgnoreCase) ? "ann voice" : null;
    }

    public bool? IsEventEnabled(string name) => EventStates.TryGetValue(name, out var on) ? on : null;

    public bool SetEventEnabled(string name, bool enabled)
    {
        if (!EventStates.ContainsKey(name)) return false;
        EventStates[name] = enabled;
        return true;
    }

    public Task<IReadOnlyList<SpeakerVoice>> GetVoicesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SpeakerVoice>>([new SpeakerVoice("fake", "v1", "Voice One", "en-US")]);
}
