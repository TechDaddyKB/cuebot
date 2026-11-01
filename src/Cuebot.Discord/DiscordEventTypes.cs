namespace Cuebot.Discord;

/// <summary>Bot events the Discord bot publishes (also its trigger type ids). Stored in actions.json: never rename.</summary>
public static class DiscordEventTypes
{
    public const string Connected = "discord.connected";
    public const string Disconnected = "discord.disconnected";
    public const string SlashCommand = "discord.slashCommand";
    public const string MessagePosted = "discord.messagePosted";
    public const string MemberJoined = "discord.memberJoined";

    /// <summary>The argument that ties a run to the slash command use it answers.</summary>
    public const string InteractionIdArg = "discordInteractionId";
}
