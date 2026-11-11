using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;
using Microsoft.Extensions.DependencyInjection;
using static Cuebot.StreamApps.Common.Steps;
using E = Cuebot.StreamApps.Warudo.WarudoEvents;

namespace Cuebot.StreamApps.Warudo;

/// <summary>Warudo service, status and triggers (its sub-actions join the module's SubActionSet).</summary>
internal static class WarudoRegistration
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton(sp => new WarudoService(sp.GetRequiredService<JsonFile<StreamAppsSettings>>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<Cuebot.Core.IConnectionStatus>(sp => sp.GetRequiredService<WarudoService>());
        services.AddSingleton<ITriggerProvider>(sp => new WarudoTriggers(sp.GetRequiredService<WarudoService>()));
        services.AddSingleton<IScriptFunctionProvider>(sp => new WarudoScriptFunctions(sp.GetRequiredService<WarudoService>()));
    }
}

/// <summary>Warudo triggers (category "Warudo"). Type ids are stored in actions.json and must never change.</summary>
internal sealed class WarudoTriggers(WarudoService warudo) : ITriggerProvider
{
    public const string Category = "Warudo";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger(Category, E.Connected, "Connected"),
        Trigger(Category, E.Disconnected, "Disconnected"),
        Trigger(Category, E.ToggleChanged, "Toggle changed",
            [
                new("receiver", "Receiver names")
                {
                    Help = "Comma-separated Receiver Names of On Stream Deck Toggle nodes; leave empty for any",
                    Suggestions = () => warudo.ReceiverSuggestions(WarudoReceiverKind.Toggle),
                },
                new("state", "Turned", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "On", "Off"] },
            ],
            (e, s) => MatchesList(s["receiver"], e.Text("receiver"))
                      && (s["state"] is "" or "Any" || string.Equals(s["state"], e.Text("state"), StringComparison.OrdinalIgnoreCase))),
    ];
}

/// <summary>Warudo sub-actions. Type ids are stored in actions.json and must never change.</summary>
internal static class WarudoSubActions
{
    public static readonly string[] TypeIds = ["warudo.trigger", "warudo.toggle", "warudo.message", "warudo.getToggle", "warudo.webSocketMessage"];

    public static IReadOnlyList<ISubActionHandler> Create(WarudoService warudo)
    {
        ParameterDescriptor Receiver(WarudoReceiverKind kind, string node) => new("receiver", "Receiver name")
        {
            Help = $"The Receiver Name typed into an \"{node}\" node in a Warudo blueprint; may use %arguments%",
            Suggestions = () => warudo.ReceiverSuggestions(kind),
        };
        return
        [
            new StreamAppSubAction("warudo.trigger", WarudoTriggers.Category, "Trigger Warudo blueprint",
                [Receiver(WarudoReceiverKind.Trigger, "On Stream Deck Trigger")],
                (_, v, ct) => warudo.TriggerAsync(v["receiver"], ct)),
            new StreamAppSubAction("warudo.toggle", WarudoTriggers.Category, "Warudo toggle on/off",
                [Receiver(WarudoReceiverKind.Toggle, "On Stream Deck Toggle"), Switch("On", "Off")],
                async (ctx, v, ct) => Store(ctx, "warudo.toggle",
                    await warudo.SetToggleAsync(v["receiver"], ParseSwitch(v["state"]), ct).ConfigureAwait(false))),
            new StreamAppSubAction("warudo.message", WarudoTriggers.Category, "Send message to Warudo",
                [
                    Receiver(WarudoReceiverKind.Message, "On Stream Deck Message"),
                    new("message", "Message", ParameterKind.Multiline) { Help = "Text the node passes on (e.g. a viewer's name); may use %arguments%" },
                ],
                (_, v, ct) => warudo.SendMessageAsync(v["receiver"], v["message"], ct)),
            new WarudoGetToggleSubAction(warudo, Receiver(WarudoReceiverKind.Toggle, "On Stream Deck Toggle")),
            new StreamAppSubAction("warudo.webSocketMessage", WarudoTriggers.Category, "Send WebSocket message to Warudo",
                [
                    new("message", "Message", ParameterKind.Multiline)
                    {
                        Help = "Text for Warudo's \"On WebSocket Message Received\" nodes (no Stream Deck node needed); may use %arguments%",
                    },
                ],
                (_, v, ct) => warudo.SendWebSocketMessageAsync(v["message"], ct)),
        ];
    }
}

/// <summary>Reads an On Stream Deck Toggle's state into %warudo.toggle% and runs the On or Off branch.</summary>
internal sealed class WarudoGetToggleSubAction(WarudoService warudo, ParameterDescriptor receiver) : ISubActionHandler
{
    public const string On = "On";
    public const string Off = "Off";

    public string TypeId => "warudo.getToggle";
    public string Category => WarudoTriggers.Category;
    public string DisplayName => "Get Warudo toggle state";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [receiver];
    public IReadOnlyList<string> Branches { get; } = [On, Off];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var state = await warudo.GetToggleAsync(parameters["receiver"], ct).ConfigureAwait(false);
        Store(context, "warudo.toggle", state);
        return SubActionResult.TakeBranch(state == true ? On : Off);
    }
}

/// <summary>Warudo functions for user scripts (C# and Python).</summary>
internal sealed class WarudoScriptFunctions(WarudoService warudo) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("warudo.isConnected", "True while Warudo's Stream Deck connection is open", [], (_, _, _) => Task.FromResult<object?>(warudo.IsConnected)),
        new("warudo.trigger", "Fire the On Stream Deck Trigger nodes with this Receiver Name", ["receiver"], async (_, a, ct) =>
        {
            await warudo.TriggerAsync(a.GetString("receiver"), ct).ConfigureAwait(false);
            return null;
        }),
        new("warudo.setToggle", "Turn On Stream Deck Toggle nodes On, Off or Toggle; returns the new state (null if unknown)", ["receiver", "state"],
            async (_, a, ct) => await warudo.SetToggleAsync(a.GetString("receiver"), ParseSwitch(a.GetString("state", "Toggle")), ct).ConfigureAwait(false)),
        new("warudo.getToggle", "An On Stream Deck Toggle's state (null if unknown)", ["receiver"],
            async (_, a, ct) => await warudo.GetToggleAsync(a.GetString("receiver"), ct).ConfigureAwait(false)),
        new("warudo.sendMessage", "Send text to the On Stream Deck Message nodes with this Receiver Name", ["receiver", "message"], async (_, a, ct) =>
        {
            await warudo.SendMessageAsync(a.GetString("receiver"), a.GetString("message"), ct).ConfigureAwait(false);
            return null;
        }),
        new("warudo.sendWebSocket", "Send text to Warudo's On WebSocket Message Received nodes", ["message"], async (_, a, ct) =>
        {
            await warudo.SendWebSocketMessageAsync(a.GetString("message"), ct).ConfigureAwait(false);
            return null;
        }),
    ];
}
