using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Discord;

/// <summary>Registers the Discord bot (the streamer's own bot over the Gateway and REST).</summary>
public static class DiscordServiceCollectionExtensions
{
    /// <summary>
    /// Adds settings (discord-bot.json), <see cref="DiscordBotService"/> (an <see cref="IConnectionStatus"/>), its triggers
    /// and sub-actions. Call <c>StartAsync</c> at startup; it connects only when auto-connect is on (default off).
    /// </summary>
    public static IServiceCollection AddDiscordBot(this IServiceCollection services)
    {
        services.AddSettingsFile<DiscordBotSettings>("discord-bot.json");
        services.AddSingleton(sp => new DiscordBotService(sp.GetRequiredService<JsonFile<DiscordBotSettings>>(), sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>(), sp.GetRequiredService<HttpClient>(),
            // Resolved on first use: the engine needs the registry, which needs these sub-actions.
            engine: () => sp.GetService<ActionEngine>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<DiscordBotService>());
        services.AddSingleton<ITriggerProvider>(sp => new DiscordTriggers(sp.GetRequiredService<DiscordBotService>()));
        services.AddSingleton(sp => new SubActionSet(DiscordSubActions.Create(sp.GetRequiredService<DiscordBotService>())));
        foreach (var typeId in DiscordSubActions.TypeIds)
            services.AddSingleton<ISubActionHandler>(sp => sp.GetRequiredService<SubActionSet>().Get(typeId));
        return services;
    }

    internal sealed class SubActionSet(IReadOnlyList<ISubActionHandler> handlers)
    {
        private readonly Dictionary<string, ISubActionHandler> _byId = handlers.ToDictionary(h => h.TypeId, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<ISubActionHandler> All => handlers;

        public ISubActionHandler Get(string typeId) => _byId[typeId];
    }
}
