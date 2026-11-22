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
    public const string DeniedEvent = "command.denied";
    private const string Source = "Commands";

    /// <summary>One "denied" event per viewer and command in this time, so spamming cannot flood chat with replies.</summary>
    private static readonly TimeSpan DeniedThrottle = TimeSpan.FromSeconds(5);

    private readonly CommandStore _store;
    private readonly GroupStore _groups;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly Chat.StreamLiveState _live;
    private readonly IDisposable _subscription;
    private readonly ViewerStatusCache? _status;
    private readonly AccountStandings? _standings;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _lookups = new(StringComparer.Ordinal);

    public CommandService(CommandStore store, GroupStore groups, EventBus bus, AppLog log, TimeProvider? time = null,
        ViewerStatusCache? status = null, AccountStandings? standings = null, Chat.StreamLiveState? live = null)
    {
        _live = live ?? new Chat.StreamLiveState();
        _status = status;
        _standings = standings;
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
            // A command with a reply always ignores the bot and Cuebot's own messages, so it cannot answer itself.
            var hasReply = !string.IsNullOrWhiteSpace(command.Reply);
            if ((command.IgnoreBotAccount || hasReply) && (args.GetBool("isBot") || args.GetBool("isBotAccount"))) continue;
            if ((command.IgnoreInternal || hasReply) && args.GetBool("isInternal")) continue;
            if (!_live.Allows(command.OnlyWhileLive, command.LivePlatforms)) continue;
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
            if (match is null) continue;

            var decision = Decide(command, args, finalAnswer: false);
            if (decision.NeedsLookup)
            {
                StartLookup(command, match, args, decision);
                continue;
            }
            var e = decision.Allowed ? Evaluate(command, match, args) : Denied(command, match, args, decision.Reason);
            if (e is null) continue;
            results.Add(e);
            _bus.Publish(e);
        }
        return results;
    }

    /// <summary>
    /// A follower or tier rule needs an answer from the platform: chat is not held up, the command runs (or is denied)
    /// when it arrives. Repeats from the same viewer while it is on its way are dropped.
    /// </summary>
    private void StartLookup(CommandDefinition command, CommandMatch match, IReadOnlyDictionary<string, object?> args, PermissionDecision decision)
    {
        var platform = args.GetString("platform");
        var userId = args.GetString("userId");
        var key = $"{command.Id}|{platform}|{userId}";
        if (_status is null || !_lookups.TryAdd(key, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _status.RefreshAsync(platform, userId, decision.NeedsFollower, decision.NeedsTier).ConfigureAwait(false);
                var final = Decide(command, args, finalAnswer: true);
                var e = final.Allowed ? Evaluate(command, match, args) : Denied(command, match, args, final.Reason);
                if (e is not null) _bus.Publish(e);
            }
            catch (Exception ex)
            {
                _log.Error(Source, $"Checking who may run '{command.Name}' failed", ex);
            }
            finally
            {
                _lookups.TryRemove(key, out _);
            }
        });
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
    /// <remarks>Never calls a platform: a follower or tier not known yet counts as not a member.</remarks>
    public bool IsPermitted(CommandDefinition command, IReadOnlyDictionary<string, object?> args) =>
        Decide(command, args, finalAnswer: true).Allowed;

    /// <summary>The outcome of the permission rules.</summary>
    private readonly record struct PermissionDecision(bool Allowed, string Reason, bool NeedsFollower = false, bool NeedsTier = false)
    {
        public bool NeedsLookup => NeedsFollower || NeedsTier;
    }

    /// <param name="finalAnswer">
    /// No lookup can follow: an unknown status does not grant access, and on a deny list it denies, because a rule that
    /// keeps people out must not open up when the platform cannot be asked.
    /// </param>
    private PermissionDecision Decide(CommandDefinition command, IReadOnlyDictionary<string, object?> args, bool finalAnswer)
    {
        var role = ChatArgs.RoleOf(args);
        if (role == ChatPermission.Broadcaster) return new(true, "");

        var platform = args.GetString("platform");
        var userId = args.GetString("userId");
        var userName = UserName(args);
        var groups = command.AllowedGroups.Count + command.DeniedGroups.Count > 0
            ? _groups.GroupsOf(platform, userId, userName)
            : [];

        bool InUsers(List<string> list) => list.Any(u => UserEntryMatches(u, platform, userId, userName));
        ViewerFact Member(string group)
        {
            group = group.Trim();
            if (groups.Contains(group, StringComparer.OrdinalIgnoreCase)) return ViewerFact.Yes;
            if (!BuiltInGroups.IsBuiltIn(group) || _groups.Exists(group)) return ViewerFact.No;
            return BuiltInMember(group, args, platform, userId, userName);
        }

        if (InUsers(command.DeniedUsers)) return new(false, "denyList");
        var denied = command.DeniedGroups.Select(g => (Group: g, Fact: Member(g))).ToArray();
        if (denied.Any(d => d.Fact == ViewerFact.Yes)) return new(false, "denyList");
        var deniedUnknown = denied.Where(d => d.Fact is ViewerFact.Unknown or ViewerFact.Failed).ToArray();
        if (deniedUnknown.Length > 0)
        {
            if (!finalAnswer && deniedUnknown.Any(d => d.Fact == ViewerFact.Unknown)) return Lookup(deniedUnknown.Select(d => d.Group));
            return new(false, "statusUnknown");
        }

        if (role >= command.MinimumRole || InUsers(command.AllowedUsers)) return new(true, "");
        var allowed = command.AllowedGroups.Select(g => (Group: g, Fact: Member(g))).ToArray();
        if (allowed.Any(a => a.Fact == ViewerFact.Yes)) return new(true, "");
        var pending = allowed.Where(a => a.Fact == ViewerFact.Unknown).ToArray();
        if (!finalAnswer && pending.Length > 0) return Lookup(pending.Select(a => a.Group));
        if (allowed.Any(a => a.Fact is ViewerFact.Unknown or ViewerFact.Failed)) return new(false, "statusUnknown");
        if (allowed.Any(a => BuiltInGroups.NeedsFollower(a.Group) && !_groups.Exists(a.Group))) return new(false, "notFollower");
        if (allowed.Any(a => BuiltInGroups.NeedsTier(a.Group) && !_groups.Exists(a.Group))) return new(false, "tier");
        return new(false, "role");

        static PermissionDecision Lookup(IEnumerable<string> names)
        {
            var list = names.ToArray();
            return new(false, "statusUnknown", list.Any(BuiltInGroups.NeedsFollower), list.Any(BuiltInGroups.NeedsTier));
        }
    }

    private ViewerFact BuiltInMember(string group, IReadOnlyDictionary<string, object?> args, string platform, string userId, string userName)
    {
        static ViewerFact Fact(bool value) => value ? ViewerFact.Yes : ViewerFact.No;
        if (group.Equals(BuiltInGroups.Moderators, StringComparison.OrdinalIgnoreCase)) return Fact(args.GetBool("isModerator"));
        if (group.Equals(BuiltInGroups.Vips, StringComparison.OrdinalIgnoreCase)) return Fact(args.GetBool("isVip"));
        if (group.Equals(BuiltInGroups.Subscribers, StringComparison.OrdinalIgnoreCase)) return Fact(args.GetBool("isSubscriber"));
        if (group.Equals(BuiltInGroups.Banned, StringComparison.OrdinalIgnoreCase))
            return Fact(_standings?.IsBanned(platform, userId, userName) == true);
        if (group.Equals(BuiltInGroups.KnownBots, StringComparison.OrdinalIgnoreCase))
            return Fact(_standings?.IsKnownBot(userName) == true);
        if (_status is null) return ViewerFact.No;
        if (group.Equals(BuiltInGroups.Followers, StringComparison.OrdinalIgnoreCase)) return _status.Follower(platform, userId);
        // Only a subscriber can have a tier, so nobody else costs an API call.
        if (!args.GetBool("isSubscriber")) return ViewerFact.No;
        return _status.TierAtLeast(platform, userId, group.Equals(BuiltInGroups.SubscribersT3, StringComparison.OrdinalIgnoreCase) ? 3 : 2);
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

    /// <summary>The "command.denied" event, or null while this viewer's last one for this command is still recent.</summary>
    private BotEvent? Denied(CommandDefinition command, CommandMatch match, IReadOnlyDictionary<string, object?> chat, string reason)
    {
        var userKey = CommandStore.UserKey(chat.GetString("platform"), chat.GetString("userId"), UserName(chat));
        if (!Cooldowns.TryStart("denied:" + command.Id, userKey, TimeSpan.Zero, DeniedThrottle).Allowed) return null;
        var args = CommandArgs(command, match, chat);
        args["deniedReason"] = reason;
        args["requiredRole"] = command.MinimumRole.ToString();
        return new BotEvent(DeniedEvent, args);
    }

    private BotEvent Evaluate(CommandDefinition command, CommandMatch match, IReadOnlyDictionary<string, object?> chat)
    {
        var platform = chat.GetString("platform");
        var userKey = CommandStore.UserKey(platform, chat.GetString("userId"), UserName(chat));
        var args = CommandArgs(command, match, chat);

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
        // Presets come last so they can use the input, and never replace what the chat event set.
        foreach (var (name, value) in command.Arguments)
            if (name.Trim().Length > 0) args.TryAdd(name.Trim(), VariableResolver.Resolve(value, args));
        return new BotEvent(TriggeredEvent, args);
    }

    private static Dictionary<string, object?> CommandArgs(CommandDefinition command, CommandMatch match, IReadOnlyDictionary<string, object?> chat)
    {
        var platform = chat.GetString("platform");
        var kind = chat.GetString("messageKind");
        var args = new Dictionary<string, object?>(chat, StringComparer.OrdinalIgnoreCase)
        {
            ["command"] = match.Command,
            ["commandId"] = command.Id.ToString(),
            ["commandName"] = command.Name,
            ["commandGroup"] = command.Group,
            ["commandDescription"] = command.Description,
            ["commandMode"] = command.Mode.ToString(),
            ["commandLocation"] = command.Location.ToString(),
            // Streamer.bot: rawInput is the text after the command; the whole message stays in %message%.
            ["rawInput"] = match.Input,
            ["commandSource"] = platform.ToLowerInvariant(),
            ["commandType"] = kind.Length == 0 ? "message" : kind.ToLowerInvariant(),
        };
        if (!args.ContainsKey("msgId") && chat.GetString("messageId") is { Length: > 0 } msgId) args["msgId"] = msgId;
        // The platform's chat variables (Streamer.bot's TwitchUser, YouTubeUser... sets); the input words stay the command's.
        if (args.Remove(ChatHub.PlatformArgsKey, out var platformArgs) && platformArgs is IReadOnlyDictionary<string, object?> source)
        {
            foreach (var (name, value) in source)
                if (!name.StartsWith("input", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("rawInput", StringComparison.OrdinalIgnoreCase))
                    args.TryAdd(name, value);
        }
        return args;
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
