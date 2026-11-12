using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Rewards;
using Cuebot.Portal.Assistant;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;
using Cuebot.Portal.Rewards;

namespace Cuebot.Portal.Tests;

/// <summary>Channel point rewards in the mod portal: the role's permission, the platforms' limits and rules, and the change log.</summary>
public sealed class PortalRewardsTests
{
    /// <summary>A platform in memory: "theirs" rewards were made outside Cuebot, as the platform reports.</summary>
    private sealed class FakeRewards(string platform, ChannelRewardRules rules) : IChannelRewards
    {
        private int _next;
        public List<ChannelReward> Items { get; } = [];
        public string Platform => platform;
        public string Name => platform == "twitch" ? "Twitch" : "Kick";
        public ChannelRewardRules Rules => rules;
        public bool Ready { get; set; } = true;
        public IReadOnlyList<ChannelReward> Cached => [.. Items];

        public Task<IReadOnlyList<ChannelReward>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ChannelReward>>([.. Items]);

        public Task<ChannelReward> CreateAsync(ChannelReward reward, CancellationToken ct = default)
        {
            if (Items.Count >= rules.MaxRewards) throw new InvalidOperationException("The platform's limit.");
            var created = reward with { Id = $"{platform}-{++_next}", Manageable = true };
            Items.Add(created);
            return Task.FromResult(created);
        }

