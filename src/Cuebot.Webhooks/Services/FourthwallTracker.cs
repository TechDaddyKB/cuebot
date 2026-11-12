using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays.Session;
using Cuebot.Core.Storage;

namespace Cuebot.Webhooks.Services;

/// <summary>fourthwall.json: what Cuebot learnt from the Open API and from webhooks. No emails are kept here.</summary>
public sealed class FourthwallData
{
    public string ShopId { get; set; } = "";
    public string ShopName { get; set; } = "";

    /// <summary>The shop's public address (like shop.example.com), for gift claim links.</summary>
    public string PublicDomain { get; set; } = "";

    public List<FourthwallTier> Tiers { get; set; } = [];

    /// <summary>The last membership state Cuebot saw per member id (from webhooks and member syncs).</summary>
    public Dictionary<string, FourthwallMember> Members { get; set; } = new(StringComparer.Ordinal);

    public DateTimeOffset? LastMemberSync { get; set; }

    /// <summary>The products the showcase widget shows (from the API).</summary>
    public List<FourthwallProduct> Products { get; set; } = [];

    public DateTimeOffset? ProductsRead { get; set; }

    /// <summary>Catch-up replays only events newer than this (set when the API was first connected), so old events never alert.</summary>
    public DateTimeOffset? CatchUpSince { get; set; }

    public DateTimeOffset? LastCatchUp { get; set; }

    /// <summary>How many events the last catch-up replayed.</summary>
    public int LastCatchUpCount { get; set; }

    /// <summary>The webhook configuration's types as Fourthwall last reported them (empty until checked).</summary>
    public List<string> WebhookTypes { get; set; } = [];

    public string WebhookUrl { get; set; } = "";

    /// <summary>Who runs stream gift draws (TWITCH, STREAMELEMENTS, OPEN_API, NOT_SELECTED), as last read.</summary>
    public string GiftingPlatform { get; set; } = "";
}

public sealed class FourthwallTier
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double Monthly { get; set; }
    public string Currency { get; set; } = "";
}

public sealed class FourthwallMember
{
    public string TierId { get; set; } = "";

    /// <summary>ACTIVE, CANCELLED, SUSPENDED or NONE.</summary>
    public string State { get; set; } = "";

    /// <summary>The price per month (annual / 12).</summary>
    public double Monthly { get; set; }

    public DateTimeOffset Updated { get; set; }
}

public sealed class FourthwallProduct
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double Price { get; set; }
    public string Currency { get; set; } = "";
    public string FormattedPrice { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public bool SoldOut { get; set; }
    public string Url { get; set; } = "";
}

/// <summary>Sales totals read from the API.</summary>
public sealed record FourthwallTotals(double Revenue, int Orders, double Donations, int DonationCount, string Currency, DateTimeOffset Since);

/// <summary>
/// The optional Fourthwall Open API connection and what Cuebot keeps from it and from webhooks: the shop (name, public
/// address for gift links), membership tiers (names for %tier%) and the last tier of each member (so Subscription
/// Changed knows upgrades from downgrades), the member count (global <c>fourthwallMemberCount</c>), the products the
/// showcase widget shows, catch-up of events no delivery brought, the webhook's setup, the shop's streaming status and gift
/// draws run from Twitch chat. Overlay session data: <c>fourthwall</c>. Without the API everything still works from
/// webhooks alone.
/// </summary>
public sealed class FourthwallTracker : ISessionDataSource, IPeriodicService, IDisposable
{
    public static readonly TimeSpan MemberSyncEvery = TimeSpan.FromHours(6);
    public static readonly TimeSpan ProductsEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FirstWorkDelay = TimeSpan.FromSeconds(30);

    /// <summary>After a periodic attempt (good or bad) the next waits at least this long: no retry storms.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>Catch-up looks back at most this far (Fourthwall retries a delivery for a short while only).</summary>
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(24);

    private readonly JsonFile<WebhooksSettings> _settings;
    private readonly JsonFile<FourthwallData>? _file;
    private readonly FourthwallData _memory = new();
    private readonly AppLog _log;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly DateTimeOffset _started;
    private readonly Dictionary<string, GiftDraw> _draws = new(StringComparer.Ordinal);
    private IReadOnlyList<FourthwallMemberInfo> _members = [];
    private int _busy;
    private bool _productsDirty;
    private DateTimeOffset _nextTry = DateTimeOffset.MinValue;
    private DateTimeOffset _nextCatchUp = DateTimeOffset.MinValue;

