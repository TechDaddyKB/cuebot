using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Users;

namespace Cuebot.Core.Commands;

/// <summary>
/// The commands manager at runtime: listens to the combined chat ("chat.message"), matches every enabled command,
/// enforces sources, permissions and cooldowns (the broadcaster is exempt from both), counts uses and publishes
/// "command.triggered" or "command.cooldown". Subscribes to the bus when constructed.
/// </summary>
public sealed class CommandService : IPeriodicService, IDisposable
{
    public const string TriggeredEvent = "command.triggered";
    public const string CooldownEvent = "command.cooldown";
    private const string Source = "Commands";

    private readonly CommandStore _store;
    private readonly GroupStore _groups;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly IDisposable _subscription;

    public CommandService(CommandStore store, GroupStore groups, EventBus bus, AppLog log, TimeProvider? time = null)
    {
        _store = store;
        _groups = groups;
        _bus = bus;
        _log = log;
        Cooldowns = new CooldownTracker(time);
        groups.Renamed += OnGroupRenamed;
        _subscription = bus.Subscribe(OnEvent);
    }

    public CooldownTracker Cooldowns { get; }

    /// <summary>
    /// Evaluates one "chat.message" event against every command and publishes the resulting events, which are also
    /// returned (one per matching command).
    /// </summary>
    public IReadOnlyList<BotEvent> Handle(BotEvent message)
    {
        var results = new List<BotEvent>();
        var args = message.Args;
        var text = args.GetString("message");
        if (text.Length == 0) return results;

        foreach (var command in _store.Snapshot)
        {
            if (!command.Enabled || command.Commands.Count == 0) continue;
            if (command.IgnoreBotAccount && (args.GetBool("isBot") || args.GetBool("isBotAccount"))) continue;
            if (command.IgnoreInternal && args.GetBool("isInternal")) continue;
            if (!SourceMatches(command, args)) continue;

            CommandMatch? match;
            try
            {
                match = CommandMatcher.MatchAny(text, command.Commands, command.Mode, command.Location, command.CaseSensitive);
            }
            catch (Exception ex)
            {
                _log.Error(Source, $"Matching '{command.Name}' failed", ex);
                continue;
            }
            if (match is null || !IsPermitted(command, args)) continue;

            var e = Evaluate(command, match, args);
            results.Add(e);
            _bus.Publish(e);
        }
        return results;
    }

