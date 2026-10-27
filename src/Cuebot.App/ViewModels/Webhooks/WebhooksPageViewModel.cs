namespace Cuebot.App.ViewModels.Webhooks;

/// <summary>The Webhooks page: the webhook services (Ko-fi, Patreon, ...) and the relay that delivers them, as two tabs.</summary>
public sealed class WebhooksPageViewModel(WebhookServicesViewModel services, WebhooksViewModel relay) : PageViewModel("Webhooks")
{
    public WebhookServicesViewModel Services { get; } = services;

    public WebhooksViewModel Relay { get; } = relay;
}
