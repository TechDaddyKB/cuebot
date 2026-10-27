using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;
using Cuebot.Webhooks.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Webhooks;

/// <summary>Registers the webhook services (Ko-fi, Patreon, Fourthwall, Shopify, Throne, custom hooks).</summary>
public static class WebhooksServiceCollectionExtensions
{
    /// <summary>
    /// Adds settings (webhooks.json), one <see cref="IWebhookHandler"/> per service (routes kofi, patreon,
    /// fourthwall, shopify, throne, custom; each also registered as its concrete type and in <see cref="WebhookServices"/>),
    /// their activity-feed adapters, triggers and script functions. Nothing to start: handlers only react to deliveries.
    /// </summary>
    public static IServiceCollection AddWebhooks(this IServiceCollection services)
    {
        services.AddSettingsFile<WebhooksSettings>("webhooks.json");
        Add<KofiWebhookHandler>(services, (s, b, l) => new KofiWebhookHandler(s, b, l));
        Add<PatreonWebhookHandler>(services, (s, b, l) => new PatreonWebhookHandler(s, b, l));
        Add<FourthwallWebhookHandler>(services, (s, b, l) => new FourthwallWebhookHandler(s, b, l));
        Add<ShopifyWebhookHandler>(services, (s, b, l) => new ShopifyWebhookHandler(s, b, l));
        Add<ThroneWebhookHandler>(services, (s, b, l) => new ThroneWebhookHandler(s, b, l));
        Add<CustomWebhookHandler>(services, (s, b, l) => new CustomWebhookHandler(s, b, l));
        services.AddSingleton<WebhookServices>();
        foreach (var adapter in WebhookChatAdapter.All()) services.AddSingleton<IChatEventAdapter>(adapter);
        services.AddSingleton<ITriggerProvider, KofiTriggers>();
        services.AddSingleton<ITriggerProvider, PatreonTriggers>();
        services.AddSingleton<ITriggerProvider, ShopifyTriggers>();
        services.AddSingleton<ITriggerProvider, FourthwallTriggers>();
        services.AddSingleton<ITriggerProvider, ThroneTriggers>();
        services.AddSingleton<ITriggerProvider, CustomWebhookTriggers>();
        services.AddSingleton<IScriptFunctionProvider, WebhooksScriptFunctions>();
        return services;
    }

    private static void Add<T>(IServiceCollection services, Func<JsonFile<WebhooksSettings>, EventBus, AppLog, T> create)
        where T : WebhookServiceBase
    {
        services.AddSingleton(sp => create(sp.GetRequiredService<JsonFile<WebhooksSettings>>(), sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IWebhookHandler>(sp => sp.GetRequiredService<T>());
    }
}

/// <summary>All webhook services, in page order (for the Webhooks page and script functions).</summary>
public sealed class WebhookServices(
    KofiWebhookHandler kofi, PatreonWebhookHandler patreon, FourthwallWebhookHandler fourthwall,
    ShopifyWebhookHandler shopify, ThroneWebhookHandler throne, CustomWebhookHandler custom)
{
    public KofiWebhookHandler Kofi { get; } = kofi;
    public PatreonWebhookHandler Patreon { get; } = patreon;
    public FourthwallWebhookHandler Fourthwall { get; } = fourthwall;
    public ShopifyWebhookHandler Shopify { get; } = shopify;
    public ThroneWebhookHandler Throne { get; } = throne;
    public CustomWebhookHandler Custom { get; } = custom;

    public IReadOnlyList<WebhookServiceBase> All => [Kofi, Patreon, Fourthwall, Shopify, Throne, Custom];

    public WebhookServiceBase? Find(string service) =>
        All.FirstOrDefault(s => string.Equals(s.Service, service.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Script functions: <c>webhooks.sendTestEvent</c>, <c>webhooks.status</c>.</summary>
internal sealed class WebhooksScriptFunctions(WebhookServices services) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("webhooks.sendTestEvent", "Publish a local test event for a webhook service (kofi, patreon, fourthwall, shopify, throne, custom) and kind",
            ["service", "kind"], (_, args, _) =>
            {
                var service = services.Find(args.GetString("service"));
                var kind = args.GetString("kind");
                if (service is null) return Task.FromResult<object?>(false);
                if (kind.Length == 0) kind = service.TestEventKinds.FirstOrDefault() ?? "";
                return Task.FromResult<object?>(service.SendTestEvent(kind));
            }),
        new("webhooks.status", "Configured/enabled state and last delivery of each webhook service", [], (_, _, _) =>
            Task.FromResult<object?>(services.All.Select(s => new
            {
                service = s.Service,
                name = s.DisplayName,
                configured = s.IsConfigured,
                enabled = s.Enabled,
                lastDelivery = s.LastDelivery?.Time,
                lastOutcome = s.LastDelivery?.Outcome.ToString(),
                testKinds = s.TestEventKinds,
            }).ToArray())),
    ];
}
