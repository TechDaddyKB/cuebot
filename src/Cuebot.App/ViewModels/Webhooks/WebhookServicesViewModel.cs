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
                "On patreon.com/portal open My Webhooks, add the Cuebot URL for Patreon, tick the members:* and members:pledge:* events (and posts:* if you like) and copy the webhook's secret here. Or connect the Patreon API below and let Cuebot create the webhook and save its secret. If the URL stops working, Patreon pauses the webhook and later sends the backlog at once: Cuebot marks those deliveries stale."),
            new WebhookCardViewModel(services.Fourthwall, services.Fourthwall.Settings, "Webhook secret",
                "In your Fourthwall admin open Settings → For developers → Webhooks, create a webhook with the Cuebot URL for Fourthwall for the events you want and copy the shop's webhook secret key here (one key for the whole shop). Fourthwall only accepts an https address on port 443, so a direct local URL or a tunnel IP is refused: use the relay. Or connect the Fourthwall API below and let Cuebot set up the webhook (the secret still has to be copied by hand)."),
            new WebhookCardViewModel(services.Shopify, services.Shopify.Settings, "Signing key / client secret",
                "In the Shopify admin open Settings → Notifications → Webhooks and create Order creation and Order payment webhooks with the Cuebot URL for Shopify. Choose the JSON format: XML webhooks are refused. Paste the key shown under 'Your webhooks will be signed with' (or your app's client secret)."),
            new WebhookCardViewModel(services.Throne, services.Throne.Settings, "URL token (optional)",
                "On throne.com open Settings → Integrations → Webhooks and add the Cuebot URL for Throne. Throne signs every delivery and Cuebot checks that signature, so no secret is needed. Throne uses one signing key for every creator, though, so for extra safety you can generate a URL token and add /<token> to the URL (or ?token=<token>); once set, deliveries without it are refused.",
                canGenerate: true, secretOptional: "No URL token — Throne's signature alone is checked"),
        ];
        Custom = new CustomHooksViewModel(services.Custom);
        Kofi = new KofiOptionsViewModel(services.Kofi);
        services.Kofi.Tracker.Changed += () => OnUi(Kofi.Refresh);
        Patreon = new PatreonOptionsViewModel(services.Patreon);
        services.Patreon.Tracker.Changed += () => OnUi(() =>
        {
            Patreon.Refresh();
            Services[1].RefreshSecret();
        });
        Fourthwall = new FourthwallOptionsViewModel(services.Fourthwall);
        services.Fourthwall.Tracker.Changed += () => OnUi(Fourthwall.Refresh);
        foreach (var service in services.All) service.Changed += () => OnUi(Refresh);
        RefreshRecent();
    }

    public IReadOnlyList<WebhookCardViewModel> Services { get; }

    public CustomHooksViewModel Custom { get; }

    public KofiOptionsViewModel Kofi { get; }

    public PatreonOptionsViewModel Patreon { get; }

    public FourthwallOptionsViewModel Fourthwall { get; }

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

    /// <summary>After the secret was saved elsewhere (Patreon's webhook set up through the API).</summary>
    internal void RefreshSecret()
    {
        OnPropertyChanged(nameof(Secret));
        OnPropertyChanged(nameof(MaskedSecret));
    }

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

/// <summary>
/// Patreon extras: the optional API connection (the creator's own tokens), the campaign currency fallback, the stale
/// setting, viewer links, the webhook Cuebot can create and its health.
/// </summary>
public sealed partial class PatreonOptionsViewModel : ObservableObject
{
    private readonly PatreonWebhookHandler _patreon;

    public PatreonOptionsViewModel(PatreonWebhookHandler patreon) => _patreon = patreon;

    private PatreonOptions Options => _patreon.Options;
    private PatreonTracker Tracker => _patreon.Tracker;

    /// <summary>Gives the Cuebot URL for Patreon (from the relay tab) for "Create webhook"; null while there is none.</summary>
    public Func<string?> WebhookUrl { get; set; } = () => null;

