using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Server;
using Cuebot.Server.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Remote;

/// <summary>Registers remote signals between Cuebot instances.</summary>
public static class RemoteServiceCollectionExtensions
{
    /// <summary>
    /// Adds remote.json, <see cref="RemoteHub"/> (an <see cref="IConnectionStatus"/> and the <c>/api/v1/remote</c>
    /// WebSocket route on the REST API listener), its triggers, sub-actions and script functions. Needs <c>AddServers()</c>.
    /// The host connects the configured instances with <c>Get&lt;RemoteHub&gt;().StartAsync()</c>.
    /// </summary>
    public static IServiceCollection AddRemote(this IServiceCollection services)
    {
        services.AddSettingsFile<RemoteSettings>("remote.json");
        services.AddSingleton(sp =>
        {
            var file = sp.GetRequiredService<JsonFile<RemoteSettings>>();
            var serverSettings = sp.GetService<JsonFile<ServerSettings>>();
            return new RemoteHub(file.Value, file.ScheduleSave, sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>(),
                sp.GetService<SignalService>(), () => serverSettings?.Value.Api.Enabled ?? false);
        });
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<RemoteHub>());
        services.AddSingleton<IApiWebSocketRoute>(sp => sp.GetRequiredService<RemoteHub>());
        services.AddSingleton<ITriggerProvider>(sp => new RemoteTriggers(sp.GetRequiredService<RemoteHub>()));
        services.AddSingleton<ISubActionHandler>(sp => new SendRemoteSignalSubAction(sp.GetRequiredService<RemoteHub>()));
        services.AddSingleton<ISubActionHandler>(sp => new WaitForRemoteSignalSubAction(sp.GetRequiredService<RemoteHub>()));
        services.AddSingleton<IScriptFunctionProvider>(sp => new RemoteScriptFunctions(sp.GetRequiredService<RemoteHub>()));
        return services;
    }
}
