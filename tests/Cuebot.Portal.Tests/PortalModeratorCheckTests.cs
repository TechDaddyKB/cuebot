using Cuebot.Core.Chat.Moderation;
using Cuebot.Portal.Members;

namespace Cuebot.Portal.Tests;

/// <summary>"Must stay a Twitch moderator" pauses and resumes members; errors never remove access.</summary>
public sealed class PortalModeratorCheckTests
{
    private readonly PortalSettings _settings = new();
    private readonly PortalDirectory _dir;
    private readonly FakeRoles _roles = new();
    private readonly PortalModeratorCheck _check;

    public PortalModeratorCheckTests()
    {
        _dir = new PortalDirectory(_settings, () => { });
        _check = new PortalModeratorCheck(_dir, [_roles]);
    }

    private PortalMember Member(IdentityProvider provider = IdentityProvider.Twitch, bool require = true, string id = "111")
    {
        var member = new PortalMember
        {
            Name = "Sam", Provider = provider, ProviderUserId = id, RoleId = DefaultRoles.StandardModId, RequireTwitchModerator = require,
        };
        _settings.Members.Add(member);
        return member;
    }

    [Fact]
    public async Task A_member_who_is_no_longer_a_twitch_moderator_is_paused_and_resumed_when_modded_again()
    {
        var member = Member();
        _roles.Answer = false;

        var now = await _check.RefreshAsync(member);
        Assert.True(now!.Suspended);
        Assert.Equal(PortalModeratorCheck.Reason, now.SuspendedReason);

        _roles.Answer = true;
        await _check.CheckAllAsync();
        Assert.False(_dir.Member(member.Id)!.Suspended);
    }

    [Fact]
    public async Task An_unknown_answer_changes_nothing_and_a_pause_by_the_streamer_is_never_lifted()
    {
        var member = Member();
        _roles.Answer = null;
        Assert.False((await _check.RefreshAsync(member))!.Suspended);

        _dir.SetSuspended(member.Id, true, "Taking a break");
        _roles.Answer = true;
        var calls = _roles.Calls;
        var after = await _check.RefreshAsync(_dir.Member(member.Id)!);
        Assert.Equal((true, "Taking a break"), (after!.Suspended, after.SuspendedReason));
        Assert.Equal(calls, _roles.Calls);   // nothing to decide for a member the streamer paused
    }

    [Fact]
    public async Task Only_twitch_members_with_the_option_are_checked_and_switching_it_off_lifts_the_pause()
    {
        var google = Member(IdentityProvider.Google);
        var relaxed = Member(require: false, id: "222");
        _roles.Answer = false;
        await _check.CheckAllAsync();
        Assert.False(_dir.Member(google.Id)!.Suspended);
        Assert.False(_dir.Member(relaxed.Id)!.Suspended);

        var strict = Member(id: "333");
        await _check.CheckAllAsync();
        Assert.True(_dir.Member(strict.Id)!.Suspended);
        _dir.UpdateMember(strict.Id, requireTwitchModerator: false);
        await _check.CheckAllAsync();
        Assert.False(_dir.Member(strict.Id)!.Suspended);
    }

    [Fact]
    public void Twitch_members_are_protected_from_moderation_by_other_members()
    {
        Member(id: "444");
        var accounts = new PortalProtectedAccounts(_dir);
        Assert.Contains("member of your mod portal", accounts.WhyProtected("Twitch", "444", "sam"));
        Assert.Null(accounts.WhyProtected("Twitch", "555", "other"));
        Assert.Null(accounts.WhyProtected("Kick", "444", "sam"));
    }

    private sealed class FakeRoles : IChannelRoles
    {
        public bool? Answer { get; set; }
        public int Calls { get; private set; }
        public string Platform => "Twitch";
        public Task<bool> IsModeratorAsync(string userId, CancellationToken ct = default) => Task.FromResult(Answer == true);

        public Task<bool?> CheckModeratorAsync(string userId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Answer);
        }
    }
}
