using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Logging;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;

namespace Cuebot.Portal.Tests;

public sealed class PortalPolicyTests
{
    private static PortalRole Role(Guid id) => DefaultRoles.Create().Single(r => r.Id == id);
    private static readonly PortalRole Standard = Role(DefaultRoles.StandardModId);
    private static readonly PortalRole Lead = Role(DefaultRoles.LeadModId);
    private static readonly PortalRole Tech = Role(DefaultRoles.TechLeadId);

    private static SubActionDefinition Step(string type) => new() { Type = type, Parameters = { ["text"] = "hi" } };

    private static ActionDefinition Action(params SubActionDefinition[] steps) => new() { Name = "a", SubActions = [.. steps] };

    [Fact]
    public void Creating_needs_the_permission_and_steps_within_the_level()
    {
        Assert.NotEmpty(PortalPolicy.CheckAction(Standard, null, Action(Step("twitch.sendMessage"))));   // no CreateActions
        Assert.Empty(PortalPolicy.CheckAction(Lead, null, Action(Step("twitch.sendMessage"), Step("obs.setScene"), Step("twitch.ban"))));
        Assert.Contains("level 4", PortalPolicy.CheckAction(Lead, null, Action(Step("core.setGlobal"))).Single());
        Assert.Empty(PortalPolicy.CheckAction(Tech, null, Action(Step("core.setGlobal"), Step("core.httpRequest"))));
        Assert.Contains("never", PortalPolicy.CheckAction(Tech, null, Action(Step("core.runProgram"))).Single());
        Assert.Contains("never", PortalPolicy.CheckAction(Tech, null, Action(Step("someplugin.unknown"))).Single());
    }

    [Fact]
    public void Steps_above_the_level_inside_branches_count_too()
    {
        var ifStep = Step("core.if");
        ifStep.Branch("True").Add(Step("twitch.ban"));
        Assert.Empty(PortalPolicy.CheckAction(Tech, null, Action(ifStep)));
        var modRole = new PortalRole { MaxRisk = StepRisk.StreamControl, Permissions = [PortalPermission.CreateActions] };
        Assert.Contains("twitch.ban", PortalPolicy.CheckAction(modRole, null, Action(ifStep)).Single());
    }

    [Fact]
    public void Steps_above_the_level_may_stay_but_not_change_or_go()
    {
        var role = new PortalRole { MaxRisk = StepRisk.Basic, Permissions = [PortalPermission.EditActions] };
        var ban = Step("twitch.ban");
        var before = Action(Step("twitch.sendMessage"), ban);
        var after = Json(before);
        after.SubActions[0].Parameters["text"] = "changed reply";
        Assert.Empty(PortalPolicy.CheckAction(role, before, after));

        var changedBan = Json(before);
        changedBan.SubActions[1].Parameters["text"] = "other";
        Assert.Contains("add or change", PortalPolicy.CheckAction(role, before, changedBan).Single());

        var removed = Json(before);
        removed.SubActions.RemoveAt(1);
        Assert.Contains("remove", PortalPolicy.CheckAction(role, before, removed).Single());
    }

    [Fact]
    public void Actions_with_never_steps_cannot_be_changed_by_anyone()
    {
        var before = Action(Step("twitch.sendMessage"), Step("code.csharp"));
        var after = Json(before);
        after.SubActions[0].Parameters["text"] = "x";
        Assert.Contains("streamer's computer", PortalPolicy.CheckAction(Tech, before, after).Single());
        Assert.NotEmpty(PortalPolicy.CheckAction(Tech, before, null));
    }

    [Fact]
    public void Group_scope_and_risky_triggers()
    {
        var scoped = new PortalRole { MaxRisk = StepRisk.Basic, Permissions = [PortalPermission.CreateActions, PortalPermission.RunActions], Groups = ["Mods"] };
        Assert.NotEmpty(PortalPolicy.CheckAction(scoped, null, Grouped(Action(Step("chat.send")), "Other")));
        Assert.Empty(PortalPolicy.CheckAction(scoped, null, Grouped(Action(Step("chat.send")), "mods")));
        Assert.NotEmpty(PortalPolicy.CheckRun(scoped, Grouped(Action(), "Other")));
        Assert.Empty(PortalPolicy.CheckRun(scoped, Grouped(Action(), "Mods")));

        var watch = Action(Step("chat.send"));
        watch.Triggers.Add(new TriggerDefinition { Type = "core.fileTailLine" });
        Assert.Contains("never", PortalPolicy.CheckAction(Tech, null, watch).Single());
    }

