using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Webhooks;
using Cuebot.Webhooks.Services;

namespace Cuebot.App.ViewModels.Webhooks;

/// <summary>
/// Webhook services page: per-service secret (masked), enable switch, last delivery and local test events for
/// Ko-fi, Patreon, Fourthwall, Shopify and Throne, the custom hook list and the last events. The URLs to paste into
/// each service are shown by the relay page.
/// </summary>
public sealed partial class WebhookServicesViewModel : PageViewModel
{
    private const int RecentShown = 50;
    private readonly WebhookServices _services;

    public WebhookServicesViewModel(WebhookServices services) : base("Webhooks")
    {
        _services = services;
        Services =
        [
            new WebhookCardViewModel(services.Kofi, services.Kofi.Settings, "Verification token",
                "On ko-fi.com open Settings → API → Webhooks, paste the Cuebot webhook URL for Ko-fi, then open Advanced and copy the Verification Token here. Donations, subscriptions, commissions and shop orders become triggers."),
            new WebhookCardViewModel(services.Patreon, services.Patreon.Settings, "Webhook secret",
                "On patreon.com/portal open My Webhooks, add the Cuebot URL for Patreon, tick the member and pledge events (and posts:publish if you like) and copy the webhook's secret here."),
            new WebhookCardViewModel(services.Fourthwall, services.Fourthwall.Settings, "Webhook secret",
                "In your Fourthwall admin open Settings → For developers → Webhooks, create a webhook with the Cuebot URL for Fourthwall for the events you want and copy the shop's webhook secret key here."),
            new WebhookCardViewModel(services.Shopify, services.Shopify.Settings, "Signing key / client secret",
                "In the Shopify admin open Settings → Notifications → Webhooks and create Order creation and Order payment webhooks (JSON) with the Cuebot URL for Shopify. Paste the key shown under 'Your webhooks will be signed with' (or your app's client secret)."),
            new WebhookCardViewModel(services.Throne, services.Throne.Settings, "URL token",
                "Throne does not sign webhooks, so Cuebot checks a token in the URL. Generate one, then on throne.com open Account → Settings → Integrations → Webhooks and add the Cuebot URL for Throne with /<token> appended (or ?token=<token>).",
                canGenerate: true),
        ];
        Custom = new CustomHooksViewModel(services.Custom);
        foreach (var service in services.All) service.Changed += () => OnUi(Refresh);
        RefreshRecent();
    }

    public IReadOnlyList<WebhookCardViewModel> Services { get; }

    public CustomHooksViewModel Custom { get; }

    /// <summary>Newest first, all services.</summary>
    public ObservableCollection<WebhookRecordViewModel> RecentEvents { get; } = [];

    public bool HasNoEvents => RecentEvents.Count == 0;

    private void Refresh()
    {
        foreach (var card in Services) card.Refresh();
        Custom.Refresh();
        RefreshRecent();
    }

    private void RefreshRecent()
    {
        var rows = _services.All
            .SelectMany(s => s.RecentEvents())
            .OrderByDescending(r => r.Time)
            .Take(RecentShown)
            .Select(r => new WebhookRecordViewModel(r))
            .ToArray();
        RecentEvents.Clear();
        foreach (var row in rows) RecentEvents.Add(row);
        OnPropertyChanged(nameof(HasNoEvents));
    }

    internal static string Describe(WebhookDeliveryInfo? info) =>
        info is null ? "No deliveries received yet" : $"Last delivery {info.Time.ToLocalTime():HH:mm:ss}: {info.Outcome} ({info.Detail})";
}

public sealed class WebhookRecordViewModel(WebhookRecord record)
{
    public WebhookRecord Record { get; } = record;
    public string Time => Record.Time.ToLocalTime().ToString("HH:mm:ss");
    public string Service => Record.Service;
    public string Summary => Record.Summary;
    public bool IsTest => Record.IsTest;
    public string EventType => Record.Type;
}

/// <summary>One service's card: secret, enable switch, last delivery and test events.</summary>
public sealed partial class WebhookCardViewModel : ObservableObject
{
    private readonly WebhookServiceBase _service;
    private readonly WebhookServiceSettings _settings;

    public WebhookCardViewModel(WebhookServiceBase service, WebhookServiceSettings settings, string secretLabel, string help, bool canGenerate = false)
    {
        _service = service;
        _settings = settings;
        SecretLabel = secretLabel;
        Help = help;
        CanGenerate = canGenerate;
        TestKinds = service.TestEventKinds;
        _selectedTestKind = TestKinds.FirstOrDefault() ?? "";
    }

