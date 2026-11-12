using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cuebot.Assistant;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Portal.Assistant;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;
using Cuebot.Server;
using Cuebot.Server.Api;
using Cuebot.Server.Mcp;

namespace Cuebot.Portal.Tests;

/// <summary>The Assistant in the mod portal: a mod's assistant can do exactly what the mod could do on the portal's pages.</summary>
public sealed class PortalAssistantTests : IAsyncDisposable
{
    private sealed class Step(string type) : ISubActionHandler
    {
        public string TypeId => type;
        public string Category => "Test";
        public string DisplayName => type;
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [new("text", "Text")];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct) =>
            Task.FromResult(SubActionResult.Next);
    }

    /// <summary>Answers every message with the text it got, so a test can see what the AI was sent.</summary>
    private sealed class EchoProvider : IAssistantProvider
    {
        public string Id => "openai";
        public string Name => "OpenAI";
        public AssistantProviderKind Kind => AssistantProviderKind.ApiKey;
        public List<string> Received { get; } = [];

        public Task<AssistantProviderStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(AssistantProviderStatus.Available("ok"));

        public IAssistantSession StartSession(AssistantContext context) => new Session(this);

        private sealed class Session(EchoProvider owner) : IAssistantSession
        {
            public async IAsyncEnumerable<AssistantUpdate> SendAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
            {
                lock (owner.Received) owner.Received.Add(text);
                await Task.Yield();
                yield return new AssistantText("ok: " + text);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private readonly AppLog _log = new();
    private readonly ProfileService _profile;
    private readonly CommandStore _commands;
    private readonly PortalDirectory _dir;
    private readonly PortalChanges _changes;
    private readonly ServerManager _servers;
    private readonly AssistantService _assistantService;
    private readonly ScopedAssistant _scoped;
    private readonly EchoProvider _provider = new();
    private readonly PortalAssistant _assistant;
    private readonly PortalApi _api;

    public PortalAssistantTests()
    {
        _profile = new ProfileService(null, _log);
        _commands = new CommandStore(null, _log);
        var registry = new Registry([new Step("chat.send"), new Step("core.runProgram"), new Step("core.setGlobal"), new Step("audio.playSound")], []);
        var bus = new EventBus(_log);
        var globals = new GlobalVariables(null, _log);
        var engine = new ActionEngine(bus, _profile, registry, globals, _log);
        _dir = new PortalDirectory(new PortalSettings(), () => { });
        _changes = new PortalChanges(new PortalChangeLog(), () => { }, _dir, [new ActionArea(_profile), new CommandArea(_commands)]);
        _servers = new ServerManager(null, new ServerServices { Engine = engine, Profile = _profile, Globals = globals, Bus = bus, Log = _log, Registry = registry });
        _assistantService = new AssistantService(null, [_provider], _servers, null, _log);
        _scoped = new ScopedAssistant(_assistantService);
        PortalApi? api = null;
        _assistant = new PortalAssistant(_dir, _scoped, (m, r) => api!.HandleAsync(m, r), changes: _changes, profile: _profile, commands: _commands, log: _log);
        _api = api = new PortalApi(_dir, _changes, _profile, _commands, engine, registry, assistant: _assistant);
    }

    public async ValueTask DisposeAsync()
    {
        await _scoped.DisposeAsync();
        await _assistantService.DisposeAsync();
        await _servers.DisposeAsync();
    }

    private PortalMember Member(Guid role)
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return _dir.RedeemInvite(_dir.CreateInvite(role).Secret, new ProviderIdentity(IdentityProvider.Google, Guid.NewGuid().ToString(), "m@x", "Sam"),
            PortalKeys.RawPublicKey(k.ExportParameters(false)), "phone").Member;
    }

    private Task<JsonObject> Call(PortalMember m, string op, JsonObject? body = null)
    {
        var request = body ?? new JsonObject();
        request["op"] = op;
        return _api.HandleAsync(m.Id, request);
    }

    private McpTool Tool(PortalMember m, string name) => _assistant.Tools(m.Id).Single(t => t.Name == name);

    private static JsonObject Action(string name, string type, Dictionary<string, string>? parameters = null) => new()
    {
        ["name"] = name,
        ["subActions"] = new JsonArray(new JsonObject
        {
            ["type"] = type,
            ["parameters"] = new JsonObject((parameters ?? new() { ["text"] = "hi" }).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
        }),
    };

    private static async Task Until(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200 && !await condition(); i++) await Task.Delay(25);
        Assert.True(await condition());
    }

    [Fact]
    public async Task The_assistants_tools_have_the_mods_rights_and_no_more()
    {
        var lead = Member(DefaultRoles.LeadModId);
        var save = Tool(lead, "save_action");

        // Level 4 steps are above a Lead Mod; code and programs are never allowed; files only from uploads.
        var tooHigh = await Assert.ThrowsAsync<ApiException>(() => save.Invoke(new JsonObject { ["action"] = Action("Set", "core.setGlobal") }, default));
        Assert.Contains("level 4", tooHigh.Message);
        var never = await Assert.ThrowsAsync<ApiException>(() => save.Invoke(new JsonObject { ["action"] = Action("Run", "core.runProgram") }, default));
        Assert.Contains("never", never.Message);
        var file = await Assert.ThrowsAsync<ApiException>(() => save.Invoke(new JsonObject
        {
            ["action"] = Action("Bonk", "audio.playSound", new() { ["file"] = "/etc/passwd" }),
        }, default));
        Assert.Contains("uploaded sound", file.Message);
        Assert.Empty(_profile.Snapshot.Actions);

        // Allowed: saved as the mod, through the change log, live for a Lead Mod.
        var saved = await save.Invoke(new JsonObject { ["action"] = Action("Hello", "chat.send") }, default);
        Assert.Equal("live", (string?)saved["status"]);
        var action = Assert.Single(_profile.Snapshot.Actions);
        Assert.Equal("Hello", action.Name);
        Assert.Equal(lead.Id, Assert.Single(_changes.All).MemberId);
        Assert.Equal(lead.Id, action.SubActions[0].EditedBy);

        // Deleting and running ask the mod first, by name.
        Assert.Equal("Delete the action \"Hello\"", Tool(lead, "delete_action").Confirmation!(new JsonObject { ["id"] = action.Id.ToString() }));
        Assert.Equal("Run the action \"Hello\" on stream now", Tool(lead, "run_action").Confirmation!(new JsonObject { ["id"] = action.Id.ToString() }));

        // The step list is the role's.
        var steps = (await Tool(lead, "list_step_types").Invoke([], default))["steps"]!.AsArray().Select(s => (string)s!["type"]!).ToList();
        Assert.Contains("chat.send", steps);
        Assert.DoesNotContain("core.setGlobal", steps);
        Assert.DoesNotContain("core.runProgram", steps);
        await Assert.ThrowsAsync<ApiException>(() => Tool(lead, "describe_step_type").Invoke(new JsonObject { ["type"] = "core.runProgram" }, default));

        // A Standard Mod cannot create actions, and their command changes wait for the streamer.
        var standard = Member(DefaultRoles.StandardModId);
        var refused = await Assert.ThrowsAsync<ApiException>(() => Tool(standard, "save_action").Invoke(new JsonObject { ["action"] = Action("Mine", "chat.send") }, default));
        Assert.Contains("cannot create actions", refused.Message);
        var command = await Tool(standard, "save_command").Invoke(new JsonObject
        {
            ["command"] = new JsonObject { ["name"] = "Hug", ["commands"] = new JsonArray("!hug") },
        }, default);
        Assert.Equal("pending", (string?)command["status"]);
        Assert.Empty(_commands.All());

        // A paused mod's assistant can do nothing.
        _dir.SetSuspended(lead.Id, true);
        await Assert.ThrowsAsync<ApiException>(() => Tool(lead, "list_actions").Invoke([], default));
    }

    [Fact]
    public async Task Mods_need_the_permission_and_get_a_daily_number_of_messages()
    {
        var standard = Member(DefaultRoles.StandardModId);
        Assert.Contains("does not include the assistant", (string?)(await Call(standard, "assistant.status"))["error"]);

        var lead = Member(DefaultRoles.LeadModId);
        _dir.AssistantMessagesPerDay = 2;
        var status = await Call(lead, "assistant.status");
        Assert.True((bool)status["ok"]!);
        Assert.Equal((2, 0), ((int)status["limit"]!, (int)status["used"]!));

        // Attachments that are not the mod's uploads are left out of what the AI is told.
        var sent = await Call(lead, "assistant.send", new JsonObject { ["text"] = "make a redeem", ["attachments"] = new JsonArray(Guid.NewGuid().ToString()) });
        Assert.True((bool)sent["ok"]!, (string?)sent["error"]);
        Assert.Equal(1, (int)sent["used"]!);
        await Until(async () => !(bool)(await Call(lead, "assistant.poll", new JsonObject { ["after"] = 0 }))["busy"]!);
        Assert.Equal(["make a redeem"], _provider.Received);

        var poll = await Call(lead, "assistant.poll", new JsonObject { ["after"] = 0 });
        Assert.Equal(["user:make a redeem", "assistant:ok: make a redeem"],
            poll["lines"]!.AsArray().Select(l => $"{(string)l!["kind"]!}:{(string)l["text"]!}"));
        var seq = (long)poll["seq"]!;
        Assert.Empty((await Call(lead, "assistant.poll", new JsonObject { ["after"] = seq }))["lines"]!.AsArray());

        Assert.True((bool)(await Call(lead, "assistant.send", new JsonObject { ["text"] = "thanks" }))["ok"]!);
        await Until(async () => !(bool)(await Call(lead, "assistant.poll"))["busy"]!);
        var limited = await Call(lead, "assistant.send", new JsonObject { ["text"] = "one more" });
        Assert.Contains("today's 2 messages", (string?)limited["error"]);

        // Each mod has their own conversation and count.
        var other = Member(DefaultRoles.LeadModId);
        Assert.Empty((await Call(other, "assistant.status"))["lines"]!.AsArray());

        // 0 turns it off for everyone.
        _dir.AssistantMessagesPerDay = 0;
        Assert.Contains("turned the assistant off", (string?)(await Call(other, "assistant.status"))["error"]);
    }
}