    /// <param name="path">fourthwall.json; null keeps everything in memory (tests).</param>
    public FourthwallTracker(JsonFile<WebhooksSettings> settings, AppLog log, HttpClient? http = null, string? path = null, TimeProvider? time = null,
        string? apiBaseUrl = null)
    {
        _settings = settings;
        _log = log;
        _time = time ?? TimeProvider.System;
        _started = _time.GetUtcNow();
        if (path is not null) _file = new JsonFile<FourthwallData>(path, log);
        Api = new FourthwallApi(http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, () => Options, apiBaseUrl);
    }

    /// <summary>Global variables for the member count (set by the app; null in most tests).</summary>
    internal GlobalVariables? Globals { get; set; }

    /// <summary>Publishes an event the API listed but no delivery brought (set by the app to the handler's replay).</summary>
    internal Func<JsonElement, TimeSpan, CancellationToken, Task<bool>>? Replay { get; set; }

    internal FourthwallApi Api { get; }

    public event Action? Changed;

    private FourthwallData Data => _file?.Value ?? _memory;
    private FourthwallOptions Options => _settings.Value.FourthwallOptions;

    /// <summary>True when an API user and password are saved.</summary>
    public bool HasCredentials => Api.HasCredentials;

    /// <summary>Set when Fourthwall refused the API user: no more periodic calls until the user connects again.</summary>
    public bool NeedsReconnect { get; private set; }

    /// <summary>The last connection problem ("" when the last call worked).</summary>
    public string LastError { get; private set; } = "";

    /// <summary>Usable for API calls: credentials, no refusal since the last connect, and the shop known.</summary>
    public bool Connected => HasCredentials && !NeedsReconnect && Data.ShopId.Length > 0;

    public string ShopName
    {
        get { lock (_gate) return Data.ShopName; }
    }

    /// <summary>The shop address for gift links: typed in on the Webhooks page, else the one the API gave.</summary>
    public string ShopAddress
    {
        get
        {
            var typed = Options.ShopAddress.Trim();
            if (typed.Length > 0) return typed;
            lock (_gate) return Data.PublicDomain;
        }
    }

    public IReadOnlyList<FourthwallTier> Tiers()
    {
        lock (_gate) return [.. Data.Tiers];
    }

    public string TierName(string id)
    {
        lock (_gate) return Data.Tiers.FirstOrDefault(t => t.Id == id)?.Name ?? "";
    }

    public IReadOnlyList<FourthwallProduct> Products()
    {
        lock (_gate) return [.. Data.Products];
    }

    /// <summary>Active members (ACTIVE, or CANCELLED until the period ends), and how many per tier name.</summary>
    public (int Count, IReadOnlyDictionary<string, int> PerTier) MemberCounts()
    {
        lock (_gate)
        {
            var active = Data.Members.Values.Where(m => m.State is "ACTIVE" or "CANCELLED").ToArray();
            var perTier = active.GroupBy(m => Data.Tiers.FirstOrDefault(t => t.Id == m.TierId)?.Name is { Length: > 0 } n ? n : m.TierId)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            return (active.Length, perTier);
        }
    }

    public DateTimeOffset? LastMemberSync
    {
        get { lock (_gate) return Data.LastMemberSync; }
    }

    public (DateTimeOffset? At, int Count) LastCatchUp
    {
        get { lock (_gate) return (Data.LastCatchUp, Data.LastCatchUpCount); }
    }

    public string GiftingPlatform
    {
        get { lock (_gate) return Data.GiftingPlatform; }
    }

    /// <summary>Webhook types Cuebot handles that the shop's webhook does not send (empty when unknown or complete).</summary>
    public IReadOnlyList<string> MissingWebhookTypes()
    {
        lock (_gate)
            return Data.WebhookTypes.Count == 0 ? [] : [.. FourthwallApi.WebhookTypes.Except(Data.WebhookTypes, StringComparer.OrdinalIgnoreCase)];
    }

