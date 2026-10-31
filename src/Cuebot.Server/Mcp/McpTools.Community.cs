using System.Text.Json.Nodes;
using Cuebot.Server.Api;

namespace Cuebot.Server.Mcp;

public static partial class McpTools
{
    private static readonly McpToolHints ReadOnlyHints = new(ReadOnly: true, Idempotent: true);
    private static readonly McpToolHints WriteHints = new(ReadOnly: false);
    private static readonly McpToolHints DestructiveHints = new(ReadOnly: false, Destructive: true, Idempotent: true);

    private const string PlatformRef = "Platform: twitch, youtube, kick, rumble...";
    private const string ViewerRef = "The viewer's user name (or user id) on that platform";

    /// <summary>Running things now, testing, sample events and action queues.</summary>
    private static IEnumerable<McpTool> AutomationTools(ApiOperations ops) =>
    [
        new("run_subactions", "Run sub-actions now",
            "Run one or more sub-actions once, right now, without saving an action: anything in list_subaction_types (switch an OBS "
            + "scene, set the stream title, speak text, time out a viewer, change a light, play a sound...). Same shape as an action's "
            + "subActions (describe_subaction_type for the parameters). Waits for the result. To make it repeatable, create an action instead.",
            Schema(["subActions"],
                ("subActions", new JsonObject
                {
                    ["type"] = "array", ["description"] = "Sub-actions as in create_action, e.g. [{\"type\": \"chat.send\", \"parameters\": {\"message\": \"hi\"}}]",
                    ["items"] = new JsonObject { ["type"] = "object" },
                }),
                ("args", new JsonObject { ["type"] = "object", ["description"] = "Arguments (%name%) the sub-actions can use" }),
                ("queue", Str("Action queue to run in (default queue when empty)")),
                ("timeoutSeconds", Num("How long to wait (default 30, max 300)")), Confirm()),
            new McpToolHints(ReadOnly: false, Destructive: true, OpenWorld: true),
            async (a, ct) => await ops.RunSubActionsAsync(a["subActions"], a["args"], OptStr(a, "queue"), OptNum(a, "timeoutSeconds") ?? 30, "MCP", ct)
                .ConfigureAwait(false))
        {
            // Checked first, so a mistake comes back at once instead of after the streamer confirmed.
            Confirmation = a => "Run " + string.Join(", ", ops.ValidateSubActions(a["subActions"])),
        },
        new("test_action", "Test action",
            "Run a saved action as if one of its triggers fired (isTest is set), with that trigger's sample arguments plus your own. "
            + "Good for checking a new action works.",
            Schema(["action"], ("action", Str(ActionRef)), ("trigger", Str("Trigger type id or its number from 1 (default the first)")),
                ("args", new JsonObject { ["type"] = "object", ["description"] = "Extra or overriding arguments, e.g. {\"user\": \"tester\"}" })),
            new McpToolHints(ReadOnly: false, OpenWorld: true),
            async (a, ct) => await ops.TestTriggerAsync(ReqStr(a, "action"), OptStr(a, "trigger"), a["args"], ct).ConfigureAwait(false)),
        new("list_emulated_events", "List sample events", "The sample platform events emulate_event can send (follow, sub, cheer, raid, tip, chat...).",
            Schema([]), ReadOnlyHints, (_, _) => Done(ApiOperations.EmulatableEvents())),
        new("emulate_event", "Send a sample event",
            "Send a sample platform event with a made-up viewer, e.g. a Twitch follow to test the alert box. target \"overlays\" (default) shows "
            + "it on every overlay only; \"everything\" publishes it as a real event so matching actions run too (they may post to chat).",
            Schema(["platform", "kind"], ("platform", Str("twitch, kick, rumble, youtube or donations")), ("kind", Str("follow, sub, resub, gift, cheer, raid, tip, superchat, kicks or chat")),
                ("target", Enum("overlays", "everything"))),
            WriteHints, (a, _) => Done(ops.Emulate(ReqStr(a, "platform"), ReqStr(a, "kind"), OptStr(a, "target")))),

        // ---- queues ----
        new("list_queues", "List action queues", "Action queues with their settings and live state (blocking, paused, waiting, running).",
            Schema([]), ReadOnlyHints, (_, _) => Done(ops.Queues())),
        new("create_queue", "Create action queue",
            "Add an action queue; actions join it with their \"queue\" field. A blocking queue runs its actions one at a time in order.",
            Schema(["name"], ("name", Str("Queue name")), ("blocking", Bool("One at a time (default false)"))), WriteHints,
            async (a, _) => await ops.CreateQueueAsync(ReqStr(a, "name"), OptBool(a, "blocking") ?? false).ConfigureAwait(false)),
        new("update_queue", "Update action queue", "Rename a queue (its actions move with it) or change whether it is blocking.",
            Schema(["queue"], ("queue", Str("Queue id or name")), ("name", Str("New name")), ("blocking", Bool("One at a time"))), WriteHints,
            async (a, _) => await ops.UpdateQueueAsync(ReqStr(a, "queue"), OptStr(a, "name"), OptBool(a, "blocking")).ConfigureAwait(false)),
        new("delete_queue", "Delete action queue", "Delete a queue; its actions move to the default queue.",
            Schema(["queue"], ("queue", Str("Queue id or name")), Confirm()), DestructiveHints,
            async (a, _) => await ops.DeleteQueueAsync(ReqStr(a, "queue")).ConfigureAwait(false))
        {
            Confirmation = a => $"Delete the action queue '{ReqStr(a, "queue")}' (its actions move to the default queue)",
        },
        new("control_queue", "Control action queue",
            "pause (waiting runs stay waiting), resume, or clear (cancel the waiting runs) one queue, or every queue with queue \"all\".",
            Schema(["queue", "command"], ("queue", Str("Queue name, \"Default\" or \"all\"")), ("command", Enum("pause", "resume", "clear"))), WriteHints,
            (a, _) => Done(ops.QueueControl(ReqStr(a, "queue"), ReqStr(a, "command")))),
    ];

