using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.SystemTools;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.BuiltIn;

/// <summary>Registration for system sub-actions and services (programs, files, HTTP, JSON, watchers).</summary>
internal static class SystemFeatures
{
    public static IServiceCollection AddSystemFeatures(this IServiceCollection services)
    {
        services.AddSingleton<ISubActionHandler, RunProgramSubAction>();
        services.AddSingleton<ISubActionHandler, ReadFileSubAction>();
        services.AddSingleton<ISubActionHandler, WriteFileSubAction>();
        services.AddSingleton<ISubActionHandler>(_ => new ReadRandomLineSubAction());
        services.AddSingleton<ISubActionHandler, ReadLinesSubAction>();
        services.AddSingleton<ISubActionHandler, ReadSpecificLineSubAction>();
        services.AddSingleton<ISubActionHandler, UdpBroadcastSubAction>();
        services.AddSingleton<ISubActionHandler, FileExistsSubAction>();
        services.AddSingleton<ISubActionHandler, DeleteFileSubAction>();
        services.AddSingleton<ISubActionHandler, HttpRequestSubAction>();
        services.AddSingleton<ISubActionHandler, JsonGetValueSubAction>();
        services.AddSingleton<ISubActionHandler, JsonCountSubAction>();

        // Watchers: definitions in watchers.json; the app host starts them with WatcherService.StartAsync.
        services.AddSettingsFile<WatcherSettings>("watchers.json");
        services.AddSingleton<IProcessLister, SystemProcessLister>();
        services.AddSingleton(sp => new WatcherService(sp.GetRequiredService<JsonFile<WatcherSettings>>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>(), sp.GetRequiredService<IProcessLister>()));
        services.AddSingleton<ITriggerProvider, SystemTriggers>();
        services.AddSingleton<ISubActionHandler, SetWatcherStateSubAction>();
        return services;
    }
}
