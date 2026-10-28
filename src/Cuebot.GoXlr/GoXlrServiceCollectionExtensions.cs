using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.GoXlr;

/// <summary>Registers the GoXLR integration (through the GoXLR Utility).</summary>
public static class GoXlrServiceCollectionExtensions
{
    /// <summary>
    /// Adds settings (goxlr.json), <see cref="GoXlrService"/> (an <see cref="IConnectionStatus"/>), triggers, sub-actions
    /// and script functions. Call <see cref="GoXlrService.StartAsync"/> at startup: it connects only when auto-connect is
    /// on (default off).
    /// </summary>
    public static IServiceCollection AddGoXlr(this IServiceCollection services)
    {
        services.AddSettingsFile<GoXlrSettings>("goxlr.json");
        services.AddSingleton(sp => new GoXlrService(sp.GetRequiredService<JsonFile<GoXlrSettings>>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<GoXlrService>());
        services.AddSingleton<ITriggerProvider>(sp => new GoXlrTriggers(sp.GetRequiredService<GoXlrService>()));
        services.AddSingleton(sp => new SubActionSet(GoXlrSubActions.Create(sp.GetRequiredService<GoXlrService>())));
        // One registration per sub-action so the Registry sees each one.
        foreach (var typeId in GoXlrSubActions.TypeIds)
            services.AddSingleton<ISubActionHandler>(sp => sp.GetRequiredService<SubActionSet>().Get(typeId));
        services.AddSingleton<IScriptFunctionProvider>(sp => new GoXlrScriptFunctions(sp.GetRequiredService<GoXlrService>()));
        return services;
    }

    /// <summary>The module's sub-actions, built once (their tables need the service).</summary>
    internal sealed class SubActionSet(IReadOnlyList<ISubActionHandler> handlers)
    {
        private readonly Dictionary<string, ISubActionHandler> _byId = handlers.ToDictionary(h => h.TypeId, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<ISubActionHandler> All => handlers;

        public ISubActionHandler Get(string typeId) => _byId[typeId];
    }
}
