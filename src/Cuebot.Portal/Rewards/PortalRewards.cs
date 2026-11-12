using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cuebot.Core.Logging;
using Cuebot.Core.Rewards;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Members;

namespace Cuebot.Portal.Rewards;

/// <summary>
/// Channel point rewards on Twitch and Kick, as a change-log area: a mod's change goes live, waits for the streamer, is
/// listed and can be undone like any other. The log keys things by Guid; a reward's key is made from its platform and
/// id (<see cref="KeyOf"/>), and a new reward's key is remembered once the platform gives it an id (until Cuebot closes).
/// </summary>
public sealed class RewardArea(IEnumerable<IChannelRewards> platforms) : IPortalArea
{
    public const string AreaName = "reward";

    private readonly Dictionary<string, IChannelRewards> _platforms = platforms.ToDictionary(p => p.Platform, StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, (string Platform, string Id)> _keys = new();

    public string Name => AreaName;

    public IChannelRewards? Platform(string name) => _platforms.GetValueOrDefault(name);

    public IEnumerable<IChannelRewards> Platforms => _platforms.Values;

    /// <summary>The change-log key of a platform's reward.</summary>
    public Guid KeyOf(string platform, string id)
    {
        var key = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(platform.ToLowerInvariant() + ":" + id)));
        _keys[key] = (platform.ToLowerInvariant(), id);
        return key;
    }

    public JsonNode? Current(Guid id) =>
        _keys.TryGetValue(id, out var k) && Platform(k.Platform)?.Cached.FirstOrDefault(r => r.Id == k.Id) is { } reward ? Node(k.Platform, reward) : null;

    public string NameOf(JsonNode value) =>
        $"{(string?)value["title"] ?? ""} ({Platform((string?)value["platform"] ?? "")?.Name ?? (string?)value["platform"]})";

    public async Task ApplyAsync(Guid id, JsonNode? value)
    {
        if (value is null)
        {
            if (!_keys.TryGetValue(id, out var gone) || Platform(gone.Platform) is not { } from) return;
            await from.DeleteAsync(gone.Id).ConfigureAwait(false);
            return;
        }
        var platformName = (string?)value["platform"] ?? "";
        var platform = Platform(platformName) ?? throw new InvalidOperationException($"Unknown platform '{platformName}'.");
        var reward = PortalChanges.FromNode<ChannelReward>(value);
        if (_keys.TryGetValue(id, out var known) && platform.Cached.Any(r => r.Id == known.Id))
        {
            await platform.UpdateAsync(reward with { Id = known.Id }).ConfigureAwait(false);
            return;
        }
        var created = await platform.CreateAsync(reward with { Id = "" }).ConfigureAwait(false);
        _keys[id] = (platform.Platform, created.Id);
        KeyOf(platform.Platform, created.Id);
    }

    /// <summary>
    /// A reward as the change log keeps it: the settings a mod can change, without the platform's id or whether Cuebot may
    /// change it, so approve and undo compare only what matters.
    /// </summary>
    public static JsonObject Node(string platform, ChannelReward reward)
    {
        var node = (JsonObject)PortalChanges.ToNode(reward with { Color = reward.Color.ToUpperInvariant() });
        node.Remove("id");
        node.Remove("manageable");
        node["platform"] = platform.ToLowerInvariant();
        return node;
    }

    /// <summary>A reward as the portal lists it: with its id, and whether Cuebot may change it (null: the platform does not say).</summary>
    public static JsonObject Listed(string platform, ChannelReward reward)
    {
        var node = Node(platform, reward);
        node["id"] = reward.Id;
        node["manageable"] = reward.Manageable;
        return node;
    }
}

/// <summary>
/// The portal's Rewards page (<c>rewards.list</c>, <c>rewards.save</c>, <c>rewards.delete</c>): Twitch and Kick channel
/// point rewards for members with <see cref="PortalPermission.ManageRewards"/>. Both platforms allow 50 rewards per
/// channel and let only the app that made a reward change it; both rules are checked here and by the platform.
/// </summary>
public sealed partial class PortalRewards(RewardArea area, PortalChanges changes, AppLog? log = null)
{
    private const string LogSource = "Mod portal";

