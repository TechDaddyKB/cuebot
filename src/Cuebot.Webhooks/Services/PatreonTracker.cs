using System.Globalization;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays.Session;
using Cuebot.Core.Storage;
using Cuebot.Core.Variables;

namespace Cuebot.Webhooks.Services;

/// <summary>patreon.json: what Cuebot learnt about the Patreon campaign from the API and from webhooks.</summary>
public sealed class PatreonData
{
    public string CampaignId { get; set; } = "";
    public string CampaignName { get; set; } = "";
    public string Vanity { get; set; } = "";
    public string CampaignUrl { get; set; } = "";

    /// <summary>The campaign's currency from the API or the last delivery that named it (empty while unknown).</summary>
    public string Currency { get; set; } = "";

    /// <summary>Patreon's own patron count (from the API, or the campaign in the last delivery).</summary>
    public int PatronCount { get; set; }

    public List<PatreonTier> Tiers { get; set; } = [];

    /// <summary>Members by Patreon member id: from the last sync, kept up to date by webhooks.</summary>
    public Dictionary<string, PatreonPatron> Members { get; set; } = new(StringComparer.Ordinal);

    /// <summary>When the member list was last read in full (null: only what webhooks told Cuebot).</summary>
    public DateTimeOffset? LastSync { get; set; }

    /// <summary>The last webhook health Cuebot read (only for a webhook Cuebot created).</summary>
    public PatreonWebhookHealth? Webhook { get; set; }
}

public sealed class PatreonTier
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public int AmountCents { get; set; }
    public bool Published { get; set; } = true;
}

public sealed class PatreonPatron
{
    public string MemberId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>active_patron, declined_patron, former_patron, or empty for a free member.</summary>
    public string Status { get; set; } = "";

    /// <summary>What one charge costs, in cents of the campaign's currency.</summary>
    public int AmountCents { get; set; }

    /// <summary>Months between charges (1 monthly, 12 yearly).</summary>
    public int Cadence { get; set; } = 1;

    public bool IsGifted { get; set; }
    public bool IsFreeTrial { get; set; }
    public List<string> TierIds { get; set; } = [];
    public string Tier { get; set; } = "";
    public DateTimeOffset? Since { get; set; }
    public string TwitchUserId { get; set; } = "";
    public string TwitchLogin { get; set; } = "";
    public string YouTubeUserId { get; set; } = "";
    public string DiscordUserId { get; set; } = "";
    public DateTimeOffset Updated { get; set; }

    /// <summary>Monthly value of the pledge (a yearly one divided by 12).</summary>
    public double Monthly => Cadence > 1 ? AmountCents / 100.0 / Cadence : AmountCents / 100.0;

    public bool IsActive => Status == "active_patron";

    /// <summary>Pays now: active, a non-zero pledge, not a free trial or a gift.</summary>
    public bool IsPaying => IsActive && AmountCents > 0 && !IsFreeTrial && !IsGifted;
}

