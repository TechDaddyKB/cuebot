using System.Text.Json.Nodes;

namespace Cuebot.Assistant;

/// <summary>
/// The tools a local model sees. Small models fail with all of Cuebot's tools (about 14K tokens of definitions): they stop
/// halfway or invent tool names. They get the tools for actions, commands and timers, and <see cref="FindTool"/>, which
/// switches on any other tool by search word for the rest of the conversation. Measured with Qwen3.5 4B/9B and Gemma 4 E4B:
/// the focused set about doubled how often a request was done right (docs/ASSISTANT.md, "Local models").
/// </summary>
internal sealed class LocalToolSet(AssistantTools tools, bool all)
{
    public const string FindTool = "find_cuebot_tools";

    /// <summary>
    /// Always offered: building, checking and deleting actions, commands and timers, and the guides. The deletes are here
    /// because without them a small model asked to delete something tended to switch it off instead (deletes still wait
    /// for the streamer's Approve).
    /// </summary>
    public static readonly string[] Core =
    [
        "list_actions", "get_action", "create_action", "update_action", "delete_action", "test_action",
        "list_commands", "get_command", "create_command", "update_command", "delete_command",
        "list_timers", "create_timer", "delete_timer",
        "list_trigger_types", "describe_trigger_type", "list_subaction_types", "describe_subaction_type",
        AssistantTools.ReadDocTool,
    ];

    private const int MaxFound = 8;

    private readonly IReadOnlyList<AssistantToolDefinition> _all = tools.List();
    private readonly HashSet<string> _active = new(all ? [] : Core, StringComparer.Ordinal);

    /// <summary>Added to the system prompt.</summary>
    public static string Instructions(bool all) => all ? "" : $"""


        You have a focused set of Cuebot's tools: actions, commands, timers and the trigger and sub-action catalogue. For
        anything else (overlays, polls, giveaways, points, quotes, speech, hotkeys, decks, connections, chat...) call
        {FindTool} with a word or two first; the tools it finds can be used from then on. Never guess a tool name or a
        trigger or sub-action type: look it up. Do exactly what the streamer asked: if you cannot, say so instead of doing
        something else (for example, do not switch an action off when they asked to delete it). To delete something,
        call the delete tool right away: Cuebot shows the streamer an Approve button. Never ask them to type "yes".
        """;

    /// <summary>The definitions to send now, in the Chat Completions "tools" shape.</summary>
    public JsonArray Definitions()
    {
        var definitions = _all.Where(t => all || _active.Contains(t.Name)).Select(Function).ToList();
        if (!all)
            definitions.Add(Function(new AssistantToolDefinition(FindTool,
                "Find more of Cuebot's tools by a word or two (e.g. 'delete action', 'overlay', 'poll', 'speech', 'send chat') and switch them on for this conversation.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["search"] = new JsonObject { ["type"] = "string", ["description"] = "What you want to do, in a few words" } },
                    ["required"] = new JsonArray("search"),
                })));
        return [.. definitions];
    }

    /// <summary>Runs <see cref="FindTool"/>: switches on the best matches and says what they do.</summary>
    public string Find(JsonObject arguments)
    {
        var words = (arguments["search"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "")
            .Split([' ', ',', '_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 1).ToArray();
        if (words.Length == 0) return new JsonObject { ["error"] = "Give a word or two, e.g. 'delete action' or 'overlay'." }.ToJsonString();
        var found = _all
            .Select(t => (Tool: t, Score: words.Sum(w => (t.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ? 3 : 0)
                                                         + (t.Description.Contains(w, StringComparison.OrdinalIgnoreCase) ? 1 : 0))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Tool.Name, StringComparer.Ordinal)
            .Take(MaxFound)
            .Select(x => x.Tool)
            .ToList();
        foreach (var tool in found) _active.Add(tool.Name);
        return new JsonObject
        {
            ["switchedOn"] = new JsonArray([.. found.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["description"] = FirstSentence(t.Description) })]),
            ["note"] = found.Count == 0 ? "No tool matches; try other words." : "These tools can be used now.",
        }.ToJsonString();
    }

    private static JsonNode Function(AssistantToolDefinition t) => new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.InputSchema.DeepClone() },
    };

    private static string FirstSentence(string text)
    {
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        return end > 0 ? text[..(end + 1)] : text;
    }
}