    private void Save() => _patreon.SaveSettings();

    public string ClientId
    {
        get => Options.ClientId;
        set => SetText(value, Options.ClientId, v => Options.ClientId = v);
    }

    /// <summary>The view masks these (PasswordChar); the Saved line shows only the last characters.</summary>
    public string ClientSecret
    {
        get => Options.ClientSecret;
        set => SetText(value, Options.ClientSecret, v => Options.ClientSecret = v, nameof(SavedTokens));
    }

    public string AccessToken
    {
        get => Options.AccessToken;
        set => SetText(value, Options.AccessToken, v => Options.AccessToken = v, nameof(SavedTokens));
    }

    public string RefreshToken
    {
        get => Options.RefreshToken;
        set => SetText(value, Options.RefreshToken, v => Options.RefreshToken = v, nameof(SavedTokens));
    }

    private void SetText(string? value, string current, Action<string> set, string? also = null,
        [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed == current) return;
        set(trimmed);
        Save();
        OnPropertyChanged(name);
        if (also is not null) OnPropertyChanged(also);
    }

    public string SavedTokens =>
        $"Access token: {Saved(Options.AccessToken)}. Refresh token: {Saved(Options.RefreshToken)}. Client secret: {Saved(Options.ClientSecret)}.";

    private static string Saved(string secret) => secret.Length > 0 ? WebhookSecrets.Mask(secret) : "not saved";

    public string Currency
    {
        get => Options.Currency;
        set => SetText((value ?? "").ToUpperInvariant(), Options.Currency, v => Options.Currency = v);
    }

    public decimal? StaleHours
    {
        get => Options.StaleHours;
        set
        {
            Options.StaleHours = Math.Max(0, (int)(value ?? 6));
            Save();
            OnPropertyChanged();
        }
    }

