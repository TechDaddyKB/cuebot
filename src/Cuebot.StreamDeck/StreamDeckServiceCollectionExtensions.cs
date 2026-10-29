using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Server;
using Cuebot.Server.Api;
using Cuebot.StreamDeck.Hid;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.StreamDeck;

/// <summary>Registers the Elgato Stream Deck integration.</summary>
public static class StreamDeckServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="StreamDeckHub"/> (an <see cref="IConnectionStatus"/> and the <c>/api/v1/streamdeck</c> WebSocket
    /// route on the REST API listener), its triggers, sub-actions and script functions. Needs <c>AddServers()</c>. Nothing
    /// to start: the plugin connects whenever the REST API is on. No settings file of its own: the plugin uses the API
    /// port and token. Also adds direct USB mode (<see cref="StreamDeckHidService"/>, streamdeck-usb.json): decks driven
    /// over HID without the Stream Deck app; it starts scanning in its <c>StartAsync</c>.
    /// </summary>
    public static IServiceCollection AddStreamDeck(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var serverSettings = sp.GetService<JsonFile<ServerSettings>>();
            ApiOperations? operations = null;
            return new StreamDeckHub(
                sp.GetRequiredService<EventBus>(),
                sp.GetRequiredService<AppLog>(),
                // Resolved on first use: the engine (behind ServerServices) is still being built when the hub is created.
                () => operations ??= new ApiOperations(sp.GetRequiredService<ServerServices>()),
                () => serverSettings?.Value.Api.Enabled ?? false,
                sp.GetService<ProfileService>(),
                sp.GetService<GlobalVariables>());
        });
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<StreamDeckHub>());
        services.AddSingleton<IApiWebSocketRoute>(sp => sp.GetRequiredService<StreamDeckHub>());
        services.AddSingleton<ITriggerProvider, StreamDeckTriggers>();
        foreach (var typeId in StreamDeckSubAction.TypeIds)
        {
            services.AddSingleton<ISubActionHandler>(sp =>
                StreamDeckSubAction.Create(sp.GetRequiredService<StreamDeckHub>()).First(h => h.TypeId == typeId));
        }
        services.AddSingleton<IScriptFunctionProvider>(sp => new StreamDeckScriptFunctions(sp.GetRequiredService<StreamDeckHub>()));

        services.AddSettingsFile<StreamDeckHidSettings>("streamdeck-usb.json");
        services.AddSingleton(sp => new StreamDeckHidService(
            sp.GetRequiredService<JsonFile<StreamDeckHidSettings>>(),
            sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>(),
            sp.GetRequiredService<StreamDeckHub>(),
            images: new KeyImageLoader(sp.GetService<HttpClient>())));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<StreamDeckHidService>());
        return services;
    }
}