    [Fact]
    public void Commands_follow_their_permissions()
    {
        var cmd = new CommandDefinition { Name = "!so", Group = "" };
        Assert.Empty(PortalPolicy.CheckCommand(Standard, null, cmd));
        Assert.Empty(PortalPolicy.CheckCommand(Standard, cmd, cmd));
        Assert.NotEmpty(PortalPolicy.CheckCommand(Standard, cmd, null));
        Assert.Empty(PortalPolicy.CheckCommand(Lead, cmd, null));
    }

    [Fact]
    public void Stamping_marks_only_new_and_changed_steps()
    {
        var keep = Step("chat.send");
        var before = Action(keep, Step("chat.send"));
        var after = Json(before);
        after.SubActions[1].Parameters["text"] = "new";
        after.SubActions.Add(Step("audio.playSound"));
        var member = Guid.NewGuid();
        PortalPolicy.StampEdits(before, after, member);
        Assert.Null(after.SubActions[0].EditedBy);
        Assert.Equal(member, after.SubActions[1].EditedBy);
        Assert.Equal(member, after.SubActions[2].EditedBy);
    }

    private static ActionDefinition Grouped(ActionDefinition a, string group)
    {
        a.Group = group;
        return a;
    }

    private static ActionDefinition Json(ActionDefinition a) => PortalChanges.FromNode<ActionDefinition>(PortalChanges.ToNode(a));
}

public sealed class PortalChangesTests
{
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly AppLog _log = new();
    private readonly ProfileService _profile;
    private readonly CommandStore _commands;
    private readonly PortalDirectory _dir;
    private readonly PortalChanges _changes;
    private readonly List<PortalChange> _notices = [];

    public PortalChangesTests()
    {
        _profile = new ProfileService(null, _log);
        _commands = new CommandStore(null, _log);
        _dir = new PortalDirectory(new PortalSettings(), () => { }, _time);
        _changes = new PortalChanges(new PortalChangeLog(), () => { }, _dir, [new ActionArea(_profile), new CommandArea(_commands)], _time);
        _changes.StreamerNotice += _notices.Add;
    }