    public static bool Handles(string op) => op.StartsWith("rewards.", StringComparison.Ordinal);

    public async Task<JsonObject> HandleAsync(PortalMember member, PortalRole? role, string op, JsonObject request)
    {
        if (!PortalPolicy.Has(role, PortalPermission.ManageRewards)) return Error("Your role cannot manage channel point rewards.");
        try
        {
            return op switch
            {
                "rewards.list" => await ListAsync().ConfigureAwait(false),
                "rewards.save" => await SaveAsync(member, request).ConfigureAwait(false),
                "rewards.delete" => await DeleteAsync(member, request).ConfigureAwait(false),
                _ => Error("Unknown request."),
            };
        }
        catch (Exception e) when (e is not (OperationCanceledException or ArgumentException or FormatException or System.Text.Json.JsonException))
        {
            // The platform's own refusal (not signed in, a reward made elsewhere, the 50 limit...).
            return Error(e.Message);
        }
    }

    private async Task<JsonObject> ListAsync()
    {
        var list = new JsonArray();
        foreach (var platform in area.Platforms.OrderBy(p => p.Name))
        {
            var entry = new JsonObject
            {
                ["platform"] = platform.Platform,
                ["name"] = platform.Name,
                ["ready"] = platform.Ready,
                ["max"] = platform.Rules.MaxRewards,
                ["maxTitle"] = platform.Rules.MaxTitleLength,
                ["maxDescription"] = platform.Rules.MaxDescriptionLength,
                ["streamLimits"] = platform.Rules.HasStreamLimits,
                ["cooldown"] = platform.Rules.HasCooldown,
            };
            if (platform.Ready)
            {
                try
                {
                    var rewards = await platform.ListAsync().ConfigureAwait(false);
                    entry["used"] = rewards.Count;
                    entry["rewards"] = new JsonArray([.. rewards.OrderBy(r => r.Cost).Select(r => (JsonNode)RewardArea.Listed(platform.Platform, r))]);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    entry["error"] = e.Message;
                }
            }
            list.Add(entry);
        }
        return Ok(new JsonObject { ["platforms"] = list });
    }

    private async Task<JsonObject> SaveAsync(PortalMember member, JsonObject request)
    {
        if (Find(request) is not { } platform) return Error("Pick Twitch or Kick.");
        if (!platform.Ready) return Error($"The streamer is not signed in to {platform.Name}.");
        if (request["reward"] is not JsonObject node) return Error("No reward given.");
        var reward = Read(node);
        if (Check(platform, reward) is { Count: > 0 } problems) return Refused(problems);

        await platform.ListAsync().ConfigureAwait(false);   // a fresh count and copy to compare with
        Guid key;
        if (reward.Id.Length == 0)
        {
            if (platform.Cached.Count >= platform.Rules.MaxRewards)
                return Error($"{platform.Name} allows {platform.Rules.MaxRewards} rewards per channel, and all {platform.Rules.MaxRewards} are used. Delete one first.");
            key = Guid.NewGuid();
            reward = reward with { Paused = false };   // both platforms create rewards unpaused
        }
        else
        {
            if (platform.Cached.FirstOrDefault(r => r.Id == reward.Id) is not { } before) return Error("That reward is gone. Reload the page.");
            if (before.Manageable == false) return Error(NotOurs(platform));
            key = area.KeyOf(platform.Platform, reward.Id);
        }
        var (result, change) = await changes.SubmitAsync(member, RewardArea.AreaName, key, RewardArea.Node(platform.Platform, reward)).ConfigureAwait(false);
        log?.Info(LogSource, $"{member.Name} {(reward.Id.Length == 0 ? "created" : "changed")} the {platform.Name} reward \"{reward.Title}\"{(result == ChangeResult.Pending ? " (waiting for approval)" : "")}.");
        return Ok(new JsonObject { ["change"] = change.Id.ToString(), ["status"] = result == ChangeResult.Pending ? "pending" : "live" });
    }

