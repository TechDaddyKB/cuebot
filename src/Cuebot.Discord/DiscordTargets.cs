using System.Text.RegularExpressions;
using Cuebot.Core.Actions;

namespace Cuebot.Discord;

/// <summary>
/// Turns what a streamer typed or picked (a suggestion like "#general · My Server · 123…", a name, a mention, an id or a
/// message link) into Discord ids.
/// </summary>
internal static partial class DiscordTargets
{
    public const string Separator = " · ";

    public static string ChannelSuggestion(DiscordGuild g, DiscordChannel c) => $"#{c.Name}{Separator}{g.Name}{Separator}{c.Id}";

    public static string RoleSuggestion(DiscordGuild g, DiscordRole r) => $"{r.Name}{Separator}{g.Name}{Separator}{r.Id}";

    public static IEnumerable<string> ChannelSuggestions(DiscordBotService bot) =>
        bot.Guilds.SelectMany(g => g.Channels.Where(c => c.IsText).OrderBy(c => c.Position).Select(c => ChannelSuggestion(g, c)));

    /// <summary>Roles a bot can hand out: not @everyone and not roles owned by an integration (other bots, boosts).</summary>
    public static IEnumerable<DiscordRole> AssignableRoles(DiscordGuild g) =>
        g.Roles.Where(r => r.Id != g.Id && !r.Managed).OrderByDescending(r => r.Position);

    public static IEnumerable<string> RoleSuggestions(DiscordBotService bot) =>
        bot.Guilds.SelectMany(g => AssignableRoles(g).Select(r => RoleSuggestion(g, r)));

    /// <summary>The id at the end of a suggestion, inside a mention (&lt;#1&gt;, &lt;@&amp;1&gt;, &lt;@1&gt;) or on its own.</summary>
    public static string? Id(string text)
    {
        var m = IdPattern().Match(text.Trim());
        return m.Success ? m.Groups["id"].Value : null;
    }

    [GeneratedRegex(@"(?:^<[#@][&!]?(?<id>\d{15,21})>$|^(?<id>\d{15,21})$|·\s*(?<id>\d{15,21})\s*$)")]
    private static partial Regex IdPattern();

    private static string NamePart(string text)
    {
        var t = text.Trim();
        var cut = t.IndexOf(Separator.Trim(), StringComparison.Ordinal);
        if (cut >= 0) t = t[..cut].Trim();
        return t.TrimStart('#', '@').Trim();
    }

    public static async Task<DiscordChannel> ChannelAsync(DiscordBotService bot, string text, CancellationToken ct)
    {
        var value = Require(text, "Pick the channel");
        var guilds = await bot.GuildsAsync(ct).ConfigureAwait(false);
        if (Id(value) is { } id)
            return guilds.SelectMany(g => g.Channels).FirstOrDefault(c => c.Id == id) ?? new DiscordChannel(id, "", "", 0, 0);
        var name = NamePart(value);
        var matches = guilds.SelectMany(g => g.Channels.Where(c => c.IsText && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"The bot cannot see a channel named #{name}. Pick one from the list (the bot must be in that server)."),
            _ => throw new ArgumentException($"More than one channel is named #{name}. Pick the right one from the list."),
        };
    }

    public static async Task<DiscordRole> RoleAsync(DiscordBotService bot, string text, CancellationToken ct)
    {
        var value = Require(text, "Pick the role");
        var guilds = await bot.GuildsAsync(ct).ConfigureAwait(false);
        var roles = guilds.SelectMany(g => g.Roles).ToArray();
        if (Id(value) is { } id)
            return roles.FirstOrDefault(r => r.Id == id)
                   ?? throw new ArgumentException("The bot does not know that role. Pick one from the list (click Refresh on the Discord bot page).");
        var name = NamePart(value);
        var matches = roles.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"There is no role named \"{name}\" in the bot's servers."),
            _ => throw new ArgumentException($"More than one role is named \"{name}\". Pick the right one from the list."),
        };
    }

    /// <summary>A member by user id, mention or name (looked up with Discord's member search) in one server, or in all of them.</summary>
    public static async Task<DiscordUser> MemberAsync(DiscordBotService bot, string text, string? guildId, CancellationToken ct)
    {
        var value = Require(text, "Type who (a user ID or name)");
        if (Id(value) is { } id) return new DiscordUser(id, "", "", false);
        var name = value.TrimStart('@').Trim();
        var guilds = guildId is { Length: > 0 } ? [guildId] : (await bot.GuildsAsync(ct).ConfigureAwait(false)).Select(g => g.Id).ToArray();
        foreach (var g in guilds)
        {
            var found = await bot.Api.SearchMembersAsync(g, name, ct).ConfigureAwait(false);
            var exact = found.Where(m => Same(m.User.UserName) || Same(m.User.DisplayName) || Same(m.Nick)).ToArray();
            if (exact.Length == 1) return exact[0].User;
            if (exact.Length == 0 && found.Count == 1) return found[0].User;
            if (exact.Length > 1 || found.Count > 1)
                throw new ArgumentException($"More than one member matches \"{name}\". Use their user ID instead (Discord: right-click them → Copy User ID).");
        }
        throw new ArgumentException($"Nobody named \"{name}\" was found in the bot's server{(guilds.Length == 1 ? "" : "s")}.");

        bool Same(string candidate) => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A message by link, "channel/message" ids, or a name it was remembered as (a global variable holding its link).</summary>
    public static (string ChannelId, string MessageId) Message(string text, GlobalVariables globals)
    {
        var value = Require(text, "Say which message");
        for (var hop = 0; hop < 2; hop++)
        {
            var link = LinkPattern().Match(value);
            if (link.Success) return (link.Groups["c"].Value, link.Groups["m"].Value);
            var pair = PairPattern().Match(value);
            if (pair.Success) return (pair.Groups["c"].Value, pair.Groups["m"].Value);
            if (globals.Get(value) is not { Length: > 0 } remembered) break;
            value = remembered.Trim();
        }
        throw new ArgumentException($"Cuebot does not know a message called \"{text.Trim()}\". Use the name you typed in \"Remember it as\" " +
                                    "when sending it, or paste the message link (Discord: right-click the message → Copy Message Link).");
    }

    [GeneratedRegex(@"discord(?:app)?\.com/channels/(?:\d+|@me)/(?<c>\d{15,21})/(?<m>\d{15,21})")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"^(?<c>\d{15,21})\s*[/:]\s*(?<m>\d{15,21})$")]
    private static partial Regex PairPattern();

    private static string Require(string text, string what) =>
        text.Trim() is { Length: > 0 } t ? t : throw new ArgumentException(what + ".");
}
