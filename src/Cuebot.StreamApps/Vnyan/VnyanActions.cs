using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;
using Cuebot.StreamApps.Obsbot;
using Microsoft.Extensions.DependencyInjection;
using static Cuebot.StreamApps.Common.Steps;

namespace Cuebot.StreamApps.Vnyan;

/// <summary>VNyan service, status, trigger and script functions (its sub-actions join the module's SubActionSet).</summary>
internal static class VnyanRegistration
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var vnyan = new VnyanService(sp.GetRequiredService<JsonFile<StreamAppsSettings>>(), sp.GetRequiredService<EventBus>(),
                sp.GetRequiredService<AppLog>(), sp.GetRequiredService<HttpClient>());
            if (sp.GetService<ProfileService>() is { } profile)
                vnyan.UsedActionNames = () => VnyanSubActions.ActionNames(profile.Snapshot.Actions);
            return vnyan;
        });
        services.AddSingleton<Cuebot.Core.IConnectionStatus>(sp => sp.GetRequiredService<VnyanService>());
        services.AddSingleton<ITriggerProvider>(_ => new VnyanTriggers());
        services.AddSingleton<IScriptFunctionProvider>(sp => new VnyanScriptFunctions(sp.GetRequiredService<VnyanService>()));
    }
}

/// <summary>VNyan triggers (category "VNyan"). Type ids are stored in actions.json and must never change.</summary>
internal sealed class VnyanTriggers : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger(VnyanSubActions.Category, VnyanEvents.Message, "VNyan message",
            [
                new("command", "Commands")
                {
                    Help = "Comma-separated commands (the first word, or the \"command\"/\"event\" of a JSON message); leave empty for any",
                },
            ],
            (e, s) => MatchesList(s["command"], e.Text("vnyanCommand"), e.Text("message").Trim())),
    ];
}

/// <summary>VNyan sub-actions. Type ids are stored in actions.json and must never change.</summary>
internal static class VnyanSubActions
{
    public const string Category = "VNyan";

    public static readonly string[] TypeIds =
        ["vnyan.send", "vnyan.sendWebSocket", "vnyan.setParameter", "vnyan.setParameters", "vnyan.setAvatarParameter"];

    private static readonly string[] NumberChoices = ["As text", "As numbers"];
    private static readonly string[] TypeChoices = ["Number", "Text"];