        public Task<ChannelReward> UpdateAsync(ChannelReward reward, CancellationToken ct = default)
        {
            var i = Items.FindIndex(r => r.Id == reward.Id);
            if (Items[i].Manageable == false) throw new InvalidOperationException("403");
            Items[i] = reward with { Manageable = Items[i].Manageable };
            return Task.FromResult(Items[i]);
        }

        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            Items.RemoveAll(r => r.Id == id);
            return Task.CompletedTask;
        }
    }

    private readonly AppLog _log = new();
    private readonly PortalDirectory _dir = new(new PortalSettings(), () => { });
    private readonly FakeRewards _twitch = new("twitch", new ChannelRewardRules(50, 45, 200, true, true));
    private readonly FakeRewards _kick = new("kick", new ChannelRewardRules(50, 50, 200, false, false));
    private readonly RewardArea _area;
    private readonly PortalChanges _changes;
    private readonly PortalApi _api;
    private readonly PortalAssistant _assistant;

    public PortalRewardsTests()
    {
        var profile = new ProfileService(null, _log);
        var commands = new CommandStore(null, _log);
        var registry = new Registry([], []);
        var engine = new ActionEngine(new EventBus(_log), profile, registry, new GlobalVariables(null, _log), _log);
        _area = new RewardArea([_twitch, _kick]);
        _changes = new PortalChanges(new PortalChangeLog(), () => { }, _dir, [new ActionArea(profile), new CommandArea(commands), _area]);
        PortalApi? api = null;
        _assistant = new PortalAssistant(_dir, null, (m, r) => api!.HandleAsync(m, r), changes: _changes, rewards: _area);
        _api = api = new PortalApi(_dir, _changes, profile, commands, engine, registry, rewards: new PortalRewards(_area, _changes, _log));
    }

    private PortalMember Member(Guid role)
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return _dir.RedeemInvite(_dir.CreateInvite(role).Secret, new ProviderIdentity(IdentityProvider.Twitch, Guid.NewGuid().ToString(), "", "Sam"),
            PortalKeys.RawPublicKey(k.ExportParameters(false)), "phone").Member;
    }

    private Task<JsonObject> Call(PortalMember m, string op, JsonObject? body = null)
    {
        var request = body ?? new JsonObject();
        request["op"] = op;
        return _api.HandleAsync(m.Id, request);
    }

    private static JsonObject Reward(string title, int cost = 100, string id = "") => new()
    {
        ["id"] = id, ["title"] = title, ["description"] = "", ["cost"] = cost, ["color"] = "#9146ff", ["enabled"] = true,
    };

    [Fact]
    public async Task A_lead_mod_creates_changes_and_deletes_rewards_through_the_change_log()
    {
        var standard = Member(DefaultRoles.StandardModId);
        Assert.Contains("cannot manage channel point rewards", (string?)(await Call(standard, "rewards.list"))["error"]);

        var lead = Member(DefaultRoles.LeadModId);
        _twitch.Items.Add(new ChannelReward("theirs", "Hydrate", "", 500, "#00E5CB", true, false, false, false, 0, 0, 0, Manageable: false));
        var list = await Call(lead, "rewards.list");
        var twitch = list["platforms"]!.AsArray().Single(p => (string?)p!["platform"] == "twitch")!;
        Assert.Equal((1, 50), ((int)twitch["used"]!, (int)twitch["max"]!));
        Assert.False((bool)twitch["rewards"]![0]!["manageable"]!);

        // Create: live for a Lead Mod, in the change log under their name.
        var saved = await Call(lead, "rewards.save", new JsonObject { ["platform"] = "twitch", ["reward"] = Reward("Bonk") });
        Assert.Equal("live", (string?)saved["status"]);
        var bonk = Assert.Single(_twitch.Items, r => r.Title == "Bonk");
        Assert.Equal("#9146FF", bonk.Color);
        var created = Assert.Single(_changes.All);
        Assert.Equal((RewardArea.AreaName, "Bonk (Twitch)", lead.Id), (created.Area, created.TargetName, created.MemberId));

        // Change it, then undo the change.
        Assert.True((bool)(await Call(lead, "rewards.save", new JsonObject { ["platform"] = "twitch", ["reward"] = Reward("Bonk", 250, bonk.Id) }))["ok"]!);
        Assert.Equal(250, _twitch.Items.Single(r => r.Id == bonk.Id).Cost);
        Assert.Equal(ChangeResult.Applied, await _changes.UndoAsync(_changes.All[^1].Id, lead));
        Assert.Equal(100, _twitch.Items.Single(r => r.Id == bonk.Id).Cost);

        // Undoing the create deletes the reward.
        Assert.Equal(ChangeResult.Applied, await _changes.UndoAsync(created.Id, lead));
        Assert.DoesNotContain(_twitch.Items, r => r.Title == "Bonk");

        // Rewards made outside Cuebot cannot be changed or deleted.
        Assert.Contains("made outside Cuebot", (string?)(await Call(lead, "rewards.save", new JsonObject { ["platform"] = "twitch", ["reward"] = Reward("Hydrate", 1, "theirs") }))["error"]);
        Assert.Contains("made outside Cuebot", (string?)(await Call(lead, "rewards.delete", new JsonObject { ["platform"] = "twitch", ["id"] = "theirs" }))["error"]);
        Assert.Single(_twitch.Items);
    }

    [Fact]
    public async Task The_limits_of_each_platform_are_checked_and_approval_applies()
    {
        var lead = Member(DefaultRoles.LeadModId);

        // Kick: 50-character titles, no cooldown or per-stream limits.
        var tooLong = await Call(lead, "rewards.save", new JsonObject { ["platform"] = "kick", ["reward"] = Reward(new string('x', 51)) });
        Assert.Contains("up to 50 characters", (string?)tooLong["error"]);
        var cooldown = Reward("Hug");
        cooldown["cooldownSeconds"] = 60;
        Assert.Contains("no cooldown", (string?)(await Call(lead, "rewards.save", new JsonObject { ["platform"] = "kick", ["reward"] = cooldown }))["error"]);
        Assert.Contains("look like #9146FF", (string?)(await Call(lead, "rewards.save", new JsonObject
        {
            ["platform"] = "kick", ["reward"] = new JsonObject { ["title"] = "Hug", ["cost"] = 1, ["color"] = "red" },
        }))["error"]);

        // 50 rewards is the most a channel can have.
        for (var i = 0; i < 50; i++) await _kick.CreateAsync(new ChannelReward("", $"R{i}", "", 1, "", true, false, false, false, 0, 0, 0, null));
        var full = await Call(lead, "rewards.save", new JsonObject { ["platform"] = "kick", ["reward"] = Reward("One more") });
        Assert.Contains("all 50 are used", (string?)full["error"]);
        Assert.Equal(50, (int)(await Call(lead, "rewards.list"))["platforms"]!.AsArray().Single(p => (string?)p!["platform"] == "kick")!["used"]!);

        // A role whose changes need approval: nothing happens on Twitch until the streamer approves.
        var careful = Member(DefaultRoles.LeadModId);
        _dir.UpdateMember(careful.Id, changes: ChangeMode.NeedsApproval);
        var pending = await Call(careful, "rewards.save", new JsonObject { ["platform"] = "twitch", ["reward"] = Reward("Later") });
        Assert.Equal("pending", (string?)pending["status"]);
        Assert.Empty(_twitch.Items);
        Assert.Equal(ChangeResult.Applied, await _changes.ApproveAsync(Assert.Single(_changes.Pending).Id));
        Assert.Equal("Later", Assert.Single(_twitch.Items).Title);

        // Not signed in.
        _twitch.Ready = false;
        Assert.Contains("not signed in", (string?)(await Call(lead, "rewards.save", new JsonObject { ["platform"] = "twitch", ["reward"] = Reward("X") }))["error"]);
    }

    [Fact]
    public async Task The_assistant_manages_rewards_with_the_same_checks()
    {
        var lead = Member(DefaultRoles.LeadModId);
        var tools = _assistant.Tools(lead.Id);
        var save = tools.Single(t => t.Name == "save_reward");
        var saved = await save.Invoke(new JsonObject { ["platform"] = "kick", ["reward"] = Reward("Bonk") }, default);
        Assert.Equal("live", (string?)saved["status"]);
        var bonk = Assert.Single(_kick.Items);
        Assert.Equal("Delete the Kick reward \"Bonk\"", tools.Single(t => t.Name == "delete_reward").Confirmation!(new JsonObject { ["platform"] = "kick", ["id"] = bonk.Id }));
        var listed = await tools.Single(t => t.Name == "list_rewards").Invoke([], default);
        Assert.Equal(2, listed["platforms"]!.AsArray().Count);

        var standard = Member(DefaultRoles.StandardModId);
        await Assert.ThrowsAsync<Cuebot.Server.Api.ApiException>(() =>
            _assistant.Tools(standard.Id).Single(t => t.Name == "save_reward").Invoke(new JsonObject { ["platform"] = "kick", ["reward"] = Reward("Mine") }, default));
        Assert.Single(_kick.Items);
    }
}
