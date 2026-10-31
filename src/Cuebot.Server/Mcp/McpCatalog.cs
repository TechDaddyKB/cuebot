using System.Text.Json.Nodes;
using Cuebot.Server.Api;

namespace Cuebot.Server.Mcp;

/// <summary>MCP resources (cuebot://...) and prompts.</summary>
public sealed class McpCatalog(ApiOperations ops, ServerServices services)
{
    public const string ActionsUri = "cuebot://actions";
    public const string SubActionsUri = "cuebot://catalog/subactions";
    public const string TriggersUri = "cuebot://catalog/triggers";
    public const string VariablesUri = "cuebot://docs/variables";
    public const string ActionsDocUri = "cuebot://docs/actions";
    public const string CapabilitiesUri = "cuebot://docs/capabilities";
    public const string OverlaysDocUri = "cuebot://docs/overlays";
    private const int MaxListedActions = 200;

    // ---- resources -------------------------------------------------------------------------------------------------

    public JsonObject ListResources()
    {
        var list = new JsonArray
        {
            Resource(ActionsUri, "actions", "Actions", "Every action (summary)", "application/json"),
            Resource(SubActionsUri, "subaction-catalog", "Sub-action catalogue", "Every sub-action type with parameters and JSON Schemas", "application/json"),
            Resource(TriggersUri, "trigger-catalog", "Trigger catalogue", "Every trigger type with settings and JSON Schemas", "application/json"),
            Resource(VariablesUri, "variables", "Variables guide", "How %arguments%, ~globals~, formats and $functions()$ work", "text/markdown"),
            Resource(ActionsDocUri, "actions-guide", "Actions guide", "How actions, triggers, sub-actions and branches fit together", "text/markdown"),
            Resource(CapabilitiesUri, "capabilities", "What Cuebot can do over MCP", "Which tools to use for which job, across every part of Cuebot", "text/markdown"),
            Resource(OverlaysDocUri, "overlays-guide", "Overlays and alerts guide", "Building overlays, widgets and alert box designs", "text/markdown"),
        };
        foreach (var action in services.Profile.Snapshot.Actions.Take(MaxListedActions))
        {
            list.Add(Resource($"{ActionsUri}/{action.Id}", $"action-{action.Id}", $"Action: {action.Name}",
                $"Definition of the action '{action.Name}'", "application/json"));
        }
        return new JsonObject { ["resources"] = list };
    }

    public static JsonObject ListResourceTemplates() => new()
    {
        ["resourceTemplates"] = new JsonArray(new JsonObject
        {
            ["uriTemplate"] = ActionsUri + "/{id}",
            ["name"] = "action",
            ["title"] = "Action by id",
            ["description"] = "The full definition of one action (id from cuebot://actions)",
            ["mimeType"] = "application/json",
        }),
    };

