using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Members;
using Cuebot.Portal.Relay;
using Cuebot.Portal.SignIn;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Portal;

/// <summary>Registers the mod portal (docs/MOD_PORTAL_PLAN.md).</summary>
public static class PortalServiceCollectionExtensions
{
    /// <summary>
    /// Adds portal.json and portal-changes.json, <see cref="PortalDirectory"/>, <see cref="PortalChanges"/>,
    /// <see cref="PortalApi"/>, provider sign-in (<see cref="PortalIdentityExchange"/>) and <see cref="PortalRelayClient"/>
    /// (also as <see cref="IConnectionStatus"/>) with the sign-in handler on its device links.
    /// </summary>
    public static IServiceCollection AddPortal(this IServiceCollection services)
    {
        services.AddSettingsFile<PortalSettings>("portal.json");
        services.AddSettingsFile<PortalChangeLog>("portal-changes.json");
        services.AddSingleton(sp =>
        {
            var file = sp.GetRequiredService<JsonFile<PortalSettings>>();
            return new PortalDirectory(file.Value, file.ScheduleSave);
        });
        services.AddSingleton(sp =>
        {
            var file = sp.GetRequiredService<JsonFile<PortalChangeLog>>();
            IPortalArea[] areas = [new ActionArea(sp.GetRequiredService<ProfileService>()), new CommandArea(sp.GetRequiredService<CommandStore>())];
            return new PortalChanges(file.Value, file.ScheduleSave, sp.GetRequiredService<PortalDirectory>(), areas);
        });
        services.AddSingleton(sp => new PortalApi(sp.GetRequiredService<PortalDirectory>(), sp.GetRequiredService<PortalChanges>(),
            sp.GetRequiredService<ProfileService>(), sp.GetRequiredService<CommandStore>(), sp.GetRequiredService<ActionEngine>(),
            sp.GetRequiredService<Registry>()));
        services.AddSingleton<IPortalIdentityExchange>(sp => new PortalIdentityExchange(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp =>
        {
            var api = sp.GetRequiredService<PortalApi>();
            return new PortalRelayClient(sp.GetRequiredService<PortalDirectory>(), sp.GetRequiredService<AppLog>())
            {
                LinkHandler = new PortalSignInHandler(sp.GetRequiredService<PortalDirectory>(), sp.GetRequiredService<IPortalIdentityExchange>(),
                    api.HandleAsync, sp.GetRequiredService<AppLog>()),
            };
        });
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<PortalRelayClient>());
        return services;
    }
}
