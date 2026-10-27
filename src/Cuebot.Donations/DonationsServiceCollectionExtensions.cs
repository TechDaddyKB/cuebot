using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Donations.StreamElements;
using Cuebot.Donations.Streamlabs;
using Cuebot.Donations.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Donations;

/// <summary>Registers the donations module (Streamlabs + StreamElements).</summary>
public static class DonationsServiceCollectionExtensions
{
    /// <summary>
    /// Adds settings (donations.json), <see cref="StreamlabsService"/> and <see cref="StreamElementsService"/>
    /// (both <see cref="IConnectionStatus"/>), their activity-feed adapters, triggers, sub-actions and script
    /// functions. Call <c>StartAsync</c> on both services at startup to auto-connect.
    /// </summary>
    public static IServiceCollection AddDonations(this IServiceCollection services)
    {
        services.AddSettingsFile<DonationsSettings>("donations.json");
        services.AddSingleton(sp => new StreamlabsService(
            sp.GetRequiredService<JsonFile<DonationsSettings>>(),
            sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new StreamElementsService(
            sp.GetRequiredService<JsonFile<DonationsSettings>>(),
            sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>(),
            sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<StreamlabsService>());
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<StreamElementsService>());
        services.AddSingleton<IChatEventAdapter, StreamlabsChatAdapter>();
        services.AddSingleton<IChatEventAdapter, StreamElementsChatAdapter>();
        services.AddSingleton<ITriggerProvider, StreamlabsTriggers>();
        services.AddSingleton<ITriggerProvider, StreamElementsTriggers>();
        services.AddSingleton<ISubActionHandler, GetUserPointsSubAction>();
        services.AddSingleton<ISubActionHandler, AddUserPointsSubAction>();
        services.AddSingleton<ISubActionHandler, GetPointsLeaderboardSubAction>();
        services.AddSingleton<ISubActionHandler, GetTipsSubAction>();
        services.AddSingleton<ISubActionHandler, GetTipLeaderboardSubAction>();
        services.AddSingleton<IScriptFunctionProvider, DonationsScriptFunctions>();
        return services;
    }
}
