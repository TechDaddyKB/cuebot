using System.Security.Cryptography;
using Cuebot.Core.Actions;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;

namespace Cuebot.Portal.Tests;

public sealed class PortalDirectoryTests
{
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly PortalSettings _settings = new();
    private int _saves;
    private readonly PortalDirectory _dir;

    public PortalDirectoryTests() => _dir = new PortalDirectory(_settings, () => _saves++, _time);

    private static byte[] DeviceKey()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return PortalKeys.RawPublicKey(k.ExportParameters(false));
    }

    private static ProviderIdentity Google(string sub = "1001", string email = "mod@example.com") =>
        new(IdentityProvider.Google, sub, email, "Mod One");

    [Fact]
    public void Starts_with_the_three_default_roles()
    {
        Assert.Equal(["Standard Mod", "Lead Mod", "Tech Lead"], _dir.Roles.Select(r => r.Name));
        Assert.All(_dir.Roles, r => Assert.True(r.MaxRisk < StepRisk.Never));
        Assert.Equal(ChangeMode.NeedsApproval, _dir.Role(DefaultRoles.StandardModId)!.Changes);
    }

    [Fact]
    public void Identity_key_is_made_once_and_reloaded()
    {
        var id = _dir.RelayId;
        Assert.NotEmpty(_settings.IdentityKey);
        var again = new PortalDirectory(_settings, () => { }, _time);
        Assert.Equal(id, again.RelayId);
    }

    [Fact]
    public void Invite_link_keeps_its_secrets_after_the_hash()
    {
        var created = _dir.CreateInvite(DefaultRoles.LeadModId, "for Sam");
        Assert.StartsWith(PortalDirectory.PortalOrigin + "/#i=", created.Link);
        Assert.Contains("&r=" + _dir.RelayId, created.Link);
        Assert.Contains("&k=" + _dir.Identity.Fingerprint, created.Link);
        Assert.DoesNotContain(created.Secret, _settings.Invites.Single().SecretHash);
        Assert.Equal(_time.Now + PortalDirectory.DefaultInviteLifetime, created.Invite.ExpiresAt);
    }

    [Fact]
    public void Redeeming_applies_the_invite_role_and_uses_the_invite_up()
    {
        var created = _dir.CreateInvite(DefaultRoles.LeadModId);
        var (member, device) = _dir.RedeemInvite(created.Secret, Google(), DeviceKey(), "Sam's phone");
        Assert.Equal(DefaultRoles.LeadModId, member.RoleId);
        Assert.Equal("1001", member.ProviderUserId);
        Assert.Equal("Sam's phone", device.Name);
        Assert.Empty(_dir.Invites);
        var again = Assert.Throws<PortalAccessException>(() => _dir.RedeemInvite(created.Secret, Google("2002"), DeviceKey(), "x"));
        Assert.Contains("already used", again.Message);
    }

    [Fact]
    public void Expired_invites_are_refused_and_removed()
    {
        var created = _dir.CreateInvite(DefaultRoles.StandardModId, lifetime: TimeSpan.FromHours(1));
        _time.Now += TimeSpan.FromHours(2);
        Assert.Contains("expired", Assert.Throws<PortalAccessException>(() => _dir.RedeemInvite(created.Secret, Google(), DeviceKey(), "x")).Message);
        Assert.Empty(_settings.Invites);
        Assert.Empty(_dir.Members);
    }

    [Fact]
    public void Locked_invites_need_the_named_account()
    {
        var inviteLock = new InviteLock { Provider = IdentityProvider.Google, Value = "Mod@Example.com" };
        var created = _dir.CreateInvite(DefaultRoles.StandardModId, inviteLock: inviteLock);
        Assert.Throws<PortalAccessException>(() => _dir.RedeemInvite(created.Secret, Google(email: "other@example.com"), DeviceKey(), "x"));

        var second = _dir.CreateInvite(DefaultRoles.StandardModId, inviteLock: inviteLock);
        var (member, _) = _dir.RedeemInvite(second.Secret, Google(email: "mod@example.com"), DeviceKey(), "x");
        Assert.Equal(DefaultRoles.StandardModId, member.RoleId);
    }

    [Fact]
    public void An_existing_member_cannot_use_another_invite()
    {
        _dir.RedeemInvite(_dir.CreateInvite(DefaultRoles.StandardModId).Secret, Google(), DeviceKey(), "x");
        var second = _dir.CreateInvite(DefaultRoles.TechLeadId);
        Assert.Throws<PortalAccessException>(() => _dir.RedeemInvite(second.Secret, Google(), DeviceKey(), "y"));
        Assert.Equal(DefaultRoles.StandardModId, _dir.Members.Single().RoleId);
    }

    [Fact]
    public void Sign_in_matches_the_provider_user_ID_not_the_name()
    {
        _dir.RedeemInvite(_dir.CreateInvite(DefaultRoles.StandardModId).Secret, Google(), DeviceKey(), "phone");
        var renamed = new ProviderIdentity(IdentityProvider.Google, "1001", "new@example.com", "New Name");
        var (member, device) = _dir.SignIn(renamed, DeviceKey(), "laptop");
        Assert.Equal(2, member.Devices.Count);
        Assert.Equal("laptop", device.Name);
        Assert.Throws<PortalAccessException>(() => _dir.SignIn(Google("9999"), DeviceKey(), "x"));
        Assert.Throws<PortalAccessException>(() => _dir.SignIn(new ProviderIdentity(IdentityProvider.Twitch, "1001", "m", "m"), DeviceKey(), "x"));
    }

    [Fact]
    public void Resume_needs_a_known_device_an_active_member_and_a_recent_provider_sign_in()
    {
        var key = DeviceKey();
        var (member, _) = _dir.RedeemInvite(_dir.CreateInvite(DefaultRoles.StandardModId).Secret, Google(), key, "phone");
        Assert.NotNull(_dir.Resume(key));
        Assert.Null(_dir.Resume(DeviceKey()));

        _dir.SetSuspended(member.Id, true, "break");
        Assert.Null(_dir.Resume(key));
        Assert.Throws<PortalAccessException>(() => _dir.SignIn(Google(), key, "phone"));
        _dir.SetSuspended(member.Id, false);
        Assert.NotNull(_dir.Resume(key));

        _time.Now += PortalDirectory.ProviderSignInLifetime + TimeSpan.FromMinutes(1);
        Assert.Null(_dir.Resume(key));
        _dir.SignIn(Google(), key, "phone");
        Assert.NotNull(_dir.Resume(key));
    }

    [Fact]
    public void Devices_are_capped_and_sign_out_everyone_clears_them()
    {
        _dir.RedeemInvite(_dir.CreateInvite(DefaultRoles.StandardModId).Secret, Google(), DeviceKey(), "first");
        for (var i = 0; i < PortalDirectory.MaxDevicesPerMember + 2; i++)
        {
            _time.Now += TimeSpan.FromMinutes(1);
            _dir.SignIn(Google(), DeviceKey(), "d" + i);
        }
        Assert.Equal(PortalDirectory.MaxDevicesPerMember, _dir.Members.Single().Devices.Count);
        Assert.DoesNotContain(_dir.Members.Single().Devices, d => d.Name == "first");
        _dir.SignOutEveryone();
        Assert.Empty(_dir.Members.Single().Devices);
    }

    [Fact]
    public void Member_changes_and_role_rules()
    {
        var (member, _) = _dir.RedeemInvite(_dir.CreateInvite(DefaultRoles.StandardModId).Secret, Google(), DeviceKey(), "x");
        Assert.Equal(ChangeMode.NeedsApproval, _dir.ChangeModeOf(member));
        _dir.UpdateMember(member.Id, roleId: DefaultRoles.TechLeadId, changes: ChangeMode.LiveAndNotify);
        Assert.Equal(ChangeMode.LiveAndNotify, _dir.ChangeModeOf(member));
        _dir.UpdateMember(member.Id, clearChanges: true);
        Assert.Equal(ChangeMode.Live, _dir.ChangeModeOf(member));

        Assert.False(_dir.DeleteRole(DefaultRoles.TechLeadId));
        Assert.True(_dir.DeleteRole(DefaultRoles.LeadModId));
        var tooMuch = _dir.SaveRole(new PortalRole { Name = "  Everything ", MaxRisk = StepRisk.Never });
        Assert.Equal(StepRisk.TrustedTech, tooMuch.MaxRisk);
        Assert.Equal("Everything", tooMuch.Name);
        Assert.True(_dir.RemoveMember(member.Id));
        Assert.True(_saves > 0);
    }

    [Fact]
    public void Bad_device_keys_and_invite_lifetimes_are_refused()
    {
        var created = _dir.CreateInvite(DefaultRoles.StandardModId);
        Assert.Throws<PortalAccessException>(() => _dir.RedeemInvite(created.Secret, Google(), new byte[65], "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => _dir.CreateInvite(DefaultRoles.StandardModId, lifetime: TimeSpan.FromDays(31)));
        Assert.Throws<ArgumentException>(() => _dir.CreateInvite(Guid.NewGuid()));
    }
}