    /// <summary>Polls, predictions, giveaways, the points store and media requests.</summary>
    private static IEnumerable<McpTool> EngagementTools(ApiOperations ops) =>
    [
        new("get_polls", "Get polls",
            "The latest poll and prediction from each source (Twitch, YouTube, Cuebot chat polls) and Cuebot's vote/bet commands.",
            Schema([]), ReadOnlyHints, (_, _) => Done(ops.Polls())),
        new("start_poll", "Start a chat poll",
            "Start a Cuebot chat poll that viewers on every platform vote in (!vote 2 or the choice's name); the Poll widget shows it. "
            + "For a native Twitch or YouTube poll use run_subactions with that platform's poll sub-action instead.",
            Schema(["title", "choices"], ("title", Str("The question")), ("choices", StrArray("2 to 10 choices")),
                ("durationSeconds", Int("How long it runs (default 60; 0 = until end_poll)"))), WriteHints,
            (a, _) => Done(ops.ChatPollCommand("startPoll", new JsonObject
            {
                ["title"] = ReqStr(a, "title"), ["choices"] = a["choices"]?.DeepClone(), ["duration"] = OptNum(a, "durationSeconds") ?? 60,
            }))),
        new("end_poll", "End the chat poll", "End the running Cuebot chat poll now and show the result.", Schema([]), WriteHints,
            (_, _) => Done(ops.ChatPollCommand("endPoll", []))),
        new("start_prediction", "Start a points prediction",
            "Start a Cuebot prediction: viewers bet Cuebot points on an outcome (!bet 1 100) until it locks.",
            Schema(["title", "outcomes"], ("title", Str("The question")), ("outcomes", StrArray("2 to 10 outcomes")),
                ("windowSeconds", Int("Betting window (default 120)")), ("currency", Str("Points currency (default the main one)"))), WriteHints,
            (a, _) => Done(ops.ChatPollCommand("startPrediction", new JsonObject
            {
                ["title"] = ReqStr(a, "title"), ["outcomes"] = a["outcomes"]?.DeepClone(), ["window"] = OptNum(a, "windowSeconds") ?? 120,
                ["currency"] = OptStr(a, "currency") ?? "",
            }))),
        new("control_prediction", "Lock, resolve or cancel the prediction",
            "lock stops betting; resolve pays out to the winner (outcome number from 1 or its title); cancel refunds every bet.",
            Schema(["command"], ("command", Enum("lock", "resolve", "cancel")), ("winner", Str("For resolve: outcome number from 1 or title"))),
            WriteHints,
            (a, _) => Done(ops.ChatPollCommand(ReqStr(a, "command"), new JsonObject { ["winner"] = OptStr(a, "winner") ?? "" }))),

        new("get_giveaway", "Get giveaway", "The current (or last) giveaway: options, entries, winners.", Schema([]), ReadOnlyHints, (_, _) => Done(ops.Giveaway())),
        new("start_giveaway", "Start a giveaway",
            "Open a giveaway viewers on every platform enter with a chat command; the Giveaway widget shows it.",
            Schema(["title"], ("title", Str("The prize")), ("command", Str("Entry command (default !join)")),
                ("ticketCost", Int("Points per ticket (default 0)")), ("maxTickets", Int("Tickets per viewer (default 1)")),
                ("currency", Str("Points currency")), ("subscriberLuck", Num("Subscribers' chance multiplier (default 1)")),
                ("subscribersOnly", Bool("Only subscribers may enter")), ("seconds", Int("Close automatically after this many seconds (0 = manually)")),
                ("winners", Int("How many winners to draw (default 1)"))), WriteHints,
            (a, _) => Done(ops.GiveawayCommand("open", (JsonObject)a.DeepClone()))),
        new("control_giveaway", "Close, draw or cancel the giveaway",
            "close stops entries; draw picks the winners; redraw picks one more; cancel ends it and refunds tickets.",
            Schema(["command"], ("command", Enum("close", "draw", "redraw", "cancel"))), WriteHints,
            (a, _) => Done(ops.GiveawayCommand(ReqStr(a, "command"), []))),

        new("get_store", "Get the points store", "Store items, pending redemptions waiting for approval, and recent ones.", Schema([]), ReadOnlyHints,
            (_, _) => Done(ops.Store())),
        new("add_store_item", "Add a store item",
            "Add an item viewers buy with Cuebot points.",
            Schema(["name", "cost"], ("name", Str("Item name")), ("description", Str("What it is")), ("cost", Int("Price in points")),
                ("currency", Str("Points currency (default the main one)")), ("stock", Int("How many (-1 = unlimited)")),
                ("needsApproval", Bool("A moderator approves each redemption")), ("subscribersOnly", Bool("Only subscribers")),
                ("userCooldownSeconds", Int("Per-viewer cooldown")), ("globalCooldownSeconds", Int("Cooldown for everyone")),
                ("enabled", Bool("Default true"))), WriteHints,
            (a, _) => Done(ops.AddStoreItem((JsonObject)a.DeepClone()))),
        new("update_store_item", "Update a store item", "Change the given fields of a store item.",
            Schema(["item", "changes"], ("item", Str("Item id or name")), ("changes", new JsonObject { ["type"] = "object", ["description"] = "Fields as in add_store_item" })),
            WriteHints, (a, _) => Done(ops.UpdateStoreItem(ReqStr(a, "item"), a["changes"] as JsonObject ?? throw Missing("changes")))),
        new("delete_store_item", "Delete a store item", "Delete a store item.",
            Schema(["item"], ("item", Str("Item id or name")), Confirm()), DestructiveHints, (a, _) => Done(ops.DeleteStoreItem(ReqStr(a, "item"))))
        {
            Confirmation = a => $"Delete the store item '{ReqStr(a, "item")}'",
        },
        new("review_redemption", "Approve or reject a redemption", "Approve a pending store redemption, or reject it (refunds the points).",
            Schema(["id", "command"], ("id", Str("Redemption id (get_store)")), ("command", Enum("approve", "reject"))), WriteHints,
            (a, _) => Done(ops.ReviewRedemption(ReqStr(a, "id"), ReqStr(a, "command")))),

        new("get_media_requests", "Get media requests", "The media request player: what plays, the queue, and whether requests are open.",
            Schema([]), ReadOnlyHints, (_, _) => Done(ops.Media())),
        new("add_media_request", "Add a media request", "Add a YouTube video to the media request queue (free, as the streamer).",
            Schema(["link"], ("link", Str("YouTube link or video id")), ("user", Str("Who asked (default API)"))), new McpToolHints(ReadOnly: false, OpenWorld: true),
            async (a, ct) => await ops.AddMediaAsync((JsonObject)a.DeepClone(), ct).ConfigureAwait(false)),
        new("control_media", "Control media requests", "skip the video playing, pause, resume, clear the queue (refunds), open or close requests.",
            Schema(["command"], ("command", Enum("skip", "pause", "resume", "clear", "open", "close"))), WriteHints,
            (a, _) => Done(ops.MediaCommand(ReqStr(a, "command")))),
        new("remove_media_request", "Remove a media request", "Remove a waiting request (refunded).",
            Schema(["id"], ("id", Str("Request id (get_media_requests)"))), WriteHints, (a, _) => Done(ops.RemoveMedia(ReqStr(a, "id")))),
    ];

