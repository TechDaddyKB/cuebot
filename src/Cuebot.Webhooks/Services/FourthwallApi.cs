using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Webhooks.Services;

/// <summary>Fourthwall refused the API user (HTTP 401/403): the user has to check the credentials.</summary>
public sealed class FourthwallAuthException(string message) : Exception(message);

/// <summary>The shop the API user belongs to.</summary>
public sealed record FourthwallShopInfo(string Id, string Name, string Domain, string PublicDomain, string Status);

/// <summary>A membership tier and its prices.</summary>
public sealed record FourthwallTierInfo(string Id, string Name, string ImageUrl, double MonthlyAmount, string Currency);

/// <summary>A member (supporter) from the member list. Email stays in memory only.</summary>
public sealed record FourthwallMemberInfo(string Id, string Email, string Nickname, string State, string TierId, string Interval, double Amount,
    string Currency);

/// <summary>A product for the showcase.</summary>
public sealed record FourthwallProductInfo(string Id, string Name, string Slug, double Price, string Currency, string ImageUrl, bool SoldOut,
    string Access);

/// <summary>A webhook configuration.</summary>
public sealed record FourthwallWebhookConfig(string Id, string Url, IReadOnlyList<string> AllowedTypes);

/// <summary>One event Fourthwall sent (or tried to send), with the body it sent.</summary>
public sealed record FourthwallWebhookEventInfo(string Id, string Type, DateTimeOffset? CreatedAt, string Body, string State, string Error, bool TestMode);

/// <summary>A promotion (discount code).</summary>
public sealed record FourthwallPromotionInfo(string Id, string Type, string Code, string Status, int UsageCount, int MaxUses);

/// <summary>A supporter still waiting for a thank-you.</summary>
public sealed record FourthwallThankYouInfo(string Id, string OrderId, string Name, string Products, string Message, double Amount, string Currency,
    string State, DateTimeOffset? CreatedAt);

/// <summary>The stream gifting setup (platform TWITCH, STREAMELEMENTS, OPEN_API or NOT_SELECTED).</summary>
public sealed record FourthwallGiftingConfig(bool Enabled, int EntryTimeLimitSeconds, string Platform);

/// <summary>An order or donation for totals.</summary>
public sealed record FourthwallSale(string Id, DateTimeOffset? CreatedAt, double Amount, string Currency, string Status, string Source);

/// <summary>A gift draw entrant from Twitch chat.</summary>
public sealed record FourthwallDrawEntrant(string UserId, string UserName);

/// <summary>Giveaway links made for one product.</summary>
public sealed record FourthwallGiveawayLinks(string PackageId, IReadOnlyList<string> Links);

/// <summary>
/// The Fourthwall Open API calls Cuebot makes (https://docs.fourthwall.com, OpenAPI platform.json and membership.json),
/// authenticated with the shop's Open API user as HTTP Basic auth, which Fourthwall recommends for a tool used with one
/// shop. Fourthwall allows 100 requests per 10 seconds per shop and answers 429 with no Retry-After, so a 429 is retried
/// after 2, 4 and 8 seconds and then given up. A 401/403 throws <see cref="FourthwallAuthException"/> at once (no
/// retries). Credentials are never logged.
/// </summary>
public sealed class FourthwallApi(HttpClient http, Func<FourthwallOptions> options, string? baseUrl = null)
{
    public const string DefaultBaseUrl = "https://api.fourthwall.com";

    /// <summary>The webhook types Cuebot handles (asked for when it sets up the webhook).</summary>
    public static IReadOnlyList<string> WebhookTypes => [.. FourthwallWebhookHandler.TypeMap.Keys];

    private readonly string _base = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');

    /// <summary>How a 429 waits before the next try (tests make it instant).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    internal string UserAgent { get; init; } = "Cuebot (+https://github.com/TechDaddyKB/cuebot)";

    /// <summary>True when an API user and password are saved.</summary>
    public bool HasCredentials => options().ApiUser.Trim().Length > 0 && options().ApiPassword.Trim().Length > 0;

    // ---- shop ----

