using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Discord;

/// <summary>The REST calls Cuebot makes (discord.com/developers/docs/resources).</summary>
public sealed class DiscordApi(DiscordRestClient rest)
{
    public DiscordRestClient Rest { get; } = rest;

    /// <summary>GET /gateway/bot: the Gateway address to connect to.</summary>
    public async Task<string> GetGatewayUrlAsync(CancellationToken ct)
    {
        var answer = await Rest.GetAsync("gateway/bot", ct).ConfigureAwait(false);
        return answer is { } a ? DiscordPayloads.Str(a, "url") : "";
    }

    /// <summary>GET /applications/@me: the bot's application id and name.</summary>
    public async Task<(string Id, string Name)> GetApplicationAsync(CancellationToken ct)
    {
        var a = await Rest.GetAsync("applications/@me", ct).ConfigureAwait(false) ?? default;
        return (DiscordPayloads.Str(a, "id"), DiscordPayloads.Str(a, "name"));
    }

    /// <summary>GET /users/@me/guilds, then each server's channels and roles.</summary>
    public async Task<IReadOnlyList<DiscordGuild>> GetGuildsAsync(CancellationToken ct)
    {
        var list = await Rest.GetAsync("users/@me/guilds", ct).ConfigureAwait(false);
        var result = new List<DiscordGuild>();
        if (list is not { ValueKind: JsonValueKind.Array } guilds) return result;
        foreach (var g in guilds.EnumerateArray())
        {
            var id = DiscordPayloads.Str(g, "id");
            var channels = await Rest.GetAsync($"guilds/{id}/channels", ct, "\"View Channels\"").ConfigureAwait(false);
            var roles = await Rest.GetAsync($"guilds/{id}/roles", ct).ConfigureAwait(false);
            result.Add(new DiscordGuild(id, DiscordPayloads.Str(g, "name"),
                channels is { ValueKind: JsonValueKind.Array } cs ? cs.EnumerateArray().Select(c => DiscordPayloads.Channel(c, id)).ToArray() : [],
                roles is { ValueKind: JsonValueKind.Array } rs ? rs.EnumerateArray().Select(r => DiscordPayloads.Role(r, id)).ToArray() : []));
        }
        return result;
    }

    /// <summary>POST /channels/{channel}/messages; returns the new message id.</summary>
    public async Task<string> CreateMessageAsync(string channelId, string content, DiscordEmbed? embed, CancellationToken ct)
    {
        var body = DiscordPayloads.Message(content, embed);
        var needs = embed is { IsEmpty: false } ? "\"Send Messages\" and \"Embed Links\"" : "\"Send Messages\"";
        var answer = await Rest.SendAsync(HttpMethod.Post, $"channels/{channelId}/messages", body, needs, ct).ConfigureAwait(false);
        return answer is { } a ? DiscordPayloads.Str(a, "id") : "";
    }

    /// <summary>PATCH /channels/{channel}/messages/{message}. Null content keeps the text.</summary>
    public Task EditMessageAsync(string channelId, string messageId, string? content, DiscordEmbed? embed, bool removeEmbeds, CancellationToken ct) =>
        Rest.SendAsync(HttpMethod.Patch, $"channels/{channelId}/messages/{messageId}", DiscordPayloads.Message(content, embed, removeEmbeds),
            "\"Send Messages\"", ct);

    public Task DeleteMessageAsync(string channelId, string messageId, CancellationToken ct) =>
        Rest.SendAsync(HttpMethod.Delete, $"channels/{channelId}/messages/{messageId}", null, "\"Manage Messages\"", ct);

    /// <summary>PUT /guilds/{guild}/members/{user}/roles/{role}.</summary>
    public Task AddRoleAsync(string guildId, string userId, string roleId, string? reason, CancellationToken ct) =>
        Rest.SendAsync(HttpMethod.Put, $"guilds/{guildId}/members/{userId}/roles/{roleId}", null, ManageRoles, ct, auditReason: reason);

    public Task RemoveRoleAsync(string guildId, string userId, string roleId, string? reason, CancellationToken ct) =>
        Rest.SendAsync(HttpMethod.Delete, $"guilds/{guildId}/members/{userId}/roles/{roleId}", null, ManageRoles, ct, auditReason: reason);

    private const string ManageRoles = "\"Manage Roles\", and the bot's own role must be above that role in Server Settings → Roles";

    /// <summary>GET /guilds/{guild}/members/search: members whose user name or nickname starts with the query.</summary>
    public async Task<IReadOnlyList<(DiscordUser User, string Nick)>> SearchMembersAsync(string guildId, string query, CancellationToken ct)
    {
        var answer = await Rest.GetAsync($"guilds/{guildId}/members/search?query={Uri.EscapeDataString(query)}&limit=10", ct).ConfigureAwait(false);
        if (answer is not { ValueKind: JsonValueKind.Array } members) return [];
        return members.EnumerateArray()
            .Where(m => m.TryGetProperty("user", out _))
            .Select(m => (DiscordPayloads.User(m.GetProperty("user")), DiscordPayloads.Str(m, "nick")))
            .ToArray();
    }

    /// <summary>POST /users/@me/channels: the direct-message channel with a user.</summary>
    public async Task<string> CreateDmAsync(string userId, CancellationToken ct)
    {
        var answer = await Rest.SendAsync(HttpMethod.Post, "users/@me/channels", new JsonObject { ["recipient_id"] = userId }, null, ct)
            .ConfigureAwait(false);
        return answer is { } a ? DiscordPayloads.Str(a, "id") : "";
    }

    /// <summary>POST /interactions/{id}/{token}/callback with type 5 (deferred: "Bot is thinking…").</summary>
    public Task DeferInteractionAsync(string interactionId, string token, bool ephemeral, CancellationToken ct)
    {
        var body = new JsonObject { ["type"] = 5 };
        if (ephemeral) body["data"] = new JsonObject { ["flags"] = 64 };
        return Rest.SendAsync(HttpMethod.Post, $"interactions/{interactionId}/{token}/callback", body, null, ct, authorize: false);
    }

    /// <summary>PATCH /webhooks/{application}/{token}/messages/@original: the deferred answer.</summary>
    public Task EditOriginalResponseAsync(string applicationId, string token, string content, DiscordEmbed? embed, CancellationToken ct) =>
        Rest.SendAsync(HttpMethod.Patch, $"webhooks/{applicationId}/{token}/messages/@original", DiscordPayloads.Message(content, embed), null, ct,
            authorize: false);

    /// <summary>POST /webhooks/{application}/{token}: another answer after the first.</summary>
    public Task CreateFollowupAsync(string applicationId, string token, string content, DiscordEmbed? embed, bool ephemeral, CancellationToken ct)
    {
        var body = DiscordPayloads.Message(content, embed);
        if (ephemeral) body["flags"] = 64;
        return Rest.SendAsync(HttpMethod.Post, $"webhooks/{applicationId}/{token}", body, null, ct, authorize: false);
    }

    /// <summary>PUT /applications/{application}/guilds/{guild}/commands: replaces the server's commands (instant).</summary>
    public Task OverwriteGuildCommandsAsync(string applicationId, string guildId, JsonArray commands, CancellationToken ct) =>
        Rest.SendAsync(HttpMethod.Put, $"applications/{applicationId}/guilds/{guildId}/commands", commands.DeepClone(),
            "\"Use Application Commands\" (invite the bot again with the button on Cuebot's Discord bot page)", ct);
}