    public string WebhookUrl
    {
        get { lock (_gate) return Data.WebhookUrl; }
    }

    /// <summary>Whether an email belongs to an active member (from the last member sync; needs the API).</summary>
    public FourthwallMemberInfo? FindMember(string email)
    {
        var wanted = email.Trim();
        if (wanted.Length == 0) return null;
        return _members.FirstOrDefault(m => string.Equals(m.Email, wanted, StringComparison.OrdinalIgnoreCase));
    }

    // ---- from webhooks ----

    /// <summary>
    /// Adds what the delivery does not say (tier names, the previous tier, gift claim links, whether Cuebot runs the draw)
    /// and remembers the member's tier. Test events are enriched but change nothing.
    /// </summary>
    internal void Record(string type, Dictionary<string, object?> a)
    {
        var isTest = a.TryGetValue("isTest", out var t) && t is true;
        switch (type)
        {
            case FourthwallEventTypes.SubscriptionPurchased or FourthwallEventTypes.SubscriptionChanged or FourthwallEventTypes.SubscriptionExpired:
                Membership(type, a, isTest);
                break;
            case FourthwallEventTypes.GiftPurchase or FourthwallEventTypes.GiftDrawStarted or FourthwallEventTypes.GiftDrawEnded:
                GiftLinks(a);
                if (type == FourthwallEventTypes.GiftDrawStarted) OpenDraw(a, isTest);
                break;
            case FourthwallEventTypes.ProductCreated or FourthwallEventTypes.ProductUpdated or FourthwallEventTypes.CollectionUpdated:
                if (!isTest) _productsDirty = true;
                break;
        }
    }

    private void Membership(string type, Dictionary<string, object?> a, bool isTest)
    {
        var memberId = WebhookFilters.Text(a, "fw.id");
        var tierId = WebhookFilters.Text(a, "fw.tierId");
        var state = WebhookFilters.Text(a, "fw.state");
        var monthly = WebhookFilters.Number(a, "fw.monthlyAmount");
        var tierName = TierName(tierId);
        a["tier"] = tierName;
        a["fw.tierName"] = tierName;
        FourthwallMember? before;
        lock (_gate) before = Data.Members.TryGetValue(memberId, out var m) ? m : null;
        a["fw.previousTierId"] = before?.TierId ?? "";
        a["fw.previousTier"] = before is null ? "" : TierName(before.TierId);
        a["fw.previousAmount"] = before?.Monthly ?? 0;
        a["fw.previousState"] = before?.State ?? "";
        a["direction"] = before is null || type != FourthwallEventTypes.SubscriptionChanged ? ""
            : monthly > before.Monthly + 0.001 ? "upgrade"
            : monthly < before.Monthly - 0.001 ? "downgrade"
            : before.TierId != tierId ? "change" : "same";
        a["fw.direction"] = a["direction"];
        if (isTest || memberId.Length == 0) return;
        lock (_gate)
        {
            if (type == FourthwallEventTypes.SubscriptionExpired || state == "NONE") Data.Members.Remove(memberId);
            else Data.Members[memberId] = new FourthwallMember { TierId = tierId, State = state, Monthly = monthly, Updated = _time.GetUtcNow() };
            Save();
        }
        PublishTotals();
        Changed?.Invoke();
    }

