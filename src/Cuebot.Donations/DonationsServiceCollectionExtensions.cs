using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Donations.DonorDrive;
using Cuebot.Donations.Pally;
using Cuebot.Donations.StreamElements;
using Cuebot.Donations.Streamlabs;
using Cuebot.Donations.TipeeeStream;
using Cuebot.Donations.TreatStream;
using Cuebot.Donations.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Donations;

/// <summary>Registers the donations module (Streamlabs, StreamElements, TipeeeStream, DonorDrive, TreatStream, Pally.gg).</summary>
public static class DonationsServiceCollectionExtensions
{
    /// <summary>
    /// Adds settings (donations.json), the six services (each an <see cref="IConnectionStatus"/>) and
    /// <see cref="DonationServices"/>, their activity-feed adapters, triggers, sub-actions and script functions. Call
    /// <c>StartAsync</c> on each service (or <see cref="DonationServices.StartAsync"/>) at startup to auto-connect.
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
        services.AddSingleton(sp => new TipeeeStreamService(
            sp.GetRequiredService<JsonFile<DonationsSettings>>(),
            sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>(),
            sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp => new DonorDriveService(
            sp.GetRequiredService<JsonFile<DonationsSettings>>(),
            sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>(),
            sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp => new TreatStreamService(
            sp.GetRequiredService<JsonFile<DonationsSettings>>(),
            sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>(),
            sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp => new PallyService(
            sp.GetRequiredService<JsonFile<DonationsSettings>>(),
            sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>()));
        services.AddSingleton<DonationServices>();
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<StreamlabsService>());
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<StreamElementsService>());
        services.AddSingleton<Cuebot.Core.Overlays.Widgets.IStreamElementsOverlaySource>(sp => sp.GetRequiredService<StreamElementsService>());
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<TipeeeStreamService>());
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<DonorDriveService>());
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<TreatStreamService>());
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<PallyService>());
        services.AddSingleton<IChatEventAdapter, StreamlabsChatAdapter>();
        services.AddSingleton<IChatEventAdapter, StreamElementsChatAdapter>();
        services.AddSingleton<IChatEventAdapter, TipeeeStreamChatAdapter>();
        services.AddSingleton<IChatEventAdapter, DonorDriveChatAdapter>();
        services.AddSingleton<IChatEventAdapter, TreatStreamChatAdapter>();
        services.AddSingleton<IChatEventAdapter, PallyChatAdapter>();
        services.AddSingleton<Cuebot.Core.Overlays.Events.IOverlayEventAdapter, DonationOverlayAdapter>();
        services.AddSingleton<ITriggerProvider, StreamlabsTriggers>();
        services.AddSingleton<ITriggerProvider, StreamElementsTriggers>();
        services.AddSingleton<ITriggerProvider, TipeeeStreamTriggers>();
        services.AddSingleton<ITriggerProvider, DonorDriveTriggers>();
        services.AddSingleton<ITriggerProvider, TreatStreamTriggers>();
        services.AddSingleton<ITriggerProvider, PallyTriggers>();
        services.AddSingleton<ISubActionHandler, GetUserPointsSubAction>();
        services.AddSingleton<ISubActionHandler, AddUserPointsSubAction>();
        services.AddSingleton<ISubActionHandler, GetPointsLeaderboardSubAction>();
        services.AddSingleton<ISubActionHandler, GetTipsSubAction>();
        services.AddSingleton<ISubActionHandler, GetTipLeaderboardSubAction>();
        services.AddSingleton<IScriptFunctionProvider, DonationsScriptFunctions>();
        return services;
    }
}
