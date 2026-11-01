using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;
using Microsoft.Extensions.DependencyInjection;
using static Cuebot.StreamApps.Common.Steps;
using E = Cuebot.StreamApps.Animaze.AnimazeEvents;

namespace Cuebot.StreamApps.Animaze;

/// <summary>Animaze service, status and triggers (its sub-actions join the module's SubActionSet).</summary>
internal static class AnimazeRegistration
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton(sp => new AnimazeService(sp.GetRequiredService<JsonFile<StreamAppsSettings>>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<Cuebot.Core.IConnectionStatus>(sp => sp.GetRequiredService<AnimazeService>());
        services.AddSingleton<ITriggerProvider>(sp => new AnimazeTriggers(sp.GetRequiredService<AnimazeService>()));
    }
}

/// <summary>Animaze triggers (category "Animaze"). Type ids are stored in actions.json and must never change.</summary>
internal sealed class AnimazeTriggers(AnimazeService animaze) : ITriggerProvider
{
    public const string Category = "Animaze";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger(Category, E.Connected, "Connected"),
        Trigger(Category, E.Disconnected, "Disconnected"),
        Trigger(Category, E.AvatarChanged, "Avatar changed",
            [
                new("avatar", "Avatars")
                {
                    Help = "Comma-separated avatar names; leave empty for any",
                    Suggestions = () => animaze.Avatars,
                },
            ],
            (e, s) => MatchesList(s["avatar"], e.Text("avatar"))),
    ];
}

/// <summary>Animaze sub-actions. Type ids are stored in actions.json and must never change.</summary>
internal static class AnimazeSubActions
{
    public static readonly string[] TypeIds =
    [
        "animaze.loadAvatar", "animaze.loadScene", "animaze.emote", "animaze.specialAction", "animaze.pose",
        "animaze.idle", "animaze.quickscene", "animaze.virtualCamera",
    ];

    public static IReadOnlyList<ISubActionHandler> Create(AnimazeService animaze)
    {
        ParameterDescriptor Named(string key, string label, string help, Func<IReadOnlyList<string>> suggestions) =>
            new(key, label) { Help = help + "; may use %arguments%", Suggestions = suggestions };
        IReadOnlyList<string> NamesOf(IReadOnlyList<AnimazeItem> items) => [.. items.Select(i => i.Name).Where(n => n.Length > 0)];
        return
        [
            new StreamAppSubAction("animaze.loadAvatar", AnimazeTriggers.Category, "Load Animaze avatar",
                [Named("avatar", "Avatar", "The avatar's name, as Animaze lists it", () => animaze.Avatars)],
                (_, v, ct) => animaze.LoadAvatarAsync(v["avatar"], ct)),
            new StreamAppSubAction("animaze.loadScene", AnimazeTriggers.Category, "Load Animaze background scene",
                [Named("scene", "Scene", "The scene's name, as Animaze lists it", () => animaze.Scenes)],
                (_, v, ct) => animaze.LoadSceneAsync(v["scene"], ct)),
            new StreamAppSubAction("animaze.emote", AnimazeTriggers.Category, "Play Animaze emote",
                [Named("emote", "Emote", "The emote's name (e.g. Angry)", () => [.. animaze.Emotes.Select(e => e.FriendlyName)])],
                async (ctx, v, ct) => Store(ctx, "animaze.emote", await animaze.TriggerEmoteAsync(v["emote"], ct).ConfigureAwait(false))),
            new StreamAppSubAction("animaze.specialAction", AnimazeTriggers.Category, "Play Animaze special action",
                [Named("action", "Special action", "The special action's name (e.g. Hello) or its number, for the current avatar", () => NamesOf(animaze.SpecialActions))],
                (_, v, ct) => animaze.TriggerSpecialActionAsync(v["action"], ct)),
            new StreamAppSubAction("animaze.pose", AnimazeTriggers.Category, "Animaze pose",
                [Named("pose", "Pose", "The pose's name or its number, for the current avatar", () => NamesOf(animaze.Poses))],
                (_, v, ct) => animaze.TriggerPoseAsync(v["pose"], ct)),
            new StreamAppSubAction("animaze.idle", AnimazeTriggers.Category, "Animaze idle animation",
                [Named("idle", "Idle animation", "The idle animation's name or its number, for the current avatar", () => NamesOf(animaze.Idles))],
                (_, v, ct) => animaze.TriggerIdleAsync(v["idle"], ct)),
            new StreamAppSubAction("animaze.quickscene", AnimazeTriggers.Category, "Load Animaze quickscene",
                [Named("quickscene", "Quickscene", "A saved quickscene's name or its number", () => NamesOf(animaze.Quickscenes))],
                (_, v, ct) => animaze.LoadQuicksceneAsync(v["quickscene"], ct)),
            new StreamAppSubAction("animaze.virtualCamera", AnimazeTriggers.Category, "Animaze virtual camera on/off",
                [new("state", "Virtual camera", ParameterKind.Choice) { DefaultValue = "On", Choices = ["On", "Off"] }],
                (_, v, ct) => animaze.SetBroadcastAsync(ParseSwitch(v["state"]) != SwitchMode.Off, ct)),
        ];
    }
}
