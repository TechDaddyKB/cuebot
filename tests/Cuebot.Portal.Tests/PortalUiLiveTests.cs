using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Members;
using Cuebot.Portal.Relay;
using Cuebot.Portal.SignIn;

namespace Cuebot.Portal.Tests;

/// <summary>
/// A Cuebot with demo actions, commands, activity and queues behind the shared relay, for trying the portal UI in a
/// real browser. Runs only with CUEBOT_PORTAL_UI_LINK set to a file: it writes an invite link there and serves until
/// that file is deleted (at most 30 minutes). Sign-in is stood in for: the provider accepts the code "test", so after
/// opening the link a test sets the pending sign-in in sessionStorage and opens /auth/return?code=test&amp;state=...
/// CUEBOT_PORTAL_UI_ROLE picks the role (standard, lead or tech; default lead).
/// </summary>
public sealed class PortalUiLiveTests
{
    private static readonly string? LinkFile = Environment.GetEnvironmentVariable("CUEBOT_PORTAL_UI_LINK");

    private sealed class TestExchange : IPortalIdentityExchange
    {
        public JsonArray Describe() =>
        [
            new JsonObject
            {
                ["id"] = "google", ["name"] = "Google", ["pkce"] = true,
                ["authorize"] = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&client_id=test",
            },
        ];

        public Task<ProviderIdentity> ExchangeAsync(IdentityProvider provider, string code, string verifier, CancellationToken ct) =>
            code == "test" ? Task.FromResult(new ProviderIdentity(provider, "demo-mod", "sam@example.com", "SamTheMod"))
                : throw new PortalAccessException("Only the test code works here.");
    }