public sealed class PatreonWebhookHealth
{
    public string Id { get; set; } = "";
    public string Uri { get; set; } = "";
    public bool Paused { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? LastAttemptedAt { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
}

/// <summary>A copy of the totals shown in variables and overlays.</summary>
public sealed record PatreonTotals(int PatronCount, int PaidCount, double Monthly, string Currency, bool FromSync);

/// <summary>A patron linked to a viewer.</summary>
public sealed record PatreonPatronInfo(string Name, string Status, string Tier, double Monthly, string Currency, DateTimeOffset? Since, bool IsPaying);

/// <summary>
/// The optional Patreon API connection and what Cuebot keeps from it and from webhooks: the campaign's currency, tiers
/// and patron count, the member list (synced on connect and every 6 hours), patron-to-viewer links, the global
/// variables <c>patreonPatronCount</c>, <c>patreonPaidCount</c>, <c>patreonMonthly</c> and <c>patreonCurrency</c>, overlay
/// session data <c>patreon</c>, and the health of a webhook Cuebot created. Without a token everything still works from
/// webhooks alone, with what they carry.
/// </summary>
public sealed class PatreonTracker : ISessionDataSource, IPeriodicService, IDisposable
{
    public static readonly TimeSpan SyncEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan FirstSyncDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    private readonly JsonFile<WebhooksSettings> _settings;
    private readonly JsonFile<PatreonData>? _file;
    private readonly PatreonData _memory = new();
    private readonly AppLog _log;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly DateTimeOffset _started;
    private int _busy;
    private DateTimeOffset _nextTry = DateTimeOffset.MinValue;

    /// <summary>After a periodic attempt (good or bad), the next one waits at least this long: no retry storms.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

    /// <param name="path">patreon.json; null keeps everything in memory (tests).</param>
    public PatreonTracker(JsonFile<WebhooksSettings> settings, AppLog log, HttpClient? http = null, string? path = null, TimeProvider? time = null,
        string? apiBaseUrl = null)
    {
        _settings = settings;
        _log = log;
        _time = time ?? TimeProvider.System;
        _started = _time.GetUtcNow();
        if (path is not null) _file = new JsonFile<PatreonData>(path, log);
        Api = new PatreonApi(http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, () => Options, SaveTokens, apiBaseUrl);
    }

    /// <summary>Global variables to keep the totals in (set by the app; null in most tests).</summary>
    internal GlobalVariables? Globals { get; set; }

    /// <summary>Per-viewer variables for linked patrons (set by the app; null in most tests).</summary>
    internal UserVariables? UserVariables { get; set; }

    /// <summary>Known viewers, to find a Twitch user id from the login Patreon links (set by the app).</summary>
    internal Cuebot.Core.Users.UserRegistry? Users { get; set; }

    internal PatreonApi Api { get; }

    /// <summary>Raised after the campaign, members or connection state change.</summary>
    public event Action? Changed;

    private PatreonData Data => _file?.Value ?? _memory;
    private PatreonOptions Options => _settings.Value.PatreonOptions;

    /// <summary>True when an access token is saved.</summary>
    public bool HasToken => Options.AccessToken.Trim().Length > 0;

    /// <summary>Set when Patreon refused the token: no more calls until the user connects again.</summary>
    public bool NeedsReconnect { get; private set; }

    /// <summary>The last connection problem ("" when the last call worked).</summary>
    public string LastError { get; private set; } = "";

    /// <summary>Usable for API calls: a token and no refusal since the last connect.</summary>
    public bool Connected => HasToken && !NeedsReconnect && Data.CampaignId.Length > 0;

    // ---- what the handler needs ----

    /// <summary>The campaign's currency: from the API or the last delivery, else the one typed in the options ("" if unknown).</summary>
    public string Currency
    {
        get
        {
            lock (_gate) return Data.Currency.Length > 0 ? Data.Currency : Options.Currency.Trim().ToUpperInvariant();
        }
    }

    public IReadOnlyList<PatreonTier> Tiers()
    {
        lock (_gate) return [.. Data.Tiers.Select(t => new PatreonTier { Id = t.Id, Title = t.Title, AmountCents = t.AmountCents, Published = t.Published })];
    }

    /// <summary>A known tier by id (a copy), or null.</summary>
    public PatreonTier? FindTier(string id)
    {
        lock (_gate) return Data.Tiers.FirstOrDefault(t => t.Id == id) is { } t
            ? new PatreonTier { Id = t.Id, Title = t.Title, AmountCents = t.AmountCents, Published = t.Published }
            : null;
    }

    /// <summary>The title of a known tier ("" if unknown).</summary>
    public string TierTitle(string id)
    {
        lock (_gate) return Data.Tiers.FirstOrDefault(t => t.Id == id)?.Title ?? "";
    }

    /// <summary>Choices for the tier setting: "Title #id", so a renamed tier still matches by its id.</summary>
    public IEnumerable<string> TierSuggestions() => Tiers().Where(t => t.Title.Length > 0).Select(t => $"{t.Title} #{t.Id}");

    public PatreonTotals Totals()
    {
        lock (_gate) return TotalsCore();
    }

    private PatreonTotals TotalsCore()
    {
        var paying = Data.Members.Values.Where(m => m.IsPaying).ToArray();
        var active = Data.Members.Values.Count(m => m.IsActive);
        var count = Data.PatronCount > 0 ? Data.PatronCount : active;
        return new PatreonTotals(count, paying.Length, Math.Round(paying.Sum(m => m.Monthly), 2),
            Data.Currency.Length > 0 ? Data.Currency : Options.Currency.Trim().ToUpperInvariant(), Data.LastSync is not null);
    }

    public string CampaignName
    {
        get
        {
            lock (_gate) return Data.CampaignName.Length > 0 ? Data.CampaignName : Data.Vanity;
        }
    }

    public DateTimeOffset? LastSync
    {
        get
        {
            lock (_gate) return Data.LastSync;
        }
    }

    public PatreonWebhookHealth? WebhookHealth
    {
        get
        {
            lock (_gate) return Data.Webhook is { } w
                ? new PatreonWebhookHealth { Id = w.Id, Uri = w.Uri, Paused = w.Paused, ConsecutiveFailures = w.ConsecutiveFailures, LastAttemptedAt = w.LastAttemptedAt, CheckedAt = w.CheckedAt }
                : null;
        }
    }

    public int MemberCount
    {
        get
        {
            lock (_gate) return Data.Members.Count;
        }
    }

    /// <summary>The linked patron for a viewer (by platform user id, else Twitch login), or null.</summary>
    public PatreonPatronInfo? FindPatron(string platform, string userId, string userName = "")
    {
        platform = platform.Trim().ToLowerInvariant();
        lock (_gate)
        {
            var m = Data.Members.Values.FirstOrDefault(p => platform switch
            {
                "twitch" => (userId.Length > 0 && p.TwitchUserId == userId)
                    || (userName.Length > 0 && (string.Equals(p.TwitchLogin, userName.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.TwitchUserId, userName.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase))),
                "youtube" => userId.Length > 0 && p.YouTubeUserId == userId,
                "discord" => userId.Length > 0 && p.DiscordUserId == userId,
                _ => false,
            });
            return m is null ? null : new PatreonPatronInfo(m.Name, m.Status, m.Tier, m.Monthly, Data.Currency, m.Since, m.IsPaying);
        }
    }

    /// <summary>
    /// Records a new Patreon event (webhook): the campaign's currency and patron count from the delivery, the member in
    /// the member list, linked viewers' variables and the totals. Adds the totals to the arguments. Test events change nothing.
    /// </summary>
    internal void Record(string type, Dictionary<string, object?> args, PatreonDelivery? delivery)
    {
        var isTest = args.TryGetValue("isTest", out var t) && t is true;
        PatreonPatron? linked = null;
        bool removed = false;
        lock (_gate)
        {
            if (!isTest && delivery is not null)
            {
                if (delivery.CampaignCurrency.Length > 0) Data.Currency = delivery.CampaignCurrency;
                if (delivery.CampaignPatronCount > 0) Data.PatronCount = delivery.CampaignPatronCount;
                if (delivery.CampaignId.Length > 0 && Data.CampaignId.Length == 0) Data.CampaignId = delivery.CampaignId;
                foreach (var tier in delivery.Tiers)
                {
                    var known = Data.Tiers.FirstOrDefault(x => x.Id == tier.Id);
                    if (known is null) Data.Tiers.Add(tier);
                    else if (tier.Title.Length > 0) { known.Title = tier.Title; known.AmountCents = tier.AmountCents; }
                }
                if (delivery.Member is { MemberId.Length: > 0 } member)
                {
                    if (type == PatreonEventTypes.FollowDeleted)
                    {
                        if (Data.Members.Remove(member.MemberId, out var gone)) linked = gone;
                        else linked = member;
                        removed = true;
                    }
                    else
                    {
                        if (type == PatreonEventTypes.PledgeDeleted && member.Status.Length == 0) member.Status = "former_patron";
                        member.Updated = _time.GetUtcNow();
                        Data.Members[member.MemberId] = member;
                        linked = member;
                    }
                }
                Save();
            }
            AddTotals(args, TotalsCore());
        }
        if (linked is not null) Link(linked, removed);
        if (!isTest)
        {
            PublishTotals();
            Changed?.Invoke();
        }
    }

    internal static void AddTotals(Dictionary<string, object?> a, PatreonTotals totals)
    {
        a["patreonPatronCount"] = totals.PatronCount;
        a["patreonPaidCount"] = totals.PaidCount;
        a["patreonMonthly"] = (decimal)totals.Monthly;
    }

    /// <summary>
    /// What the delivery does not say: for Pledge Updated whether it was an upgrade (from the pledge history when
    /// connected, else from the member list), for posts whether they are for paying patrons. Never throws.
    /// </summary>
    internal async Task EnrichAsync(WebhookEvent e, CancellationToken ct)
    {
        var a = e.Args;
        if (e.Type == PatreonEventTypes.PledgeUpdated)
        {
            var memberId = WebhookFilters.Text(a, "id");
            double? previous = null;
            var previousTier = "";
            lock (_gate)
            {
                if (Data.Members.TryGetValue(memberId, out var known))
                {
                    previous = known.Monthly;
                    previousTier = known.Tier;
                }
            }
            var direction = "";
            if (Connected && memberId.Length > 0)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(LookupTimeout);
                    var history = await Api.PledgeHistoryAsync(memberId, timeout.Token).ConfigureAwait(false);
                    if (history.Count > 0)
                    {
                        direction = history[0].Type switch
                        {
                            "pledge_upgrade" => "upgrade",
                            "pledge_downgrade" => "downgrade",
                            _ => "",
                        };
                        if (history.Count > 1)
                        {
                            previous = history[1].AmountCents / 100.0;
                            previousTier = history[1].TierTitle;
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or FormatException or System.Text.Json.JsonException or PatreonAuthException)
                {
                    Fail(ex);
                }
            }
            var now = WebhookFilters.Number(a, "amount");
            if (direction.Length == 0 && previous is { } before)
                direction = now > before + 0.001 ? "upgrade" : now < before - 0.001 ? "downgrade" : "same";
            a["direction"] = direction;
            a["previousAmount"] = previous is { } p ? (decimal)Math.Round(p, 2) : null;
            a["previousTier"] = previousTier;
        }
        else if (e.Type is PatreonEventTypes.PostPublished or PatreonEventTypes.PostUpdated && Connected)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(LookupTimeout);
                var post = await Api.PostAsync(WebhookFilters.Text(a, "id"), timeout.Token).ConfigureAwait(false);
                a["isPaid"] = post.IsPaid;
                a["isPublic"] = post.IsPublic;
                SetPostTiers(a, post.TierIds);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or FormatException or System.Text.Json.JsonException or PatreonAuthException)
            {
                Fail(ex);
            }
        }
    }

