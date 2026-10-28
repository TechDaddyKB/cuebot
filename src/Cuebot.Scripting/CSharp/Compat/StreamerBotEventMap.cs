namespace Cuebot.Scripting.Compat;

/// <summary>A Streamer.bot event name: <c>{source, type}</c> as used in WebSocket envelopes and subscriptions.</summary>
public readonly record struct StreamerBotEventName(string Source, string Type)
{
    public override string ToString() => $"{Source}.{Type}";
}

/// <summary>
/// Maps Cuebot bus event types ("module.camelCase") to the closest Streamer.bot event source and type. Explicit
/// entries cover the names that differ; everything else maps generically: the module becomes the source
/// (<see cref="Sources"/> gives the Streamer.bot spelling) and the rest is PascalCased ("twitch.giftBomb" →
/// Twitch.GiftBomb), so events added by modules later are forwarded without changes here.
/// </summary>
public static class StreamerBotEventMap
{
    /// <summary>Module prefix → Streamer.bot source spelling.</summary>
    public static readonly IReadOnlyDictionary<string, string> Sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["twitch"] = "Twitch",
        ["youtube"] = "YouTube",
        ["kick"] = "Kick",
        ["obs"] = "Obs",
        ["rumble"] = "Rumble",
        ["speech"] = "Speech",
        ["chat"] = "Chat",
        ["command"] = "Command",
        ["groups"] = "Group",
        ["custom"] = "Custom",
        ["streamlabs"] = "Streamlabs",
        ["streamelements"] = "StreamElements",
        ["kofi"] = "Kofi",
        ["vtubestudio"] = "VTubeStudio",
        ["voicemod"] = "VoiceMod",
        ["midi"] = "Midi",
        ["hotkeys"] = "Inputs",
        ["speakerbot"] = "SpeakerBot",
    };

    /// <summary>Cuebot event type → Streamer.bot name, where the generic rule would be wrong.</summary>
    public static readonly IReadOnlyDictionary<string, StreamerBotEventName> Explicit =
        new Dictionary<string, StreamerBotEventName>(StringComparer.OrdinalIgnoreCase)
        {
            ["twitch.resub"] = new("Twitch", "ReSub"),
            ["twitch.firstWords"] = new("Twitch", "FirstWord"),
            ["twitch.messageDeleted"] = new("Twitch", "ChatMessageDeleted"),
            ["twitch.userTimedOut"] = new("Twitch", "UserTimedOut"),
            ["youtube.chatMessage"] = new("YouTube", "Message"),
            ["youtube.memberMilestone"] = new("YouTube", "MemberMileStone"),
            ["youtube.userTimedOut"] = new("YouTube", "UserTimedout"),
            ["kick.sub"] = new("Kick", "Subscription"),
            ["kick.resub"] = new("Kick", "Resubscription"),
            ["kick.giftSub"] = new("Kick", "GiftSubscription"),
            ["kick.massGiftSub"] = new("Kick", "MassGiftSubscription"),
            ["obs.streamStarted"] = new("Obs", "StreamingStarted"),
            ["obs.streamStopped"] = new("Obs", "StreamingStopped"),
            ["obs.recordStarted"] = new("Obs", "RecordingStarted"),
            ["obs.recordStopped"] = new("Obs", "RecordingStopped"),
            ["command.triggered"] = new("Command", "Triggered"),
            ["command.cooldown"] = new("Command", "Cooldown"),
            ["groups.added"] = new("Group", "Added"),
            ["groups.removed"] = new("Group", "Removed"),
            ["groups.cleared"] = new("Group", "Cleared"),
            ["groups.usersAdded"] = new("Group", "UsersAdded"),
            ["groups.usersRemoved"] = new("Group", "UsersRemoved"),
            ["variables.globalCreated"] = new("Misc", "GlobalVariableCreated"),
            ["variables.globalUpdated"] = new("Misc", "GlobalVariableUpdated"),
            ["variables.globalDeleted"] = new("Misc", "GlobalVariableDeleted"),
            ["variables.userUpdated"] = new("Misc", "UserGlobalVariableUpdated"),
            ["core.appStarted"] = new("Misc", "StreamerbotStarted"),
            ["core.timer"] = new("Misc", "TimedAction"),
            ["core.processStarted"] = new("Misc", "ProcessStarted"),
            ["core.processStopped"] = new("Misc", "ProcessStopped"),
            ["core.fileCreated"] = new("FileWatcher", "Created"),
            ["core.fileChanged"] = new("FileWatcher", "Changed"),
            ["core.fileDeleted"] = new("FileWatcher", "Deleted"),
            ["core.fileRenamed"] = new("FileWatcher", "Renamed"),
            ["core.fileTailLine"] = new("FileTail", "Changed"),
            [CodeTriggerRegistry.EventType] = new("Custom", "CodeEvent"),
            ["websocket.clientOpen"] = new("WebsocketClient", "Open"),
            ["websocket.clientClose"] = new("WebsocketClient", "Close"),
            ["websocket.clientMessage"] = new("WebsocketClient", "Message"),
            ["websocket.serverOpen"] = new("WebsocketCustomServer", "Open"),
            ["websocket.serverClose"] = new("WebsocketCustomServer", "Close"),
            ["websocket.serverMessage"] = new("WebsocketCustomServer", "Message"),
            ["speech.started"] = new("Speech", "SpeechStarted"),
            ["speech.finished"] = new("Speech", "SpeechFinished"),
            ["speech.stateChanged"] = new("Speech", "StateChanged"),
        };

    /// <summary>Event types whose Streamer.bot source is the platform in the event's <c>platform</c> argument.</summary>
    private static readonly IReadOnlyDictionary<string, string> PerPlatform = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["users.firstWords"] = "FirstWords",
        ["users.presentViewers"] = "PresentViewers",
    };

    /// <summary>Streamer.bot name for a Cuebot event. Never null; unknown modules map generically.</summary>
    public static StreamerBotEventName Map(string cuebotType, IReadOnlyDictionary<string, object?>? args = null)
    {
        if (Explicit.TryGetValue(cuebotType, out var known)) return known;
        if (PerPlatform.TryGetValue(cuebotType, out var perPlatformType))
        {
            var platform = args?.GetValueOrDefault("platform") as string;
            var source = string.IsNullOrWhiteSpace(platform) ? "Misc" : SourceFor(platform);
            return new(source, source == "Twitch" && perPlatformType == "FirstWords" ? "FirstWord" : perPlatformType);
        }

        var dot = cuebotType.IndexOf('.');
        if (dot <= 0) return new("General", Pascal(cuebotType));
        return new(SourceFor(cuebotType[..dot]), Pascal(cuebotType[(dot + 1)..]));
    }

    /// <summary>Streamer.bot source spelling of a module or platform name ("youtube" → "YouTube").</summary>
    public static string SourceFor(string module) =>
        Sources.TryGetValue(module.Trim(), out var source) ? source : Pascal(module.Trim());

    /// <summary>First letter upper-cased; dots and dashes removed ("chatMessage" → "ChatMessage").</summary>
    public static string Pascal(string text)
    {
        var parts = text.Split(['.', '-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    /// <summary>
    /// Event sources and types advertised by GetEvents: Streamer.bot's names for everything Cuebot can produce today,
    /// plus Cuebot's own sources (Chat, Speech, Rumble). Subscriptions accept any name, so this list is informational.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Catalog { get; } = BuildCatalog();

    private static Dictionary<string, IReadOnlyList<string>> BuildCatalog()
    {
        var catalog = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Add(string source, params string[] types)
        {
            if (!catalog.TryGetValue(source, out var set)) catalog[source] = set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var t in types) set.Add(t);
        }

        foreach (var name in Explicit.Values) Add(name.Source, name.Type);
        Add("Twitch", "ChatMessage", "Follow", "Sub", "ReSub", "GiftSub", "GiftBomb", "Cheer", "Raid", "RewardRedemption",
            "StreamOnline", "StreamOffline", "FirstWord", "PresentViewers", "Whisper", "Announcement", "ChatMessageDeleted",
            "UserBanned", "UserTimedOut", "AdRun", "HypeTrainStart", "HypeTrainUpdate", "HypeTrainEnd", "PollCreated",
            "PollCompleted", "PredictionCreated", "PredictionCompleted", "ShoutoutCreated", "StreamUpdate", "ViewerCountUpdate");
        Add("YouTube", "Message", "MessageDeleted", "SuperChat", "SuperSticker", "NewSponsor", "MemberMileStone",
            "MembershipGift", "GiftMembershipReceived", "NewSubscriber", "FirstWords", "PresentViewers", "BroadcastStarted",
            "BroadcastEnded", "UserBanned", "UserTimedout", "StatisticsUpdated");
        Add("Kick", "ChatMessage", "Follow", "Subscription", "Resubscription", "GiftSubscription", "MassGiftSubscription",
            "KicksGifted", "RewardRedemption", "StreamOnline", "StreamOffline", "FirstWords", "PresentViewers", "UserBanned",
            "UserTimedOut", "ChannelUpdate", "ViewerCountUpdate");
        Add("Obs", "Connected", "Disconnected", "SceneChanged");
        Add("Application", "ActionAdded", "ActionDeleted", "ActionUpdated", "CommandAdded", "CommandDeleted", "CommandUpdated");
        Add("Raw", "ActionCompleted");
        Add("General", "Custom");
        Add("Custom", "Event");
        Add("Rumble", "ChatMessage", "Rant", "Follow", "Subscribe", "GiftSub", "StreamOnline", "StreamOffline");
        Add("Chat", "Message", "Activity");
        Add("SpeechToText", "Command", "Dictation");
        return catalog.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.ToArray(), StringComparer.Ordinal);
    }
}