    /// <summary>Quotes, points, user groups, viewers and their variables.</summary>
    private static IEnumerable<McpTool> CommunityTools(ApiOperations ops) =>
    [
        new("list_quotes", "List quotes", "Quotes, newest first; search matches the text, who said it or the game.",
            Schema([], ("search", Str("Text to look for")), ("limit", Int("Maximum quotes (default 50)"))), ReadOnlyHints,
            (a, _) => Done(ops.Quotes(OptStr(a, "search"), (int?)OptNum(a, "limit") ?? 50))),
        new("add_quote", "Add a quote", "Add a quote (shown by !quote).",
            Schema(["text"], ("text", Str("The quote")), ("user", Str("Who said it")), ("game", Str("Game or category (default the current one)")),
                ("platform", Str("Platform"))), WriteHints,
            (a, _) => Done(ops.AddQuote(ReqStr(a, "text"), OptStr(a, "user"), OptStr(a, "game"), OptStr(a, "platform")))),
        new("update_quote", "Update a quote", "Change a quote's text, who said it, the game or the platform.",
            Schema(["id"], ("id", Int("Quote number")), ("text", Str("New text")), ("user", Str("Who said it")), ("game", Str("Game")), ("platform", Str("Platform"))),
            WriteHints, (a, _) => Done(ops.UpdateQuote(QuoteId(a), OptStr(a, "text"), OptStr(a, "user"), OptStr(a, "game"), OptStr(a, "platform")))),
        new("delete_quote", "Delete a quote", "Delete a quote.", Schema(["id"], ("id", Int("Quote number")), Confirm()), DestructiveHints,
            (a, _) => Done(ops.DeleteQuote(QuoteId(a))))
        {
            Confirmation = a => $"Delete quote #{QuoteId(a)}",
        },

        new("list_points_currencies", "List points currencies", "Cuebot points currencies: the main one and any extra ones, with how they are earned.",
            Schema([]), ReadOnlyHints, (_, _) => Done(ops.PointsCurrencies())),
        new("get_user_points", "Get a viewer's points", "A viewer's balance and rank.",
            Schema(["platform", "user"], ("platform", Str(PlatformRef)), ("user", Str(ViewerRef)), ("currency", Str("Currency (default the main one)"))),
            ReadOnlyHints, (a, _) => Done(ops.UserPoints(ReqStr(a, "platform"), ReqStr(a, "user"), OptStr(a, "currency")))),
        new("change_user_points", "Change a viewer's points", "add, remove or set a viewer's points.",
            Schema(["platform", "user", "command", "amount"], ("platform", Str(PlatformRef)), ("user", Str(ViewerRef)), ("command", Enum("add", "remove", "set")),
                ("amount", Int("Points")), ("currency", Str("Currency (default the main one)"))),
            WriteHints,
            (a, _) => Done(ops.ChangeUserPoints(ReqStr(a, "platform"), ReqStr(a, "user"), ReqStr(a, "command"), (long)(OptNum(a, "amount") ?? throw Missing("amount")),
                OptStr(a, "currency")))),
        new("points_leaderboard", "Points leaderboard", "Top viewers by points.",
            Schema([], ("platform", Str("Only this platform")), ("count", Int("How many (default 10)")), ("currency", Str("Currency (default the main one)"))),
            ReadOnlyHints, (a, _) => Done(ops.PointsLeaderboard(OptStr(a, "platform"), (int?)OptNum(a, "count") ?? 10, OptStr(a, "currency")))),

        new("list_groups", "List user groups", "User groups and their members (used by command permissions, points bonuses and sub-actions).",
            Schema([]), ReadOnlyHints, (_, _) => Done(ops.Groups())),
        new("manage_group", "Create, rename, clear or delete a user group", "create, rename (to newName), clear (remove every member) or delete a group.",
            Schema(["group", "command"], ("group", Str("Group name")), ("command", Enum("create", "rename", "clear", "delete")), ("newName", Str("For rename")),
                Confirm()), new McpToolHints(ReadOnly: false, Destructive: true),
            (a, _) => Done(ops.GroupCommand(ReqStr(a, "group"), ReqStr(a, "command"), OptStr(a, "newName"))))
        {
            Confirmation = a => ReqStr(a, "command") is "delete" or "clear" ? $"{ReqStr(a, "command")} the user group '{ReqStr(a, "group")}'"
                : null,
        },
        new("set_group_member", "Add or remove a group member", "Add a viewer to a group (made if needed) or remove them.",
            Schema(["group", "platform", "user", "member"], ("group", Str("Group name")), ("platform", Str(PlatformRef)), ("user", Str(ViewerRef)),
                ("member", Bool("true to add, false to remove"))), WriteHints,
            (a, _) => Done(ops.GroupMember(ReqStr(a, "group"), ReqStr(a, "platform"), ReqStr(a, "user"), OptBool(a, "member") ?? throw Missing("member")))),

        new("find_viewers", "Find viewers", "Viewers Cuebot has seen in chat (most recent first): name, platform, roles, first/last seen, messages.",
            Schema([], ("platform", Str("Only this platform")), ("search", Str("Name contains")), ("limit", Int("Maximum (default 25)"))), ReadOnlyHints,
            (a, _) => Done(ops.Viewers(OptStr(a, "platform"), OptStr(a, "search"), (int?)OptNum(a, "limit") ?? 25))),
        new("get_viewer_variables", "Get a viewer's variables", "Variables stored for one viewer (as the user-variable sub-actions read them).",
            Schema(["platform", "user"], ("platform", Str(PlatformRef)), ("user", Str(ViewerRef))), ReadOnlyHints,
            (a, _) => Done(ops.ViewerVariables(ReqStr(a, "platform"), ReqStr(a, "user")))),
        new("set_viewer_variable", "Set a viewer's variable", "Create or change a variable for one viewer.",
            Schema(["platform", "user", "name", "value"], ("platform", Str(PlatformRef)), ("user", Str(ViewerRef)), ("name", Str("Variable name")),
                ("value", new JsonObject { ["type"] = new JsonArray("string", "number", "boolean", "null"), ["description"] = "The value" }),
                ("persisted", Bool("Keep across restarts (default true)"))), WriteHints,
            (a, _) => Done(ops.SetViewerVariable(ReqStr(a, "platform"), ReqStr(a, "user"), ReqStr(a, "name"), a["value"], OptBool(a, "persisted") ?? true))),
        new("delete_viewer_variable", "Delete a viewer's variable", "Delete one variable for one viewer.",
            Schema(["platform", "user", "name"], ("platform", Str(PlatformRef)), ("user", Str(ViewerRef)), ("name", Str("Variable name")), Confirm()),
            DestructiveHints, (a, _) => Done(ops.DeleteViewerVariable(ReqStr(a, "platform"), ReqStr(a, "user"), ReqStr(a, "name"))))
        {
            Confirmation = a => $"Delete {ReqStr(a, "user")}'s variable '{ReqStr(a, "name")}'",
        },
    ];

    private static int QuoteId(JsonObject args) => (int?)OptNum(args, "id") ?? throw Missing("id");
}