    /// <summary><c>fw.giftUrls</c> / <c>fw.gifts[i].url</c>: where winners claim (https://&lt;shop&gt;/gifts/&lt;giftId&gt;).</summary>
    private void GiftLinks(Dictionary<string, object?> a)
    {
        var shop = ShopAddress.Trim().TrimEnd('/');
        if (shop.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || shop.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            shop = new Uri(shop).Authority;
        var urls = new List<string>();
        for (var i = 0; a.ContainsKey($"fw.gifts[{i}].id"); i++)
        {
            var id = WebhookFilters.Text(a, $"fw.gifts[{i}].id");
            var url = shop.Length > 0 && id.Length > 0 ? $"https://{shop}/gifts/{Uri.EscapeDataString(id)}" : "";
            a[$"fw.gifts[{i}].url"] = url;
            if (url.Length > 0) urls.Add(url);
        }
        a["fw.giftUrls"] = string.Join(" ", urls);
        a["fw.giftUrl"] = urls.FirstOrDefault() ?? "";
    }

    // ---- gift draws ----

    private sealed class GiftDraw(string id, DateTimeOffset deadline)
    {
        public string Id { get; } = id;
        public DateTimeOffset Deadline { get; } = deadline;
        public Dictionary<string, FourthwallDrawEntrant> Entrants { get; } = new(StringComparer.Ordinal);
        public bool Finishing { get; set; }
    }

    private void OpenDraw(Dictionary<string, object?> a, bool isTest)
    {
        var command = Options.EntryCommand.Trim();
        var runs = Options.RunGiftDraws && Connected && !isTest && command.Length > 0;
        a["fw.cuebotRunsDraw"] = runs;
        a["fw.entryCommand"] = command;
        if (!runs) return;
        var id = WebhookFilters.Text(a, "fw.drawId");
        var seconds = WebhookFilters.Number(a, "fw.durationSeconds");
        if (id.Length == 0) return;
        lock (_gate) _draws.TryAdd(id, new GiftDraw(id, _time.GetUtcNow() + TimeSpan.FromSeconds(seconds > 0 ? seconds : 60)));
        _log.Info("Fourthwall", $"Gift draw open: viewers can type {command} in Twitch chat for {(seconds > 0 ? seconds : 60):0} seconds");
        Changed?.Invoke();
    }

    /// <summary>Entrants so far in the open draws.</summary>
    public int OpenDrawEntrants()
    {
        lock (_gate) return _draws.Values.Sum(d => d.Entrants.Count);
    }

    public bool DrawOpen
    {
        get { lock (_gate) return _draws.Values.Any(d => !d.Finishing); }
    }

    /// <summary>Bus events: Twitch chat entries for open draws, and the Twitch stream going live or offline.</summary>
    internal void OnEvent(BotEvent e)
    {
        if (e.Type == ChatHub.MessageEvent) Enter(e);
        else if (e.Type is "twitch.streamOnline" or "twitch.streamOffline" && Options.MarkStreamingLive && Connected)
        {
            var online = e.Type == "twitch.streamOnline";
            var id = e.Args.GetString("userId");
            var login = e.Args.GetString("userName");
            if (id.Length == 0 || login.Length == 0) return;
            _ = Task.Run(() => SetStreamingAsync(online, id, login));
        }
    }

    private void Enter(BotEvent e)
    {
        if (!string.Equals(e.Args.GetString("platform"), "Twitch", StringComparison.OrdinalIgnoreCase)) return;
        var command = Options.EntryCommand.Trim();
        var text = e.Args.GetString("message").Trim();
        if (command.Length == 0 || !(text.Equals(command, StringComparison.OrdinalIgnoreCase)
                                     || text.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase))) return;
        var userId = e.Args.GetString("userId");
        var login = e.Args.GetString("userName") is { Length: > 0 } l ? l : e.Args.GetString("user");
        if (userId.Length == 0 || login.Length == 0) return;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var draw in _draws.Values.Where(d => !d.Finishing && now <= d.Deadline))
                draw.Entrants.TryAdd(userId, new FourthwallDrawEntrant(userId, login.ToLowerInvariant()));
        }
    }

    /// <summary>Hands Fourthwall the entrants of every draw whose time is up. Returns how many draws were finished.</summary>
    internal async Task<int> FinishDueDrawsAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        List<GiftDraw> due;
        lock (_gate)
        {
            due = [.. _draws.Values.Where(d => !d.Finishing && now > d.Deadline)];
            foreach (var d in due) d.Finishing = true;
        }
        var finished = 0;
        foreach (var draw in due)
        {
            IReadOnlyList<FourthwallDrawEntrant> entrants;
            lock (_gate) entrants = [.. draw.Entrants.Values];
            try
            {
                await Api.FinishDrawAsync(draw.Id, entrants, ct).ConfigureAwait(false);
                _log.Info("Fourthwall", $"Gift draw finished with {entrants.Count} entrant(s); Fourthwall picks the winners");
                finished++;
            }
            catch (Exception ex) when (IsApiError(ex))
            {
                Fail(ex);
            }
            lock (_gate) _draws.Remove(draw.Id);
        }
        if (due.Count > 0) Changed?.Invoke();
        return finished;
    }

    // ---- streaming ----

    /// <summary>Marks the shop as streaming (or not) on the Twitch channel. Returns a line for the UI.</summary>
    public async Task<string> SetStreamingAsync(bool online, string broadcasterId, string broadcasterLogin, CancellationToken ct = default)
    {
        if (!HasCredentials) return Report("Connect the Fourthwall API first.");
        try
        {
            if (online) await Api.StartStreamingAsync(broadcasterId, broadcasterLogin, "", ct).ConfigureAwait(false);
            else await Api.EndStreamingAsync(broadcasterId, broadcasterLogin, ct).ConfigureAwait(false);
            _log.Info("Fourthwall", online ? "Told Fourthwall the stream is live" : "Told Fourthwall the stream ended");
            return Report(online ? "Fourthwall now shows the shop as streaming." : "Fourthwall now shows the stream as ended.");
        }
        catch (Exception ex) when (IsApiError(ex))
        {
            Fail(ex);
            return LastError;
        }
    }

    // ---- connection ----

    /// <summary>Reads the shop, tiers, members, products, gifting setup and the webhook. Returns a line for the UI.</summary>
    public async Task<string> ConnectAsync(string webhookUrl = "", CancellationToken ct = default)
    {
        NeedsReconnect = false;
        LastError = "";
        try
        {
            var shop = await Api.ShopAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                Data.ShopId = shop.Id;
                Data.ShopName = shop.Name;
                Data.PublicDomain = shop.PublicDomain.Length > 0 ? shop.PublicDomain : shop.Domain;
                Data.CatchUpSince ??= _time.GetUtcNow();
                Save();
            }
            await ReadTiersAsync(ct).ConfigureAwait(false);
            await SyncMembersAsync(ct).ConfigureAwait(false);
            await RefreshProductsAsync(ct).ConfigureAwait(false);
            await ReadGiftingAsync(ct).ConfigureAwait(false);
            await CheckWebhookAsync(webhookUrl, ct).ConfigureAwait(false);
            var (count, _) = MemberCounts();
            return Report($"Connected to {shop.Name}: {Tiers().Count} tier(s), {count} member(s), {Products().Count} product(s).");
        }
        catch (Exception ex) when (IsApiError(ex))
        {
            Fail(ex);
            return LastError;
        }
    }

    /// <summary>Forgets the API user (the webhooks keep working).</summary>
    public void Disconnect()
    {
        Options.ApiUser = "";
        Options.ApiPassword = "";
        _settings.ScheduleSave();
        NeedsReconnect = false;
        LastError = "";
        _members = [];
        Changed?.Invoke();
    }

    private async Task ReadTiersAsync(CancellationToken ct)
    {
        var tiers = await Api.TiersAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            Data.Tiers = [.. tiers.Select(t => new FourthwallTier { Id = t.Id, Name = t.Name, Monthly = t.MonthlyAmount, Currency = t.Currency })];
            Save();
        }
    }

    private async Task ReadGiftingAsync(CancellationToken ct)
    {
        try
        {
            var config = await Api.GiftingConfigAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                Data.GiftingPlatform = config.Platform;
                Save();
            }
        }
        catch (HttpRequestException)
        {
            // Gifting may be off for the shop; nothing to show then.
        }
    }

    /// <summary>Reads the member list (emails stay in memory only) and recounts members per tier.</summary>
    public async Task<string> SyncMembersAsync(CancellationToken ct = default)
    {
        if (!HasCredentials) return Report("Connect the Fourthwall API first.");
        if (Tiers().Count == 0) await ReadTiersAsync(ct).ConfigureAwait(false);
        var members = await Api.MembersAsync(ct).ConfigureAwait(false);
        _members = members;
        lock (_gate)
        {
            Data.Members = members.Where(m => m.Id.Length > 0 && m.State != "NONE").ToDictionary(m => m.Id, m => new FourthwallMember
            {
                TierId = m.TierId, State = m.State, Monthly = FourthwallWebhookHandler.Monthly(m.Amount, m.Interval), Updated = _time.GetUtcNow(),
            }, StringComparer.Ordinal);
            Data.LastMemberSync = _time.GetUtcNow();
            Save();
        }
        PublishTotals();
        return Report($"Synced {members.Count} Fourthwall member(s).");
    }

    /// <summary>Reads the products for the showcase widget (the chosen collection, else every public product).</summary>
    public async Task<string> RefreshProductsAsync(CancellationToken ct = default)
    {
        if (!HasCredentials) return Report("Connect the Fourthwall API first.");
        _productsDirty = false;
        var products = await Api.ProductsAsync(Options.ProductsCollection, 50, ct).ConfigureAwait(false);
        var shop = ShopAddress.Trim().TrimEnd('/');
        lock (_gate)
        {
            Data.Products = [.. products.Where(p => p.Access is "" or "PUBLIC").Select(p => new FourthwallProduct
            {
                Id = p.Id, Name = p.Name, Price = p.Price, Currency = p.Currency,
                FormattedPrice = p.Price > 0 ? Money.Format(p.Price, p.Currency) : "", ImageUrl = p.ImageUrl, SoldOut = p.SoldOut,
                Url = shop.Length > 0 && p.Slug.Length > 0 ? $"https://{shop}/products/{p.Slug}" : "",
            })];
            Data.ProductsRead = _time.GetUtcNow();
            Save();
        }
        Changed?.Invoke();
        return Report($"Read {Products().Count} Fourthwall product(s) for the showcase.");
    }

    // ---- webhook ----

    /// <summary>Reads the shop's webhooks and reports which types the one for <paramref name="url"/> is missing.</summary>
    public async Task<string> CheckWebhookAsync(string url = "", CancellationToken ct = default)
    {
        if (!HasCredentials) return Report("Connect the Fourthwall API first.");
        var hooks = await Api.WebhooksAsync(ct).ConfigureAwait(false);
        var hook = Pick(hooks, url);
        lock (_gate)
        {
            Data.WebhookTypes = hook is null ? [] : [.. hook.AllowedTypes];
            Data.WebhookUrl = hook?.Url ?? "";
            Save();
        }
        Changed?.Invoke();
        if (hook is null) return Report(url.Length > 0 ? "No Fourthwall webhook sends to Cuebot's URL yet. Press Set up webhook." : "No Fourthwall webhook found.");
        var missing = MissingWebhookTypes();
        return Report(missing.Count == 0 ? "The Fourthwall webhook sends every event Cuebot handles."
            : $"The Fourthwall webhook does not send: {string.Join(", ", missing)}. Press Set up webhook to add them.");
    }

    private FourthwallWebhookConfig? Pick(IReadOnlyList<FourthwallWebhookConfig> hooks, string url) =>
        hooks.FirstOrDefault(h => url.Length > 0 && string.Equals(h.Url.TrimEnd('/'), url.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        ?? hooks.FirstOrDefault(h => h.Id == Options.WebhookId)
        ?? (url.Length == 0 && hooks.Count == 1 ? hooks[0] : null);

    /// <summary>
    /// Creates the webhook for <paramref name="url"/> (the Cuebot URL for Fourthwall) with every type Cuebot handles, or
    /// adds the missing types to the one already there. The signing secret is the shop's and is not returned by the API:
    /// it still has to be copied by hand.
    /// </summary>
    public async Task<string> SetUpWebhookAsync(string url, CancellationToken ct = default)
    {
        if (!HasCredentials) return Report("Connect the Fourthwall API first.");
        if (url.Trim().Length == 0) return Report("There is no Cuebot URL for Fourthwall yet: turn on the shared relay or set up your own (Relay tab).");
        try
        {
            var hooks = await Api.WebhooksAsync(ct).ConfigureAwait(false);
            var hook = hooks.FirstOrDefault(h => string.Equals(h.Url.TrimEnd('/'), url.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            string result;
            if (hook is null)
            {
                hook = await Api.CreateWebhookAsync(url.Trim(), FourthwallApi.WebhookTypes, ct).ConfigureAwait(false);
                result = "Created the Fourthwall webhook for Cuebot's URL with every event Cuebot handles.";
            }
            else if (FourthwallApi.WebhookTypes.Except(hook.AllowedTypes, StringComparer.OrdinalIgnoreCase).Any())
            {
                var types = hook.AllowedTypes.Union(FourthwallApi.WebhookTypes, StringComparer.OrdinalIgnoreCase).ToArray();
                hook = await Api.UpdateWebhookAsync(hook.Id, hook.Url, types, ct).ConfigureAwait(false);
                result = "Added the missing events to the Fourthwall webhook.";
            }
            else result = "The Fourthwall webhook already sends every event Cuebot handles.";
            Options.WebhookId = hook.Id;
            _settings.ScheduleSave();
            lock (_gate)
            {
                Data.WebhookTypes = [.. hook.AllowedTypes];
                Data.WebhookUrl = hook.Url;
                Save();
            }
            return Report(result + " Copy the shop's webhook secret from Settings → For developers → Webhooks into the card above if you have not.");
        }
        catch (Exception ex) when (IsApiError(ex))
        {
            Fail(ex);
            return LastError;
        }
    }

    // ---- catch-up ----

    /// <summary>
    /// Asks Fourthwall for the events it sent recently and replays those Cuebot never handled (newer than the first
    /// connection and the last 24 hours; duplicates are skipped by envelope id). Returns a line for the UI.
    /// </summary>
    public async Task<string> CatchUpAsync(CancellationToken ct = default)
    {
        if (!HasCredentials) return Report("Connect the Fourthwall API first.");
        if (Replay is null) return Report("Catch-up is not available.");
        var now = _time.GetUtcNow();
        DateTimeOffset since;
        lock (_gate)
        {
            Data.CatchUpSince ??= now;
            since = Data.CatchUpSince.Value > now - CatchUpWindow ? Data.CatchUpSince.Value : now - CatchUpWindow;
        }
        var events = await Api.WebhookEventsAsync(100, ct).ConfigureAwait(false);
        var replayed = 0;
        foreach (var info in events.Where(x => x.CreatedAt is { } at && at > since && !x.TestMode).OrderBy(x => x.CreatedAt))
        {
            if (Envelope(info) is not { } envelope) continue;
            try
            {
                if (await Replay(envelope, now - info.CreatedAt!.Value, ct).ConfigureAwait(false)) replayed++;
            }
            catch (FormatException ex)
            {
                _log.Warn("Fourthwall", $"Could not replay event {info.Id}: {ex.Message}");
            }
        }
        lock (_gate)
        {
            Data.LastCatchUp = now;
            Data.LastCatchUpCount = replayed;
            Save();
        }
        if (replayed > 0) _log.Info("Fourthwall", $"Caught up on {replayed} Fourthwall event(s) no delivery brought");
        return Report(replayed == 0 ? "No missed Fourthwall events." : $"Replayed {replayed} Fourthwall event(s) that never arrived.");
    }

    /// <summary>The event's body as an envelope (the body may be the envelope itself or only its data).</summary>
    internal static JsonElement? Envelope(FourthwallWebhookEventInfo info)
    {
        if (JsonReaders.TryParse(info.Body) is not { ValueKind: JsonValueKind.Object } body) return null;
        if (body.Has("data") && body.Has("type")) return body;
        var wrapped = new JsonObject
        {
            ["id"] = info.Id, ["type"] = info.Type, ["testMode"] = info.TestMode,
            ["createdAt"] = info.CreatedAt?.ToString("o", CultureInfo.InvariantCulture), ["data"] = JsonNode.Parse(body.GetRawText()),
        };
        return JsonReaders.TryParse(wrapped.ToJsonString());
    }

    // ---- totals ----

    /// <summary>Revenue and order count (purchases, not cancelled) and completed donations since <paramref name="since"/>.</summary>
    public async Task<FourthwallTotals> TotalsAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var orders = (await Api.OrdersSinceAsync(since, ct).ConfigureAwait(false))
            .Where(o => FourthwallWebhookHandler.IsPurchaseSource(o.Source) && o.Status != "CANCELLED").ToArray();
        var donations = (await Api.DonationsSinceAsync(since, ct).ConfigureAwait(false))
            .Where(d => FourthwallWebhookHandler.IsCompletedDonation(d.Status)).ToArray();
        var currency = orders.Select(o => o.Currency).Concat(donations.Select(d => d.Currency)).FirstOrDefault(c => c.Length > 0) ?? "";
        return new FourthwallTotals(Math.Round(orders.Sum(o => o.Amount), 2), orders.Length, Math.Round(donations.Sum(d => d.Amount), 2),
            donations.Length, currency, since);
    }

    // ---- periodic ----

    /// <summary>
    /// Finishes due gift draws; with the API connected also catches up every few minutes, re-reads products after a
    /// product change (or every 30 minutes) and syncs members every 6 hours (first 30 s after start).
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        bool drawsDue;
        lock (_gate) drawsDue = _draws.Values.Any(d => !d.Finishing && _time.GetUtcNow() > d.Deadline);
        if (drawsDue) _ = Task.Run(() => FinishDueDrawsAsync());

        if (!HasCredentials || NeedsReconnect || now - _started < FirstWorkDelay || now < _nextTry) return;
        DateTimeOffset? lastSync, productsRead;
        lock (_gate) (lastSync, productsRead) = (Data.LastMemberSync, Data.ProductsRead);
        var catchUpDue = Options.CatchUpMinutes > 0 && Replay is not null && now >= _nextCatchUp;
        var syncDue = Data.ShopId.Length == 0 || lastSync is null || now - lastSync >= MemberSyncEvery;
        var productsDue = _productsDirty || productsRead is null || now - productsRead >= ProductsEvery;
        if (!catchUpDue && !syncDue && !productsDue) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _nextTry = now + RetryAfter;
        if (catchUpDue) _nextCatchUp = now + TimeSpan.FromMinutes(Math.Max(1, Options.CatchUpMinutes));
        _ = Task.Run(async () =>
        {
            try
            {
                if (syncDue && Data.ShopId.Length == 0) await ConnectAsync().ConfigureAwait(false);
                else
                {
                    if (syncDue) await SyncMembersAsync().ConfigureAwait(false);
                    if (productsDue) await RefreshProductsAsync().ConfigureAwait(false);
                }
                if (catchUpDue && Connected) await CatchUpAsync().ConfigureAwait(false);
                if (LastError.Length == 0) _nextTry = DateTimeOffset.MinValue;
            }
            catch (Exception ex) when (IsApiError(ex))
            {
                Fail(ex);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }

    public static bool IsApiError(Exception ex) =>
        ex is HttpRequestException or OperationCanceledException or FormatException or JsonException or FourthwallAuthException;

    private void Fail(Exception ex)
    {
        if (ex is FourthwallAuthException) NeedsReconnect = true;
        LastError = ex switch
        {
            FourthwallAuthException => ex.Message,
            OperationCanceledException => "Fourthwall did not answer in time.",
            _ => $"Fourthwall API call failed: {ex.Message}",
        };
        _log.Warn("Fourthwall", LastError);
        Changed?.Invoke();
    }

    private string Report(string text)
    {
        Changed?.Invoke();
        return text;
    }

    private void PublishTotals()
    {
        if (Globals is null) return;
        Globals.SetValue("fourthwallMemberCount", MemberCounts().Count, persist: false);
    }

    // ---- overlay session data ----

    /// <summary><c>fourthwall</c>: {shop, memberCount, tiers [{id, name, members}], products [...], drawOpen, drawEntrants}.</summary>
    public void AddTo(JsonObject data)
    {
        var (count, perTier) = MemberCounts();
        data["fourthwall"] = new JsonObject
        {
            ["shop"] = ShopName,
            ["memberCount"] = count,
            ["tiers"] = new JsonArray([.. Tiers().Select(t => (JsonNode)new JsonObject
            {
                ["id"] = t.Id, ["name"] = t.Name, ["members"] = perTier.GetValueOrDefault(t.Name),
            })]),
            ["products"] = new JsonArray([.. Products().Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Name, ["price"] = p.FormattedPrice, ["amount"] = p.Price, ["currency"] = p.Currency, ["image"] = p.ImageUrl,
                ["soldOut"] = p.SoldOut, ["url"] = p.Url,
            })]),
            ["drawOpen"] = DrawOpen,
            ["drawEntrants"] = OpenDrawEntrants(),
        };
    }

    private void Save() => _file?.ScheduleSave();

    public void Dispose() => _file?.Dispose();
}
