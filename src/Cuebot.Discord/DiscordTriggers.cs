using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Discord;

/// <summary>Triggers of the Discord bot. Type ids equal the event types and are stored in actions.json.</summary>
internal sealed class DiscordTriggers(DiscordBotService bot) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType(DiscordEventTypes.SlashCommand, DiscordSubActions.Category, "Slash command used", DiscordEventTypes.SlashCommand,
            [
                new ParameterDescriptor("command", "Command")
                {
                    Suggestions = () => bot.Settings.Commands.Select(c => c.Name.Trim().TrimStart('/')).Where(n => n.Length > 0),
                    Help = "The command name without the slash, like socials. Empty runs for every command",
                },
            ],
            (e, s, _) => MatchesList(s["command"].Replace("/", "", StringComparison.Ordinal), Text(e, "command"))),
        new EventTriggerType(DiscordEventTypes.MessagePosted, DiscordSubActions.Category, "Message posted in a channel", DiscordEventTypes.MessagePosted,
            [
                new ParameterDescriptor("channel", "Channel")
                {
                    Suggestions = () => DiscordTargets.ChannelSuggestions(bot),
                    Help = "Only messages in this channel. Empty means every channel the bot can see",
                },
                new ParameterDescriptor("contains", "Text contains") { Help = "Only messages containing these words. Empty means every message" },
            ],
            (e, s, _) => MatchesChannel(s["channel"], Text(e, "channelId"), Text(e, "channelName"))
                         && (s["contains"].Trim().Length == 0 || Text(e, "message").Contains(s["contains"].Trim(), StringComparison.CurrentCultureIgnoreCase))),
        new EventTriggerType(DiscordEventTypes.MemberJoined, DiscordSubActions.Category, "Member joined the server", DiscordEventTypes.MemberJoined),
        new EventTriggerType(DiscordEventTypes.Connected, DiscordSubActions.Category, "Bot connected", DiscordEventTypes.Connected),
        new EventTriggerType(DiscordEventTypes.Disconnected, DiscordSubActions.Category, "Bot disconnected", DiscordEventTypes.Disconnected),
    ];

    private static string Text(BotEvent e, string key) =>
        e.Args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    internal static bool MatchesList(string filter, string value)
    {
        var wanted = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return wanted.Length == 0 || wanted.Any(w => string.Equals(w, value, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool MatchesChannel(string filter, string channelId, string channelName)
    {
        if (filter.Trim().Length == 0) return true;
        if (DiscordTargets.Id(filter) is { } id) return id == channelId;
        var name = filter.Trim().TrimStart('#');
        var cut = name.IndexOf('·');
        if (cut >= 0) name = name[..cut].Trim();
        return string.Equals(name, channelName, StringComparison.OrdinalIgnoreCase);
    }
}
