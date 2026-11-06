using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Chat;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Widgets;
using Cuebot.Obs;
using Cuebot.Core.Storage;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Members;
using Cuebot.Portal.Overlays;
using Cuebot.Portal.Relay;
using Cuebot.Portal.SignIn;
using Cuebot.Portal.Uploads;
using Cuebot.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Portal;

/// <summary>Registers the mod portal (docs/MOD_PORTAL_PLAN.md).</summary>
public static class PortalServiceCollectionExtensions
{
    /// <summary>
    /// Adds portal.json and portal-changes.json, <see cref="PortalDirectory"/>, <see cref="PortalChanges"/>,
    /// <see cref="PortalApi"/>, provider sign-in (<see cref="PortalIdentityExchange"/>) and <see cref="PortalRelayClient"/>
    /// (also as <see cref="IConnectionStatus"/>) with the sign-in handler on its device links, and uploads
    /// (portal-media.json, the portal-media folder, <see cref="PortalUploads"/>) and overlays (<see cref="PortalOverlays"/>).
    /// </summary>
    public static IServiceCollection AddPortal(this IServiceCollection services)
    {
        services.AddSettingsFile<PortalSettings>("portal.json");
        services.AddSettingsFile<PortalChangeLog>("portal-changes.json");
        services.AddSettingsFile<PortalMediaLibrary>("portal-media.json");
        services.AddSingleton(sp =>
        {
            var file = sp.GetRequiredService<JsonFile<PortalMediaLibrary>>();
            return new PortalMediaStore(file.Value, file.ScheduleSave, sp.GetRequiredService<AppPaths>().GetDirectory("portal-media"));
        });
        services.AddSingleton(sp =>
        {
            var file = sp.GetRequiredService<JsonFile<PortalSettings>>();
            return new PortalDirectory(file.Value, file.ScheduleSave);
        });
        services.AddSingleton(sp =>
        {
            var file = sp.GetRequiredService<JsonFile<PortalChangeLog>>();
            IPortalArea[] areas = [new ActionArea(sp.GetRequiredService<ProfileService>()), new CommandArea(sp.GetRequiredService<CommandStore>()),
                new UploadArea(sp.GetRequiredService<PortalMediaStore>()),
                new OverlayArea(sp.GetRequiredService<OverlayStore>(), sp.GetRequiredService<ElementStore>(), sp.GetRequiredService<WidgetCodeStore>()),
                new WidgetArea(sp.GetRequiredService<ElementStore>(), sp.GetRequiredService<WidgetCodeStore>())];
            return new PortalChanges(file.Value, file.ScheduleSave, sp.GetRequiredService<PortalDirectory>(), areas);
        });
        services.AddSingleton(sp => new PortalControls(sp.GetRequiredService<ProfileService>(),
            sp.GetService<ChatHub>(), Later(sp), sp.GetService<ObsService>(), sp.GetService<QueueService>(),
            sp.GetService<SpeechService>(), sp.GetService<AudioService>(), sp.GetService<AppLog>()));
        // Uploads are off (and say so) when no ffmpeg is found.
        services.AddSingleton(sp => new PortalUploads(sp.GetRequiredService<PortalMediaStore>(), sp.GetRequiredService<PortalChanges>(),
            MediaTools.Find() is { } tools ? new MediaCleaner(tools) : null, sp.GetRequiredService<AppPaths>().File("portal-quarantine"),
            sp.GetService<AppLog>()));
        services.AddSingleton(sp => new PortalOverlays(sp.GetRequiredService<OverlayStore>(), sp.GetRequiredService<ElementStore>(),
            sp.GetRequiredService<WidgetCodeStore>(), sp.GetRequiredService<AssetStore>(), sp.GetRequiredService<PortalChanges>(),
            sp.GetRequiredService<PortalMediaStore>()));
        services.AddSingleton(sp => new PortalApi(sp.GetRequiredService<PortalDirectory>(), sp.GetRequiredService<PortalChanges>(),
            sp.GetRequiredService<ProfileService>(), sp.GetRequiredService<CommandStore>(), sp.GetRequiredService<ActionEngine>(),
            sp.GetRequiredService<Registry>(), sp.GetRequiredService<PortalControls>(), sp.GetRequiredService<PortalUploads>(),
            sp.GetRequiredService<PortalOverlays>()));
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

    /// <summary>
    /// The connections, looked up when the dashboard asks. Resolving them up front would loop: the relay client is one
    /// of them, and it needs <see cref="PortalApi"/>, which needs <see cref="PortalControls"/>.
    /// </summary>
    private static IEnumerable<IConnectionStatus> Later(IServiceProvider sp)
    {
        foreach (var c in sp.GetServices<IConnectionStatus>()) yield return c;
    }
}