    /// <summary>The action names typed into "Send to VNyan" steps (without %variables%), for suggestions and the card.</summary>
    public static IReadOnlyList<string> ActionNames(IEnumerable<ActionDefinition> actions)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        void Walk(IEnumerable<SubActionDefinition> steps)
        {
            foreach (var step in steps)
            {
                if (string.Equals(step.Type, "vnyan.send", StringComparison.OrdinalIgnoreCase)
                    && step.Parameters.TryGetValue("action", out var name) && name.Trim() is { Length: > 0 } n && !n.Contains('%'))
                    names.Add(n);
                foreach (var branch in step.Branches.Values) Walk(branch);
            }
        }
        foreach (var action in actions) Walk(action.SubActions);
        return [.. names];
    }

    public static IReadOnlyList<ISubActionHandler> Create(VnyanService vnyan) =>
    [
        new StreamAppSubAction("vnyan.send", Category, "Send to VNyan",
            [
                new("action", "Action name")
                {
                    Help = "The name typed into an \"API Message\" node in VNyan's node graph (capital letters matter); may use %arguments%",
                    Suggestions = vnyan.UsedActionNames,
                },
                new("payload", "Values", ParameterKind.Multiline)
                {
                    Help = "Optional. One name=value per line (or a JSON object). Values may use %arguments%; they are filled in after the lines are read, so quotes or line breaks in a chat message cannot break them.",
                    ResolveVariables = false,
                },
                new("numbers", "Numbers", ParameterKind.Choice)
                {
                    DefaultValue = "As text",
                    Choices = NumberChoices,
                    Help = "As text sends every value as text (VNyan's dictionaries hold text). As numbers sends values such as 100 or 2.5 as JSON numbers, for nodes that read the node's json output",
                },
            ],
            (ctx, v, ct) => vnyan.SendAsync(v["action"],
                VnyanProtocol.Payload(ParsePairNodes(v["payload"], ctx.Resolve), VnyanProtocol.ParseNumbers(v["numbers"])), ct)),
        new StreamAppSubAction("vnyan.sendWebSocket", Category, "Send WebSocket command",
            [
                new("command", "Command") { Help = "The Command Text of a \"WebSocket\" node in VNyan, e.g. Wave; may use %arguments%" },
                new("value", "Value", ParameterKind.Number) { Help = "Optional number sent after the command; VNyan gives it to the node as Value (and [message])" },
            ],
            (_, v, ct) => vnyan.SendWebSocketAsync(VnyanProtocol.Command(v["command"], v["value"]), ct)),
        new StreamAppSubAction("vnyan.setParameter", Category, "Set parameter",
            [
                new("name", "Parameter") { Help = "The VNyan parameter's name, without [ ] or < >" },
                new("value", "Value") { Help = "A number (2.5, not 2,5) or text; may use %arguments%" },
                new("type", "Type", ParameterKind.Choice)
                {
                    DefaultValue = "Number",
                    Choices = TypeChoices,
                    Help = "Number sets [name] (VNyan numbers are decimals); Text sets <name>",
                },
            ],
            (_, v, ct) => vnyan.SendOscAsync([VnyanProtocol.Parameter(v["name"], v["value"], IsNumberType(v["type"]))], ct)),
        new StreamAppSubAction("vnyan.setParameters", Category, "Set several parameters",
            [
                new("values", "Values", ParameterKind.Multiline)
                {
                    Help = "One name=value per line; values may use %arguments%. Sent together as one OSC bundle.",
                    ResolveVariables = false,
                },
                new("type", "Type", ParameterKind.Choice)
                {
                    DefaultValue = "Number when it is one",
                    Choices = ["Number when it is one", "Text"],
                    Help = "Number when it is one: values such as 100 or 2.5 set [name], everything else <name>. Text: every value sets <name>",
                },
            ],
            (ctx, v, ct) => vnyan.SendOscAsync(ParameterMessages(ParsePairNodes(v["values"], ctx.Resolve), !v["type"].StartsWith("Text", StringComparison.OrdinalIgnoreCase)), ct)),
        new StreamAppSubAction("vnyan.setAvatarParameter", Category, "Set avatar parameter (VRChat style)",
            [
                new("name", "Parameter") { Help = "The name after /avatar/parameters/, e.g. HatOn" },
                new("value", "Value") { Help = "A number (true/false send 1/0); may use %arguments%" },
            ],
            (_, v, ct) => vnyan.SendOscAsync([VnyanProtocol.AvatarParameter(v["name"], v["value"])], ct)),
    ];

    private static bool IsNumberType(string type) => !type.Trim().Equals("Text", StringComparison.OrdinalIgnoreCase);

    /// <summary>OSC messages for name=value pairs: plain numbers as Float when <paramref name="numbers"/>, the rest as String.</summary>
    internal static List<OscMessage> ParameterMessages(IEnumerable<(string Name, JsonNode? Value)> pairs, bool numbers)
    {
        var messages = new List<OscMessage>();
        foreach (var (name, value) in pairs)
        {
            var text = value is null ? "" : value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
            var isNumber = numbers && (value?.GetValueKind() == JsonValueKind.Number || VnyanProtocol.IsPlainNumber(text.Trim()));
            messages.Add(VnyanProtocol.Parameter(name, isNumber ? text.Trim() : text, isNumber));
        }
        if (messages.Count == 0) throw new ArgumentException("Values are required (one name=value per line).");
        return messages;
    }
}

/// <summary>VNyan functions for user scripts (C# and Python).</summary>
internal sealed class VnyanScriptFunctions(VnyanService vnyan) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("vnyan.send", "Fire VNyan's API Message nodes with this action name; payload is an object whose numbers stay numbers", ["action", "payload"],
            async (_, a, ct) =>
            {
                var payload = a.GetElement("payload") switch
                {
                    { ValueKind: JsonValueKind.Object } o => JsonNode.Parse(o.GetRawText())!.AsObject(),
                    { ValueKind: JsonValueKind.String } s => VnyanProtocol.Payload(ParsePairNodes(s.GetString() ?? ""), VnyanNumbers.Text),
                    _ => new JsonObject(),
                };
                await vnyan.SendAsync(a.GetString("action"), payload, ct).ConfigureAwait(false);
                return null;
            }),
        new("vnyan.sendWebSocket", "Send a command (and optional number) to VNyan's WebSocket nodes", ["command", "value"], async (_, a, ct) =>
        {
            await vnyan.SendWebSocketAsync(VnyanProtocol.Command(a.GetString("command"), Text(a, "value")), ct).ConfigureAwait(false);
            return null;
        }),
        new("vnyan.setParameter", "Set a VNyan parameter over OSC: a number sets [name], text sets <name>", ["name", "value"], async (_, a, ct) =>
        {
            var number = a.GetElement("value") is { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False };
            await vnyan.SendOscAsync([VnyanProtocol.Parameter(a.GetString("name"), Text(a, "value"), number)], ct).ConfigureAwait(false);
            return null;
        }),
    ];

    private static string Text(JsonElement a, string name) => a.GetElement(name) switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
        _ => "",
    };
}