    public async Task<FourthwallShopInfo> ShopAsync(CancellationToken ct)
    {
        var s = await SendAsync(HttpMethod.Get, "/open-api/v1.0/shops/current", [], null, ct).ConfigureAwait(false);
        return new FourthwallShopInfo(s.Str("id"), s.Str("name"), s.Str("domain"), s.Str("publicDomain"), s.Str("status"));
    }

    // ---- memberships ----

    public async Task<IReadOnlyList<FourthwallTierInfo>> TiersAsync(CancellationToken ct)
    {
        var doc = await SendAsync(HttpMethod.Get, "/open-api/v1.0/memberships/tiers", [], null, ct).ConfigureAwait(false);
        var list = doc.ValueKind == JsonValueKind.Array ? doc.EnumerateArray() : doc.Items("results");
        return [.. list.Select(t =>
        {
            var variants = t.Items("variants").ToArray();
            var monthly = variants.FirstOrDefault(v => v.Str("interval") == "MONTHLY");
            var pick = monthly.ValueKind == JsonValueKind.Object ? monthly : variants.FirstOrDefault();
            var amount = pick.ValueKind == JsonValueKind.Object
                ? FourthwallWebhookHandler.Monthly(pick.PathNumber("amount.value"), pick.Str("interval"))
                : 0;
            return new FourthwallTierInfo(t.Str("id"), t.Str("name"), t.Str("imageUrl"), amount,
                pick.ValueKind == JsonValueKind.Object ? pick.PathStr("amount.currency") : "");
        })];
    }

    /// <summary>Every member, page by page (100 a page, at most 100 pages).</summary>
    public async Task<IReadOnlyList<FourthwallMemberInfo>> MembersAsync(CancellationToken ct)
    {
        var members = new List<FourthwallMemberInfo>();
        await foreach (var m in PagesAsync("/open-api/v1.0/memberships/members", [], 100, ct).ConfigureAwait(false))
        {
            var v = m.Path("subscription.variant");
            members.Add(new FourthwallMemberInfo(m.Str("id"), m.Str("email"), m.Str("nickname"), m.PathStr("subscription.type"),
                v.Str("tierId"), v.Str("interval"), v?.PathNumber("amount.value") ?? 0, v?.PathStr("amount.currency") ?? ""));
        }
        return members;
    }