    private async Task<JsonObject> DeleteAsync(PortalMember member, JsonObject request)
    {
        if (Find(request) is not { } platform) return Error("Pick Twitch or Kick.");
        var id = (string?)request["id"] ?? "";
        await platform.ListAsync().ConfigureAwait(false);
        if (platform.Cached.FirstOrDefault(r => r.Id == id) is not { } before) return Error("That reward is gone. Reload the page.");
        if (before.Manageable == false) return Error(NotOurs(platform));
        var (result, change) = await changes.SubmitAsync(member, RewardArea.AreaName, area.KeyOf(platform.Platform, id), null).ConfigureAwait(false);
        log?.Info(LogSource, $"{member.Name} deleted the {platform.Name} reward \"{before.Title}\"{(result == ChangeResult.Pending ? " (waiting for approval)" : "")}.");
        return Ok(new JsonObject { ["change"] = change.Id.ToString(), ["status"] = result == ChangeResult.Pending ? "pending" : "live" });
    }

    private IChannelRewards? Find(JsonObject request) => area.Platform((string?)request["platform"] ?? "");

    private static string NotOurs(IChannelRewards platform) =>
        $"This reward was made outside Cuebot, and {platform.Name} only lets the app that made a reward change it. Change it on {platform.Name}.";

    /// <summary>The reward the portal sent, with defaults for what it left out.</summary>
    internal static ChannelReward Read(JsonObject n)
    {
        static int Int(JsonObject n, string name) => n[name] is not JsonValue v ? 0
            : v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<long>(out var l) ? (int)Math.Clamp(l, int.MinValue, int.MaxValue)
            : v.TryGetValue<double>(out var d) ? (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue)
            : v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : 0;
        static bool Bool(JsonObject n, string name, bool fallback = false) => n[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
        static string Str(JsonObject n, string name) => n[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        return new ChannelReward(Str(n, "id"), Str(n, "title"), Str(n, "description"), Int(n, "cost"), Str(n, "color"), Bool(n, "enabled", true),
            Bool(n, "paused"), Bool(n, "userInput"), Bool(n, "skipQueue"), Int(n, "maxPerStream"), Int(n, "maxPerUserPerStream"),
            Int(n, "cooldownSeconds"), null);
    }

    internal static IReadOnlyList<string> Check(IChannelRewards platform, ChannelReward r)
    {
        var rules = platform.Rules;
        var problems = new List<string>();
        if (r.Title.Length == 0) problems.Add("The reward needs a title.");
        if (r.Title.Length > rules.MaxTitleLength) problems.Add($"{platform.Name} titles can be up to {rules.MaxTitleLength} characters.");
        if (r.Description.Length > rules.MaxDescriptionLength) problems.Add($"{platform.Name} descriptions can be up to {rules.MaxDescriptionLength} characters.");
        if (r.Cost < 1) problems.Add("The cost must be at least 1.");
        if (r.Color.Length > 0 && !Hex().IsMatch(r.Color)) problems.Add("The colour must look like #9146FF.");
        if (r.MaxPerStream < 0 || r.MaxPerUserPerStream < 0 || r.CooldownSeconds < 0) problems.Add("Limits cannot be below 0.");
        if (!rules.HasStreamLimits && (r.MaxPerStream > 0 || r.MaxPerUserPerStream > 0)) problems.Add($"{platform.Name} rewards have no per-stream limits.");
        if (!rules.HasCooldown && r.CooldownSeconds > 0) problems.Add($"{platform.Name} rewards have no cooldown.");
        return problems;
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex Hex();

    private static JsonObject Ok(JsonObject body)
    {
        body["ok"] = true;
        return body;
    }

    private static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

    private static JsonObject Refused(IReadOnlyList<string> problems) => new()
    {
        ["ok"] = false,
        ["error"] = string.Join(" ", problems),
        ["problems"] = new JsonArray([.. problems.Select(p => (JsonNode)p)]),
    };
}
