namespace Cuebot.Core.Commands;

/// <summary>How a command's texts are compared with a chat message.</summary>
public enum CommandMode
{
    /// <summary>Each command text is literal, e.g. "!hello".</summary>
    Basic,

    /// <summary>Each command text is a .NET regular expression; groups become arguments.</summary>
    Regex,
}

/// <summary>Where in the message the command must appear.</summary>
public enum CommandLocation
{
    /// <summary>The message starts with the command, followed by whitespace or the end ("!so bob").</summary>
    Start,

    /// <summary>The whole message (trimmed) is the command.</summary>
    Exact,

    /// <summary>The command appears anywhere in the message as a separate word.</summary>
    Anywhere,
}

/// <summary>Chat roles, lowest to highest. A chatter has every level up to their highest role.</summary>
public enum ChatPermission
{
    Everyone,
    Subscriber,
    Vip,
    Moderator,
    Broadcaster,
}

/// <summary>
/// A platform-agnostic chat command (commands.json). It listens to the combined chat ("chat.message") and, on a
/// match that passes permissions and cooldowns, publishes "command.triggered".
/// </summary>
/// <remarks>
/// Permissions: the broadcaster can always run a command. Otherwise a chatter is denied when they (or one of their
/// groups) are on a deny list, and allowed when their role is at least <see cref="MinimumRole"/> or they (or one of
/// their groups) are on an allow list. To restrict a command to listed people only, set the minimum role to
/// Broadcaster and fill the allow lists.
/// </remarks>
public sealed class CommandDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New command";
    public string Group { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Listed by the "Get commands" sub-action.</summary>
    public bool Include { get; set; } = true;

    public CommandMode Mode { get; set; } = CommandMode.Basic;
    public CommandLocation Location { get; set; } = CommandLocation.Start;

    /// <summary>Aliases (one per line in the editor). They share one cooldown and one counter.</summary>
    public List<string> Commands { get; set; } = [];

    /// <summary>
    /// Where the command listens: platform names ("Twitch") or "Platform:Kind" ("Twitch:Whisper") where the kind
    /// is the chat event's <c>messageKind</c> argument (default "Message"). Empty means every source.
    /// </summary>
    public List<string> Sources { get; set; } = [];

    public bool CaseSensitive { get; set; }

    /// <summary>Ignore messages flagged <c>isBot</c> / <c>isBotAccount</c> (the bot's own account).</summary>
    public bool IgnoreBotAccount { get; set; } = true;

    /// <summary>Ignore messages flagged <c>isInternal</c> (sent from Cuebot's own chat window).</summary>
    public bool IgnoreInternal { get; set; } = true;

    public double GlobalCooldownSeconds { get; set; }
    public double UserCooldownSeconds { get; set; }

    public ChatPermission MinimumRole { get; set; } = ChatPermission.Everyone;

    /// <summary>User names or ids that may run the command regardless of <see cref="MinimumRole"/>.</summary>
    public List<string> AllowedUsers { get; set; } = [];

    /// <summary>Groups (groups.json) whose members may run the command regardless of <see cref="MinimumRole"/>.</summary>
    public List<string> AllowedGroups { get; set; } = [];

    /// <summary>User names or ids that may never run the command (the broadcaster excepted).</summary>
    public List<string> DeniedUsers { get; set; } = [];

    public List<string> DeniedGroups { get; set; } = [];

    /// <summary>Keep the use counter across restarts.</summary>
    public bool PersistCounter { get; set; }

    /// <summary>Keep the per-user use counters across restarts.</summary>
    public bool PersistUserCounter { get; set; }
}

/// <summary>commands.json.</summary>
public sealed class CommandsFile
{
    public int Version { get; set; } = 1;
    public List<CommandDefinition> Commands { get; set; } = [];
}

/// <summary>command-counters.json: persisted counters keyed by command id, user counters by "platform:userId".</summary>
public sealed class CommandCountersFile
{
    public Dictionary<Guid, long> Counters { get; set; } = [];
    public Dictionary<Guid, Dictionary<string, long>> UserCounters { get; set; } = [];
}
