namespace Cuebot.Scripting.Compat;

/// <summary>A Streamer.bot event name: <c>{source, type}</c> as used in WebSocket envelopes and subscriptions.</summary>
public readonly record struct StreamerBotEventName(string Source, string Type)
{
    public override string ToString() => $"{Source}.{Type}";
}

/// <summary>
/// Maps Cuebot bus event types ("module.camelCase") to Streamer.bot's WebSocket event source and type. The names
/// follow the public event list (the GetEvents output published with the official client, and the <c>EventType</c>
/// enum of the C# docs), including its misspellings (<c>CameraHubWebcamrDisconnected</c>, <c>LayerVisbilityChanged</c>,
/// ...) because clients subscribe to exactly those strings.
/// <para>
/// <see cref="Explicit"/> lists the names that differ from the generic rule; <see cref="Resolvers"/> handle the events
/// whose name depends on their arguments (platform, emote provider, cron timer, Meld event kind). Everything else maps
/// generically: the module becomes the source (<see cref="Sources"/> gives the Streamer.bot spelling, plus a type
/// prefix for modules that live under a shared source, such as Wave Link under Elgato) and the rest is PascalCased
/// ("twitch.giftBomb" → Twitch.GiftBomb), so events added by modules later are forwarded without changes here.
/// Cuebot-only modules (Chat, Speech, Rumble, Lumia, mtion, OBSBOT, PolyPop, Points, game controller hot-plug) keep
/// their own source names: Streamer.bot documents nothing for them.
/// </para>
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
        ["slobs"] = "StreamlabsDesktop",
        ["meldstudio"] = "MeldStudio",
        ["speechToText"] = "SpeechToText",
        ["command"] = "Command",
        ["groups"] = "Group",
        ["custom"] = "Custom",
        ["quotes"] = "Quote",
        ["streamlabs"] = "Streamlabs",
        ["streamelements"] = "StreamElements",
        ["kofi"] = "Kofi",
        ["patreon"] = "Patreon",
        ["fourthwall"] = "Fourthwall",
        ["shopify"] = "Shopify",
        ["throne"] = "Throne",
        ["tipeeestream"] = "TipeeeStream",
        ["treatstream"] = "TreatStream",
        ["donordrive"] = "DonorDrive",
        ["streamloots"] = "StreamLoots",
        ["crowdcontrol"] = "CrowdControl",
        ["pally"] = "Pallygg",
        ["pulsoid"] = "Pulsoid",
        ["hyperate"] = "HypeRate",
        ["tits"] = "ThrowingSystem",
        ["vtubestudio"] = "VTubeStudio",
        ["voicemod"] = "VoiceMod",
        ["streamDeck"] = "StreamDeck",
        ["elgato"] = "Elgato",
        ["wavelink"] = "Elgato",
        ["midi"] = "Midi",
        ["hotkeys"] = "Inputs",
        ["input"] = "Inputs",
        ["desktop"] = "System",
        ["remote"] = "StreamerBotRemote",
        ["webhook"] = "StreamerBot",
        ["speakerbot"] = "SpeakerBot",
        ["variables"] = "Misc",
        ["core"] = "Misc",
        ["users"] = "Misc",
        // Cuebot-only sources (no Streamer.bot counterpart).
        ["chat"] = "Chat",
        ["speech"] = "Speech",
        ["rumble"] = "Rumble",
        ["lumia"] = "Lumia",
        ["mtion"] = "Mtion",
        ["obsbot"] = "Obsbot",
        ["polypop"] = "PolyPop",
        ["points"] = "Points",
        ["controller"] = "Controller",
    };

    /// <summary>Type prefix for modules that share a Streamer.bot source ("wavelink.mixUpdated" → Elgato.WaveLinkV3MixUpdated).</summary>
    private static readonly IReadOnlyDictionary<string, string> TypePrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["wavelink"] = "WaveLinkV3",
    };

    /// <summary>Cuebot event type → Streamer.bot name, where the generic rule would be wrong.</summary>
    public static readonly IReadOnlyDictionary<string, StreamerBotEventName> Explicit =
        new Dictionary<string, StreamerBotEventName>(StringComparer.OrdinalIgnoreCase)
        {
            // Twitch
            ["twitch.resub"] = new("Twitch", "ReSub"),
            ["twitch.messageDeleted"] = new("Twitch", "ChatMessageDeleted"),
            ["twitch.subGift"] = new("Twitch", "GiftSub"),                 // one per recipient, as Streamer.bot's GiftSub
            ["twitch.giftSub"] = new("Twitch", "GiftSubscriptions"),       // EventSub's aggregate; GiftSub/GiftBomb cover it
            ["twitch.pyramid"] = new("Twitch", "PyramidSuccess"),
            ["twitch.userWarned"] = new("Twitch", "WarnedUser"),
            ["twitch.automodMessageHeld"] = new("Twitch", "AutoModMessageHeld"),
            ["twitch.automodMessageUpdated"] = new("Twitch", "AutoModMessageUpdate"),
            ["twitch.broadcasterConnected"] = new("Twitch", "BroadcasterEventSubConnected"),
            ["twitch.broadcasterDisconnected"] = new("Twitch", "BroadcasterEventSubDisconnected"),
            ["twitch.botConnected"] = new("Twitch", "BotEventSubConnected"),
            ["twitch.botDisconnected"] = new("Twitch", "BotEventSubDisconnected"),
            ["twitch.emoteModeOn"] = new("Twitch", "ChatEmoteModeOn"),
            ["twitch.emoteModeOff"] = new("Twitch", "ChatEmoteModeOff"),
            ["twitch.followerModeOn"] = new("Twitch", "ChatFollowerModeOn"),
            ["twitch.followerModeOff"] = new("Twitch", "ChatFollowerModeOff"),
            ["twitch.followerModeChanged"] = new("Twitch", "ChatFollowerModeChanged"),
            ["twitch.slowModeOn"] = new("Twitch", "ChatSlowModeOn"),
            ["twitch.slowModeOff"] = new("Twitch", "ChatSlowModeOff"),
            ["twitch.slowModeChanged"] = new("Twitch", "ChatSlowModeChanged"),
            ["twitch.subscriberModeOn"] = new("Twitch", "ChatSubscriberModeOn"),
            ["twitch.subscriberModeOff"] = new("Twitch", "ChatSubscriberModeOff"),
            ["twitch.uniqueModeOn"] = new("Twitch", "ChatUniqueModeOn"),
            ["twitch.uniqueModeOff"] = new("Twitch", "ChatUniqueModeOff"),
            ["twitch.sharedChatBegin"] = new("Twitch", "SharedChatSessionBegin"),
            ["twitch.sharedChatUpdate"] = new("Twitch", "SharedChatSessionUpdate"),
            ["twitch.sharedChatEnd"] = new("Twitch", "SharedChatSessionEnd"),
            ["twitch.sharedChatGiftBomb"] = new("Twitch", "SharedChatCommunitySubGift"),
            ["twitch.sharedChatModiversary"] = new("Twitch", "SharedModiversary"),
            ["twitch.sharedChatUserTimedOut"] = new("Twitch", "SharedChatUserTimedout"),
            ["twitch.sharedChatUserUntimedOut"] = new("Twitch", "SharedChatUserUntimedout"),

            // YouTube
            ["youtube.chatMessage"] = new("YouTube", "Message"),
            ["youtube.memberMilestone"] = new("YouTube", "MemberMileStone"),
            ["youtube.userTimedOut"] = new("YouTube", "UserTimedout"),
            ["youtube.broadcastStatisticsUpdated"] = new("YouTube", "StatisticsUpdated"),
            ["youtube.sponsorOnlyStarted"] = new("YouTube", "NewSponsorOnlyStarted"),
            ["youtube.sponsorOnlyEnded"] = new("YouTube", "NewSponsorOnlyEnded"),

            // Kick
            ["kick.chatConnected"] = new("Kick", "BroadcasterChatConnected"),
            ["kick.chatDisconnected"] = new("Kick", "BroadcasterChatDisconnected"),
            ["kick.sevenTvEmoteAdded"] = new("Kick", "SevenTVEmoteAdded"),
            ["kick.sevenTvEmoteRemoved"] = new("Kick", "SevenTVEmoteRemoved"),

            // Broadcast software
            ["obs.streamStarted"] = new("Obs", "StreamingStarted"),
            ["obs.streamStopped"] = new("Obs", "StreamingStopped"),
            ["obs.recordStarted"] = new("Obs", "RecordingStarted"),
            ["obs.recordStopped"] = new("Obs", "RecordingStopped"),
            ["slobs.streamStarted"] = new("StreamlabsDesktop", "StreamingStarted"),
            ["slobs.streamStopped"] = new("StreamlabsDesktop", "StreamingStopped"),
            ["slobs.recordStarted"] = new("StreamlabsDesktop", "RecordingStarted"),
            ["slobs.recordStopped"] = new("StreamlabsDesktop", "RecordingStopped"),

            // Core
            ["command.triggered"] = new("Command", "Triggered"),
            ["command.cooldown"] = new("Command", "Cooldown"),
            ["groups.added"] = new("Group", "Added"),
            ["groups.removed"] = new("Group", "Removed"),
            ["groups.cleared"] = new("Group", "Cleared"),
            ["groups.usersAdded"] = new("Group", "UsersAdded"),
            ["groups.usersRemoved"] = new("Group", "UsersRemoved"),
            ["quotes.shown"] = new("Quote", "Show"),
            ["variables.globalCreated"] = new("Misc", "GlobalVariableCreated"),
            ["variables.globalUpdated"] = new("Misc", "GlobalVariableUpdated"),
            ["variables.globalDeleted"] = new("Misc", "GlobalVariableDeleted"),
            ["variables.userUpdated"] = new("Misc", "UserGlobalVariableUpdated"),
            ["core.appStarted"] = new("Misc", "StreamerbotStarted"),
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
            ["desktop.clipboardChanged"] = new("System", "ClipboardChanged"),
            ["desktop.toastActivated"] = new("Misc", "ToastActivation"),
            ["hotkeys.pressed"] = new("Inputs", "InputKeyPress"),
            ["input.mouseClick"] = new("Inputs", "InputMouseClick"),
            ["controller.button"] = new("Inputs", "InputControllerButton"),
            ["controller.axis"] = new("Inputs", "InputControllerAxis"),
            ["controller.hat"] = new("Inputs", "InputControllerHat"),
            ["remote.connected"] = new("StreamerBotRemote", "InstanceConnected"),
            ["remote.disconnected"] = new("StreamerBotRemote", "InstanceDisconnected"),
            ["remote.signal"] = new("StreamerBotRemote", "InstanceSignal"),
            ["webhook.custom"] = new("StreamerBot", "CustomWebook"),

            // Integrations
            ["streamlabs.merch"] = new("Streamlabs", "Merchandise"),
            ["pulsoid.heartRate"] = new("Pulsoid", "HeartRatePulse"),
            ["hyperate.heartRate"] = new("HypeRate", "HeartRatePulse"),
            ["hyperate.clipCreated"] = new("HypeRate", "TwitchClipCreated"),
            ["voicemod.voiceChanged"] = new("VoiceMod", "VoiceLoaded"),
            ["elgato.cameraHub.webcam.disconnected"] = new("Elgato", "CameraHubWebcamrDisconnected"),
            ["elgato.cameraHub.webcam.tiltChanged"] = new("Elgato", "CameraHubTiltChanged"),
            ["elgato.controlCenter.devicePoweredOn"] = new("Elgato", "ControlCenterDeviceOn"),
            ["elgato.controlCenter.devicePoweredOff"] = new("Elgato", "ControlCenterDeviceOff"),
            ["elgato.controlCenter.brightnessChanged"] = new("Elgato", "ControlCenterDeviceBrightnessChanged"),
            ["elgato.controlCenter.temperatureChanged"] = new("Elgato", "ControlCenterDeviceTemperatureChanged"),
            ["wavelink.levelMeter"] = new("Elgato", "WaveLinkV3LevelMeterUpdated"),
            ["wavelink.mainOutputChanged"] = new("Elgato", "WaveLinkV3MainOutputDeviceChanged"),

            // Cuebot-only
            ["speech.started"] = new("Speech", "SpeechStarted"),
            ["speech.finished"] = new("Speech", "SpeechFinished"),
            ["speech.stateChanged"] = new("Speech", "StateChanged"),
        };

    /// <summary>An argument-dependent name: <see cref="Resolve"/> picks one of <see cref="Names"/>.</summary>
    public sealed record Resolver(Func<IReadOnlyDictionary<string, object?>?, StreamerBotEventName> Resolve,
        IReadOnlyList<StreamerBotEventName> Names);

    /// <summary>Events whose Streamer.bot name depends on their arguments.</summary>
    public static readonly IReadOnlyDictionary<string, Resolver> Resolvers = new Dictionary<string, Resolver>(StringComparer.OrdinalIgnoreCase)
    {
        ["users.firstWords"] = PerPlatform(new() { ["Twitch"] = "FirstWord", ["YouTube"] = "FirstWords", ["Kick"] = "FirstWords" }, "FirstWords"),
        ["users.presentViewers"] = PerPlatform(new() { ["Twitch"] = "PresentViewers", ["YouTube"] = "PresentViewers", ["Kick"] = "PresentViewers" }, "PresentViewers"),
        ["chat.emoteAdded"] = Emote("Added"),
        ["chat.emoteRemoved"] = Emote("Removed"),
        // Cron schedules are their own trigger in Streamer.bot; interval timers are Timed Actions.
        ["core.timer"] = new(args => Str(args, "timerCron").Length > 0 ? new("System", "Cron") : new("Misc", "TimedAction"),
            [new("Misc", "TimedAction"), new("System", "Cron")]),
        ["meldstudio.event"] = MeldEvent(),
        // Cuebot publishes core.cron only in minutes where a Cron trigger fires.
        ["core.cron"] = new(_ => new("System", "Cron"), [new("System", "Cron")]),
    };

    /// <summary>Streamer.bot name for a Cuebot event. Never null; unknown modules map generically.</summary>
    public static StreamerBotEventName Map(string cuebotType, IReadOnlyDictionary<string, object?>? args = null)
    {
        if (Explicit.TryGetValue(cuebotType, out var known)) return known;
        if (Resolvers.TryGetValue(cuebotType, out var resolver)) return resolver.Resolve(args);

        var dot = cuebotType.IndexOf('.');
        if (dot <= 0) return new("General", Pascal(cuebotType));
        var module = cuebotType[..dot];
        return new(SourceFor(module), TypePrefixes.GetValueOrDefault(module, "") + Pascal(cuebotType[(dot + 1)..]));
    }

    /// <summary>Every name <see cref="Map"/> can return for <paramref name="cuebotType"/> (several for argument-dependent events).</summary>
    public static IEnumerable<StreamerBotEventName> PossibleNames(string cuebotType) =>
        Resolvers.TryGetValue(cuebotType, out var resolver) ? resolver.Names : [Map(cuebotType)];

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
    /// Events the servers send themselves rather than through the bus (action runs, profile changes, custom
    /// broadcasts, shutdown). They are part of every catalog.
    /// </summary>
    public static readonly IReadOnlyList<StreamerBotEventName> ServerEvents =
    [
        new("Raw", "Action"), new("Raw", "SubAction"), new("Raw", "ActionCompleted"),
        new("Application", "ActionAdded"), new("Application", "ActionUpdated"), new("Application", "ActionDeleted"),
        new("Application", "CommandAdded"), new("Application", "CommandUpdated"), new("Application", "CommandDeleted"),
        new("General", "Custom"), new("Misc", "StreamerbotExiting"),
    ];

    /// <summary>
    /// The catalog for a set of Cuebot event types: every name <see cref="Map"/> can emit for them, plus
    /// <see cref="ServerEvents"/>. GetEvents builds it from the event types of the loaded modules' triggers.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildCatalog(IEnumerable<string> cuebotTypes)
    {
        var catalog = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Add(StreamerBotEventName name)
        {
            if (!catalog.TryGetValue(name.Source, out var set)) catalog[name.Source] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(name.Type);
        }

        foreach (var type in cuebotTypes)
        {
            foreach (var name in PossibleNames(type)) Add(name);
        }
        foreach (var name in ServerEvents) Add(name);
        return catalog.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>
    /// The catalog without a module registry: every explicitly mapped and argument-dependent event, the server's own
    /// events and the common platform events that map generically.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Catalog =>
        _catalog ??= BuildCatalog(Explicit.Keys.Concat(Resolvers.Keys).Concat(CommonGenericTypes));

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>? _catalog;

    /// <summary>Frequently used events that need no explicit entry (for <see cref="Catalog"/>).</summary>
    private static readonly string[] CommonGenericTypes =
    [
        "twitch.chatMessage", "twitch.follow", "twitch.sub", "twitch.giftBomb", "twitch.cheer", "twitch.raid",
        "twitch.rewardRedemption", "twitch.streamOnline", "twitch.streamOffline", "twitch.whisper", "twitch.announcement",
        "twitch.chatMessageDeleted", "twitch.userBanned", "twitch.userTimedOut", "twitch.adRun", "twitch.hypeTrainStart",
        "twitch.hypeTrainUpdate", "twitch.hypeTrainLevelUp", "twitch.hypeTrainEnd", "twitch.pollCreated", "twitch.pollCompleted",
        "twitch.predictionCreated", "twitch.predictionCompleted", "twitch.shoutoutCreated", "twitch.streamUpdate",
        "twitch.viewerCountUpdate", "youtube.messageDeleted", "youtube.superChat", "youtube.superSticker", "youtube.newSponsor",
        "youtube.membershipGift", "youtube.giftMembershipReceived", "youtube.newSubscriber", "youtube.broadcastStarted",
        "youtube.broadcastEnded", "youtube.userBanned", "kick.chatMessage", "kick.follow", "kick.subscription",
        "kick.resubscription", "kick.giftSubscription", "kick.massGiftSubscription", "kick.kicksGifted", "kick.rewardRedemption",
        "kick.streamOnline", "kick.streamOffline", "kick.userBanned", "kick.userTimedOut", "kick.channelUpdate",
        "kick.viewerCountUpdate", "obs.connected", "obs.disconnected", "obs.sceneChanged", "quotes.added", "chat.message",
        "chat.activity", "speechToText.command", "speechToText.dictation", "rumble.chatMessage", "rumble.rant",
        "rumble.follow", "rumble.subscribe", "rumble.giftSub", "rumble.streamOnline", "rumble.streamOffline",
    ];

    private static Resolver PerPlatform(Dictionary<string, string> types, string fallback)
    {
        var names = types.Select(p => new StreamerBotEventName(p.Key, p.Value)).Append(new("Misc", fallback)).ToArray();
        return new(args =>
        {
            var source = SourceFor(Str(args, "platform"));
            return types.TryGetValue(source, out var type) ? new(source, type) : new("Misc", fallback);
        }, names);
    }

    /// <summary>7TV and BetterTTV emote changes are per platform in Streamer.bot (FFZ and other platforms stay Chat.*).</summary>
    private static Resolver Emote(string change)
    {
        var providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["7TV"] = "SevenTV", ["BTTV"] = "BetterTTV" };
        var platforms = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Twitch"] = ["SevenTV", "BetterTTV"], ["YouTube"] = ["SevenTV", "BetterTTV"], ["Kick"] = ["SevenTV"],
        };
        var fallback = new StreamerBotEventName("Chat", "Emote" + change);
        var names = platforms.SelectMany(p => p.Value.Select(v => new StreamerBotEventName(p.Key, $"{v}Emote{change}")))
            .Append(fallback).ToArray();
        return new(args =>
        {
            var platform = SourceFor(Str(args, "platform"));
            return providers.TryGetValue(Str(args, "provider"), out var provider)
                   && platforms.TryGetValue(platform, out var supported) && supported.Contains(provider)
                ? new(platform, $"{provider}Emote{change}")
                : fallback;
        }, names);
    }

    /// <summary>Meld session changes: the four names Streamer.bot's triggers use (spelled as it does), else MeldStudio.Event.</summary>
    private static Resolver MeldEvent()
    {
        var kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LayerVisibilityChanged"] = "LayerVisbilityChanged",
            ["EffectEnabledStateChanged"] = "LEffectEnabledStateChanged",
            ["TrackMonitoringStateChanged"] = "TrackMonitoringStateChanged",
            ["TrackMutedStateChanged"] = "TrackMustedStateChanged",
        };
        var names = kinds.Values.Select(t => new StreamerBotEventName("MeldStudio", t)).Append(new("MeldStudio", "Event")).ToArray();
        return new(args => new("MeldStudio", kinds.GetValueOrDefault(Str(args, "meldStudioEvent.event"), "Event")), names);
    }

    private static string Str(IReadOnlyDictionary<string, object?>? args, string key)
    {
        if (args is null) return "";
        if (args.TryGetValue(key, out var value)) return value?.ToString() ?? "";
        // Bus args are case-insensitive, but callers may pass any dictionary.
        foreach (var (k, v) in args)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return v?.ToString() ?? "";
        }
        return "";
    }
}
