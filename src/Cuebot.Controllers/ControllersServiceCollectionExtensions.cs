using Cuebot.Controllers.Device;
using Cuebot.Controllers.Device.Sdl;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Controllers;

/// <summary>Registers game-controller input (SDL3).</summary>
public static class ControllersServiceCollectionExtensions
{
    /// <summary>
    /// Adds settings (controllers.json), the SDL3 <see cref="IControllerBackend"/> (replace it to use another device
    /// layer), <see cref="ControllerService"/> (an <see cref="IConnectionStatus"/> and <see cref="IPeriodicService"/>, so the
    /// core timer starts/stops watching as triggers come and go), triggers, sub-actions and script functions. Call
    /// <see cref="ControllerService.StartAsync"/> at startup. Nothing touches a device until something needs it.
    /// </summary>
    public static IServiceCollection AddGameControllers(this IServiceCollection services)
    {
        services.AddSettingsFile<ControllerSettings>("controllers.json");
        services.AddSingleton<IControllerBackend>(sp => new SdlControllerBackend(sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new ControllerService(sp.GetRequiredService<IControllerBackend>(),
            sp.GetRequiredService<JsonFile<ControllerSettings>>(), sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>(),
            sp.GetRequiredService<AppPaths>(), () => UsesControllerTriggers(sp.GetRequiredService<ProfileService>().Snapshot)));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<ControllerService>());
        services.AddSingleton<IPeriodicService>(sp => sp.GetRequiredService<ControllerService>());
        services.AddSingleton<ITriggerProvider>(sp => new ControllerTriggers(sp.GetRequiredService<ControllerService>()));

        services.AddSingleton(sp => new SubActionSet(ControllerSubActions.Create(sp.GetRequiredService<ControllerService>())));
        foreach (var typeId in ControllerSubActions.TypeIds)
            services.AddSingleton<ISubActionHandler>(sp => sp.GetRequiredService<SubActionSet>().Get(typeId));

        services.AddSingleton<IScriptFunctionProvider>(sp => new ControllerScriptFunctions(sp.GetRequiredService<ControllerService>()));
        return services;
    }

    /// <summary>True when an enabled action has an enabled game-controller trigger.</summary>
    public static bool UsesControllerTriggers(ProfileSnapshot profile) =>
        profile.Actions.Any(a => a.Enabled && a.Triggers.Any(t => t.Enabled && ControllerTriggers.TriggerIds.Contains(t.Type, StringComparer.OrdinalIgnoreCase)));

    /// <summary>The module's sub-actions, built once.</summary>
    internal sealed class SubActionSet(IReadOnlyList<ISubActionHandler> handlers)
    {
        private readonly Dictionary<string, ISubActionHandler> _byId = handlers.ToDictionary(h => h.TypeId, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<ISubActionHandler> All => handlers;

        public ISubActionHandler Get(string typeId) => _byId[typeId];
    }
}
