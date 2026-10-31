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
            "This stream's totals (follows, subs, tips, bits... counts and amounts) and goals, as labels and goal widgets show them.",
            Schema([]), ReadOnlyHints, (_, _) => Done(ops.StreamTotals())),
        new("set_stream_totals", "Reset totals or set a goal", "reset this stream's totals, or setGoal for a type (follower, subscriber, tip, cheer...).",
            Schema(["command"], ("command", Enum("reset", "setGoal")), ("type", Str("For setGoal: follower, subscriber, tip, cheer...")), ("amount", Num("For setGoal: the goal")),
                Confirm()), new McpToolHints(ReadOnly: false, Destructive: true),
            (a, _) => Done(ops.StreamTotalsCommand(ReqStr(a, "command"), OptStr(a, "type"), OptNum(a, "amount"))))
        {
            Confirmation = a => ReqStr(a, "command") == "reset" ? "Reset this stream's totals (follows, subs, tips...)" : null,
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
        new("save_deck", "Create or change a deck", "Create a deck or replace one (the full definition, in get_deck's shape).",
            Schema(["deck"], ("deck", new JsonObject { ["type"] = "object" })), WriteHints, (a, _) => Done(ops.SaveDeck(a["deck"] as JsonObject ?? throw Missing("deck")))),
        new("delete_deck", "Delete a deck", "Delete a deck.", Schema(["deck"], ("deck", Str("Deck id or name")), Confirm()), DestructiveHints,
            (a, _) => Done(ops.DeleteDeck(ReqStr(a, "deck"))))
        {
            Confirmation = a => $"Delete the deck '{ReqStr(a, "deck")}'",
        },
    ];
}