    private sealed class Step(string type, string name, string category, params ParameterDescriptor[] parameters) : ISubActionHandler
    {
        public string TypeId => type;
        public string Category => category;
        public string DisplayName => name;
        public IReadOnlyList<ParameterDescriptor> Parameters => parameters;
        public IReadOnlyList<string> Branches { get; init; } = [];
        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) => Task.FromResult(SubActionResult.Next);
    }

    private sealed class Trigger(string type, string name, string category, params ParameterDescriptor[] parameters) : ITriggerType
    {
        public string TypeId => type;
        public string Category => category;
        public string DisplayName => name;
        public IReadOnlyList<ParameterDescriptor> Parameters => parameters;
        public IReadOnlyCollection<string> EventTypes => [type];
        public bool Matches(BotEvent e, TriggerDefinition trigger, ParameterValues settings, IDictionary<string, object?> args) => false;
    }

    [Fact]
    public async Task Serves_a_demo_portal()
    {
        if (string.IsNullOrWhiteSpace(LinkFile)) return;
        using var log = new AppLog();
        var bus = new EventBus(log);
        var profile = new ProfileService(null, log);
        var commands = new CommandStore(null, log);
        Step[] steps =
        [
            new("twitch.sendMessage", "Send chat message", "Twitch", new ParameterDescriptor("message", "Message", ParameterKind.Multiline)),
            new("audio.playSound", "Play sound", "Audio", new ParameterDescriptor("file", "Sound file"),
                new ParameterDescriptor("volume", "Volume", ParameterKind.Number) { DefaultValue = "100" }),
            new("core.delay", "Wait", "Logic", new ParameterDescriptor("seconds", "Seconds", ParameterKind.Number) { DefaultValue = "1" }),
            new("core.if", "If / Else", "Logic", new ParameterDescriptor("left", "Value"),
                new ParameterDescriptor("op", "Is", ParameterKind.Choice) { Choices = ["equal to", "not equal to", "more than", "less than"] },
                new ParameterDescriptor("right", "Value")) { Branches = ["Then", "Else"] },
            new("obs.setScene", "Switch scene", "OBS", new ParameterDescriptor("scene", "Scene")),
            new("twitch.setTitle", "Set stream title", "Twitch", new ParameterDescriptor("title", "Title")),
            new("core.setGlobal", "Set global variable", "Variables", new ParameterDescriptor("name", "Name"), new ParameterDescriptor("value", "Value")),
            new("core.runProgram", "Run program", "System", new ParameterDescriptor("path", "Program")),
        ];
        var registry = new Registry(steps,
        [
            new Trigger("command.triggered", "Chat command", "Commands", new ParameterDescriptor("command", "Command")),
            new Trigger("twitch.follow", "Follow", "Twitch"),
            new Trigger("twitch.cheer", "Cheer", "Twitch", new ParameterDescriptor("min", "At least (bits)", ParameterKind.Number)),
        ]);
        var engine = new ActionEngine(bus, profile, registry, new GlobalVariables(null, log), log);
        var sound = Path.Combine(Path.GetTempPath(), "airhorn.wav");
        await File.WriteAllBytesAsync(sound, [0]);
        Seed(profile, commands, sound);

        var chat = new ChatHub(bus, log, [new TestFeedAdapter()], []);
        foreach (var (kind, user, title) in new[]
                 {
                     (FeedKind.Follow, "PixelPanda", "followed"), (FeedKind.Subscription, "NightOwl42", "subscribed (Tier 1)"),
                     (FeedKind.Raid, "CozyCoder", "raided with 37 viewers"), (FeedKind.Tip, "Mara", "tipped $5.00"),
                 })
            TestFeedAdapter.Publish(bus, kind, user, title);

        var directory = new PortalDirectory(new PortalSettings { Enabled = true, RelayUrl = Environment.GetEnvironmentVariable("CUEBOT_PORTAL_RELAY_URL") ?? "" }, () => { });
        var changes = new PortalChanges(new PortalChangeLog(), () => { }, directory, [new ActionArea(profile), new CommandArea(commands)]);
        var controls = new PortalControls(profile, chat,
            [new TestConnection("Twitch", ConnectionState.Connected, "Connected as TechDaddy"), new TestConnection("OBS", ConnectionState.Connected, "Connected"),
             new TestConnection("Ko-fi", ConnectionState.Error, "Webhook not set up")],
            obs: null, engine.Queues, speech: null, audio: null, log);
        var api = new PortalApi(directory, changes, profile, commands, engine, registry, controls);
        await using var client = new PortalRelayClient(directory, log) { LinkHandler = new PortalSignInHandler(directory, new TestExchange(), api.HandleAsync, log) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        await client.ConnectAsync(timeout.Token);

        var role = Environment.GetEnvironmentVariable("CUEBOT_PORTAL_UI_ROLE") switch
        {
            "standard" => DefaultRoles.StandardModId,
            "tech" => DefaultRoles.TechLeadId,
            _ => DefaultRoles.LeadModId,
        };
        await File.WriteAllTextAsync(LinkFile, directory.CreateInvite(role, "Live UI test").Link, timeout.Token);
        try
        {
            while (File.Exists(LinkFile)) await Task.Delay(500, timeout.Token);
        }
        catch (OperationCanceledException)
        {
        }
        File.Delete(sound);
    }

    private static void Seed(ProfileService profile, CommandStore commands, string sound)
    {
        SubActionDefinition S(string type, params (string Key, string Value)[] p)
        {
            var s = new SubActionDefinition { Type = type };
            foreach (var (k, v) in p) s.Parameters[k] = v;
            return s;
        }
        TriggerDefinition T(string type, params (string Key, string Value)[] p)
        {
            var t = new TriggerDefinition { Type = type };
            foreach (var (k, v) in p) t.Settings[k] = v;
            return t;
        }
        var hype = new ActionDefinition
        {
            Name = "Hype train", Group = "Alerts", Queue = "Alerts",
            Triggers = [T("twitch.cheer", ("min", "500"))],
            SubActions =
            [
                S("audio.playSound", ("file", sound), ("volume", "80")),
                S("twitch.sendMessage", ("message", "Thank you %user% for the bits!")),
                S("core.if", ("left", "%bits%"), ("op", "more than"), ("right", "1000")),
            ],
        };
        hype.SubActions[2].Branch("Then").Add(S("obs.setScene", ("scene", "Celebration")));
        profile.Profile.Actions.AddRange(
        [
            hype,
            new ActionDefinition
            {
                Name = "Discord link", Group = "Chat",
                Triggers = [T("command.triggered", ("command", "!discord"))],
                SubActions = [S("twitch.sendMessage", ("message", "Join us: discord.gg/example"))],
            },
            new ActionDefinition { Name = "Follow thanks", Group = "Alerts", Triggers = [T("twitch.follow")], SubActions = [S("twitch.sendMessage", ("message", "Welcome %user%!"))] },
            new ActionDefinition { Name = "Set title: Just chatting", Group = "Stream", SubActions = [S("twitch.setTitle", ("title", "Just chatting"))] },
            new ActionDefinition { Name = "Start recording script", Group = "Stream", SubActions = [S("core.runProgram", ("path", "obs-record.sh"))] },
        ]);
        profile.Profile.Queues.Add(new QueueDefinition { Name = "Alerts" });
        profile.NotifyChanged();
        commands.Save(new CommandDefinition { Name = "Discord", Commands = ["!discord"] });
        commands.Save(new CommandDefinition { Name = "Lurk", Commands = ["!lurk"], UserCooldownSeconds = 60 });
    }
}
