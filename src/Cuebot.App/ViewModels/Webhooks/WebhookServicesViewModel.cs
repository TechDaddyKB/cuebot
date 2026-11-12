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
                "Open ko-fi.com/manage/webhooks (Settings → API → Webhooks), paste the Cuebot webhook URL for Ko-fi, then open Advanced and copy the Verification Token here. Ko-fi's \"Send test\" buttons there arrive as test events. Donations, memberships, commissions and shop orders become triggers. Ko-fi sends payments only and never re-sends one that was dropped: with the shared relay, payments made while Cuebot is closed are lost for totals and goals (your own relay keeps them for 24 hours)."),
            new WebhookCardViewModel(services.Patreon, services.Patreon.Settings, "Webhook secret",
                "On patreon.com/portal open My Webhooks, add the Cuebot URL for Patreon, tick the member and pledge events (and posts:publish if you like) and copy the webhook's secret here."),
            new WebhookCardViewModel(services.Fourthwall, services.Fourthwall.Settings, "Webhook secret",
                "In your Fourthwall admin open Settings → For developers → Webhooks, create a webhook with the Cuebot URL for Fourthwall for the events you want and copy the shop's webhook secret key here."),
            new WebhookCardViewModel(services.Shopify, services.Shopify.Settings, "Signing key / client secret",
                "In the Shopify admin open Settings → Notifications → Webhooks and create Order creation and Order payment webhooks with the Cuebot URL for Shopify. Choose the JSON format: XML webhooks are refused. Paste the key shown under 'Your webhooks will be signed with' (or your app's client secret)."),
            new WebhookCardViewModel(services.Throne, services.Throne.Settings, "URL token (optional)",
                "On throne.com open Settings → Integrations → Webhooks and add the Cuebot URL for Throne. Throne signs every delivery and Cuebot checks that signature, so no secret is needed. Throne uses one signing key for every creator, though, so for extra safety you can generate a URL token and add /<token> to the URL (or ?token=<token>); once set, deliveries without it are refused.",
                canGenerate: true, secretOptional: "No URL token — Throne's signature alone is checked"),
        ];
        Custom = new CustomHooksViewModel(services.Custom);
        Kofi = new KofiOptionsViewModel(services.Kofi);
        services.Kofi.Tracker.Changed += () => OnUi(Kofi.Refresh);
        foreach (var service in services.All) service.Changed += () => OnUi(Refresh);
        RefreshRecent();
    }

    public IReadOnlyList<WebhookCardViewModel> Services { get; }

    public CustomHooksViewModel Custom { get; }

    public KofiOptionsViewModel Kofi { get; }

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

    private readonly string? _noSecretText;

    /// <param name="secretOptional">For a service that works without a secret (Throne): the text shown while none is saved.</param>
    public WebhookCardViewModel(WebhookServiceBase service, WebhookServiceSettings settings, string secretLabel, string help, bool canGenerate = false,
        string? secretOptional = null)
    {
        _service = service;
        _settings = settings;
        _noSecretText = secretOptional;
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
    public string MaskedSecret => _settings.Secret.Length > 0 ? $"Saved: {WebhookSecrets.Mask(_settings.Secret)}"
        : _noSecretText ?? $"No {SecretLabel.ToLowerInvariant()} saved — deliveries are refused";

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

/// <summary>Ko-fi extras: private supporters, the goal Cuebot keeps, product names, membership lapse days.</summary>
public sealed partial class KofiOptionsViewModel : ObservableObject
{
    private readonly KofiWebhookHandler _kofi;

    public KofiOptionsViewModel(KofiWebhookHandler kofi)
    {
        _kofi = kofi;
        _productsText = FormatProducts(kofi.Options.Products);
    }

    private KofiOptions Options => _kofi.Options;

    private void Save() => _kofi.SaveSettings();

    /// <summary>Shows private supporters' names and messages in triggers and overlays (off: "Anonymous", no message).</summary>
    public bool ShowPrivateSupporters
    {
        get => Options.ShowPrivateSupporters;
        set
        {
            if (value == Options.ShowPrivateSupporters) return;
            Options.ShowPrivateSupporters = value;
            Save();
            OnPropertyChanged();
        }
    }

    public string GoalTitle
    {
        get => Options.Goal.Title;
        set
        {
            value ??= "";
            if (value == Options.Goal.Title) return;
            _kofi.Tracker.SetGoal(title: value);
            OnPropertyChanged();
        }
    }

    public decimal? GoalTarget
    {
        get => (decimal)Options.Goal.Target;
        set
        {
            _kofi.Tracker.SetGoal(target: (double)(value ?? 0));
            OnPropertyChanged();
        }
    }

    public decimal? GoalStart
    {
        get => (decimal)Options.Goal.StartAmount;
        set
        {
            _kofi.Tracker.SetGoal(startAmount: (double)(value ?? 0));
            OnPropertyChanged();
        }
    }

    public string GoalCurrency
    {
        get => Options.Goal.Currency;
        set
        {
            value ??= "";
            if (value.Trim().Equals(Options.Goal.Currency, StringComparison.OrdinalIgnoreCase)) return;
            _kofi.Tracker.SetGoal(currency: value);
            OnPropertyChanged();
        }
    }

    public bool CountDonations { get => Options.Goal.CountDonations; set => SetCount(value, v => Options.Goal.CountDonations = v); }
    public bool CountMemberships { get => Options.Goal.CountMemberships; set => SetCount(value, v => Options.Goal.CountMemberships = v); }
    public bool CountCommissions { get => Options.Goal.CountCommissions; set => SetCount(value, v => Options.Goal.CountCommissions = v); }
    public bool CountShopOrders { get => Options.Goal.CountShopOrders; set => SetCount(value, v => Options.Goal.CountShopOrders = v); }

    private void SetCount(bool value, Action<bool> set, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        set(value);
        Save();
        OnPropertyChanged(name);
    }

    public decimal? LapseDays
    {
        get => Options.LapseDays;
        set
        {
            Options.LapseDays = Math.Max(1, (int)(value ?? 35));
            Save();
            OnPropertyChanged();
        }
    }

    /// <summary>"code = name | image URL" per line.</summary>
    [ObservableProperty] private string _productsText;

    partial void OnProductsTextChanged(string value)
    {
        Options.Products = ParseProducts(value ?? "");
        Save();
    }

    public string GoalProgress
    {
        get
        {
            var g = _kofi.Tracker.Goal();
            var money = (double v) => g.Currency.Length > 0 ? $"{v:0.##} {g.Currency}" : $"{v:0.##}";
            return g.Target > 0
                ? $"{money(g.Current)} of {money(g.Target)} ({g.Percent}%){(g.Reached ? " — reached" : "")}; {money(g.Raised)} counted by Cuebot"
                : $"No target set; {money(g.Raised)} counted by Cuebot";
        }
    }

    public string Summary
    {
        get
        {
            var members = _kofi.Tracker.Members();
            var lapsed = members.Count(m => m.LapseReported);
            return $"{members.Count} member(s) seen ({lapsed} probably lapsed), {_kofi.Tracker.Commissions().Count} open commission(s).";
        }
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(GoalProgress));
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void ResetGoal()
    {
        _kofi.Tracker.ResetGoal();
        Refresh();
    }

    internal static string FormatProducts(IEnumerable<KofiProduct> products) =>
        string.Join("\n", products.Select(p => p.ImageUrl.Length > 0 ? $"{p.Code} = {p.Name} | {p.ImageUrl}" : $"{p.Code} = {p.Name}"));

    internal static List<KofiProduct> ParseProducts(string text) =>
    [
        .. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0) return null;
                var rest = line[(eq + 1)..];
                var bar = rest.IndexOf('|', StringComparison.Ordinal);
                return new KofiProduct
                {
                    Code = line[..eq].Trim(),
                    Name = (bar >= 0 ? rest[..bar] : rest).Trim(),
                    ImageUrl = bar >= 0 ? rest[(bar + 1)..].Trim() : "",
                };
            })
            .OfType<KofiProduct>(),
    ];
}
