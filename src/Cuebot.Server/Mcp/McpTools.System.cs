using System.Text.Json.Nodes;
using Cuebot.Server.Api;

namespace Cuebot.Server.Mcp;

public static partial class McpTools
{
    /// <summary>Connections, speech, feature settings, stream totals, hotkeys and decks.</summary>
    private static IEnumerable<McpTool> SystemTools(ApiOperations ops) =>
    [
        new("list_connections", "List connections",
            "Every connection (Twitch, YouTube, Kick, Rumble, OBS, donation services, Elgato, GoXLR...) with its state and whether "
            + "connect_service can start or stop it.", Schema([]), ReadOnlyHints, (_, _) => Done(ops.Connections())),
        new("connect_service", "Connect or disconnect a service",
            "Connect or disconnect a connection by its name from list_connections, using the sign-in and settings saved in Cuebot "
            + "(it cannot sign in for the streamer; that is done on the service's page).",
            Schema(["name", "connect"], ("name", Str("Connection name, e.g. OBS, Twitch")), ("connect", Bool("true to connect, false to disconnect"))),
            new McpToolHints(ReadOnly: false, OpenWorld: true),
            async (a, ct) => await ops.SetConnectionAsync(ReqStr(a, "name"), OptBool(a, "connect") ?? throw Missing("connect"), ct).ConfigureAwait(false)),

        new("get_speech_settings", "Get speech settings",
            "Cuebot speech: on/off, voice aliases, the default alias, reading chat aloud, word replacements, master volume and filters "
            + "(engine keys are never shown).", Schema([]), ReadOnlyHints, (_, _) => Done(ops.SpeechSettings())),
        new("update_speech_settings", "Change speech settings",
            "Change enabled, defaultAlias, speakChat, replacements, masterVolume or filters (objects merged). Voice aliases: save_voice_alias.",
            Schema(["changes"], ("changes", new JsonObject { ["type"] = "object", ["description"] = "e.g. {\"speakChat\": {\"enabled\": true}}" })), WriteHints,
            (a, _) => Done(ops.UpdateSpeechSettings(a["changes"] as JsonObject ?? throw Missing("changes")))),
        new("list_speech_voices", "List speech voices", "Speech engines (Windows, Azure, Polly, Google, ElevenLabs...) and their voices.",
            Schema([], ("engine", Str("Only this engine id"))), ReadOnlyHints, async (a, ct) => await ops.SpeechVoicesAsync(OptStr(a, "engine"), ct).ConfigureAwait(false)),
        new("save_voice_alias", "Create or change a voice alias",
            "A voice alias names a voice and its settings (engineId, voiceId, rate, pitch, volume, style); actions and viewers speak with it.",
            Schema(["alias"], ("alias", new JsonObject { ["type"] = "object", ["description"] = "{name, engineId, voiceId, rate, pitch, volume, style}" })), WriteHints,
            (a, _) => Done(ops.SaveVoiceAlias(a["alias"] as JsonObject ?? throw Missing("alias")))),
        new("delete_voice_alias", "Delete a voice alias", "Delete a voice alias.",
            Schema(["name"], ("name", Str("Alias name")), Confirm()), DestructiveHints, (a, _) => Done(ops.DeleteVoiceAlias(ReqStr(a, "name"))))
        {
            Confirmation = a => $"Delete the voice alias '{ReqStr(a, "name")}'",
        },
        new("speak", "Speak text", "Say something with Cuebot's speech now (filters apply), with a voice alias or the default voice.",
            Schema(["text"], ("text", Str("What to say")), ("alias", Str("Voice alias (default voice when empty)"))), WriteHints,
            async (a, ct) => await ops.SpeakAsync(ReqStr(a, "text"), OptStr(a, "alias"), ct).ConfigureAwait(false)),
        new("set_viewer_voice", "Give a viewer a voice", "Make a viewer's chat messages read with a voice alias (as !voice does).",
            Schema(["platform", "user", "alias"], ("platform", Str(PlatformRef)), ("user", Str(ViewerRef)), ("alias", Str("Voice alias"))), WriteHints,
            (a, _) => Done(ops.SetViewerVoice(ReqStr(a, "platform"), ReqStr(a, "user"), ReqStr(a, "alias")))),

        new("get_feature_settings", "Get feature settings",
            "Settings of a feature page: points (names, how they are earned), media (request command, cost, length limit...), "
            + "polls (vote and bet commands) or viewers (first-words reset...).",
            Schema(["feature"], ("feature", Enum("points", "media", "polls", "viewers"))), ReadOnlyHints,
            (a, _) => Done(ops.FeatureSettings(ReqStr(a, "feature")))),
        new("update_feature_settings", "Change feature settings", "Change settings of points, media, polls or viewers (keys from get_feature_settings).",
            Schema(["feature", "changes"], ("feature", Enum("points", "media", "polls", "viewers")),
                ("changes", new JsonObject { ["type"] = "object", ["description"] = "Fields to change" })), WriteHints,
            (a, _) => Done(ops.UpdateFeatureSettings(ReqStr(a, "feature"), a["changes"] as JsonObject ?? throw Missing("changes")))),

        new("get_stream_totals", "Get stream totals",
            "This stream's totals (follows, subs, tips, bits... counts and amounts per stream, week, month and all time, latest "
            + "entries, recent lists, top donations and donators) and goals, as labels and goal widgets show them; info says when "
            + "this stream started, how the totals reset and how many events are stored.",
            Schema([]), ReadOnlyHints, (_, _) => Done(ops.StreamTotals())),
        new("set_stream_totals", "Change stream totals",
            "reset this stream's totals; setGoal (type, amount); set a count or amount by hand (type, period session|week|month|total, "
            + "count, amount); setLatest (type, name, amount; empty name clears the latest entry); setTopDonation (type, period "
            + "session|weekly|monthly|alltime, name, amount; empty name clears it); setDonator (type, period, name, amount; 0 removes "
            + "them from the top donator board); removeRecent (type, or empty for the list of every type; index 0 = newest); "
            + "setResetMode (mode afterOffline or manual); setCountTestEvents (enabled: whether test and emulated events count in "
            + "week, month and all-time totals). To take a single event back out, use delete_stream_events instead: the totals "
            + "are then recalculated.",
            Schema(["command"],
                ("command", Enum("reset", "setGoal", "set", "setLatest", "setTopDonation", "setDonator", "removeRecent", "setResetMode",
                    "setCountTestEvents")),
                ("type", Str("follower, subscriber, tip, cheer, raid, superchat, kicks...")),
                ("period", Str("set: session, week, month or total; setTopDonation/setDonator: session, weekly, monthly or alltime")),
                ("count", Num("For set")), ("amount", Num("For setGoal, set, setLatest, setTopDonation, setDonator")),
                ("name", Str("For setLatest, setTopDonation, setDonator")), ("index", Int("For removeRecent")),
                ("mode", Enum("afterOffline", "manual")), ("enabled", Bool("For setCountTestEvents")),
                Confirm()), new McpToolHints(ReadOnly: false, Destructive: true),
            (a, _) => Done(ops.StreamTotalsCommand(ReqStr(a, "command"), a)))
        {
            Confirmation = a => ReqStr(a, "command") switch
            {
                "reset" => "Reset this stream's totals (follows, subs, tips...)",
                "set" => $"Set the {OptStr(a, "type")} total for {OptStr(a, "period")} by hand",
                "setLatest" => string.IsNullOrWhiteSpace(OptStr(a, "name")) ? $"Clear the latest {OptStr(a, "type")}" : $"Replace the latest {OptStr(a, "type")}",
                "setTopDonation" => string.IsNullOrWhiteSpace(OptStr(a, "name"))
                    ? $"Clear the {OptStr(a, "period")} top {OptStr(a, "type")}" : $"Replace the {OptStr(a, "period")} top {OptStr(a, "type")}",
                "setDonator" => $"Change {OptStr(a, "name")}'s {OptStr(a, "period")} {OptStr(a, "type")} total",
                "removeRecent" => "Remove an entry from a recent list",
                _ => null,
            },
        },
        new("list_stream_events", "List stored stream events",
            "The events behind the stream totals, newest first: id, type, name, amount, platform, time, whether it is a test, "
            + "whether it counts in the week/month/all-time totals and in this stream. Filters: type, platform, name, from, to "
            + "(ISO dates), testOnly, limit (default 100). Events from before Cuebot kept this list are not in it; change those "
            + "totals with set_stream_totals.",
            Schema([], ("type", Str("follower, subscriber, tip, cheer...")), ("platform", Str("twitch, kick, youtube, StreamElements...")),
                ("name", Str("Part of a name")), ("from", Str("ISO date and time")), ("to", Str("ISO date and time")),
                ("testOnly", Bool("Only test and emulated events")), ("limit", Int("At most this many (default 100)"))),
            ReadOnlyHints, (a, _) => Done(ops.StreamEvents(a))),
        new("update_stream_event", "Edit a stored stream event",
            "Change a stored event's name, amount or test mark (id from list_stream_events); every total, latest entry, recent "
            + "list and leaderboard it counted in is recalculated.",
            Schema(["id"], ("id", Str("The event's id")), ("name", Str("New name")), ("amount", Num("New amount")),
                ("isTest", Bool("Mark it as a test event (left out of lasting totals) or as real"))),
            WriteHints, (a, _) => Done(ops.UpdateStreamEvent(ReqStr(a, "id"), a))),
        new("delete_stream_events", "Remove stored stream events",
            "Take events out of the stream totals and recalculate everything they counted in: one by id, or every one matching "
            + "the filter (testOnly: true removes all test and emulated events; from/to remove those between two dates). "
            + "all: true with no filter removes every stored event.",
            Schema([], ("id", Str("One event's id")), ("type", Str("follower, subscriber, tip, cheer...")), ("platform", Str("Platform")),
                ("name", Str("Part of a name")), ("from", Str("ISO date and time")), ("to", Str("ISO date and time")),
                ("testOnly", Bool("Only test and emulated events")), ("all", Bool("Every stored event (only with no other filter)")), Confirm()),
            DestructiveHints, (a, _) => Done(ops.DeleteStreamEvents(a)))
        {
            Confirmation = a => OptStr(a, "id") is { Length: > 0 } id
                ? $"Remove the stored event {id} from the stream totals"
                : OptBool(a, "testOnly") == true ? "Remove every test event from the stream totals" : "Remove the matching events from the stream totals",
        },

        new("list_subathon_history", "List subathon timer and goal history",
            "Every change to subathon timers (seconds) and goal ladders (points), newest first: id, timer or goal, time, kind "
            + "(Event, Manual, Set), name, source (Twitch, Throne, Ko-fi... or By hand), what it was, money amount, the value it "
            + "added, whether it was removed, whether it is a test, and whether correcting it still changes the timer or goal "
            + "(not after a reset). Also totals by source. Filters: owner (timer or goal name or id), from, to (ISO dates), name, "
            + "source, testOnly, includeRemoved (default true), limit (default 100).",
            Schema([], ("owner", Str("Timer or goal name or id")), ("from", Str("ISO date and time")), ("to", Str("ISO date and time")),
                ("name", Str("Part of a name")), ("source", Str("Twitch, Throne, Ko-fi, By hand...")), ("testOnly", Bool("Only test and emulated changes")),
                ("includeRemoved", Bool("Include removed changes (default true)")), ("limit", Int("At most this many (default 100)"))),
            ReadOnlyHints, (a, _) => Done(ops.SubathonHistory(a))),
        new("correct_subathon_history", "Correct a subathon history entry",
            "Fix one change (id from list_subathon_history): remove: true takes it back off its timer or goal (time that already "
            + "ran cannot be taken back), restore: true puts it back, value sets the seconds or points it added, amount sets the "
            + "money it was worth (the value follows at the same rate), note adds a note. The timer or goal changes to match "
            + "unless the entry is from before its last reset.",
            Schema(["id"], ("id", Str("The entry's id")), ("remove", Bool("Take it back")), ("restore", Bool("Put a removed entry back")),
                ("value", Num("New seconds or points")), ("amount", Num("New money amount")), ("note", Str("A note"))),
            WriteHints, (a, _) => Done(ops.CorrectSubathonHistory(ReqStr(a, "id"), a))),
        new("delete_subathon_history", "Take back subathon history entries",
            "Take every change matching the filter off its timer or goal (testOnly: true takes back all test and emulated "
            + "changes). all: true with no filter takes back everything.",
            Schema([], ("owner", Str("Timer or goal name or id")), ("from", Str("ISO date and time")), ("to", Str("ISO date and time")),
                ("name", Str("Part of a name")), ("source", Str("Source")), ("testOnly", Bool("Only test and emulated changes")),
                ("all", Bool("Everything (only with no other filter)")), Confirm()),
            DestructiveHints, (a, _) => Done(ops.RemoveSubathonHistory(a)))
        {
            Confirmation = a => OptBool(a, "testOnly") == true
                ? "Take every test change off the subathon timers and goals"
                : "Take the matching changes off the subathon timers and goals",
        },

        new("request_feature", "Request a feature",
            "File a feature request for Cuebot's developers as a PUBLIC GitHub issue (labelled enhancement), for something the streamer "
            + "wants that Cuebot cannot do. Only after checking with the other tools that it really cannot (no trigger, sub-action, "
            + "setting or workaround), and only when the streamer agrees to send one. Write it so a developer understands it without "
            + "this conversation: what the streamer wants to do, what they tried or what Cuebot lacks, and a suggestion if there is "
            + "one. Never include names, channel names, links to their accounts, tokens or other private details. Cuebot always "
            + "shows the streamer the full text with Approve and Deny first.",
            Schema(["title", "body"],
                ("title", Str("Short summary, e.g. VTube Studio: throw items from the top (5 to 120 characters)")),
                ("body", Str("The request in Markdown: what they want, why Cuebot cannot do it now, a suggestion (40 to 6000 characters)")),
                Confirm()),
            new McpToolHints(ReadOnly: false, OpenWorld: true),
            async (a, ct) => await ops.RequestFeatureAsync(ReqStr(a, "title"), ReqStr(a, "body"), ct).ConfigureAwait(false))
        {
            AlwaysConfirm = true,
            Confirmation = a => $"File this feature request as a public GitHub issue:\n\n{ReqStr(a, "title").Trim()}\n\n{ReqStr(a, "body").Trim()}",
        },

        new("list_hotkeys", "List hotkeys", "Global hotkeys (keyboard shortcuts that run actions) and their groups.", Schema([]), ReadOnlyHints,
            (_, _) => Done(ops.Hotkeys())),
        new("save_hotkey", "Create or change a hotkey",
            "A global hotkey that runs an action: {id (to change one), name, gesture (e.g. Ctrl+Shift+F1), action (id or name), enabled, group}.",
            Schema(["hotkey"], ("hotkey", new JsonObject { ["type"] = "object" })), WriteHints,
            async (a, _) => await ops.SaveHotkeyAsync(a["hotkey"] as JsonObject ?? throw Missing("hotkey")).ConfigureAwait(false)),
        new("delete_hotkey", "Delete a hotkey", "Delete a hotkey.", Schema(["hotkey"], ("hotkey", Str("Hotkey id or name")), Confirm()), DestructiveHints,
            async (a, _) => await ops.DeleteHotkeyAsync(ReqStr(a, "hotkey")).ConfigureAwait(false))
        {
            Confirmation = a => $"Delete the hotkey '{ReqStr(a, "hotkey")}'",
        },

        new("list_decks", "List decks", "Web decks (button pages for a phone or tablet that run actions).", Schema([]), ReadOnlyHints, (_, _) => Done(ops.Decks())),
        new("get_deck", "Get deck", "A deck's full definition: pages, buttons, their actions and looks.", Schema(["deck"], ("deck", Str("Deck id or name"))), ReadOnlyHints,
            (a, _) => Done(ops.GetDeck(ReqStr(a, "deck")))),
        new("save_deck", "Create or change a deck", "Create a deck or replace one (the full definition, in get_deck's shape). A button's action is the action's name, as list_actions shows it.",
            Schema(["deck"], ("deck", new JsonObject { ["type"] = "object" })), WriteHints, (a, _) => Done(ops.SaveDeck(a["deck"] as JsonObject ?? throw Missing("deck")))),
        new("delete_deck", "Delete a deck", "Delete a deck.", Schema(["deck"], ("deck", Str("Deck id or name")), Confirm()), DestructiveHints,
            (a, _) => Done(ops.DeleteDeck(ReqStr(a, "deck"))))
        {
            Confirmation = a => $"Delete the deck '{ReqStr(a, "deck")}'",
        },
    ];
}
