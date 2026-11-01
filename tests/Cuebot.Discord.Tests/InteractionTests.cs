using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Discord.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Discord.Tests;

/// <summary>Slash commands end to end: the real action engine, a fake Gateway and REST API.</summary>
public sealed class InteractionTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-discord-engine-").FullName;
    private readonly FakeGateway _gateway = new();
    private readonly FakeDiscordHttp _http = new();
    private readonly ServiceProvider _services;

    public InteractionTests()
    {
        var collection = new ServiceCollection().AddCuebotCore(new AppPaths(_dir)).AddDiscordBot();
        collection.AddSingleton(sp => new DiscordBotService(sp.GetRequiredService<JsonFile<DiscordBotSettings>>(), sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>(), new HttpClient(_http), _gateway.Create, TestEnvironment.Fast, engine: () => sp.GetService<ActionEngine>()));
        _services = collection.BuildServiceProvider();
        Bot.Settings.BotToken = TestToken.Value;
        Bot.Settings.Commands.Add(new DiscordSlashCommand { Name = "socials", Description = "My socials" });
        Bot.Settings.Commands.Add(new DiscordSlashCommand { Name = "secret", Description = "Psst", Private = true });
        _services.GetRequiredService<ActionEngine>().Start();
    }

    private DiscordBotService Bot => _services.GetRequiredService<DiscordBotService>();

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private async Task AddActionAsync(string command, params SubActionDefinition[] steps)
    {
        await _services.GetRequiredService<ProfileService>().EditAsync(p => p.Actions.Add(new ActionDefinition
        {
            Name = "slash " + command,
            Triggers = [new TriggerDefinition { Type = DiscordEventTypes.SlashCommand, Settings = new() { ["command"] = command } }],
            SubActions = [.. steps],
        }));
    }

    private async Task<FakeGatewaySocket> ConnectAsync()
    {
        await Bot.ConnectAsync();
        var socket = await _gateway.NextAsync();
        socket.Hello();
        await socket.NextSentAsync(Gateway.GatewayOp.Identify);
        socket.Ready();
        socket.GuildCreate();
        await Wait.UntilAsync(() => Bot.State == ConnectionState.Connected && Bot.Guilds.Count == 1, "ready");
        return socket;
    }

    private static object Interaction(string name, string id = "910000000000000001", string token = "interaction-token-1", string? option = null) => new
    {
        id, token, type = 2, application_id = "100000000000000001", guild_id = Ids.Guild, channel_id = Ids.General,
        member = new { nick = "", user = new { id = Ids.User, username = "viewer1", global_name = "Viewer One" } },
        data = new
        {
            id = "920000000000000001", name, type = 1,
            options = option is null ? Array.Empty<object>() : [new { name = "song", type = 3, value = option }],
        },
    };

    private FakeRequest[] Originals(string token) => _http.Matching(HttpMethod.Patch, $"webhooks/100000000000000001/{token}/messages/@original");

    [Fact]
    public async Task A_slash_command_is_deferred_at_once_and_the_reply_step_edits_the_answer()
    {
        await AddActionAsync("socials", new SubActionDefinition
        {
            Type = DiscordSubActions.ReplyToSlashCommand,
            Parameters = new() { ["text"] = "Hi %user%, you asked for %option% in #%channelName%" },
        });
        var socket = await ConnectAsync();
        socket.Dispatch("INTERACTION_CREATE", Interaction("socials", option: "twitch"));

        await Wait.UntilAsync(() => Originals("interaction-token-1").Length == 1, "reply");
        var defer = Assert.Single(_http.Matching(HttpMethod.Post, "interactions/910000000000000001/interaction-token-1/callback"));
        Assert.Equal(5, defer.Json!["type"]!.GetValue<int>());
        Assert.Null(defer.Json!["data"]);
        Assert.Equal("Hi Viewer One, you asked for twitch in #general", Originals("interaction-token-1")[0].Json!["content"]!.GetValue<string>());
        Assert.True(_http.Requests.ToList().IndexOf(defer) < _http.Requests.ToList().IndexOf(Originals("interaction-token-1")[0]));

        // Answered: no default reply follows.
        await Task.Delay(500);
        Assert.Single(Originals("interaction-token-1"));
    }

    [Fact]
    public async Task An_action_without_a_reply_step_gets_the_default_answer_after_it_finished()
    {
        await AddActionAsync("socials", new SubActionDefinition { Type = "core.delay", Parameters = new() { ["milliseconds"] = "400" } });
        Bot.Settings.DefaultReply = "All done";
        var socket = await ConnectAsync();
        socket.Dispatch("INTERACTION_CREATE", Interaction("socials"));
        await Wait.UntilAsync(() => Originals("interaction-token-1").Length == 1, "default reply");
        Assert.Equal("All done", Originals("interaction-token-1")[0].Json!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_command_no_action_handles_still_gets_the_default_answer()
    {
        var socket = await ConnectAsync();
        socket.Dispatch("INTERACTION_CREATE", Interaction("secret", token: "interaction-token-2"));
        await Wait.UntilAsync(() => Originals("interaction-token-2").Length == 1, "default reply");
        Assert.Equal("Done ✅", Originals("interaction-token-2")[0].Json!["content"]!.GetValue<string>());
        // A private command defers with the ephemeral flag.
        var defer = Assert.Single(_http.Matching(HttpMethod.Post, "interactions/"));
        Assert.Equal(64, defer.Json!["data"]!["flags"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_second_reply_is_sent_as_a_follow_up()
    {
        var reply = new SubActionDefinition { Type = DiscordSubActions.ReplyToSlashCommand, Parameters = new() { ["text"] = "one" } };
        var again = new SubActionDefinition { Type = DiscordSubActions.ReplyToSlashCommand, Parameters = new() { ["text"] = "two" } };
        await AddActionAsync("socials", reply, again);
        var socket = await ConnectAsync();
        socket.Dispatch("INTERACTION_CREATE", Interaction("socials"));
        await Wait.UntilAsync(() => _http.Matching(HttpMethod.Post, "webhooks/100000000000000001/interaction-token-1").Length == 1, "follow-up");
        Assert.Equal("two", _http.Matching(HttpMethod.Post, "webhooks/")[0].Json!["content"]!.GetValue<string>());
        Assert.Equal("one", Assert.Single(Originals("interaction-token-1")).Json!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_reply_step_outside_a_slash_command_fails_with_a_plain_message()
    {
        var engine = _services.GetRequiredService<ActionEngine>();
        var action = new ActionDefinition
        {
            Name = "no interaction",
            SubActions = [new SubActionDefinition { Type = DiscordSubActions.ReplyToSlashCommand, Parameters = new() { ["text"] = "hi" } }],
        };
        Assert.False(await engine.RunAsync(action));
        Assert.Contains(engine.History.Entries, e => e.Error?.Contains("Slash command used") == true);
    }

    [Fact]
    public async Task Send_then_edit_and_delete_by_the_remembered_name()
    {
        await ConnectAsync();
        var engine = _services.GetRequiredService<ActionEngine>();
        var post = new ActionDefinition
        {
            Name = "live",
            SubActions =
            [
                new SubActionDefinition
                {
                    Type = DiscordSubActions.SendMessage,
                    Parameters = new() { ["channel"] = "#announcements", ["text"] = "I'm live!", ["rememberAs"] = "live post" },
                },
            ],
        };
        Assert.True(await engine.RunAsync(post));
        var sent = Assert.Single(_http.Matching(HttpMethod.Post, $"channels/{Ids.Announcements}/messages"));
        Assert.Equal("I'm live!", sent.Json!["content"]!.GetValue<string>());
        Assert.Equal($"https://discord.com/channels/{Ids.Guild}/{Ids.Announcements}/600000000000000001",
            _services.GetRequiredService<GlobalVariables>().Get("live post"));

        var end = new ActionDefinition
        {
            Name = "ended",
            SubActions =
            [
                new SubActionDefinition
                {
                    Type = DiscordSubActions.EditMessage,
                    Parameters = new() { ["message"] = "live post", ["text"] = "Stream ended, thanks!", ["removeEmbed"] = "true" },
                },
                new SubActionDefinition { Type = DiscordSubActions.DeleteMessage, Parameters = new() { ["message"] = "live post" } },
            ],
        };
        Assert.True(await engine.RunAsync(end));
        var edit = Assert.Single(_http.Matching(HttpMethod.Patch, $"channels/{Ids.Announcements}/messages/600000000000000001"));
        Assert.Equal("Stream ended, thanks!", edit.Json!["content"]!.GetValue<string>());
        Assert.Empty(edit.Json!["embeds"]!.AsArray());
        Assert.Single(_http.Matching(HttpMethod.Delete, $"channels/{Ids.Announcements}/messages/600000000000000001"));
    }

    [Fact]
    public async Task Give_and_take_a_role_by_name_with_a_member_found_by_name()
    {
        _http.Handler = r => r.Path.StartsWith($"guilds/{Ids.Guild}/members/search", StringComparison.Ordinal)
            ? FakeDiscordHttp.Json(new[] { new { nick = (string?)null, user = new { id = Ids.User, username = "viewer1" } } })
            : null;
        await ConnectAsync();
        var engine = _services.GetRequiredService<ActionEngine>();
        var action = new ActionDefinition
        {
            Name = "roles",
            SubActions =
            [
                new SubActionDefinition { Type = DiscordSubActions.AddRole, Parameters = new() { ["member"] = "viewer1", ["role"] = "live notifications", ["reason"] = "Asked with /notify" } },
                new SubActionDefinition { Type = DiscordSubActions.RemoveRole, Parameters = new() { ["member"] = Ids.User, ["role"] = $"Live notifications · My Server · {Ids.LiveRole}" } },
            ],
        };
        Assert.True(await engine.RunAsync(action));
        var add = Assert.Single(_http.Matching(HttpMethod.Put, $"guilds/{Ids.Guild}/members/{Ids.User}/roles/{Ids.LiveRole}"));
        Assert.Equal(Uri.EscapeDataString("Asked with /notify"), add.Headers["X-Audit-Log-Reason"]);
        Assert.Single(_http.Matching(HttpMethod.Delete, $"guilds/{Ids.Guild}/members/{Ids.User}/roles/{Ids.LiveRole}"));
        Assert.Contains("query=viewer1", _http.Matching(HttpMethod.Get, $"guilds/{Ids.Guild}/members/search")[0].Path);
    }

    [Fact]
    public async Task A_role_refusal_explains_the_role_order()
    {
        _http.Handler = r => r.Method == HttpMethod.Put && r.Path.Contains("/roles/", StringComparison.Ordinal)
            ? FakeDiscordHttp.Json(new { message = "Missing Permissions", code = 50013 }, System.Net.HttpStatusCode.Forbidden)
            : null;
        await ConnectAsync();
        var engine = _services.GetRequiredService<ActionEngine>();
        var action = new ActionDefinition
        {
            Name = "role",
            SubActions = [new SubActionDefinition { Type = DiscordSubActions.AddRole, Parameters = new() { ["member"] = Ids.User, ["role"] = "Live notifications" } }],
        };
        Assert.False(await engine.RunAsync(action));
        var error = engine.History.Entries.Single(e => e.ActionName == "role").Error!;
        Assert.Contains("Manage Roles", error);
        Assert.Contains("above", error);
    }

    [Fact]
    public async Task Direct_messages_open_a_dm_channel_first()
    {
        await ConnectAsync();
        var engine = _services.GetRequiredService<ActionEngine>();
        var action = new ActionDefinition
        {
            Name = "dm",
            SubActions = [new SubActionDefinition { Type = DiscordSubActions.SendDirectMessage, Parameters = new() { ["member"] = $"<@{Ids.User}>", ["text"] = "Thanks!" } }],
        };
        Assert.True(await engine.RunAsync(action));
        Assert.Equal(Ids.User, Assert.Single(_http.Matching(HttpMethod.Post, "users/@me/channels")).Json!["recipient_id"]!.GetValue<string>());
        Assert.Single(_http.Matching(HttpMethod.Post, "channels/700000000000000001/messages"));
    }

    [Fact]
    public async Task Register_puts_the_command_list_into_every_server()
    {
        Bot.Settings.Commands[0].OptionName = "Platform";
        Bot.Settings.Commands[0].OptionDescription = "Which one";
        Bot.Settings.Commands[0].OptionRequired = true;
        await ConnectAsync();
        Assert.Equal(1, await Bot.RegisterCommandsAsync());
        var put = Assert.Single(_http.Matching(HttpMethod.Put, $"applications/100000000000000001/guilds/{Ids.Guild}/commands"));
        var commands = put.Json!.AsArray();
        Assert.Equal(2, commands.Count);
        Assert.Equal("socials", commands[0]!["name"]!.GetValue<string>());
        Assert.Equal("My socials", commands[0]!["description"]!.GetValue<string>());
        Assert.Equal(1, commands[0]!["type"]!.GetValue<int>());
        var option = commands[0]!["options"]![0]!;
        Assert.Equal(3, option["type"]!.GetValue<int>());
        Assert.Equal("platform", option["name"]!.GetValue<string>());
        Assert.True(option["required"]!.GetValue<bool>());
        Assert.Null(commands[1]!["options"]);
    }

    [Fact]
    public async Task Triggers_filter_by_command_channel_and_text()
    {
        var registry = _services.GetRequiredService<Registry>();
        var slash = registry.GetTrigger(DiscordEventTypes.SlashCommand)!;
        var e = new BotEvent(DiscordEventTypes.SlashCommand, new Dictionary<string, object?> { ["command"] = "socials" });
        bool Match(ITriggerType t, BotEvent ev, Dictionary<string, string> settings) =>
            t.Matches(ev, new TriggerDefinition { Type = t.TypeId, Settings = settings }, ParameterValues.From(t.Parameters, settings), new Dictionary<string, object?>());
        Assert.True(Match(slash, e, new() { ["command"] = "/socials" }));
        Assert.True(Match(slash, e, new() { ["command"] = "" }));
        Assert.False(Match(slash, e, new() { ["command"] = "notify" }));

        var posted = registry.GetTrigger(DiscordEventTypes.MessagePosted)!;
        var m = new BotEvent(DiscordEventTypes.MessagePosted, new Dictionary<string, object?>
        {
            ["channelId"] = Ids.General, ["channelName"] = "general", ["message"] = "please !CLIP that",
        });
        Assert.True(Match(posted, m, new() { ["channel"] = "#general", ["contains"] = "!clip" }));
        Assert.True(Match(posted, m, new() { ["channel"] = $"#general · My Server · {Ids.General}" }));
        Assert.False(Match(posted, m, new() { ["channel"] = "#announcements" }));
        Assert.False(Match(posted, m, new() { ["contains"] = "raid" }));
        await Task.CompletedTask;
    }
}