    /// <summary>The resource contents, or null for an unknown URI. Throws <see cref="ApiException"/> for a missing action.</summary>
    public JsonObject? ReadResource(string uri)
    {
        var trimmed = uri.Trim().TrimEnd('/');
        (string Mime, string Text)? content = trimmed.ToLowerInvariant() switch
        {
            ActionsUri => Json(ops.ListActions()),
            SubActionsUri => Json(new JsonObject
            {
                ["subActions"] = new JsonArray([.. (services.Registry?.SubActions ?? []).Select(h => (JsonNode)CatalogJson.SubAction(h, detailed: true))]),
            }),
            TriggersUri => Json(new JsonObject
            {
                ["triggers"] = new JsonArray([.. (services.Registry?.Triggers ?? []).Select(t => (JsonNode)CatalogJson.Trigger(t, detailed: true))]),
            }),
            VariablesUri => ("text/markdown", VariablesGuide),
            ActionsDocUri => ("text/markdown", ActionsGuide),
            CapabilitiesUri => ("text/markdown", CapabilitiesGuide),
            OverlaysDocUri => ("text/markdown", OverlaysGuide),
            _ when trimmed.StartsWith(ActionsUri + "/", StringComparison.OrdinalIgnoreCase) =>
                Json(ops.GetAction(Uri.UnescapeDataString(trimmed[(ActionsUri.Length + 1)..]))),
            _ => null,
        };
        if (content is not { } c) return null;
        return new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["uri"] = uri, ["mimeType"] = c.Mime, ["text"] = c.Text }),
        };

        static (string, string) Json(JsonNode node) => ("application/json", node.ToJsonString());
    }

    private static JsonObject Resource(string uri, string name, string title, string description, string mime) => new()
    {
        ["uri"] = uri, ["name"] = name, ["title"] = title, ["description"] = description, ["mimeType"] = mime,
    };

    // ---- prompts ---------------------------------------------------------------------------------------------------

    public static JsonObject ListPrompts() => new()
    {
        ["prompts"] = new JsonArray(
            new JsonObject
            {
                ["name"] = "create_action_from_description",
                ["title"] = "Create an action from a description",
                ["description"] = "Build a Cuebot action (triggers + sub-actions) from a plain-language description, using the catalogue tools",
                ["arguments"] = new JsonArray(
                    Argument("description", "What the action should do, e.g. 'when someone types !dice, roll 1-6 and reply in chat'", true),
                    Argument("trigger", "What should start it, if not in the description", false)),
            },
            new JsonObject
            {
                ["name"] = "troubleshoot_action",
                ["title"] = "Troubleshoot an action",
                ["description"] = "Find out why an action does not run or does the wrong thing, from its definition and recent runs",
                ["arguments"] = new JsonArray(
                    Argument("action", "Action id or name", true),
                    Argument("problem", "What goes wrong", false)),
            }),
    };

    /// <summary>The prompt messages, or null for an unknown prompt. Throws <see cref="ApiException"/> for bad arguments.</summary>
    public JsonObject? GetPrompt(string name, JsonObject arguments)
    {
        string? Arg(string key) => arguments[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;
        switch (name)
        {
            case "create_action_from_description":
            {
                var description = Arg("description") ?? throw ApiException.BadRequest("Argument 'description' is required");
                var trigger = Arg("trigger");
                var text =
                    $$"""
                    Create a Cuebot action that does the following:

                    {{description}}
                    {{(trigger is null ? "" : $"\nIt should start on: {trigger}\n")}}
                    How to build it:
                    1. Read the resources {{ActionsDocUri}} and {{VariablesUri}} (in short: %arg% = run arguments, ~name~ = global variables, $function()$ = inline functions).
                    2. Find the trigger with list_trigger_types (search words, e.g. "chat message", "command", "follow") and check its settings with describe_trigger_type.
                       For chat commands, create the command with create_command and use the "command.triggered" trigger with settings {"command": "<command name>"}.
                    3. Find each step with list_subaction_types (search words) and read its parameters with describe_subaction_type.
                       Branching steps (core.if: True/False, core.switch: one branch per case + Default, core.group: Steps) hold child steps in "branches".
                    4. Call create_action with an "action" object holding name, group, triggers and subActions. All parameter values are strings.
                    5. If create_action reports problems, fix them and try again. Mention any warnings.
                    6. Offer to test it with run_action (pass the arguments the trigger would provide, e.g. {"user": "tester", "message": "!dice"}),
                       then check the result with get_history.
                    Keep the action small and readable, give it a clear name, and do not delete or overwrite existing actions without asking.
                    """;
                return Messages("Create an action from a description", text);
            }
            case "troubleshoot_action":
            {
                var reference = Arg("action") ?? throw ApiException.BadRequest("Argument 'action' is required");
                var action = ops.GetAction(reference);
                var history = ops.History(10, reference, includeArgs: true);
                var problem = Arg("problem") ?? "It does not work as expected.";
                var text =
                    $"""
                    Help me troubleshoot the Cuebot action "{action["name"]}".

                    Problem: {problem}

                    Its definition:
                    ```json
                    {action.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })}
                    ```

                    Its most recent runs (newest first):
                    ```json
                    {history.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })}
                    ```

                    Check, in order: is the action enabled and does it have an enabled trigger whose settings match the event (describe_trigger_type)?
                    Did it run at all (no runs above = the trigger never matched)? Did a run fail (state Failed, error)? Are disabled sub-actions or
                    branches skipped? Do the %arguments% it uses exist in the run arguments (compare with "args" above), and are ~globals~ set
                    (get_globals)? Use describe_subaction_type to check parameter values. Explain the cause, propose a fix, and only change the action
                    (update_action) after I agree. You can test with run_action and check get_history.
                    """;
                return Messages($"Troubleshoot '{action["name"]}'", text);
            }
            default:
                return null;
        }
    }

    private static JsonObject Argument(string name, string description, bool required) => new()
    {
        ["name"] = name, ["description"] = description, ["required"] = required,
    };

    private static JsonObject Messages(string description, string text) => new()
    {
        ["description"] = description,
        ["messages"] = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
        }),
    };

    // ---- guides ----------------------------------------------------------------------------------------------------

    public const string VariablesGuide =
        """
        # Variables in Cuebot

        Every sub-action parameter (except code) is a text template, resolved when the step runs. Substituted values are
        never parsed again, so a chat message containing "%x%" stays literal text.

        ## %arguments%
        `%name%` inserts an argument of the current run (case-insensitive). Arguments come from the trigger (for example
        `%user%`, `%message%`, `%platform%` for chat; check the trigger with describe_trigger_type and a real run with
        get_history includeArgs=true), from sub-actions that set them ("Set argument", "Get global variable", HTTP and JSON
        steps...) and from run_action / POST /api/v1/actions/{id}/run args. Unknown names are left as they are.

        Built-ins: `%date%`, `%time%`, `%longtime%`, `%datetime%`, `%unixtime%`, `%unixtimems%`, `%filedatetime%`,
        `%lf%` `%cr%` `%crlf%` `%tab%`, `%random%` (1-100) or `%random:1-6%`, `%randomdecimal%`, `%guid%`.

        ## ~globals~
        `~name~` inserts a global variable (get_globals / set_global, or the Variables sub-actions). Persisted globals survive
        restarts; temporary ones do not.

        ## Formats
        `%name:format%` and `~name:format~` apply a .NET format: `%amount:c2%`, `%n:N0%`, `%date:yyyy-MM-dd%`, `%time:t%`.
        Append `|culture` to choose a culture: `%amount:c2|de-DE%`.

        ## $functions()$
        Inline functions run after variables, innermost first: `$math(expression)$`, `$length(text)$`, `$upper(text)$`,
        `$lower(text)$`, `$trim(text)$`, `$urlencode(text)$`, `$replace(text,find,replacement)$`,
        `$substring(text,start[,length])$`, `$pick(a,b,c...)$`, `$parse(variableName)$` (look up a variable whose name is
        built at run time).

        ## Code
        C# and Python code sub-actions are not templated: they read arguments with `Args["name"]` and call host functions
        (list_script_functions). Streamer.bot-style `CPH` methods work in C#.
        """;

    public const string CapabilitiesGuide =
        """
        # What Cuebot can do over MCP

        Almost everything the streamer does in Cuebot has a tool. Pick by job:

        | Job | Tools |
        | --- | --- |
        | Automations (when X happens, do Y) | list_trigger_types, describe_trigger_type, list_subaction_types, describe_subaction_type, create_action, update_action, test_action, run_action |
        | Do something once, right now (switch an OBS scene, set the title, time someone out, speak, change a light) | run_subactions with any sub-action type |
        | Chat commands and timers | create_command / update_command, create_timer / update_timer / set_timer_state |
        | Variables | get_globals / set_global; get_viewer_variables / set_viewer_variable |
        | Action queues | list_queues, create_queue, update_queue, control_queue |
        | Polls and predictions | start_poll, end_poll, start_prediction, control_prediction, get_polls (native Twitch/YouTube polls: run_subactions) |
        | Giveaways, points store, media requests | start_giveaway, control_giveaway; add_store_item, review_redemption; add_media_request, control_media |
        | Quotes, points, groups, viewers | list_quotes / add_quote; get_user_points / change_user_points; manage_group / set_group_member; find_viewers |
        | Overlays and widgets | list_overlays, create_overlay, add_widget, add_custom_widget, update_layer, get_widget, update_widget_settings, get_overlay_link |
        | Alert box | get_alert_designs, update_alert_designs, then emulate_event to see it |
        | Sharing, gallery, StreamElements | export_overlay_package, import_overlay_package (.cuebot-overlay files); list_gallery, import_gallery_item; list_streamelements_overlays, import_streamelements_overlay |
        | Speech | speak, get_speech_settings / update_speech_settings, list_speech_voices, save_voice_alias, set_viewer_voice, control_speech |
        | Connections | list_connections, connect_service (uses the sign-in saved in Cuebot) |
        | Hotkeys, decks | save_hotkey; get_deck / save_deck |
        | Feature settings | get_feature_settings / update_feature_settings (points, media, polls, viewers) |
        | Stream totals and goals | get_stream_totals, set_stream_totals |
        | Trying things | emulate_event (sample follow, sub, cheer, raid, tip...), test_action, get_history |
        | Sharing automations | export_bundle, preview_import, apply_import |

        Not available over MCP, on purpose: signing in to platforms, API keys and tokens, server and network settings,
        the MCP/API tokens themselves and app updates. Ask the streamer to do those in Cuebot.

        Ask before deleting or overwriting. Destructive tools may answer with a confirmationToken: show the user what will
        happen, and pass the token back only after they agree.
        """;

    public const string OverlaysGuide =
        """
        # Overlays, widgets and alerts

        An overlay is a page OBS shows as a browser source (get_overlay_link gives the link and size). It holds layers:
        Cuebot's widgets (alert box, chat box, label, goal, event list, credits, countdown, emote rain, hype cup, hype boss,
        poll, giveaway, media player, charity, products, seasonal), custom widgets (HTML/CSS/JS), and text, image, video
        and slideshow layers. list_widget_kinds lists them with the animations.

        ## Building
        1. create_overlay (1920 x 1080 unless the streamer says otherwise), or pick one with list_overlays.
        2. add_widget with a kind; settings sets its first values, place its position ({x, y, width, height}).
        3. get_widget shows every setting of a widget (type, label, options, value); update_widget_settings changes them by key.
        4. update_layer moves, resizes, hides or animates a layer (enter / exit), and changes text/image/video layer settings.
        5. Overlays open in OBS update at once.

        ## Alert box
        - get_alert_designs returns every alert type (follower, subscriber, tip, cheer, raid, superchat, kicks, redemption,
          jewels, charityCampaignDonation, merch, custom) with enabled, design and variations, plus what each design field means.
        - update_alert_designs: {"types": {"raid": {"enabled": true, "design": {"template": "{name} raided with {amount}!"}}}}.
          design fields are merged; variations (when given) replace the list. A variation is
          {name, param (amount, tier, platform, name, gifted, bulk, currency, message), op (atLeast, exact, between, is, contains),
          value, value2, chance, design (only the fields that differ)}; the first match wins (randomPick picks among matches).
        - The custom type is used by actions with the "Show alert" sub-action (overlays.showAlert) for any trigger.
        - emulate_event {platform: twitch, kind: raid} shows a sample on the overlays without running actions.

        ## Alerts for other triggers
        To alert on something that is not a platform event (a channel point reward, a command, a hotkey), create an action
        with that trigger and an "overlays.showAlert" sub-action (describe_subaction_type overlays.showAlert), and design the
        alert box's "custom" type (or name an existing type in the sub-action).

        ## Custom widgets
        add_custom_widget takes StreamElements-style panes: html, css, js, fields (JSON text: key -> {type, label, value})
        and data. {key} placeholders in the panes are filled from the fields. The widget gets onWidgetLoad and
        onEventReceived like on StreamElements.
        """;

    public const string ActionsGuide =
        """
        # Actions in Cuebot

        An action has triggers and a list of sub-actions. Any enabled trigger starts it; its sub-actions then run in
        order with the run's arguments.

        - Triggers: `{"type": "<trigger type id>", "enabled": true, "settings": {"key": "value"}}`. Empty settings usually
          mean "any". list_trigger_types / describe_trigger_type show the types and settings.
        - Sub-actions: `{"type": "<sub-action type id>", "enabled": true, "parameters": {"key": "value"}}`. All values are
          strings ("true", "10"). list_subaction_types / describe_subaction_type show the types and parameters.
        - Branches: steps such as `core.if` (True / False), `core.switch` (one branch per case line + Default),
          `core.while` (Body) and `core.group` (Steps) hold child steps: `"branches": {"True": [ ...steps... ]}`. The
          chosen branch runs, then execution continues after the step.
        - Queues: `"queue": "<name>"` makes runs wait in a named queue; empty uses the Default queue. `"concurrent": true`
          lets runs of the same action overlap.
        - Chat commands are separate objects (create_command); an action runs for one with the trigger
          `{"type": "command.triggered", "settings": {"command": "<command name>"}}`.
        - Timers are separate objects (create_timer); an action runs on one with
          `{"type": "core.timer", "settings": {"timer": "<timer name>"}}`.
        - Test with run_action and read the result with get_history (includeArgs=true shows the arguments).
        """;
}
