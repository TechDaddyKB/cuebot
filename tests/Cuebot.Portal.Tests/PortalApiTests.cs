using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;

namespace Cuebot.Portal.Tests;

public sealed class PortalApiTests : IDisposable
{
    private sealed class Step(string type, StepRisk risk) : ISubActionHandler
    {
        public string TypeId => type;
        public string Category => "Test";
        public string DisplayName => type;
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [new("text", "Text")];
        public StepRisk Risk => risk;
        public int Runs;

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            return Task.FromResult(SubActionResult.Next);
        }
    }

    private readonly AppLog _log = new();
    private readonly ProfileService _profile;
    private readonly CommandStore _commands;
    private readonly ActionEngine _engine;
    private readonly PortalDirectory _dir;
    private readonly PortalApi _api;
    private readonly Step _chat = new("chat.send", StepRisk.Basic);
    private readonly Step _program = new("core.runProgram", StepRisk.Never);

    public PortalApiTests()
    {
        _profile = new ProfileService(null, _log);
        _commands = new CommandStore(null, _log);
        var registry = new Registry([_chat, _program, new Step("core.setGlobal", StepRisk.TrustedTech)], []);
        _engine = new ActionEngine(new EventBus(_log), _profile, registry, new GlobalVariables(null, _log), _log);
        _dir = new PortalDirectory(new PortalSettings(), () => { });
        var changes = new PortalChanges(new PortalChangeLog(), () => { }, _dir, [new ActionArea(_profile), new CommandArea(_commands)]);
        _api = new PortalApi(_dir, changes, _profile, _commands, _engine, registry);
    }

    private PortalMember Member(Guid role)
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return _dir.RedeemInvite(_dir.CreateInvite(role).Secret, new ProviderIdentity(IdentityProvider.Google, Guid.NewGuid().ToString(), "m@x", "Mod"),
            PortalKeys.RawPublicKey(k.ExportParameters(false)), "phone").Member;
    }

    private ActionDefinition AddAction(string name, params string[] types)
    {
        var a = new ActionDefinition { Name = name, SubActions = [.. types.Select(t => new SubActionDefinition { Type = t })] };
        _profile.Profile.Actions.Add(a);
        _profile.NotifyChanged();
        return a;
    }

    private Task<JsonObject> Call(PortalMember m, string op, JsonObject? body = null)
    {
        var request = body ?? new JsonObject();
        request["op"] = op;
        return _api.HandleAsync(m.Id, request);
    }

    [Fact]
    public async Task Me_reports_the_role()
    {
        var m = Member(DefaultRoles.LeadModId);
        var me = await Call(m, "me");
        Assert.True((bool)me["ok"]!);
        Assert.Equal("Lead Mod", (string)me["role"]!);
        Assert.Equal(3, (int)me["maxRisk"]!);
        Assert.Equal("LiveAndNotify", (string)me["changes"]!);
    }

    [Fact]
    public async Task The_step_catalog_offers_only_the_roles_levels()
    {
        var types = (await Call(Member(DefaultRoles.TechLeadId), "steps.catalog"))["steps"]!.AsArray().Select(s => (string)s!["type"]!).ToList();
        Assert.Contains("core.setGlobal", types);
        Assert.DoesNotContain("core.runProgram", types);
        var standard = (await Call(Member(DefaultRoles.StandardModId), "steps.catalog"))["steps"]!.AsArray().Select(s => (string)s!["type"]!);
        Assert.Equal(["chat.send"], standard);
    }

    [Fact]
    public async Task Running_an_action_runs_it_as_set_up()
    {
        var a = AddAction("Hype", "chat.send", "core.runProgram");
        var list = (await Call(Member(DefaultRoles.StandardModId), "actions.list"))["actions"]!.AsArray();
        Assert.True((bool)list.Single()!["locked"]!);
        var result = await Call(Member(DefaultRoles.StandardModId), "actions.run", new JsonObject { ["id"] = a.Id.ToString() });
        Assert.True((bool)result["ok"]!);
        for (var i = 0; i < 100 && _program.Runs == 0; i++) await Task.Delay(20);
        Assert.Equal(1, _program.Runs);   // the streamer's own program step, with no input from the mod
    }

    [Fact]
    public async Task Saving_stamps_the_member_and_goes_live_for_trusted_roles()
    {
        var tech = Member(DefaultRoles.TechLeadId);
        var action = new ActionDefinition { Name = "New", SubActions = [new SubActionDefinition { Type = "chat.send" }] };
        var result = await Call(tech, "actions.save", new JsonObject { ["action"] = PortalChanges.ToNode(action) });
        Assert.Equal("live", (string)result["status"]!);
        Assert.Equal(tech.Id, _profile.Snapshot.Actions.Single().SubActions.Single().EditedBy);

        var standard = Member(DefaultRoles.StandardModId);
        var refused = await Call(standard, "actions.save", new JsonObject { ["action"] = PortalChanges.ToNode(new ActionDefinition { Name = "x" }) });
        Assert.False((bool)refused["ok"]!);
        Assert.Contains("cannot create actions", (string)refused["error"]!);
    }

    [Fact]
    public async Task Never_steps_and_locked_actions_are_refused()
    {
        var tech = Member(DefaultRoles.TechLeadId);
        var withProgram = new ActionDefinition { Name = "Bad", SubActions = [new SubActionDefinition { Type = "core.runProgram" }] };
        Assert.False((bool)(await Call(tech, "actions.save", new JsonObject { ["action"] = PortalChanges.ToNode(withProgram) }))["ok"]!);

        var locked = AddAction("Locked", "chat.send", "core.runProgram");
        var copy = PortalChanges.FromNode<ActionDefinition>(PortalChanges.ToNode(locked));
        copy.Name = "Renamed";
        var result = await Call(tech, "actions.save", new JsonObject { ["action"] = PortalChanges.ToNode(copy) });
        Assert.Contains("streamer's computer", (string)result["error"]!);
        Assert.False((bool)(await Call(tech, "actions.delete", new JsonObject { ["id"] = locked.Id.ToString() }))["ok"]!);
    }

    [Fact]
    public async Task Commands_that_start_code_steps_are_locked()
    {
        var command = new CommandDefinition { Name = "!restart", Commands = ["!restart"], MinimumRole = ChatPermission.Moderator };
        _commands.Save(command);
        var a = AddAction("Restart", "core.runProgram");
        _profile.Profile.Actions[0].Triggers.Add(new TriggerDefinition { Type = "command.triggered", Settings = { ["command"] = command.Id.ToString() } });
        _profile.NotifyChanged();

        var lead = Member(DefaultRoles.LeadModId);
        var open = PortalChanges.FromNode<CommandDefinition>(PortalChanges.ToNode(command));
        open.MinimumRole = ChatPermission.Everyone;
        var result = await Call(lead, "commands.save", new JsonObject { ["command"] = PortalChanges.ToNode(open) });
        Assert.Contains("code, programs or files", (string)result["error"]!);
        Assert.Equal(ChatPermission.Moderator, _commands.Get(command.Id)!.MinimumRole);
        Assert.NotEqual(Guid.Empty, a.Id);
    }

    [Fact]
    public async Task Standard_mods_commands_wait_for_approval_and_suspension_stops_everything()
    {
        var m = Member(DefaultRoles.StandardModId);
        var command = new CommandDefinition { Name = "!socials", Commands = ["!socials"] };
        var result = await Call(m, "commands.save", new JsonObject { ["command"] = PortalChanges.ToNode(command) });
        Assert.Equal("pending", (string)result["status"]!);
        Assert.Null(_commands.Get(command.Id));

        _dir.SetSuspended(m.Id, true);
        Assert.False((bool)(await Call(m, "me"))["ok"]!);
        Assert.False((bool)(await _api.HandleAsync(Guid.NewGuid(), new JsonObject { ["op"] = "me" }))["ok"]!);
    }

    public void Dispose() => _engine.Dispose();
}
