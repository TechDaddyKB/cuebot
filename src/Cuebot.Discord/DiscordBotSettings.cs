using Cuebot.Core.Storage;

namespace Cuebot.Discord;

/// <summary>Persisted Discord bot configuration (discord-bot.json). The token is a secret and never logged.</summary>
public sealed class DiscordBotSettings
{
    /// <summary>The bot token from the Developer Portal (Bot → Reset Token). A secret.</summary>
    [Secret]
    public string BotToken { get; set; } = "";

    public bool AutoConnect { get; set; }

    /// <summary>Ask for the privileged MESSAGE_CONTENT intent ("Message Content Intent" in the portal).</summary>
    public bool MessageContentIntent { get; set; }

    /// <summary>Ask for the privileged GUILD_MEMBERS intent ("Server Members Intent" in the portal).</summary>
    public bool ServerMembersIntent { get; set; }

    /// <summary>The bot's application id, learned from Discord (needed for the invite link and slash commands).</summary>
    public string ApplicationId { get; set; } = "";

    /// <summary>The bot's name as Discord last reported it (shown on the page).</summary>
    public string BotName { get; set; } = "";

    /// <summary>The slash commands Cuebot registers in each of the bot's servers.</summary>
    public List<DiscordSlashCommand> Commands { get; set; } = [];

    /// <summary>What a slash command answers when its action finished without a "Reply to slash command" step.</summary>
    public string DefaultReply { get; set; } = "Done ✅";
}

/// <summary>A slash command the streamer defined, registered as a guild command.</summary>
public sealed class DiscordSlashCommand
{
    /// <summary>1-32 lowercase letters, digits, - or _ (Discord's rule), without the slash.</summary>
    public string Name { get; set; } = "";

    /// <summary>1-100 characters, shown in Discord's command list.</summary>
    public string Description { get; set; } = "";

    /// <summary>Optional text option ("what the viewer can type after the command"); empty for none.</summary>
    public string OptionName { get; set; } = "";

    public string OptionDescription { get; set; } = "";

    public bool OptionRequired { get; set; }

    /// <summary>Only the person who used the command sees the answer (an "ephemeral" reply).</summary>
    public bool Private { get; set; }
}

/// <summary>Timeouts and intervals; tests use short ones.</summary>
public sealed record DiscordTimings(
    TimeSpan ConnectTimeout,
    TimeSpan ReconnectDelay,
    TimeSpan MaxReconnectDelay,
    TimeSpan InvalidSessionMinDelay,
    TimeSpan InvalidSessionMaxDelay,
    TimeSpan ReplyGrace,
    TimeSpan InteractionLifetime,
    TimeSpan MaxRateLimitWait)
{
    public static DiscordTimings Default { get; } = new(
        ConnectTimeout: TimeSpan.FromSeconds(10),
        ReconnectDelay: TimeSpan.FromSeconds(2),
        MaxReconnectDelay: TimeSpan.FromSeconds(60),
        // Invalid session (d=false): the docs ask for a random 1-5 s wait before identifying again.
        InvalidSessionMinDelay: TimeSpan.FromSeconds(1),
        InvalidSessionMaxDelay: TimeSpan.FromSeconds(5),
        // How long an action may take to start after a slash command before the default reply is sent.
        ReplyGrace: TimeSpan.FromSeconds(3),
        // An interaction token stays valid for 15 minutes; stop a little before.
        InteractionLifetime: TimeSpan.FromMinutes(14.5),
        MaxRateLimitWait: TimeSpan.FromSeconds(60));
}