    /// <summary>Creates a members-only (or public) post; with a video link it is a video embed post. Returns the post id.</summary>
    public async Task<string> CreatePostAsync(string title, string text, string videoUrl, string access, bool notify, DateTimeOffset liveAt,
        CancellationToken ct)
    {
        var core = new JsonObject { ["title"] = title, ["liveAt"] = liveAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) };
        if (text.Length > 0) core["description"] = text;
        var body = new JsonObject
        {
            ["attributes"] = new JsonObject { ["core"] = core },
            ["access"] = new JsonObject { ["type"] = access },
            ["notifications"] = new JsonObject { ["enabled"] = notify },
        };
        var video = videoUrl.Trim().Length > 0;
        if (video) body["embed"] = new JsonObject { ["url"] = videoUrl.Trim() };
        var doc = await SendAsync(HttpMethod.Post, video ? "/open-api/v1.1/memberships/posts/video_embeds" : "/open-api/v1.1/memberships/posts/texts",
            [], body, ct).ConfigureAwait(false);
        return doc.Child("postId") is { } id ? JsonReaders.Text(id) : doc.Str("id");
    }

    // ---- streaming and gifting ----

    /// <summary>PUT /streaming/start for the Twitch channel (required for stream gift purchases).</summary>
    public Task StartStreamingAsync(string broadcasterId, string broadcasterLogin, string thumbnailUrl, CancellationToken ct)
    {
        var service = new JsonObject { ["type"] = "TWITCH", ["broadcasterId"] = broadcasterId, ["broadcasterLogin"] = broadcasterLogin };
        if (thumbnailUrl.Length > 0) service["thumbnailUrl"] = thumbnailUrl;
        return SendAsync(HttpMethod.Put, "/open-api/v1.0/streaming/start", [], new JsonObject { ["services"] = new JsonArray(service) }, ct);
    }

    public Task EndStreamingAsync(string broadcasterId, string broadcasterLogin, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, "/open-api/v1.0/streaming/end", [], new JsonObject
        {
            ["services"] = new JsonArray(new JsonObject { ["type"] = "TWITCH", ["broadcasterId"] = broadcasterId, ["broadcasterLogin"] = broadcasterLogin }),
        }, ct);

    /// <summary>"ONLINE" / "OFFLINE" for the Twitch service, "" when none is listed.</summary>
    public async Task<string> StreamingStatusAsync(CancellationToken ct)
    {
        var doc = await SendAsync(HttpMethod.Get, "/open-api/v1.0/streaming", [], null, ct).ConfigureAwait(false);
        return doc.Items("services").FirstOrDefault(s => s.Str("type") == "TWITCH") is { ValueKind: JsonValueKind.Object } t ? t.Str("status") : "";
    }

    public async Task<FourthwallGiftingConfig> GiftingConfigAsync(CancellationToken ct)
    {
        var doc = await SendAsync(HttpMethod.Get, "/open-api/v1.0/gifting/config", [], null, ct).ConfigureAwait(false);
        return new FourthwallGiftingConfig(doc.Bool("enabled"), (int)doc.Number("entryTimeLimitSeconds"), doc.Str("platform"));
    }

    /// <summary>PUT /gifting/draw/{id}/finish with the entrants; Fourthwall picks the winners from them.</summary>
    public Task FinishDrawAsync(string drawId, IReadOnlyList<FourthwallDrawEntrant> entrants, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"/open-api/v1.0/gifting/draw/{Uri.EscapeDataString(drawId)}/finish", [], new JsonObject
        {
            ["participants"] = new JsonArray([.. entrants.Select(e => (JsonNode)new JsonObject
            {
                ["service"] = "TWITCH", ["userId"] = e.UserId, ["userName"] = e.UserName,
            })]),
        }, ct);

    // ---- promotions ----

    /// <summary>
    /// Creates a single-code shop promotion. <paramref name="kind"/>: "percent" (value = percentage), "flat" (value in
    /// <paramref name="currency"/>) or "shipping" (free shipping). <paramref name="maxUses"/> 0 = unlimited.
    /// </summary>
    public async Task<FourthwallPromotionInfo> CreatePromotionAsync(string code, string kind, double value, string currency, int maxUses,
        bool oncePerCustomer, CancellationToken ct)
    {
        JsonObject discount = kind switch
        {
            "flat" => new JsonObject
            {
                ["type"] = "FLAT_RATE",
                ["money"] = new JsonObject { ["value"] = value, ["currency"] = currency.Trim().ToUpperInvariant() },
                ["freeShipping"] = false,
            },
            "shipping" => new JsonObject { ["type"] = "FREE_SHIPPING" },
            _ => new JsonObject { ["type"] = "PERCENTAGE", ["percentage"] = value, ["shipping"] = "Included" },
        };
        var limits = new JsonObject { ["oneUsePerCustomer"] = oncePerCustomer };
        if (maxUses > 0) limits["maximumUse"] = maxUses;
        var doc = await SendAsync(HttpMethod.Post, "/open-api/v1.0/promotions", [], new JsonObject
        {
            ["type"] = "SHOP_SINGLE", ["code"] = code, ["discount"] = discount, ["limits"] = limits,
        }, ct).ConfigureAwait(false);
        return Promotion(doc);
    }

    /// <summary>Sets a promotion LIVE or ENDED.</summary>
    public async Task<FourthwallPromotionInfo> SetPromotionStatusAsync(string promotionId, bool live, CancellationToken ct) =>
        Promotion(await SendAsync(HttpMethod.Put, $"/open-api/v1.0/promotions/{Uri.EscapeDataString(promotionId)}", [],
            new JsonObject { ["status"] = live ? "LIVE" : "ENDED" }, ct).ConfigureAwait(false));

    /// <summary>The promotion that has this code, or null.</summary>
    public async Task<FourthwallPromotionInfo?> FindPromotionAsync(string code, CancellationToken ct)
    {
        var doc = await SendAsync(HttpMethod.Get, "/open-api/v1.0/promotions", [("codes", code), ("size", "20")], null, ct).ConfigureAwait(false);
        return doc.Items("results").Select(Promotion).FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    private static FourthwallPromotionInfo Promotion(JsonElement p)
    {
        var code = p.Str("code") is { Length: > 0 } c ? c
            : p.Items("codes").Select(x => x.ValueKind == JsonValueKind.Object ? x.Str("code") : JsonReaders.Text(x)).FirstOrDefault() ?? "";
        return new FourthwallPromotionInfo(p.Str("id"), p.Str("type"), code, p.Str("status"), (int)p.Number("usageCount"),
            (int)p.PathNumber("limits.maximumUsesNumber"));
    }

    // ---- products and collections ----

    /// <summary>The shop's products, or those of one collection (id or slug), at most <paramref name="max"/>.</summary>
    public async Task<IReadOnlyList<FourthwallProductInfo>> ProductsAsync(string collection, int max, CancellationToken ct)
    {
        var path = "/open-api/v1.0/products";
        if (collection.Trim().Length > 0)
        {
            var c = await SendAsync(HttpMethod.Get, $"/open-api/v1.0/collections/{Uri.EscapeDataString(collection.Trim())}", [], null, ct).ConfigureAwait(false);
            path = $"/open-api/v1.0/collections/{Uri.EscapeDataString(c.Str("id"))}/products";
        }
        var products = new List<FourthwallProductInfo>();
        await foreach (var p in PagesAsync(path, [], 50, ct).ConfigureAwait(false))
        {
            products.Add(Product(p));
            if (products.Count >= max) break;
        }
        return products;
    }

    internal static FourthwallProductInfo Product(JsonElement p)
    {
        var variants = p.Items("variants").ToArray();
        var prices = variants.Where(v => v.PathNumber("unitPrice.value") > 0).ToArray();
        var cheapest = prices.Length > 0 ? prices.MinBy(v => v.PathNumber("unitPrice.value")) : default;
        var image = p.PathStr("thumbnailImage.url") is { Length: > 0 } thumb ? thumb
            : p.Items("images").FirstOrDefault() is { ValueKind: JsonValueKind.Object } first ? first.Str("url") : "";
        return new FourthwallProductInfo(p.Str("id"), p.Str("name"), p.Str("slug"),
            cheapest.ValueKind == JsonValueKind.Object ? cheapest.PathNumber("unitPrice.value") : 0,
            cheapest.ValueKind == JsonValueKind.Object ? cheapest.PathStr("unitPrice.currency") : "",
            image, p.PathStr("state.type") == "SOLD_OUT", p.PathStr("access.type"));
    }

    /// <summary>Shows or hides a product in the shop.</summary>
    public Task SetProductAvailableAsync(string productId, bool available, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"/open-api/v1.0/products/{Uri.EscapeDataString(productId)}/availability", [],
            new JsonObject { ["available"] = available }, ct);

    /// <summary>Shows or hides a collection (id or slug: a slug is looked up first).</summary>
    public async Task SetCollectionAvailableAsync(string collection, bool available, CancellationToken ct)
    {
        var id = collection.Trim();
        if (!id.StartsWith("col_", StringComparison.Ordinal))
            id = (await SendAsync(HttpMethod.Get, $"/open-api/v1.0/collections/{Uri.EscapeDataString(id)}", [], null, ct).ConfigureAwait(false)).Str("id");
        await SendAsync(HttpMethod.Put, $"/open-api/v1.0/collections/{Uri.EscapeDataString(id)}/availability", [],
            new JsonObject { ["available"] = available }, ct).ConfigureAwait(false);
    }

    // ---- webhooks ----

    public async Task<IReadOnlyList<FourthwallWebhookConfig>> WebhooksAsync(CancellationToken ct)
    {
        var doc = await SendAsync(HttpMethod.Get, "/open-api/v1.0/webhooks", [], null, ct).ConfigureAwait(false);
        return [.. doc.Items("results").Select(Webhook)];
    }

    public async Task<FourthwallWebhookConfig> CreateWebhookAsync(string url, IReadOnlyList<string> types, CancellationToken ct) =>
        Webhook(await SendAsync(HttpMethod.Post, "/open-api/v1.0/webhooks", [], WebhookBody(url, types), ct).ConfigureAwait(false));

    public async Task<FourthwallWebhookConfig> UpdateWebhookAsync(string id, string url, IReadOnlyList<string> types, CancellationToken ct) =>
        Webhook(await SendAsync(HttpMethod.Put, $"/open-api/v1.0/webhooks/{Uri.EscapeDataString(id)}", [], WebhookBody(url, types), ct).ConfigureAwait(false));

    private static JsonObject WebhookBody(string url, IReadOnlyList<string> types) => new()
    {
        ["url"] = url, ["allowedTypes"] = new JsonArray([.. types.Select(t => (JsonNode)t)]),
    };

    private static FourthwallWebhookConfig Webhook(JsonElement w) =>
        new(w.Str("id"), w.Str("url"), [.. w.Items("allowedTypes").Select(JsonReaders.Text)]);

    /// <summary>The newest events Fourthwall sent (one page).</summary>
    public async Task<IReadOnlyList<FourthwallWebhookEventInfo>> WebhookEventsAsync(int size, CancellationToken ct)
    {
        var doc = await SendAsync(HttpMethod.Get, "/open-api/v1.0/webhook-events",
            [("page", "0"), ("size", size.ToString(CultureInfo.InvariantCulture))], null, ct).ConfigureAwait(false);
        return [.. doc.Items("results").Select(e => new FourthwallWebhookEventInfo(e.Str("id"), e.Str("type"), Time(e.Str("createdAt")),
            e.Child("body") is { ValueKind: JsonValueKind.Object } o ? o.GetRawText() : e.Str("body"), e.Str("state"), e.Str("error"), e.Bool("testMode")))];
    }

    // ---- thank-yous ----

    public async Task<IReadOnlyList<FourthwallThankYouInfo>> WaitingThankYousAsync(int max, CancellationToken ct)
    {
        var list = new List<FourthwallThankYouInfo>();
        await foreach (var k in PagesAsync("/open-api/v1.0/thank-you-contributions", [("state", "WAITING")], 50, ct).ConfigureAwait(false))
        {
            list.Add(new FourthwallThankYouInfo(k.Str("id"), k.Str("orderId"), k.PathStr("supporter.fullName"),
                string.Join(", ", k.Items("products").Select(p => p.Str("title")).Where(t => t.Length > 0)), k.Str("message"),
                k.PathNumber("total.amount"), k.PathStr("total.currency"), k.Str("state"), Time(k.Str("createdAt"))));
            if (list.Count >= max) break;
        }
        return list;
    }

    /// <summary>Uploads a picture or video as the thank-you for a contribution; returns the thank-you's media link.</summary>
    public async Task<string> SendThankYouAsync(string contributionId, string fileName, Stream file, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue(MediaType(fileName));
        form.Add(content, "file", Path.GetFileName(fileName));
        var doc = await SendAsync(HttpMethod.Post, $"/open-api/v1.0/thank-you-contributions/{Uri.EscapeDataString(contributionId)}", [], null, ct, form)
            .ConfigureAwait(false);
        return doc.Str("mediaUrl");
    }

    private static string MediaType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4", ".mov" => "video/quicktime", ".webm" => "video/webm", ".mkv" => "video/x-matroska",
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    // ---- totals ----

    /// <summary>Orders created after <paramref name="since"/> (newest pages first, at most 20 pages of 100).</summary>
    public async Task<IReadOnlyList<FourthwallSale>> OrdersSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        var list = new List<FourthwallSale>();
        var after = since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        await foreach (var o in PagesAsync("/open-api/v1.0/order", [("createdAt[gt]", after)], 100, ct, maxPages: 20).ConfigureAwait(false))
            list.Add(new FourthwallSale(o.Str("id"), Time(o.Str("createdAt")), o.PathNumber("amounts.total.value"), o.PathStr("amounts.total.currency"),
                o.Str("status"), o.PathStr("source.type")));
        return list;
    }

    /// <summary>
    /// Donations created after <paramref name="since"/>. The endpoint has no date filter: pages are read until a page
    /// holds only older donations (at most 20 pages of 100).
    /// </summary>
    public async Task<IReadOnlyList<FourthwallSale>> DonationsSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        var list = new List<FourthwallSale>();
        for (var page = 0; page < 20; page++)
        {
            var doc = await SendAsync(HttpMethod.Get, "/open-api/v1.0/donations",
                [("page", page.ToString(CultureInfo.InvariantCulture)), ("size", "100")], null, ct).ConfigureAwait(false);
            var items = doc.Items("results").ToArray();
            var newer = items.Select(d => new FourthwallSale(d.Str("id"), Time(d.Str("createdAt")), d.PathNumber("amounts.total.value"),
                d.PathStr("amounts.total.currency"), d.Str("status"), "")).Where(d => d.CreatedAt is { } at && at > since).ToArray();
            list.AddRange(newer);
            if (items.Length == 0 || newer.Length == 0 || page + 1 >= (int)doc.Number("totalPages")) break;
        }
        return list;
    }

    // ---- giveaway links ----

    public async Task<FourthwallGiveawayLinks> CreateGiveawayLinksAsync(string productId, int count, CancellationToken ct)
    {
        var doc = await SendAsync(HttpMethod.Post, "/open-api/v1.0/giveaway-links", [], new JsonObject
        {
            ["productId"] = productId, ["number"] = count,
        }, ct).ConfigureAwait(false);
        return new FourthwallGiveawayLinks(doc.Str("packageId"), [.. doc.Items("giveawayLinks").Select(l => l.Str("link")).Where(l => l.Length > 0)]);
    }

    // ---- plumbing ----

    private async IAsyncEnumerable<JsonElement> PagesAsync(string path, IReadOnlyList<(string Key, string Value)> query, int size,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, int maxPages = 100)
    {
        for (var page = 0; page < maxPages; page++)
        {
            List<(string, string)> q = [.. query, ("page", page.ToString(CultureInfo.InvariantCulture)), ("size", size.ToString(CultureInfo.InvariantCulture))];
            var doc = await SendAsync(HttpMethod.Get, path, q, null, ct).ConfigureAwait(false);
            var items = doc.Items("results").ToArray();
            foreach (var item in items) yield return item;
            var totalPages = (int)doc.Number("totalPages");
            if (items.Length == 0 || (totalPages > 0 ? page + 1 >= totalPages : items.Length < size)) yield break;
        }
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, IReadOnlyList<(string Key, string Value)> query, JsonNode? body,
        CancellationToken ct, HttpContent? content = null)
    {
        var o = options();
        if (o.ApiUser.Trim().Length == 0 || o.ApiPassword.Trim().Length == 0)
            throw new FourthwallAuthException("No Fourthwall API user. Enter the Open API user name and password and connect.");
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.ApiUser.Trim()}:{o.ApiPassword.Trim()}"));
        var url = _base + path + (query.Count == 0 ? "" : "?" + string.Join("&", query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}")));
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            if (content is not null) request.Content = content;
            else if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            // A multipart body cannot be sent twice, so an upload is not retried.
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3 && content is null)
            {
                await Delay(TimeSpan.FromSeconds(2 << attempt), ct).ConfigureAwait(false);
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new FourthwallAuthException(response.StatusCode == HttpStatusCode.Forbidden
                    ? "Fourthwall refused the request (403): the API user may not do this."
                    : "Fourthwall refused the API user (401). Check the Open API user name and password.");
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("Fourthwall is rate limiting Cuebot (too many requests); try again in a minute.");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Fourthwall answered {(int)response.StatusCode} {response.ReasonPhrase}{ErrorCode(text)}");
            return text.Trim().Length == 0 ? JsonReaders.ParseBody("{}") : JsonReaders.TryParse(text) ?? JsonReaders.ParseBody("{}");
        }
    }

    /// <summary>Fourthwall's error code (like WEBHOOK_URI_PORT_NOT_ALLOWED), when the answer names one.</summary>
    private static string ErrorCode(string text)
    {
        if (JsonReaders.TryParse(text) is not { ValueKind: JsonValueKind.Object } e) return "";
        var code = e.Str("code").Length > 0 ? e.Str("code") : e.Str("error");
        return code.Length > 0 ? $" ({code})" : "";
    }

    internal static DateTimeOffset? Time(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
}