    /// <summary>True when the message's platform (and kind) is one of the command's sources, or it has none.</summary>
    public static bool SourceMatches(CommandDefinition command, IReadOnlyDictionary<string, object?> args)
    {
        if (command.Sources.Count == 0) return true;
        var platform = args.GetString("platform");
        var kind = args.GetString("messageKind");
        if (kind.Length == 0) kind = "Message";
        foreach (var source in command.Sources)
        {
            var parts = source.Split(':', 2, StringSplitOptions.TrimEntries);
            if (!string.Equals(parts[0], platform, StringComparison.OrdinalIgnoreCase) && parts[0] != "*") continue;
            if (parts.Length == 1 || parts[1] == "*" || string.Equals(parts[1], kind, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Applies the role, allow and deny rules (see <see cref="CommandDefinition"/>).</summary>
    public bool IsPermitted(CommandDefinition command, IReadOnlyDictionary<string, object?> args)
    {
        var role = ChatArgs.RoleOf(args);
        if (role == ChatPermission.Broadcaster) return true;

        var platform = args.GetString("platform");
        var userId = args.GetString("userId");
        var userName = UserName(args);
        var groups = command.AllowedGroups.Count + command.DeniedGroups.Count > 0
            ? _groups.GroupsOf(platform, userId, userName)
            : [];

        bool InGroups(List<string> list) => list.Any(g => groups.Contains(g.Trim(), StringComparer.OrdinalIgnoreCase));
        bool InUsers(List<string> list) => list.Any(u => UserEntryMatches(u, platform, userId, userName));

        if (InUsers(command.DeniedUsers) || InGroups(command.DeniedGroups)) return false;
        return role >= command.MinimumRole || InUsers(command.AllowedUsers) || InGroups(command.AllowedGroups);
    }

    /// <summary>An allow/deny entry: a user name or id, optionally prefixed with "Platform:".</summary>
    private static bool UserEntryMatches(string entry, string platform, string userId, string userName)
    {
        entry = entry.Trim();
        if (entry.Length == 0) return false;
        var colon = entry.IndexOf(':');
        if (colon > 0)
        {
            if (!string.Equals(entry[..colon], platform, StringComparison.OrdinalIgnoreCase)) return false;
            entry = entry[(colon + 1)..].Trim();
        }
        entry = entry.TrimStart('@');
        return (userName.Length > 0 && string.Equals(entry, userName, StringComparison.OrdinalIgnoreCase))
               || (userId.Length > 0 && string.Equals(entry, userId, StringComparison.OrdinalIgnoreCase));
    }

    private static string UserName(IReadOnlyDictionary<string, object?> args)
    {
        var name = args.GetString("userName");
        return name.Length > 0 ? name : args.GetString("user");
    }

    private BotEvent Evaluate(CommandDefinition command, CommandMatch match, IReadOnlyDictionary<string, object?> chat)
    {
        var platform = chat.GetString("platform");
        var userKey = CommandStore.UserKey(platform, chat.GetString("userId"), UserName(chat));
        var kind = chat.GetString("messageKind");
        var args = new Dictionary<string, object?>(chat, StringComparer.OrdinalIgnoreCase)
        {
            ["command"] = match.Command,
            ["commandId"] = command.Id.ToString(),
            ["commandName"] = command.Name,
            ["commandGroup"] = command.Group,
            ["commandMode"] = command.Mode.ToString(),
            ["commandLocation"] = command.Location.ToString(),
            // Streamer.bot: rawInput is the text after the command; the whole message stays in %message%.
            ["rawInput"] = match.Input,
            ["commandSource"] = platform.ToLowerInvariant(),
            ["commandType"] = kind.Length == 0 ? "message" : kind.ToLowerInvariant(),
        };
        if (!args.ContainsKey("msgId") && chat.GetString("messageId") is { Length: > 0 } msgId) args["msgId"] = msgId;

        if (ChatArgs.RoleOf(chat) != ChatPermission.Broadcaster)
        {
            var key = command.Id.ToString();
            var cooldown = Cooldowns.TryStart(key, userKey, Seconds(command.GlobalCooldownSeconds), Seconds(command.UserCooldownSeconds));
            if (!cooldown.Allowed)
            {
                args["cooldownType"] = cooldown.Kind.ToString();
                args["remaining"] = (int)Math.Ceiling(cooldown.Remaining.TotalSeconds);
                args["remainingMs"] = (long)Math.Ceiling(cooldown.Remaining.TotalMilliseconds);
                var global = (int)Math.Ceiling(Cooldowns.GlobalRemaining(key).TotalSeconds);
                var user = (int)Math.Ceiling(Cooldowns.UserRemaining(key, userKey).TotalSeconds);
                args["globalCooldownLeft"] = global;
                args["userCooldownLeft"] = user;
                args["cooldownLeft"] = Math.Max(global, user);
                return new BotEvent(CooldownEvent, args);
            }
        }

        var (counter, userCounter) = _store.Increment(command.Id, userKey);
        args["counter"] = counter;
        args["userCounter"] = userCounter;
        args["input"] = match.Input;
        for (var i = 0; i < match.Words.Count; i++) args["input" + ChatArgs.Invariant(i)] = match.Words[i];
        args["inputCount"] = match.Words.Count;
        if (command.Mode == CommandMode.Regex)
        {
            args["match"] = match.Matched;
            args["match[0]"] = match.Matched;
            args["match.count"] = match.Groups.Count + 1;
            var index = 1;
            foreach (var (name, value) in match.Groups)
            {
                args[$"match[{index++}]"] = value;
                args[int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? "group" + name : name] = value;
            }
        }
        return new BotEvent(TriggeredEvent, args);
    }

    private static TimeSpan Seconds(double value) => value > 0 ? TimeSpan.FromSeconds(value) : TimeSpan.Zero;

    private void OnEvent(BotEvent e)
    {
        if (e.Type == ChatHub.MessageEvent) Handle(e);
    }

    void IPeriodicService.Tick(DateTimeOffset now) => Cooldowns.Prune();

    // Commands name the user groups they allow or deny, so a rename in the Users & Groups page carries over.
    private void OnGroupRenamed(string oldName, string newName) => _store.RenameUserGroup(oldName, newName);

    public void Dispose()
    {
        _groups.Renamed -= OnGroupRenamed;
        _subscription.Dispose();
    }
}
