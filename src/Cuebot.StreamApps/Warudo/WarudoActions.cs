using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
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
                    Suggestions = () => warudo.Receivers(WarudoReceiverKind.Toggle),
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
    public static readonly string[] TypeIds = ["warudo.trigger", "warudo.toggle", "warudo.message"];

    public static IReadOnlyList<ISubActionHandler> Create(WarudoService warudo)
    {
        ParameterDescriptor Receiver(WarudoReceiverKind kind, string node) => new("receiver", "Receiver name")
        {
            Help = $"The Receiver Name typed into an \"{node}\" node in a Warudo blueprint; may use %arguments%",
            Suggestions = () => warudo.Receivers(kind),
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
        ];
    }
}
