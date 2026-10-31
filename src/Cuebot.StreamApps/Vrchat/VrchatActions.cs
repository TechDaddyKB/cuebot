using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;
using Microsoft.Extensions.DependencyInjection;
using static Cuebot.StreamApps.Common.Steps;
using E = Cuebot.StreamApps.Vrchat.VrchatEvents;

namespace Cuebot.StreamApps.Vrchat;

/// <summary>VRChat service, status and triggers (its sub-actions join the module's SubActionSet).</summary>
internal static class VrchatRegistration
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton(sp => new VrchatService(sp.GetRequiredService<JsonFile<StreamAppsSettings>>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<Cuebot.Core.IConnectionStatus>(sp => sp.GetRequiredService<VrchatService>());
        services.AddSingleton<ITriggerProvider>(sp => new VrchatTriggers(sp.GetRequiredService<VrchatService>()));
    }
}

/// <summary>VRChat triggers (category "VRChat"). Type ids are stored in actions.json and must never change.</summary>
internal sealed class VrchatTriggers(VrchatService vrchat) : ITriggerProvider
{
    public const string Category = "VRChat";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger(Category, E.AvatarChanged, "Avatar changed",
            [new("avatar", "Avatar ids") { Help = "Comma-separated avatar ids (avtr_...); leave empty for any" }],
            (e, s) => MatchesList(s["avatar"], e.Text("avatarId"))),
        Trigger(Category, E.ParameterChanged, "Avatar parameter changed",
            [
                new("parameter", "Parameters")
                {
                    Help = "Comma-separated parameter names, as in your avatar's expression parameters; leave empty for any",
                    Suggestions = () => vrchat.Parameters.Keys.Where(k => !VrchatService.Noisy.Contains(k)).Order(StringComparer.OrdinalIgnoreCase),
                },
                new("value", "Only when it becomes") { Help = "e.g. true, 3 or 0.5; leave empty for any value" },
            ],
            (e, s) => MatchesList(s["parameter"], e.Text("parameter")) && ValueMatches(s["value"], e.Args.GetValueOrDefault("value"))),
    ];

    private static bool ValueMatches(string wanted, object? value)
    {
        var w = wanted.Trim();
        if (w.Length == 0) return true;
        return value switch
        {
            bool b => (w.ToLowerInvariant() is "true" or "on" or "1") == b && w.ToLowerInvariant() is "true" or "on" or "1" or "false" or "off" or "0",
            int or long or double => double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                                     && Math.Abs(Convert.ToDouble(value, CultureInfo.InvariantCulture) - n) < 0.0001,
            _ => string.Equals(w, Convert.ToString(value, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase),
        };
    }
}

/// <summary>VRChat sub-actions. Type ids are stored in actions.json and must never change.</summary>
internal static class VrchatSubActions
{
    public static readonly string[] TypeIds = ["vrchat.setParameter", "vrchat.chatbox", "vrchat.typing", "vrchat.getParameter"];

    public static IReadOnlyList<ISubActionHandler> Create(VrchatService vrchat)
    {
        ParameterDescriptor Parameter() => new("parameter", "Parameter")
        {
            Help = "The parameter's name exactly as in your avatar (capital letters matter)",
            Suggestions = () => vrchat.Parameters.Keys.Where(k => !VrchatService.Noisy.Contains(k)).Order(StringComparer.OrdinalIgnoreCase),
        };
        return
        [
            new StreamAppSubAction("vrchat.setParameter", VrchatTriggers.Category, "Set avatar parameter",
                [
                    Parameter(),
                    new("type", "Type", ParameterKind.Choice)
                    {
                        DefaultValue = "Bool",
                        Choices = ["Bool", "Int", "Float"],
                        ChoiceLabels = new Dictionary<string, string>
                        {
                            ["Bool"] = "Bool (on/off)",
                            ["Int"] = "Int (whole number 0-255)",
                            ["Float"] = "Float (number from -1 to 1)",
                        },
                    },
                    new("value", "Value") { Help = "Bool: true, false or toggle. Int: 0 to 255. Float: -1 to 1. May use %arguments%", DefaultValue = "true" },
                ],
                async (ctx, v, ct) => Store(ctx, "vrchat.value", await vrchat.SetParameterAsync(v["parameter"],
                    Enum.TryParse<VrchatParameterType>(v["type"], true, out var t) ? t : VrchatParameterType.Bool, v["value"], ct).ConfigureAwait(false))),
            new StreamAppSubAction("vrchat.chatbox", VrchatTriggers.Category, "Send chatbox message",
                [
                    new("text", "Text", ParameterKind.Multiline) { Help = "Shown above your avatar for everyone in the world (144 characters at most); may use %arguments%" },
                    new("immediately", "Show it right away (skip the keyboard)", ParameterKind.Boolean) { DefaultValue = "true" },
                    new("sound", "Play the notification sound", ParameterKind.Boolean) { DefaultValue = "true" },
                ],
                async (ctx, v, ct) => Store(ctx, "vrchat.chatbox", await vrchat.SendChatboxAsync(v["text"],
                    !bool.TryParse(v["immediately"], out var now) || now, !bool.TryParse(v["sound"], out var sound) || sound, ct).ConfigureAwait(false))),
            new StreamAppSubAction("vrchat.typing", VrchatTriggers.Category, "Show typing bubble",
                [new("typing", "Show it", ParameterKind.Boolean) { DefaultValue = "true" }],
                (_, v, ct) => vrchat.SetTypingAsync(!bool.TryParse(v["typing"], out var on) || on, ct)),
            new StreamAppSubAction("vrchat.getParameter", VrchatTriggers.Category, "Get avatar parameter",
                [Parameter()],
                (ctx, v, _) =>
                {
                    var name = Require(v["parameter"], "Parameter");
                    if (!vrchat.Parameters.TryGetValue(name, out var value))
                        throw new StreamAppRequestException($"VRChat has not reported '{name}' yet (it sends a parameter when it changes, or when you load the avatar).");
                    ctx.Args["vrchat.value"] = value;
                    ctx.Args["vrchat.avatarId"] = vrchat.AvatarId;
                    return Task.CompletedTask;
                }),
        ];
    }
}