    public bool LinkViewers
    {
        get => Options.LinkViewers;
        set
        {
            if (value == Options.LinkViewers) return;
            Options.LinkViewers = value;
            Save();
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private string _info = "";
    [ObservableProperty] private bool _busy;

    public string Status
    {
        get
        {
            if (!Tracker.HasToken) return "Not connected. Webhooks still work; the currency and member list come from deliveries only.";
            if (Tracker.NeedsReconnect) return Tracker.LastError;
            var t = Tracker.Totals();
            var campaign = Tracker.CampaignName is { Length: > 0 } n ? n : "your campaign";
            var sync = Tracker.LastSync is { } at ? $"members synced {at.ToLocalTime():g}" : "members not synced yet";
            return $"{campaign}: {t.PatronCount} patron(s), {t.PaidCount} paying, {t.Monthly:0.##} {t.Currency} a month; {Tracker.Tiers().Count} tier(s); {sync}."
                + (Tracker.LastError.Length > 0 ? " Last problem: " + Tracker.LastError : "");
        }
    }

    public string WebhookHealth => Tracker.WebhookHealth is { } h
        ? h.Paused
            ? $"Patreon PAUSED the webhook after {h.ConsecutiveFailures} failed deliveries (checked {h.CheckedAt.ToLocalTime():g}). Fix the relay, then press Resume: Patreon sends what it held back."
            : $"Webhook active{(h.LastAttemptedAt is { } last ? $", last delivery {last.ToLocalTime():g}" : "")} (checked {h.CheckedAt.ToLocalTime():g})."
        : "No webhook made by Cuebot. A webhook made by hand in the portal works too, but its health cannot be read from here.";

    public bool CanResume => Tracker.WebhookHealth is { Paused: true };

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(WebhookHealth));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(SavedTokens));
        OnPropertyChanged(nameof(AccessToken));
        OnPropertyChanged(nameof(RefreshToken));
    }

    private async Task Run(Func<Task<string>> work)
    {
        if (Busy) return;
        Busy = true;
        try { Info = await work(); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or FormatException or System.Text.Json.JsonException or PatreonAuthException)
        {
            Info = ex is PatreonAuthException ? ex.Message : $"Patreon: {ex.Message}";
        }
        finally
        {
            Busy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private Task Connect() => Run(() => Tracker.ConnectAsync());

    [RelayCommand]
    private Task Sync() => Run(() => Tracker.SyncMembersAsync());

    [RelayCommand]
    private Task CreateWebhook() => Run(() => Tracker.CreateWebhookAsync(WebhookUrl() ?? "", secret =>
    {
        _patreon.Settings.Secret = secret;
        _patreon.SaveSettings();
    }));

    [RelayCommand]
    private Task CheckWebhook() => Run(() => Tracker.CheckWebhookAsync());

    [RelayCommand]
    private Task ResumeWebhook() => Run(() => Tracker.ResumeWebhookAsync());

    [RelayCommand]
    private void Disconnect()
    {
        Tracker.Disconnect();
        Info = "Disconnected: the tokens were removed. Webhooks keep working.";
        Refresh();
    }
}

/// <summary>
/// Fourthwall extras: supporter names on stream, the shop address for gift links, and the optional Open API connection
/// (the shop's API user) with webhook setup, catch-up, the streaming status, gift draws and the showcase products.
/// </summary>
public sealed partial class FourthwallOptionsViewModel : ObservableObject
{
    private readonly FourthwallWebhookHandler _fourthwall;

    public FourthwallOptionsViewModel(FourthwallWebhookHandler fourthwall) => _fourthwall = fourthwall;

    private FourthwallOptions Options => _fourthwall.Options;
    private FourthwallTracker Tracker => _fourthwall.Tracker;

    /// <summary>Gives the Cuebot URL for Fourthwall (from the relay tab) for "Set up webhook"; null while there is none.</summary>
    public Func<string?> WebhookUrl { get; set; } = () => null;

    private void Save() => _fourthwall.SaveSettings();

    private void SetText(string? value, string current, Action<string> set, string? also = null,
        [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed == current) return;
        set(trimmed);
        Save();
        OnPropertyChanged(name);
        if (also is not null) OnPropertyChanged(also);
    }

    private void SetFlag(bool value, bool current, Action<bool> set, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (value == current) return;
        set(value);
        Save();
        OnPropertyChanged(name);
    }

    public bool HideSupporterNames
    {
        get => Options.HideSupporterNames;
        set => SetFlag(value, Options.HideSupporterNames, v => Options.HideSupporterNames = v);
    }

    public string ShopAddress
    {
        get => Options.ShopAddress;
        set => SetText(value, Options.ShopAddress, v => Options.ShopAddress = v);
    }

    public string ApiUser
    {
        get => Options.ApiUser;
        set => SetText(value, Options.ApiUser, v => Options.ApiUser = v, nameof(SavedCredentials));
    }

    /// <summary>The view masks this (PasswordChar); the Saved line shows only the last characters.</summary>
    public string ApiPassword
    {
        get => Options.ApiPassword;
        set => SetText(value, Options.ApiPassword, v => Options.ApiPassword = v, nameof(SavedCredentials));
    }

    public string SavedCredentials =>
        $"API user: {(Options.ApiUser.Length > 0 ? WebhookSecrets.Mask(Options.ApiUser) : "not saved")}. Password: {(Options.ApiPassword.Length > 0 ? WebhookSecrets.Mask(Options.ApiPassword) : "not saved")}.";

    public bool MarkStreamingLive
    {
        get => Options.MarkStreamingLive;
        set => SetFlag(value, Options.MarkStreamingLive, v => Options.MarkStreamingLive = v);
    }

    public bool RunGiftDraws
    {
        get => Options.RunGiftDraws;
        set
        {
            SetFlag(value, Options.RunGiftDraws, v => Options.RunGiftDraws = v);
            OnPropertyChanged(nameof(GiftingNote));
        }
    }

    public string EntryCommand
    {
        get => Options.EntryCommand;
        set => SetText(value, Options.EntryCommand, v => Options.EntryCommand = v);
    }

    public decimal? CatchUpMinutes
    {
        get => Options.CatchUpMinutes;
        set
        {
            Options.CatchUpMinutes = Math.Max(0, (int)(value ?? 10));
            Save();
            OnPropertyChanged();
        }
    }

    public string ProductsCollection
    {
        get => Options.ProductsCollection;
        set => SetText(value, Options.ProductsCollection, v => Options.ProductsCollection = v);
    }

    [ObservableProperty] private string _info = "";
    [ObservableProperty] private bool _busy;

    public string Status
    {
        get
        {
            if (!Tracker.HasCredentials) return "Not connected. Webhooks still work; tier names, gift links from the shop address above and everything else come from deliveries only.";
            if (Tracker.NeedsReconnect) return Tracker.LastError;
            var (count, _) = Tracker.MemberCounts();
            var shop = Tracker.ShopName is { Length: > 0 } n ? n : "your shop";
            var sync = Tracker.LastMemberSync is { } at ? $"members synced {at.ToLocalTime():g}" : "members not synced yet";
            var catchUp = Tracker.LastCatchUp is { At: { } c } last ? $"; last catch-up {c.ToLocalTime():t} ({last.Count} replayed)" : "";
            return $"{shop}: {Tracker.Tiers().Count} tier(s), {count} member(s), {Tracker.Products().Count} product(s) for the showcase; {sync}{catchUp}."
                + (Tracker.LastError.Length > 0 ? " Last problem: " + Tracker.LastError : "");
        }
    }

    public string WebhookHealth
    {
        get
        {
            if (!Tracker.HasCredentials) return "";
            if (Tracker.WebhookUrl.Length == 0) return "No Fourthwall webhook checked yet. Press Check webhook or Set up webhook.";
            var missing = Tracker.MissingWebhookTypes();
            return missing.Count == 0 ? "The webhook sends every event Cuebot handles."
                : $"The webhook does not send: {string.Join(", ", missing)}. Press Set up webhook to add them.";
        }
    }

    public string GiftingNote => Options.RunGiftDraws && Tracker.GiftingPlatform is { Length: > 0 } p && p != "OPEN_API"
        ? $"Fourthwall's stream gifting is run by {p}, not Open API, so Fourthwall will not wait for Cuebot's draw. Change it in Fourthwall's gifting settings."
        : "";

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(WebhookHealth));
        OnPropertyChanged(nameof(GiftingNote));
        OnPropertyChanged(nameof(SavedCredentials));
        OnPropertyChanged(nameof(ApiUser));
        OnPropertyChanged(nameof(ApiPassword));
    }

    private async Task Run(Func<Task<string>> work)
    {
        if (Busy) return;
        Busy = true;
        try { Info = await work(); }
        catch (Exception ex) when (FourthwallTracker.IsApiError(ex))
        {
            Info = ex is FourthwallAuthException ? ex.Message : $"Fourthwall: {ex.Message}";
        }
        finally
        {
            Busy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private Task Connect() => Run(() => Tracker.ConnectAsync(WebhookUrl() ?? ""));

    [RelayCommand]
    private Task Sync() => Run(() => Tracker.SyncMembersAsync());

    [RelayCommand]
    private Task RefreshProducts() => Run(() => Tracker.RefreshProductsAsync());

    [RelayCommand]
    private Task SetUpWebhook() => Run(() => Tracker.SetUpWebhookAsync(WebhookUrl() ?? ""));

    [RelayCommand]
    private Task CheckWebhook() => Run(() => Tracker.CheckWebhookAsync(WebhookUrl() ?? ""));

    [RelayCommand]
    private Task CatchUp() => Run(() => Tracker.CatchUpAsync());

    [RelayCommand]
    private void Disconnect()
    {
        Tracker.Disconnect();
        Info = "Disconnected: the API user was removed. Webhooks keep working.";
        Refresh();
    }
}