    public string Name => _service.DisplayName;
    public string Route => _service.Service;
    public string SecretLabel { get; }
    public string Help { get; }
    public bool CanGenerate { get; }

    /// <summary>The secret; the view masks it (PasswordChar).</summary>
    public string Secret
    {
        get => _settings.Secret;
        set
        {
            var trimmed = value.Trim();
            if (trimmed == _settings.Secret) return;
            _settings.Secret = trimmed;
            _service.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaskedSecret));
        }
    }

    /// <summary>"Saved: ••••••••abcd", never the secret itself (except a generated Throne token, which must go in the URL).</summary>
    public string MaskedSecret => _settings.Secret.Length == 0 ? $"No {SecretLabel.ToLowerInvariant()} saved — deliveries are refused" : $"Saved: {WebhookSecrets.Mask(_settings.Secret)}";

    public bool Enabled
    {
        get => _settings.Enabled;
        set
        {
            if (value == _settings.Enabled) return;
            _settings.Enabled = value;
            _service.SaveSettings();
            OnPropertyChanged();
        }
    }

    public string LastDelivery => WebhookServicesViewModel.Describe(_service.LastDelivery);

    public IReadOnlyList<string> TestKinds { get; }

    [ObservableProperty] private string _selectedTestKind;
    [ObservableProperty] private string _info = "";

    /// <summary>Shown once after generating, so it can be copied into the service's URL.</summary>
    [ObservableProperty] private string _generatedToken = "";

    internal void Refresh() => OnPropertyChanged(nameof(LastDelivery));

    [RelayCommand]
    private void SendTest()
    {
        Info = _service.SendTestEvent(SelectedTestKind)
            ? $"Sent a local test {SelectedTestKind} (isTest = true); nothing was sent to {Name}."
            : $"Unknown test event '{SelectedTestKind}'";
    }

    [RelayCommand]
    private void Generate()
    {
        Secret = WebhookVerification.NewToken();
        GeneratedToken = Secret;
        Info = "New token generated. Update the URL in the service; the old one no longer works.";
    }
}

/// <summary>The custom hooks list: add, remove, enable, regenerate tokens, test.</summary>
public sealed partial class CustomHooksViewModel : ObservableObject
{
    private readonly CustomWebhookHandler _handler;

    public CustomHooksViewModel(CustomWebhookHandler handler)
    {
        _handler = handler;
        Reload();
    }

    public ObservableCollection<CustomHookViewModel> Hooks { get; } = [];

    public bool HasNoHooks => Hooks.Count == 0;

    public string LastDelivery => WebhookServicesViewModel.Describe(_handler.LastDelivery);

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _info = "";

    internal void Refresh() => OnPropertyChanged(nameof(LastDelivery));

    private void Reload()
    {
        Hooks.Clear();
        foreach (var hook in _handler.Hooks) Hooks.Add(new CustomHookViewModel(this, _handler, hook));
        OnPropertyChanged(nameof(HasNoHooks));
    }

    [RelayCommand]
    private void Add()
    {
        Error = "";
        try
        {
            var hook = _handler.AddHook(NewName);
            NewName = "";
            Reload();
            Info = $"Added '{hook.Name}'. Call …/custom/{hook.Name}?token=<token> (or send the token in an X-Cuebot-Token header).";
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
        }
    }

    internal void Remove(CustomWebhookSettings hook)
    {
        _handler.RemoveHook(hook);
        Reload();
    }

    internal void Report(string info) => Info = info;
}

public sealed partial class CustomHookViewModel(CustomHooksViewModel owner, CustomWebhookHandler handler, CustomWebhookSettings hook) : ObservableObject
{
    public string Name => hook.Name;

    /// <summary>Hooks' tokens are made by Cuebot and must be copied into the caller, so the view can reveal them on request.</summary>
    public string Token => hook.Token;

    public string MaskedToken => WebhookSecrets.Mask(hook.Token);

    [ObservableProperty] private bool _showToken;

    public bool Enabled
    {
        get => hook.Enabled;
        set
        {
            if (value == hook.Enabled) return;
            hook.Enabled = value;
            handler.SaveSettings();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void Regenerate()
    {
        hook.Token = WebhookVerification.NewToken();
        handler.SaveSettings();
        OnPropertyChanged(nameof(Token));
        OnPropertyChanged(nameof(MaskedToken));
        owner.Report($"New token for '{hook.Name}'; the old one no longer works.");
    }

    [RelayCommand]
    private void Test() =>
        owner.Report(handler.SendTestEvent(hook.Name) ? $"Sent a local test call of '{hook.Name}' (isTest = true)." : "Could not send a test event");

    [RelayCommand]
    private void Remove() => owner.Remove(hook);
}
