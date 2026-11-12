namespace Cuebot.App.ViewModels.Webhooks;

/// <summary>The Webhooks page: the webhook services (Ko-fi, Patreon, ...) and the relay that delivers them, as two tabs.</summary>
public sealed class WebhooksPageViewModel : PageViewModel
{
    public WebhooksPageViewModel(WebhookServicesViewModel services, WebhooksViewModel relay) : base("Webhooks")
    {
        Services = services;
        Relay = relay;
        // "Create webhook" on the Patreon card registers the URL the relay tab shows.
        services.Patreon.WebhookUrl = () => relay.WebhookUrl(Cuebot.Webhooks.Services.PatreonWebhookHandler.Route);
    }

    public WebhookServicesViewModel Services { get; }

    public WebhooksViewModel Relay { get; }
}