    private PortalMember Member(Guid role)
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var invite = _dir.CreateInvite(role);
        return _dir.RedeemInvite(invite.Secret, new ProviderIdentity(IdentityProvider.Twitch, Guid.NewGuid().ToString(), "m", "Mod"),
            PortalKeys.RawPublicKey(k.ExportParameters(false)), "phone").Member;
    }

    private static JsonNode Command(Guid id, string name) => PortalChanges.ToNode(new CommandDefinition { Id = id, Name = name, Commands = [name] });

    [Fact]
    public async Task Live_members_apply_at_once_and_can_undo_their_own()
    {
        var tech = Member(DefaultRoles.TechLeadId);
        var id = Guid.NewGuid();
        var (result, change) = await _changes.SubmitAsync(tech, CommandArea.AreaName, id, Command(id, "!hi"));
        Assert.Equal(ChangeResult.Applied, result);
        Assert.Equal(ChangeKind.Create, change.Kind);
        Assert.Equal("!hi", _commands.Get(id)!.Name);
        Assert.Empty(_notices);

        Assert.Equal(ChangeResult.Applied, await _changes.UndoAsync(change.Id, tech));
        Assert.Null(_commands.Get(id));
        Assert.Equal(ChangeStatus.Undone, _changes.Find(change.Id)!.Status);
    }

    [Fact]
    public async Task Approval_members_wait_and_the_streamer_decides()
    {
        var mod = Member(DefaultRoles.StandardModId);
        var id = Guid.NewGuid();
        var (result, change) = await _changes.SubmitAsync(mod, CommandArea.AreaName, id, Command(id, "!socials"));
        Assert.Equal(ChangeResult.Pending, result);
        Assert.Null(_commands.Get(id));
        Assert.Single(_notices);
        Assert.Single(_changes.Pending);

        Assert.Equal(ChangeResult.Applied, await _changes.ApproveAsync(change.Id));
        Assert.Equal("!socials", _commands.Get(id)!.Name);
        Assert.Equal(ChangeResult.NotPending, await _changes.ApproveAsync(change.Id));

        var (_, second) = await _changes.SubmitAsync(mod, CommandArea.AreaName, id, Command(id, "!links"));
        Assert.Equal(ChangeResult.Rejected, await _changes.RejectAsync(second.Id));
        Assert.Equal("!socials", _commands.Get(id)!.Name);
    }

    [Fact]
    public async Task Approve_and_undo_refuse_when_the_thing_changed_since()
    {
        var mod = Member(DefaultRoles.StandardModId);
        var id = Guid.NewGuid();
        _commands.Save(new CommandDefinition { Id = id, Name = "!old", Commands = ["!old"] });
        var (_, change) = await _changes.SubmitAsync(mod, CommandArea.AreaName, id, Command(id, "!new"));
        _commands.Save(new CommandDefinition { Id = id, Name = "!streamer", Commands = ["!streamer"] });
        Assert.Equal(ChangeResult.Conflict, await _changes.ApproveAsync(change.Id));
        Assert.Equal(ChangeResult.Applied, await _changes.ApproveAsync(change.Id, force: true));
        Assert.Equal("!new", _commands.Get(id)!.Name);

        _commands.Save(new CommandDefinition { Id = id, Name = "!again", Commands = ["!again"] });
        Assert.Equal(ChangeResult.Conflict, await _changes.UndoAsync(change.Id));
    }

    [Fact]
    public async Task Undoing_someone_elses_change_needs_the_permission_and_notify_members_notify()
    {
        var tech = Member(DefaultRoles.TechLeadId);
        var standard = Member(DefaultRoles.StandardModId);
        var lead = Member(DefaultRoles.LeadModId);
        var id = Guid.NewGuid();
        var (_, change) = await _changes.SubmitAsync(tech, CommandArea.AreaName, id, Command(id, "!x"));
        Assert.Equal(ChangeResult.NotAllowed, await _changes.UndoAsync(change.Id, standard));
        Assert.Equal(ChangeResult.Applied, await _changes.UndoAsync(change.Id, lead));

        await _changes.SubmitAsync(lead, CommandArea.AreaName, id, Command(id, "!y"));
        Assert.Single(_notices);
        Assert.Equal("!y", _commands.Get(id)!.Name);
    }

    [Fact]
    public async Task Pending_changes_expire_after_seven_days()
    {
        var mod = Member(DefaultRoles.StandardModId);
        var id = Guid.NewGuid();
        var (_, change) = await _changes.SubmitAsync(mod, CommandArea.AreaName, id, Command(id, "!late"));
        _time.Now += PortalChanges.PendingLifetime + TimeSpan.FromMinutes(1);
        Assert.Empty(_changes.Pending);
        Assert.Equal(ChangeStatus.Expired, _changes.Find(change.Id)!.Status);
        Assert.Equal(ChangeResult.NotPending, await _changes.ApproveAsync(change.Id));
    }

    [Fact]
    public async Task Actions_are_replaced_and_deleted_through_the_profile()
    {
        var tech = Member(DefaultRoles.TechLeadId);
        var action = new ActionDefinition { Name = "Hello", SubActions = [new SubActionDefinition { Type = "chat.send" }] };
        await _changes.SubmitAsync(tech, ActionArea.AreaName, action.Id, PortalChanges.ToNode(action));
        Assert.Equal("Hello", _profile.Snapshot.Actions.Single().Name);
        action.Name = "Hello again";
        await _changes.SubmitAsync(tech, ActionArea.AreaName, action.Id, PortalChanges.ToNode(action));
        Assert.Equal("Hello again", _profile.Snapshot.Actions.Single().Name);
        await _changes.SubmitAsync(tech, ActionArea.AreaName, action.Id, null);
        Assert.Empty(_profile.Snapshot.Actions);
    }
}
