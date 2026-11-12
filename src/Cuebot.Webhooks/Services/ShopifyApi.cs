using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Webhooks.Services;

/// <summary>Shopify refused the credentials (HTTP 401/403 or a refused token request): the user has to check them.</summary>
public sealed class ShopifyAuthException(string message) : Exception(message);

/// <summary>The store the credentials belong to.</summary>
public sealed record ShopifyShopInfo(string Name, string MyshopifyDomain, string PrimaryUrl, string Currency);

/// <summary>A product for the showcase widget.</summary>
public sealed record ShopifyProductInfo(string Id, string Title, string Handle, string Url, string ImageUrl, double Price, string Currency,
    int TotalInventory, bool Tracked);

/// <summary>What an inventory item is (for Low Stock / Sold Out names).</summary>
public sealed record ShopifyInventoryItemInfo(string Id, string Sku, string ProductTitle, string VariantTitle, string DisplayName);

/// <summary>
/// The Shopify Admin GraphQL API calls Cuebot makes (https://shopify.dev/docs/api/admin-graphql, version
/// <see cref="ShopifyApiVersions.AdminApiVersion"/>), for a store-owned app only: a Dev Dashboard app installed on the
/// streamer's own store, authenticated with the client credentials grant
/// (https://shopify.dev/docs/apps/build/authentication-authorization/client-credentials-grant: POST
/// /admin/oauth/access_token, tokens last 24 hours and are renewed when they run out or are refused), or an Admin API
/// access token an existing custom app already has. Scopes used: read_orders, read_products, read_inventory. A
/// throttled query (429 or a THROTTLED error, Shopify's leaky bucket) is retried after 2, 4 and 8 seconds; 401/403 throws
/// <see cref="ShopifyAuthException"/>. Tokens and secrets are never logged.
/// </summary>
public sealed class ShopifyApi(HttpClient http, Func<ShopifyOptions> options, TimeProvider? time = null, string? baseUrl = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private string _token = "";
    private DateTimeOffset _tokenExpires;

    /// <summary>How a throttled call waits before the next try (tests make it instant).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    internal string UserAgent { get; init; } = "Cuebot (+https://github.com/TechDaddyKB/cuebot)";

    /// <summary>The store address, normalised to <c>name.myshopify.com</c> ("" when none is entered).</summary>
    public static string NormaliseShop(string text)
    {
        var shop = text.Trim().ToLowerInvariant();
        if (shop.StartsWith("https://", StringComparison.Ordinal)) shop = shop[8..];
        else if (shop.StartsWith("http://", StringComparison.Ordinal)) shop = shop[7..];
        shop = shop.Split('/')[0];
        if (shop.Length > 0 && !shop.Contains('.', StringComparison.Ordinal)) shop += ".myshopify.com";
        return shop;
    }

    private string Shop => NormaliseShop(options().ShopDomain);

    /// <summary>True when the store and either an access token or a client id and secret are saved.</summary>
    public bool HasCredentials
    {
        get
        {
            var o = options();
            return Shop.Length > 0 && (o.AccessToken.Trim().Length > 0 || (o.ClientId.Trim().Length > 0 && o.ClientSecret.Trim().Length > 0));
        }
    }

    /// <summary>Forgets the cached token (after the credentials change).</summary>
    public void ForgetToken()
    {
        lock (_gate)
        {
            _token = "";
            _tokenExpires = default;
        }
    }

    private string Root => (baseUrl ?? $"https://{Shop}").TrimEnd('/');

    // ---- queries ----

    public async Task<ShopifyShopInfo> ShopAsync(CancellationToken ct)
    {
        var data = await QueryAsync("{ shop { name myshopifyDomain currencyCode primaryDomain { url } } }", null, ct).ConfigureAwait(false);
        var s = data.Child("shop");
        return new ShopifyShopInfo(s.Str("name"), s.Str("myshopifyDomain"), s.PathStr("primaryDomain.url"), s.Str("currencyCode"));
    }

    /// <summary>Active products, most recently updated first (at most <paramref name="max"/>, up to 50).</summary>
    public async Task<IReadOnlyList<ShopifyProductInfo>> ProductsAsync(int max, CancellationToken ct)
    {
        const string query = """
            query($first: Int!) { products(first: $first, sortKey: UPDATED_AT, reverse: true, query: "status:active") { nodes {
              id title handle onlineStoreUrl totalInventory tracksInventory featuredMedia { preview { image { url } } }
              priceRangeV2 { minVariantPrice { amount currencyCode } } } } }
            """;
        var data = await QueryAsync(query, new JsonObject { ["first"] = Math.Clamp(max, 1, 50) }, ct).ConfigureAwait(false);
        return [.. data.Path("products").Items("nodes").Select(p => new ShopifyProductInfo(
            p.Str("id"), p.Str("title"), p.Str("handle"), p.Str("onlineStoreUrl"), p.PathStr("featuredMedia.preview.image.url"),
            p.PathNumber("priceRangeV2.minVariantPrice.amount"), p.PathStr("priceRangeV2.minVariantPrice.currencyCode"),
            (int)p.Number("totalInventory"), p.Bool("tracksInventory")))];
    }

    /// <summary>What an inventory item is (its variant's product and title, and the SKU).</summary>
    public async Task<ShopifyInventoryItemInfo?> InventoryItemAsync(string inventoryItemId, CancellationToken ct)
    {
        const string query = """
            query($id: ID!) { inventoryItem(id: $id) { id sku variant { title displayName product { title } } } }
            """;
        var gid = inventoryItemId.StartsWith("gid://", StringComparison.Ordinal) ? inventoryItemId : $"gid://shopify/InventoryItem/{inventoryItemId}";
        var data = await QueryAsync(query, new JsonObject { ["id"] = gid }, ct).ConfigureAwait(false);
        if (data.Child("inventoryItem") is not { ValueKind: JsonValueKind.Object } item) return null;
        var variant = item.Child("variant");
        var variantTitle = variant.Str("title");
        return new ShopifyInventoryItemInfo(inventoryItemId, item.Str("sku"), variant.PathStr("product.title"),
            variantTitle == "Default Title" ? "" : variantTitle, variant.Str("displayName"));
    }

    /// <summary>
    /// Orders matching a Shopify search <paramref name="search"/> (like <c>financial_status:paid updated_at:&gt;'2026-11-11T00:00:00Z'</c>),
    /// newest first, at most <paramref name="max"/> (pages of 50, up to 250), each turned into the shape of the order
    /// webhook's payload so it reads exactly like a delivery.
    /// </summary>
    public async Task<IReadOnlyList<string>> OrdersAsync(string search, int max, CancellationToken ct)
    {
        const string query = """
            query($first: Int!, $after: String, $query: String) {
              orders(first: $first, after: $after, query: $query, sortKey: CREATED_AT, reverse: true) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  id legacyResourceId name createdAt processedAt updatedAt cancelledAt test note sourceName currencyCode presentmentCurrencyCode
                  displayFinancialStatus discountCodes
                  totalPriceSet { shopMoney { amount currencyCode } presentmentMoney { amount currencyCode } }
                  customAttributes { key value }
                  customer { firstName lastName }
                  lineItems(first: 50) { nodes { name title quantity sku variantTitle product { legacyResourceId }
                    originalUnitPriceSet { shopMoney { amount } } } }
                } } }
            """;
        var orders = new List<string>();
        string? after = null;
        while (orders.Count < Math.Min(max, 250))
        {
            var variables = new JsonObject { ["first"] = Math.Min(50, max - orders.Count), ["query"] = search };
            if (after is not null) variables["after"] = after;
            var data = await QueryAsync(query, variables, ct).ConfigureAwait(false);
            var page = data.Path("orders");
            foreach (var o in page.Items("nodes")) orders.Add(RestOrder(o));
            if (!(page.Path("pageInfo.hasNextPage") is { ValueKind: JsonValueKind.True }) || page.PathStr("pageInfo.endCursor") is not { Length: > 0 } cursor) break;
            after = cursor;
        }
        return orders;
    }

    /// <summary>A GraphQL order in the REST / webhook shape Cuebot's order mapping reads.</summary>
    internal static string RestOrder(JsonElement o)
    {
        var id = o.Str("legacyResourceId");
        var shop = o.Path("totalPriceSet.shopMoney");
        var presentment = o.Path("totalPriceSet.presentmentMoney");
        var order = new JsonObject
        {
            ["id"] = long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : id,
            ["admin_graphql_api_id"] = o.Str("id"),
            ["name"] = o.Str("name"),
            ["created_at"] = o.Str("createdAt"),
            ["processed_at"] = o.Str("processedAt"),
            ["updated_at"] = o.Str("updatedAt"),
            ["cancelled_at"] = o.Str("cancelledAt") is { Length: > 0 } c ? c : null,
            ["test"] = o.Bool("test"),
            ["note"] = o.Str("note"),
            ["source_name"] = o.Str("sourceName"),
            ["currency"] = shop.Str("currencyCode") is { Length: > 0 } sc ? sc : o.Str("currencyCode"),
            ["presentment_currency"] = o.Str("presentmentCurrencyCode"),
            ["financial_status"] = o.Str("displayFinancialStatus").ToLowerInvariant(),
            ["total_price"] = shop.Str("amount"),
            ["total_price_set"] = new JsonObject
            {
                ["shop_money"] = new JsonObject { ["amount"] = shop.Str("amount"), ["currency_code"] = shop.Str("currencyCode") },
                ["presentment_money"] = new JsonObject { ["amount"] = presentment.Str("amount"), ["currency_code"] = presentment.Str("currencyCode") },
            },
            ["discount_codes"] = new JsonArray([.. o.Items("discountCodes").Select(d => (JsonNode)new JsonObject { ["code"] = JsonReaders.Text(d) })]),
            ["note_attributes"] = new JsonArray([.. o.Items("customAttributes").Select(a => (JsonNode)new JsonObject { ["name"] = a.Str("key"), ["value"] = a.Str("value") })]),
            ["line_items"] = new JsonArray([.. o.Path("lineItems").Items("nodes").Select(i => (JsonNode)new JsonObject
            {
                ["name"] = i.Str("name"), ["title"] = i.Str("title"), ["quantity"] = (long)i.Number("quantity"), ["sku"] = i.Str("sku"),
                ["variant_title"] = i.Str("variantTitle"), ["product_id"] = i.PathStr("product.legacyResourceId"),
                ["price"] = i.PathStr("originalUnitPriceSet.shopMoney.amount"),
            })]),
        };
        if (o.Child("customer") is { ValueKind: JsonValueKind.Object } customer)
            order["customer"] = new JsonObject { ["first_name"] = customer.Str("firstName"), ["last_name"] = customer.Str("lastName") };
        return order.ToJsonString();
    }

    internal static DateTimeOffset? Time(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    // ---- plumbing ----

    /// <summary>Runs a GraphQL query and returns its <c>data</c>; GraphQL errors throw <see cref="HttpRequestException"/>.</summary>
    internal async Task<JsonElement> QueryAsync(string query, JsonObject? variables, CancellationToken ct)
    {
        var body = new JsonObject { ["query"] = query };
        if (variables is not null) body["variables"] = variables;
        var text = body.ToJsonString();
        var renewed = false;
        for (var attempt = 0; ; attempt++)
        {
            var token = await TokenAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Root}/admin/api/{ShopifyApiVersions.AdminApiVersion}/graphql.json");
            request.Headers.Add("X-Shopify-Access-Token", token);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Content = new StringContent(text, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && !renewed && options().AccessToken.Trim().Length == 0)
            {
                // A client-credentials token may have been revoked or run out early: get a new one once.
                ForgetToken();
                renewed = true;
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ShopifyAuthException(response.StatusCode == HttpStatusCode.Forbidden
                    ? "Shopify refused the request (403): the app may be missing a scope (read_orders, read_products, read_inventory)."
                    : "Shopify refused the access token (401). Check the store address and the app's credentials, then connect again.");
            var reply = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var json = JsonReaders.TryParse(reply);
            var throttled = response.StatusCode == HttpStatusCode.TooManyRequests
                || (json is { } j && j.Items("errors").Any(e => e.PathStr("extensions.code") == "THROTTLED"));
            if (throttled && attempt < 3)
            {
                await Delay(TimeSpan.FromSeconds(2 << attempt), ct).ConfigureAwait(false);
                continue;
            }
            if (throttled) throw new HttpRequestException("Shopify is rate limiting Cuebot (too many requests); try again in a minute.");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Shopify answered {(int)response.StatusCode} {response.ReasonPhrase}");
            if (json is not { ValueKind: JsonValueKind.Object } root) throw new HttpRequestException("Shopify sent an answer Cuebot could not read.");
            if (root.Items("errors").FirstOrDefault() is { ValueKind: JsonValueKind.Object } error)
            {
                var code = error.PathStr("extensions.code");
                if (code == "ACCESS_DENIED")
                    throw new ShopifyAuthException($"Shopify refused the query: {error.Str("message")} (the app may be missing a scope).");
                throw new HttpRequestException($"Shopify: {error.Str("message")}{(code.Length > 0 ? $" ({code})" : "")}");
            }
            return root.Child("data") ?? JsonReaders.ParseBody("{}");
        }
    }

    /// <summary>The pasted access token, else a cached client-credentials token (renewed 5 minutes before it runs out).</summary>
    private async Task<string> TokenAsync(CancellationToken ct)
    {
        var o = options();
        if (Shop.Length == 0) throw new ShopifyAuthException("No Shopify store. Enter the store's myshopify.com address and connect.");
        if (o.AccessToken.Trim().Length > 0) return o.AccessToken.Trim();
        if (o.ClientId.Trim().Length == 0 || o.ClientSecret.Trim().Length == 0)
            throw new ShopifyAuthException("No Shopify app credentials. Enter the app's client id and client secret (or an access token) and connect.");
        lock (_gate)
        {
            if (_token.Length > 0 && _time.GetUtcNow() < _tokenExpires - TimeSpan.FromMinutes(5)) return _token;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Root}/admin/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = o.ClientId.Trim(), ["client_secret"] = o.ClientSecret.Trim(),
            }),
        };
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ShopifyAuthException("Shopify refused the app's client id and secret. Check them, and that the app is installed on this store.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Shopify answered {(int)response.StatusCode} {response.ReasonPhrase} to the token request");
        if (JsonReaders.TryParse(text) is not { ValueKind: JsonValueKind.Object } json || json.Str("access_token") is not { Length: > 0 } token)
            throw new HttpRequestException("Shopify's token answer had no access token.");
        var seconds = json.Number("expires_in");
        lock (_gate)
        {
            _token = token;
            _tokenExpires = _time.GetUtcNow() + TimeSpan.FromSeconds(seconds > 0 ? seconds : 86399);
            return _token;
        }
    }
}
