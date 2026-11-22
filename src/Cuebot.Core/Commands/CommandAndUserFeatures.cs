using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Commands;

/// <summary>Registration for the commands manager, users and groups, and timers.</summary>
/// <remarks>
/// <see cref="CommandService"/> and <see cref="UserRegistry"/> subscribe to the bus when created and are ticked by
/// <see cref="TimerService"/> (as <see cref="IPeriodicService"/>s), so resolving TimerService (the host does at
/// start-up) brings them to life before platforms connect.
/// </remarks>
internal static class CommandAndUserFeatures
{
    public static IServiceCollection AddCommandAndUserFeatures(this IServiceCollection services)
    {
        services.AddSingleton(sp => new CommandStore(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new GroupStore(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<AppLog>(),
            sp.GetRequiredService<EventBus>()));
        services.AddSingleton(sp => new UserRegistry(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>(), presentViewersArgs: sp.GetServices<IPresentViewersArgs>));
        services.AddSettingsFile<AccountLinksData>("account-links.json");
        services.AddSingleton(sp => new AccountLinks(sp.GetRequiredService<Storage.JsonFile<AccountLinksData>>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new ViewerStatusCache(sp.GetServices<IViewerStatusSource>(), sp.GetRequiredService<EventBus>()));
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<Storage.JsonFile<Chat.Moderation.ModerationSettings>>();
            return new Chat.AccountStandings(sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>(), () => settings.Value,
                sp.GetService<Chat.Moderation.ModLog>(), sp.GetServices<Chat.IBanListSource>());
        });
        services.AddSingleton<IPeriodicService>(sp => sp.GetRequiredService<Chat.AccountStandings>());
        services.AddSingleton(sp => new CommandService(sp.GetRequiredService<CommandStore>(), sp.GetRequiredService<GroupStore>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>(), status: sp.GetRequiredService<ViewerStatusCache>(),
            standings: sp.GetRequiredService<Chat.AccountStandings>(), live: sp.GetRequiredService<Chat.StreamLiveState>()));
        // Created at start-up with the other periodic services, so replies work before any page is opened.
        services.AddSingleton<IPeriodicService>(sp => new CommandReplier(sp.GetRequiredService<CommandStore>(),
            sp.GetRequiredService<Chat.ChatHub>(), sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>(),
            sp.GetRequiredService<GlobalVariables>().Get));
        services.AddSingleton<IPeriodicService>(sp => sp.GetRequiredService<UserRegistry>());
        services.AddSingleton<IViewerDataStore>(sp => sp.GetRequiredService<UserRegistry>());
        services.AddSingleton<IViewerDataStore>(sp => sp.GetRequiredService<GroupStore>());
        services.AddSingleton<IViewerDataStore>(sp => sp.GetRequiredService<CommandStore>());
        services.AddSingleton(sp => new ViewerData(sp.GetServices<IViewerDataStore>(), sp.GetRequiredService<UserRegistry>(),
            sp.GetRequiredService<AppLog>(), persons: sp.GetServices<IPersonDataStore>()));
        services.AddSingleton<IPeriodicService>(sp => sp.GetRequiredService<CommandService>());
        services.AddSingleton<IPeriodicService>(sp => new Timers.CronClock(sp.GetRequiredService<EventBus>(), sp.GetRequiredService<ProfileService>()));
        services.AddSingleton<ITriggerProvider, Timers.CronTriggers>();
        services.AddSingleton(sp => new TimerService(sp.GetRequiredService<ProfileService>(), sp.GetRequiredService<EventBus>(),
            periodic: sp.GetServices<IPeriodicService>(), log: sp.GetRequiredService<AppLog>(), live: sp.GetRequiredService<Chat.StreamLiveState>()));

        // Commands
        services.AddSingleton<ITriggerProvider, CommandTriggers>();
        services.AddSingleton<ISubActionHandler, GetCommandStateSubAction>();
        services.AddSingleton<ISubActionHandler, SetCommandStateSubAction>();
        services.AddSingleton<ISubActionHandler, GetCommandGroupStateSubAction>();
        services.AddSingleton<ISubActionHandler, SetCommandGroupStateSubAction>();
        services.AddSingleton<ISubActionHandler, GetCommandsSubAction>();
        services.AddSingleton<ISubActionHandler, ResetCommandCountersSubAction>();

        // Users and groups
        services.AddSingleton<ITriggerProvider, UserTriggers>();
        services.AddSingleton<ITriggerProvider, GroupTriggers>();
        services.AddSingleton<ISubActionHandler, ResetFirstWordsSubAction>();
        services.AddSingleton<ISubActionHandler, AddRandomPresentUserSubAction>();
        services.AddSingleton<ISubActionHandler, AddUserToGroupSubAction>();
        services.AddSingleton<ISubActionHandler, RemoveUserFromGroupSubAction>();
        services.AddSingleton<ISubActionHandler, UserInGroupSubAction>();
        services.AddSingleton<ISubActionHandler, ClearGroupSubAction>();
        services.AddSingleton<IScriptFunctionProvider, GroupScriptFunctions>();

        // Timers
        services.AddSingleton<ISubActionHandler, SetTimerStateSubAction>();
        services.AddSingleton<ISubActionHandler>(sp => new GetTimerStateSubAction(sp.GetRequiredService<TimerService>(),
            sp.GetRequiredService<ProfileService>()));
        return services;
    }
}
