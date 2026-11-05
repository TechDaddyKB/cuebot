using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels;
using Cuebot.App.Views;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Logging;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests;

public sealed class ModPortalPageTests
{
    private readonly AppLog _log = new();
    private readonly CommandStore _commands;
    private readonly PortalDirectory _directory = new(new PortalSettings(), () => { });
    private readonly PortalChanges _changes;

    public ModPortalPageTests()
    {
        _commands = new CommandStore(null, _log);
        _changes = new PortalChanges(new PortalChangeLog(), () => { }, _directory,
            [new ActionArea(new ProfileService(null, _log)), new CommandArea(_commands)]);
    }

    private PortalMember Join(Guid role, string name)
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return _directory.RedeemInvite(_directory.CreateInvite(role).Secret, new ProviderIdentity(IdentityProvider.Twitch, name, name, name),
            PortalKeys.RawPublicKey(k.ExportParameters(false)), "phone").Member;
    }

    private static Window Show(ModPortalViewModel vm)
    {
        var window = new Window
        {
            Content = new ModPortalView { DataContext = vm },
            Width = 1100,
            Height = 3000,
            Background = Application.Current!.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
        };
        window.Show();
        UiPump.Pump();
        return window;
    }

    private static IEnumerable<string> Texts(Window w) =>
        w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0);

    [AvaloniaFact]
    public void Making_an_invite_shows_the_link_once_and_lists_it()
    {
        var vm = new ModPortalViewModel(_directory, _changes);
        var window = Show(vm);
        Assert.Equal("Standard Mod", vm.InviteRole?.Name);
        vm.InviteLabel = "For Sam";
        vm.CreateInviteCommand.Execute(null);
        UiPump.Pump();
        Assert.StartsWith(PortalDirectory.PortalOrigin + "/#i=", vm.NewInviteLink);
        Assert.Contains(Texts(window), t => t.StartsWith("For Sam · Standard Mod"));
        vm.HideInviteCommand.Execute(null);
        Assert.False(vm.HasNewInvite);
        vm.Invites.Single().WithdrawCommand.Execute(null);
        UiPump.Pump();
        Assert.Empty(vm.Invites);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Members_change_role_and_pending_changes_can_be_approved_then_undone()
    {
        var mod = Join(DefaultRoles.StandardModId, "sam");
        var vm = new ModPortalViewModel(_directory, _changes);
        var window = Show(vm);
        var member = vm.Members.Single();
        Assert.Equal("Standard Mod", member.Role?.Name);
        member.Role = vm.Roles.Single(r => r.Id == DefaultRoles.LeadModId);
        Assert.Equal(DefaultRoles.LeadModId, _directory.Member(mod.Id)!.RoleId);
        member.Changes = "Needs approval";
        Assert.Equal(ChangeMode.NeedsApproval, _directory.ChangeModeOf(_directory.Member(mod.Id)!));

        var id = Guid.NewGuid();
        await _changes.SubmitAsync(_directory.Member(mod.Id)!, CommandArea.AreaName, id,
            PortalChanges.ToNode(new CommandDefinition { Id = id, Name = "!socials", Commands = ["!socials"] }));
        UiPump.Pump();
        var pending = vm.Pending.Single();
        Assert.Contains("sam created the command '!socials'", pending.Text);
        await pending.ApproveCommand.ExecuteAsync(null);
        UiPump.Pump();
        Assert.Empty(vm.Pending);
        Assert.NotNull(_commands.Get(id));
        var recent = vm.Recent.Single();
        Assert.True(recent.CanUndo);
        await recent.UndoCommand.ExecuteAsync(null);
        UiPump.Pump();
        Assert.Null(_commands.Get(id));
        Assert.StartsWith("Undone", vm.Recent.Single().Status);
        window.Close();
    }

    [AvaloniaFact]
    public void Role_edits_save_and_a_role_in_use_cannot_be_deleted()
    {
        Join(DefaultRoles.TechLeadId, "tech");
        var vm = new ModPortalViewModel(_directory, _changes);
        var window = Show(vm);
        var standard = vm.Roles.Single(r => r.Id == DefaultRoles.StandardModId);
        standard.Level = ModPortalViewModel.Levels[1];
        standard.Permissions.Single(p => p.Permission == PortalPermission.UploadMedia).IsOn = true;
        var saved = _directory.Role(DefaultRoles.StandardModId)!;
        Assert.Equal(StepRisk.StreamControl, saved.MaxRisk);
        Assert.Contains(PortalPermission.UploadMedia, saved.Permissions);

        vm.Roles.Single(r => r.Id == DefaultRoles.TechLeadId).DeleteCommand.Execute(null);
        Assert.Contains("still given", vm.Message);
        Assert.NotNull(_directory.Role(DefaultRoles.TechLeadId));
        window.Close();
    }
}