    /// <summary>tierIds / tierNames ("Gold, Silver") for a post limited to tiers.</summary>
    internal void SetPostTiers(Dictionary<string, object?> a, IReadOnlyList<string> tierIds)
    {
        a["tierIds"] = tierIds.ToList();
        a["tierNames"] = string.Join(", ", tierIds.Select(id => TierTitle(id) is { Length: > 0 } title ? title : id));
    }

    // ---- connection ----

    /// <summary>
    /// Reads the campaign (currency, tiers, patron count) with the saved token, then the member list and the webhook's
    /// health. Returns a line for the UI.
    /// </summary>
    public async Task<string> ConnectAsync(CancellationToken ct = default)
    {
        NeedsReconnect = false;
        LastError = "";
        try
        {
            var campaignId = await Api.CampaignIdAsync(ct).ConfigureAwait(false);
            if (campaignId.Length == 0) return Report("This Patreon account has no campaign. Use the creator's token of the account that runs your page.");
            await ReadCampaignAsync(campaignId, ct).ConfigureAwait(false);
            await SyncMembersAsync(ct).ConfigureAwait(false);
            await CheckWebhookAsync(ct).ConfigureAwait(false);
            var totals = Totals();
            return Report($"Connected to {CampaignName}: {Data.Tiers.Count} tier(s), {totals.PatronCount} patron(s), currency {(totals.Currency.Length > 0 ? totals.Currency : "unknown")}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or FormatException or System.Text.Json.JsonException or PatreonAuthException)
        {
            Fail(ex);
            return LastError;
        }
    }

    /// <summary>Forgets the connection (tokens and the campaign); the member list and links stay until the next sync.</summary>
    public void Disconnect()
    {
        Options.AccessToken = "";
        Options.RefreshToken = "";
        Options.TokenExpiresAt = null;
        _settings.ScheduleSave();
        NeedsReconnect = false;
        LastError = "";
        Changed?.Invoke();
    }

    private async Task ReadCampaignAsync(string campaignId, CancellationToken ct)
    {
        var (campaign, tiers) = await Api.CampaignAsync(campaignId, ct).ConfigureAwait(false);
        lock (_gate)
        {
            Data.CampaignId = campaign.Id.Length > 0 ? campaign.Id : campaignId;
            Data.CampaignName = campaign.Name;
            Data.Vanity = campaign.Vanity;
            Data.CampaignUrl = campaign.Url;
            if (campaign.Currency.Length > 0) Data.Currency = campaign.Currency;
            Data.PatronCount = campaign.PatronCount;
            Data.Tiers = [.. tiers.Select(t => new PatreonTier { Id = t.Id, Title = t.Title, AmountCents = t.AmountCents, Published = t.Published })];
            Save();
        }
    }

    /// <summary>Reads the whole member list (replacing what webhooks told Cuebot) and updates links and totals.</summary>
    public async Task<string> SyncMembersAsync(CancellationToken ct = default)
    {
        string campaignId;
        lock (_gate) campaignId = Data.CampaignId;
        if (campaignId.Length == 0 || !HasToken) return Report("Connect Patreon first.");
        var members = await Api.MembersAsync(campaignId, ct).ConfigureAwait(false);
        List<PatreonPatron> gone;
        List<PatreonPatron> now;
        lock (_gate)
        {
            var fresh = members.Select(m => FromApi(m)).ToDictionary(m => m.MemberId, StringComparer.Ordinal);
            gone = [.. Data.Members.Values.Where(m => !fresh.ContainsKey(m.MemberId))];
            Data.Members = fresh;
            Data.LastSync = _time.GetUtcNow();
            now = [.. fresh.Values];
            Save();
        }
        foreach (var m in gone) Link(m, removed: true);
        foreach (var m in now) Link(m, removed: false);
        PublishTotals();
        Changed?.Invoke();
        return Report($"Synced {members.Count} Patreon member(s).");
    }

    private PatreonPatron FromApi(PatreonMemberInfo m) => new()
    {
        MemberId = m.MemberId, UserId = m.UserId, Name = m.Name, Status = m.Status, AmountCents = m.AmountCents, Cadence = m.Cadence,
        IsGifted = m.IsGifted, IsFreeTrial = m.IsFreeTrial, TierIds = [.. m.TierIds], Tier = TierNames(m.TierIds), Since = m.Since,
        TwitchUserId = m.TwitchUserId, TwitchLogin = TwitchLogin(m.TwitchUrl), YouTubeUserId = m.YouTubeUserId, DiscordUserId = m.DiscordUserId,
        Updated = _time.GetUtcNow(),
    };

    /// <summary>The paid tier titles of these ids (the Free tier is dropped next to a paid one), joined with ", ".</summary>
    private string TierNames(IEnumerable<string> ids)
    {
        var tiers = ids.Select(id => Data.Tiers.FirstOrDefault(t => t.Id == id)).OfType<PatreonTier>().ToList();
        if (tiers.Any(t => t.AmountCents > 0)) tiers.RemoveAll(t => t.AmountCents <= 0);
        return string.Join(", ", tiers.Select(t => t.Title).Where(t => t.Length > 0));
    }

    /// <summary>"twitch.tv/name" → "name".</summary>
    internal static string TwitchLogin(string url)
    {
        if (url.Trim().Length == 0) return "";
        var path = Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return path.Trim('/').Split('/').LastOrDefault() ?? "";
    }

    // ---- webhook ----

    /// <summary>Reads the health of the webhook Cuebot created (Patreon lists only webhooks made by this client).</summary>
    public async Task<string> CheckWebhookAsync(CancellationToken ct = default)
    {
        if (!HasToken) return Report("Connect Patreon first.");
        var hooks = await Api.WebhooksAsync(ct).ConfigureAwait(false);
        var id = Options.WebhookId;
        var hook = hooks.FirstOrDefault(h => h.Id == id) ?? (hooks.Count == 1 ? hooks[0] : null);
        lock (_gate)
        {
            Data.Webhook = hook is null ? null : Health(hook);
            Save();
        }
        Changed?.Invoke();
        if (hook is null) return Report("No webhook made by Cuebot was found. A webhook made by hand in the portal cannot be checked from here.");
        return Report(hook.Paused
            ? $"Patreon paused the webhook after {hook.ConsecutiveFailures} failed deliveries. Fix the relay, then press Resume."
            : "The Patreon webhook is active.");
    }

    /// <summary>
    /// Creates the webhook for <paramref name="uri"/> (the Cuebot URL for Patreon) with every member, pledge and post
    /// trigger, and saves the secret Patreon returns, so it need not be copied by hand.
    /// </summary>
    public async Task<string> CreateWebhookAsync(string uri, Action<string> saveSecret, CancellationToken ct = default)
    {
        string campaignId;
        lock (_gate) campaignId = Data.CampaignId;
        if (!Connected) return Report("Connect Patreon first.");
        if (uri.Trim().Length == 0) return Report("There is no Cuebot URL for Patreon yet: turn on the shared relay or set up your own (Relay tab).");
        try
        {
            var existing = (await Api.WebhooksAsync(ct).ConfigureAwait(false)).FirstOrDefault(h => h.Uri == uri.Trim());
            var hook = existing ?? await Api.CreateWebhookAsync(campaignId, uri.Trim(), ct).ConfigureAwait(false);
            if (hook.Secret.Length > 0) saveSecret(hook.Secret);
            Options.WebhookId = hook.Id;
            _settings.ScheduleSave();
            lock (_gate)
            {
                Data.Webhook = Health(hook);
                Save();
            }
            Changed?.Invoke();
            return Report(existing is null
                ? "Created the Patreon webhook with every member, pledge and post event and saved its secret."
                : "Cuebot's Patreon webhook already exists for this URL; saved its secret.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or FormatException or System.Text.Json.JsonException or PatreonAuthException)
        {
            Fail(ex);
            return LastError;
        }
    }

    /// <summary>Un-pauses Cuebot's webhook. Patreon then sends what it held back (stale ones are marked isStale).</summary>
    public async Task<string> ResumeWebhookAsync(CancellationToken ct = default)
    {
        var id = Options.WebhookId.Length > 0 ? Options.WebhookId : WebhookHealth?.Id ?? "";
        if (!Connected || id.Length == 0) return Report("No webhook made by Cuebot to resume.");
        try
        {
            var hook = await Api.ResumeWebhookAsync(id, ct).ConfigureAwait(false);
            lock (_gate)
            {
                Data.Webhook = Health(hook);
                Save();
            }
            Changed?.Invoke();
            return Report("Resumed the Patreon webhook. Patreon now sends the deliveries it held back.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or FormatException or System.Text.Json.JsonException or PatreonAuthException)
        {
            Fail(ex);
            return LastError;
        }
    }

    private PatreonWebhookHealth Health(PatreonWebhookInfo hook) => new()
    {
        Id = hook.Id, Uri = hook.Uri, Paused = hook.Paused, ConsecutiveFailures = hook.ConsecutiveFailures, LastAttemptedAt = hook.LastAttemptedAt,
        CheckedAt = _time.GetUtcNow(),
    };

    // ---- periodic ----

    /// <summary>Every <see cref="SyncEvery"/> (first 30 s after start): refresh a token close to running out, sync members, check the webhook.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (!HasToken || NeedsReconnect || now - _started < FirstSyncDelay || now < _nextTry) return;
        DateTimeOffset? last;
        lock (_gate) last = Data.LastSync;
        var refreshDue = Options.TokenExpiresAt is { } expires && expires - now < TimeSpan.FromDays(2) && Api.CanRefresh;
        if (!refreshDue && last is { } l && now - l < SyncEvery) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _nextTry = now + RetryAfter;
        _ = Task.Run(async () =>
        {
            try
            {
                if (refreshDue) await Api.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
                if (last is null || now - last >= SyncEvery) await ConnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or FormatException or System.Text.Json.JsonException or PatreonAuthException)
            {
                Fail(ex);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }

    /// <summary>Runs the periodic work now and waits for it (tests).</summary>
    internal Task SyncNowAsync() => ConnectAsync();

    private void SaveTokens(PatreonTokens tokens)
    {
        Options.AccessToken = tokens.AccessToken;
        Options.RefreshToken = tokens.RefreshToken;
        Options.TokenExpiresAt = tokens.ExpiresAt;
        _settings.ScheduleSave();
        _log.Info("Patreon", "Refreshed the Patreon access token");
    }

    private void Fail(Exception ex)
    {
        if (ex is PatreonAuthException) NeedsReconnect = true;
        LastError = ex switch
        {
            PatreonAuthException => ex.Message,
            OperationCanceledException => "Patreon did not answer in time.",
            _ => $"Patreon API call failed: {ex.Message}",
        };
        _log.Warn("Patreon", LastError);
        Changed?.Invoke();
    }

    private string Report(string text)
    {
        Changed?.Invoke();
        return text;
    }

    // ---- variables and links ----

    private void PublishTotals()
    {
        if (Globals is null) return;
        var t = Totals();
        Globals.SetValue("patreonPatronCount", t.PatronCount, persist: false);
        Globals.SetValue("patreonPaidCount", t.PaidCount, persist: false);
        Globals.SetValue("patreonMonthly", (decimal)t.Monthly, persist: false);
        Globals.SetValue("patreonCurrency", t.Currency, persist: false);
    }

    /// <summary>Stores isPatron, patreonTier, patreonStatus, patreonAmount and patreonSince on the linked Twitch / YouTube viewer.</summary>
    private void Link(PatreonPatron m, bool removed)
    {
        if (UserVariables is null || !Options.LinkViewers) return;
        void Set(string platform, string userId, string userName)
        {
            if (userId.Length == 0) return;
            var paying = !removed && m.IsActive;
            UserVariables.Set(platform, userId, userName.Length > 0 ? userName : null, "isPatron", paying, persist: true);
            UserVariables.Set(platform, userId, null, "patreonTier", paying ? m.Tier : "", persist: true);
            UserVariables.Set(platform, userId, null, "patreonStatus", removed ? "" : m.Status, persist: true);
            UserVariables.Set(platform, userId, null, "patreonAmount", paying ? (decimal)Math.Round(m.Monthly, 2) : 0m, persist: true);
            UserVariables.Set(platform, userId, null, "patreonSince",
                paying && m.Since is { } since ? since.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "", persist: true);
        }
        // Patreon's twitch.user_id may be the login rather than Twitch's numeric id: then look the viewer up by name.
        var twitchLogin = m.TwitchLogin.Length > 0 ? m.TwitchLogin : m.TwitchUserId.All(char.IsAsciiDigit) ? "" : m.TwitchUserId;
        var twitchId = m.TwitchUserId.Length > 0 && m.TwitchUserId.All(char.IsAsciiDigit) ? m.TwitchUserId
            : twitchLogin.Length > 0 ? Users?.FindByName("twitch", twitchLogin)?.UserId ?? "" : "";
        Set("twitch", twitchId, twitchLogin);
        Set("youtube", m.YouTubeUserId, "");
    }

    // ---- overlay session data ----

    /// <summary><c>patreon</c>: {campaign, currency, patronCount, paidCount, monthly, tiers}.</summary>
    public void AddTo(JsonObject data)
    {
        var t = Totals();
        data["patreon"] = new JsonObject
        {
            ["campaign"] = CampaignName,
            ["currency"] = t.Currency,
            ["patronCount"] = t.PatronCount,
            ["paidCount"] = t.PaidCount,
            ["monthly"] = t.Monthly,
            ["tiers"] = new JsonArray([.. Tiers().Select(x => (JsonNode)new JsonObject
            {
                ["id"] = x.Id, ["title"] = x.Title, ["amount"] = x.AmountCents / 100.0,
            })]),
        };
    }

    private void Save() => _file?.ScheduleSave();

    public void Dispose() => _file?.Dispose();
}

/// <summary>What the handler read from a delivery for <see cref="PatreonTracker.Record"/> (never published as arguments).</summary>
internal sealed record PatreonDelivery(string CampaignId, string CampaignCurrency, int CampaignPatronCount, IReadOnlyList<PatreonTier> Tiers, PatreonPatron? Member);
