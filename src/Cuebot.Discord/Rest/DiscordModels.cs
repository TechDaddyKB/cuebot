using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cuebot.Discord;

/// <summary>A text channel the bot can see.</summary>
public sealed record DiscordChannel(string Id, string GuildId, string Name, int Type, int Position)
{
    /// <summary>Text (0) and announcement (5) channels: the ones a bot can post in.</summary>
    public bool IsText => Type is 0 or 5;
}

public sealed record DiscordRole(string Id, string GuildId, string Name, int Position, bool Managed);

/// <summary>A server (guild) the bot is in, with its channels and roles.</summary>
public sealed record DiscordGuild(string Id, string Name, IReadOnlyList<DiscordChannel> Channels, IReadOnlyList<DiscordRole> Roles);

public sealed record DiscordUser(string Id, string UserName, string DisplayName, bool IsBot);

/// <summary>Embed fields of a message step. Empty fields are left out; an embed with no field is not sent.</summary>
public sealed record DiscordEmbed(string Title, string Description, string Url, string Color, string ImageUrl)
{
    public bool IsEmpty => Title.Length == 0 && Description.Length == 0 && Url.Length == 0 && ImageUrl.Length == 0;
}

/// <summary>JSON builders and parsers for REST bodies (pure functions, unit-tested).</summary>
public static partial class DiscordPayloads
{
    public const int MaxContentLength = 2000;

    /// <summary>A create/edit message body. <paramref name="content"/> null leaves the text alone (edit only).</summary>
    public static JsonObject Message(string? content, DiscordEmbed? embed, bool removeEmbeds = false)
    {
        if (content is { Length: > MaxContentLength })
            throw new ArgumentException($"Discord allows at most {MaxContentLength} characters in a message (this one has {content.Length}).");
        var body = new JsonObject();
        if (content is not null) body["content"] = content;
        if (embed is { IsEmpty: false }) body["embeds"] = new JsonArray(Embed(embed));
        else if (removeEmbeds) body["embeds"] = new JsonArray();
        return body;
    }

    public static JsonObject Embed(DiscordEmbed e)
    {
        var o = new JsonObject();
        if (e.Title.Length > 0) o["title"] = Limit(e.Title, 256, "The embed title");
        if (e.Description.Length > 0) o["description"] = Limit(e.Description, 4096, "The embed description");
        if (e.Url.Length > 0) o["url"] = e.Url;
        if (ParseColor(e.Color) is { } color) o["color"] = color;
        if (e.ImageUrl.Length > 0) o["image"] = new JsonObject { ["url"] = e.ImageUrl };
        return o;
    }

    private static string Limit(string text, int max, string what) =>
        text.Length <= max ? text : throw new ArgumentException($"{what} can be at most {max} characters.");

    /// <summary>"#5865F2", "5865F2", "0x5865F2" or a whole number; empty for none.</summary>
    public static int? ParseColor(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;
        var named = t.ToLowerInvariant() switch
        {
            "red" => 0xED4245, "green" => 0x57F287, "blue" => 0x3498DB, "blurple" => 0x5865F2, "purple" => 0x9B59B6,
            "yellow" => 0xFEE75C, "orange" => 0xE67E22, "pink" => 0xEB459E, "white" => 0xFFFFFF, "black" => 0x000000,
            "twitch" => 0x9146FF, "youtube" => 0xFF0000, "kick" => 0x53FC18,
            _ => -1,
        };
        if (named >= 0) return named;
        var hex = t.StartsWith('#') ? t[1..] : t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : null;
        if (hex is not null && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) && h is >= 0 and <= 0xFFFFFF) return h;
        if (hex is null && t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h6) && t.Any(char.IsLetter)) return h6;
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d is >= 0 and <= 0xFFFFFF) return d;
        throw new ArgumentException($"The embed colour must look like #9146FF (got '{text}').");
    }

    /// <summary>Discord's rule for command and option names.</summary>
    public static bool IsValidCommandName(string name) => CommandName().IsMatch(name);

    [GeneratedRegex(@"^[-_\p{Ll}\p{Lo}\p{N}]{1,32}$")]
    private static partial Regex CommandName();

    /// <summary>The bulk-overwrite body for guild commands: chat-input commands with an optional text option.</summary>
    public static JsonArray Commands(IEnumerable<DiscordSlashCommand> commands)
    {
        var array = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in commands)
        {
            var name = c.Name.Trim().TrimStart('/').ToLowerInvariant();
            if (!IsValidCommandName(name))
                throw new ArgumentException($"\"/{c.Name}\" is not a valid command name: use 1-32 lowercase letters, numbers, - or _ (no spaces).");
            if (!seen.Add(name)) throw new ArgumentException($"There are two commands named /{name}.");
            var description = c.Description.Trim();
            if (description.Length == 0) description = name;
            if (description.Length > 100) throw new ArgumentException($"The description of /{name} can be at most 100 characters.");
            var command = new JsonObject { ["name"] = name, ["description"] = description, ["type"] = 1 };
            var option = c.OptionName.Trim().ToLowerInvariant();
            if (option.Length > 0)
            {
                if (!IsValidCommandName(option))
                    throw new ArgumentException($"The option \"{c.OptionName}\" of /{name} is not valid: use 1-32 lowercase letters, numbers, - or _.");
                var optionDescription = c.OptionDescription.Trim();
                if (optionDescription.Length == 0) optionDescription = option;
                if (optionDescription.Length > 100) throw new ArgumentException($"The option description of /{name} can be at most 100 characters.");
                command["options"] = new JsonArray(new JsonObject
                {
                    ["type"] = 3, ["name"] = option, ["description"] = optionDescription, ["required"] = c.OptionRequired,
                });
            }
            array.Add(command);
        }
        if (array.Count > 100) throw new ArgumentException("Discord allows at most 100 slash commands per server.");
        return array;
    }

    public static DiscordUser User(JsonElement u)
    {
        var name = Str(u, "username");
        var global = Str(u, "global_name");
        return new DiscordUser(Str(u, "id"), name, global.Length > 0 ? global : name, u.TryGetProperty("bot", out var b) && b.ValueKind == JsonValueKind.True);
    }

    public static DiscordChannel Channel(JsonElement c, string guildId) =>
        new(Str(c, "id"), guildId.Length > 0 ? guildId : Str(c, "guild_id"), Str(c, "name"), Int(c, "type"), Int(c, "position"));

    public static DiscordRole Role(JsonElement r, string guildId) =>
        new(Str(r, "id"), guildId, Str(r, "name"), Int(r, "position"), r.TryGetProperty("managed", out var m) && m.ValueKind == JsonValueKind.True);

    /// <summary>A GUILD_CREATE payload (or a guild object plus channel and role lists).</summary>
    public static DiscordGuild Guild(JsonElement g)
    {
        var id = Str(g, "id");
        var channels = g.TryGetProperty("channels", out var cs) && cs.ValueKind == JsonValueKind.Array
            ? cs.EnumerateArray().Select(c => Channel(c, id)).ToArray() : [];
        var roles = g.TryGetProperty("roles", out var rs) && rs.ValueKind == JsonValueKind.Array
            ? rs.EnumerateArray().Select(r => Role(r, id)).ToArray() : [];
        return new DiscordGuild(id, Str(g, "name"), channels, roles);
    }

    internal static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number => v.GetRawText(), _ => "" }
            : "";

    internal static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}
