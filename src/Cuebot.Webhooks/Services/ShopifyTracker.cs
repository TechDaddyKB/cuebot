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

/// <summary>shopify.json: the merch counter, recent orders (for cancellations and refunds), stock levels and what the API gave. No contact details.</summary>
public sealed class ShopifyData
{
    public ShopifySales Sales { get; set; } = new();

    /// <summary>Recent paid or created orders by id (last 500), so a cancellation or refund can be matched and taken off the counter.</summary>
    public Dictionary<string, ShopifyOrderRecord> Orders { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Last known stock per "inventory item id:location id".</summary>
    public Dictionary<string, int> Stock { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Product / variant names per inventory item id (from the API).</summary>
    public Dictionary<string, ShopifyItemName> ItemNames { get; set; } = new(StringComparer.Ordinal);

    public string ShopName { get; set; } = "";
    public string Currency { get; set; } = "";

    public List<ShopifyProduct> Products { get; set; } = [];
    public DateTimeOffset? ProductsRead { get; set; }

    /// <summary>Catch-up only replays orders newer than this (set when the API was first connected), so old orders never alert.</summary>
    public DateTimeOffset? CatchUpSince { get; set; }
    public DateTimeOffset? LastCatchUp { get; set; }
    public int LastCatchUpCount { get; set; }

    /// <summary>The last delivery Cuebot verified (kept across restarts: Shopify deletes an admin webhook that keeps failing, silently).</summary>
    public DateTimeOffset? LastDeliveryAt { get; set; }

    /// <summary>The Webhook API version that delivery named (X-Shopify-API-Version).</summary>
    public string LastApiVersion { get; set; } = "";

    public string LastShopDomain { get; set; } = "";
}

/// <summary>This stream's merch counter (paid orders minus cancellations and refunds; test events never count).</summary>
public sealed class ShopifySales
{
    public DateTimeOffset Since { get; set; }
    public double Revenue { get; set; }
    public int Orders { get; set; }
    public int Items { get; set; }
    public double Refunded { get; set; }
    public string Currency { get; set; } = "";

    /// <summary>Items sold per product name.</summary>
    public Dictionary<string, int> Products { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When the goal was reached in this counter (Goal Reached runs once per counter).</summary>
    public DateTimeOffset? GoalReachedAt { get; set; }
}

public sealed class ShopifyOrderRecord
{
    public string Name { get; set; } = "";
    public string OrderName { get; set; } = "";
    public double Amount { get; set; }
    public string Currency { get; set; } = "";
    public int Items { get; set; }
    public Dictionary<string, int> Products { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while it is counted in <see cref="ShopifyData.Sales"/>.</summary>
    public bool Counted { get; set; }

    /// <summary>Taken off already by refunds (money / items), so a later cancellation only takes the rest.</summary>
    public double RefundedAmount { get; set; }
    public int RefundedItems { get; set; }
    public bool Cancelled { get; set; }
    public DateTimeOffset Time { get; set; }
}

public sealed class ShopifyItemName
{
    public string Product { get; set; } = "";
    public string Variant { get; set; } = "";
    public string Sku { get; set; } = "";
}

public sealed class ShopifyProduct
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double Price { get; set; }
    public string Currency { get; set; } = "";
    public string FormattedPrice { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public bool SoldOut { get; set; }
    public int Stock { get; set; }
    public string Url { get; set; } = "";
}

/// <summary>The merch goal and where it stands.</summary>
public sealed record ShopifyGoalState(string Title, string Measure, double Current, double Target, string Currency, bool Reached)
{
    public int Percent => Target > 0 ? (int)Math.Min(100, Math.Floor(Current / Target * 100)) : 0;
}

/// <summary>Sales totals read from the Admin API.</summary>
public sealed record ShopifyTotals(double Revenue, int Orders, int Items, string Currency, DateTimeOffset Since);

/// <summary>
/// What Cuebot keeps for Shopify: the merch counter for this stream (paid orders; a cancellation or refund takes the
/// order back off), the merch goal on top of it (Goal Reached once per counter), stock levels for Low Stock / Sold Out /
/// Restocked, and the last delivery's API version and time. With the optional Admin API connected it also names stock
/// items, reads products for the showcase widget, totals sales and catches up on paid orders no delivery brought. Overlay
/// session data: <c>shopify</c>. Everything except the API part works from webhooks alone.
/// </summary>
public sealed class ShopifyTracker : ISessionDataSource, IPeriodicService, IDisposable
{
    private const int OrdersKept = 500;
    public static readonly TimeSpan ProductsEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FirstWorkDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>Catch-up looks back at most this far.</summary>
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(24);

    /// <summary>After this long without a delivery the Webhooks page warns that Shopify may have deleted the webhook.</summary>
    public static readonly TimeSpan QuietWarning = TimeSpan.FromDays(7);

    private readonly JsonFile<WebhooksSettings> _settings;
    private readonly JsonFile<ShopifyData>? _file;
    private readonly ShopifyData _memory = new();
    private readonly AppLog _log;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly DateTimeOffset _started;
    private int _busy;
    private bool _productsDirty;
    private DateTimeOffset _nextTry = DateTimeOffset.MinValue;
    private DateTimeOffset _nextCatchUp = DateTimeOffset.MinValue;

    /// <param name="path">shopify.json; null keeps everything in memory (tests).</param>
    public ShopifyTracker(JsonFile<WebhooksSettings> settings, AppLog log, HttpClient? http = null, string? path = null, TimeProvider? time = null,
        string? apiBaseUrl = null)
    {
        _settings = settings;
        _log = log;
        _time = time ?? TimeProvider.System;
        _started = _time.GetUtcNow();
        if (path is not null) _file = new JsonFile<ShopifyData>(path, log);
        lock (_gate)
        {
            if (Data.Sales.Since == default) Data.Sales.Since = _started;
        }
        Api = new ShopifyApi(http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, () => Options, _time, apiBaseUrl);
    }

    /// <summary>Publishes a paid order the API listed but no delivery brought (set by the app to the handler's replay).</summary>
    internal Func<string, TimeSpan, CancellationToken, Task<bool>>? Replay { get; set; }

    internal ShopifyApi Api { get; }

    internal TimeProvider Time => _time;

    public event Action? Changed;

    private ShopifyData Data => _file?.Value ?? _memory;
    private ShopifyOptions Options => _settings.Value.ShopifyOptions;

    public bool HasCredentials => Api.HasCredentials;

    /// <summary>Set when Shopify refused the credentials: no more periodic calls until the user connects again.</summary>
    public bool NeedsReconnect { get; private set; }

    public string LastError { get; private set; } = "";

    public bool Connected => HasCredentials && !NeedsReconnect && ShopName.Length > 0;

    public string ShopName
    {
        get { lock (_gate) return Data.ShopName; }
    }

    public IReadOnlyList<ShopifyProduct> Products()
    {
        lock (_gate) return [.. Data.Products];
    }

    public (DateTimeOffset? At, int Count) LastCatchUp
    {
        get { lock (_gate) return (Data.LastCatchUp, Data.LastCatchUpCount); }
    }

    public (DateTimeOffset? At, string ApiVersion, string Shop) LastDelivery
    {
        get { lock (_gate) return (Data.LastDeliveryAt, Data.LastApiVersion, Data.LastShopDomain); }
    }

    // ---- deliveries ----

    /// <summary>Remembers a verified delivery's time, API version and shop (for the quiet-webhook and version warnings).</summary>
    internal void NoteDelivery(string apiVersion, string shop)
    {
        lock (_gate)
        {
            Data.LastDeliveryAt = _time.GetUtcNow();
            if (apiVersion.Length > 0) Data.LastApiVersion = apiVersion;
            if (shop.Length > 0) Data.LastShopDomain = shop;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>A warning when Shopify has been quiet for a week (an admin webhook that keeps failing is deleted without an email); "" otherwise.</summary>
    public string QuietWarningText()
    {
        DateTimeOffset? last;
        lock (_gate) last = Data.LastDeliveryAt;
        if (last is not { } at || _time.GetUtcNow() - at < QuietWarning) return "";
        var days = (int)(_time.GetUtcNow() - at).TotalDays;
        return $"No Shopify delivery for {days} days. If you expected orders, check the webhook still exists in Shopify (Settings → Notifications → Webhooks): Shopify deletes an admin webhook whose address keeps failing, for example after the relay's route secret changed, and sends no email.";
    }

    // ---- sales counter ----

    /// <summary>
    /// Counts a paid order, takes a cancelled or refunded one back off, remembers created orders and fills the args the
    /// delivery does not have (a refund's buyer and order number, whether it was counted). Returns Goal Reached when this
    /// order reached the goal. Test events change nothing.
    /// </summary>
    internal IReadOnlyList<BotEvent> Record(string type, Dictionary<string, object?> a)
    {
        var isTest = a.TryGetValue("isTest", out var t) && t is true;
        var id = WebhookFilters.Text(a, "orderId");
        var followUps = new List<BotEvent>();
        lock (_gate)
        {
            Data.Orders.TryGetValue(id, out var known);
            switch (type)
            {
                case ShopifyEventTypes.OrderCreated:
                    if (!isTest && id.Length > 0 && known is null) Remember(id, a, counted: false);
                    break;
                case ShopifyEventTypes.OrderPaid:
                    a["wasCounted"] = false;
                    if (isTest || id.Length == 0 || known is { Counted: true } || known is { Cancelled: true }) break;
                    var order = Remember(id, a, counted: true);
                    Add(order, +1);
                    a["wasCounted"] = true;
                    if (GoalJustReached() is { } reached) followUps.Add(reached);
                    break;
                case ShopifyEventTypes.OrderCancelled:
                    a["wasCounted"] = known is { Counted: true };
                    if (isTest || known is null) break;
                    if (known.Counted)
                    {
                        Data.Sales.Revenue = Math.Round(Data.Sales.Revenue - Math.Max(0, known.Amount - known.RefundedAmount), 2);
                        Data.Sales.Orders = Math.Max(0, Data.Sales.Orders - 1);
                        Data.Sales.Items = Math.Max(0, Data.Sales.Items - Math.Max(0, known.Items - known.RefundedItems));
                        foreach (var (name, qty) in known.Products) Take(name, qty);
                        known.Counted = false;
                    }
                    known.Cancelled = true;
                    Save();
                    break;
                case ShopifyEventTypes.RefundCreated:
                    if (known is not null)
                    {
                        if (WebhookFilters.Text(a, "user") is "" or "Someone") a["user"] = known.Name.Length > 0 ? known.Name : "Someone";
                        if (WebhookFilters.Text(a, "orderName").Length == 0) a["orderName"] = known.OrderName;
                    }
                    a["wasCounted"] = known is { Counted: true };
                    if (isTest || known is not { Counted: true }) break;
                    var amount = Math.Min(WebhookFilters.Number(a, "amount"), Math.Max(0, known.Amount - known.RefundedAmount));
                    var items = (int)Math.Min(WebhookFilters.Number(a, "itemCount"), Math.Max(0, known.Items - known.RefundedItems));
                    known.RefundedAmount += amount;
                    known.RefundedItems += items;
                    Data.Sales.Revenue = Math.Round(Data.Sales.Revenue - amount, 2);
                    Data.Sales.Refunded = Math.Round(Data.Sales.Refunded + amount, 2);
                    Data.Sales.Items = Math.Max(0, Data.Sales.Items - items);
                    for (var i = 0; a.ContainsKey($"item{i}.name"); i++)
                        Take(WebhookFilters.Text(a, $"item{i}.name"), (int)WebhookFilters.Number(a, $"item{i}.quantity"));
                    Save();
                    break;
            }
            AddSales(a);
        }
        if (!isTest && type is ShopifyEventTypes.OrderPaid or ShopifyEventTypes.OrderCancelled or ShopifyEventTypes.RefundCreated) Changed?.Invoke();
        return followUps;
    }

    private ShopifyOrderRecord Remember(string id, Dictionary<string, object?> a, bool counted)
    {
        var products = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; a.ContainsKey($"item{i}.name"); i++)
        {
            var name = WebhookFilters.Text(a, $"item{i}.name");
            products[name] = products.GetValueOrDefault(name) + Math.Max(1, (int)WebhookFilters.Number(a, $"item{i}.quantity"));
        }
        var record = new ShopifyOrderRecord
        {
            Name = WebhookFilters.Text(a, "user"), OrderName = WebhookFilters.Text(a, "orderName"), Amount = WebhookFilters.Number(a, "amount"),
            Currency = WebhookFilters.Text(a, "currency"), Items = (int)WebhookFilters.Number(a, "itemCount"), Products = products,
            Counted = counted, Time = _time.GetUtcNow(),
        };
        Data.Orders[id] = record;
        if (Data.Orders.Count > OrdersKept)
            foreach (var old in Data.Orders.OrderBy(o => o.Value.Time).Take(Data.Orders.Count - OrdersKept).Select(o => o.Key).ToArray())
                Data.Orders.Remove(old);
        Save();
        return record;
    }

    private void Add(ShopifyOrderRecord order, int sign)
    {
        var s = Data.Sales;
        s.Revenue = Math.Round(s.Revenue + sign * order.Amount, 2);
        s.Orders += sign;
        s.Items += sign * order.Items;
        if (s.Currency.Length == 0) s.Currency = order.Currency;
        foreach (var (name, qty) in order.Products) s.Products[name] = s.Products.GetValueOrDefault(name) + sign * qty;
        Save();
    }

    private void Take(string name, int qty)
    {
        if (!Data.Sales.Products.TryGetValue(name, out var have)) return;
        if (have - qty <= 0) Data.Sales.Products.Remove(name);
        else Data.Sales.Products[name] = have - qty;
    }

    private BotEvent? GoalJustReached()
    {
        var goal = GoalLocked();
        if (!goal.Reached || Data.Sales.GoalReachedAt is not null) return null;
        Data.Sales.GoalReachedAt = _time.GetUtcNow();
        Save();
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["service"] = "Shopify", ["isTest"] = false };
        AddGoal(args, goal);
        return new BotEvent(ShopifyEventTypes.GoalReached, args);
    }

    /// <summary>Adds the counter to an event's args (%shopifySalesRevenue%, %shopifySalesItems% ...).</summary>
    private void AddSales(Dictionary<string, object?> a)
    {
        var s = Data.Sales;
        a["shopifySalesRevenue"] = (decimal)s.Revenue;
        a["shopifySalesOrders"] = s.Orders;
        a["shopifySalesItems"] = s.Items;
        AddGoal(a, GoalLocked());
    }

    /// <summary>An amount written for people ("$5.00"), for the Webhooks page.</summary>
    public static string FormatMoney(double amount, string currency) => Money.Format(amount, currency);

    public ShopifySales Sales()
    {
        lock (_gate) return Json.Clone(Data.Sales);
    }

    /// <summary>Starts the counter (and the goal) again from zero.</summary>
    public ShopifySales ResetSales()
    {
        lock (_gate)
        {
            Data.Sales = new ShopifySales { Since = _time.GetUtcNow(), Currency = Data.Currency };
            foreach (var o in Data.Orders.Values) o.Counted = false;
            Save();
        }
        _log.Info("Shopify", "Merch counter reset");
        Changed?.Invoke();
        return Sales();
    }

    public ShopifyGoalState Goal()
    {
        lock (_gate) return GoalLocked();
    }

    private ShopifyGoalState GoalLocked()
    {
        var g = Options.Goal;
        var revenue = g.Measure == ShopifyGoalSettings.Revenue;
        var current = revenue ? Data.Sales.Revenue : Data.Sales.Items;
        return new ShopifyGoalState(g.Title, revenue ? ShopifyGoalSettings.Revenue : ShopifyGoalSettings.Items, current, g.Target,
            revenue ? (Data.Sales.Currency.Length > 0 ? Data.Sales.Currency : Data.Currency) : "", g.Target > 0 && current >= g.Target);
    }

    /// <summary>Changes the goal; null keeps a value. A new target lets Goal Reached run again.</summary>
    public ShopifyGoalState SetGoal(string? title, double? target, string? measure)
    {
        var g = Options.Goal;
        if (title is not null) g.Title = title;
        if (target is { } t) g.Target = Math.Max(0, t);
        if (measure is not null)
            g.Measure = measure.Trim().StartsWith("rev", StringComparison.OrdinalIgnoreCase) ? ShopifyGoalSettings.Revenue : ShopifyGoalSettings.Items;
        _settings.ScheduleSave();
        lock (_gate)
        {
            if (!GoalLocked().Reached) Data.Sales.GoalReachedAt = null;
            Save();
        }
        Changed?.Invoke();
        return Goal();
    }

    internal static void AddGoal(Dictionary<string, object?> a, ShopifyGoalState g)
    {
        a["shopifyGoalTitle"] = g.Title;
        a["shopifyGoalCurrent"] = (decimal)g.Current;
        a["shopifyGoalTarget"] = (decimal)g.Target;
        a["shopifyGoalPercent"] = g.Percent;
        a["shopifyGoalMeasure"] = g.Measure;
        a["shopifyGoalReached"] = g.Reached;
    }

    /// <summary>Bus events: a stream going live starts the counter again (when that option is on).</summary>
    internal void OnEvent(BotEvent e)
    {
        if (e.Type != ChatHub.ActivityEvent || !Options.ResetSalesOnStreamStart) return;
        if (!string.Equals(e.Args.GetString("kind"), nameof(FeedKind.StreamOnline), StringComparison.OrdinalIgnoreCase)) return;
        // Several platforms going live together reset once.
        lock (_gate)
        {
            if (_time.GetUtcNow() - Data.Sales.Since < TimeSpan.FromMinutes(10)) return;
        }
        ResetSales();
    }

    // ---- stock ----

    /// <summary>
    /// Records an inventory level and says which event it makes: Sold Out when the item's stock (all known locations)
    /// reaches 0, Restocked when it comes back above 0, Low Stock when it falls to the threshold or below; null when it
    /// crossed nothing (or it is a test). Sets total, previous and threshold args.
    /// </summary>
    internal string? Stock(string itemId, string locationId, int available, bool isTest, Dictionary<string, object?> a)
    {
        var threshold = Math.Max(0, Options.LowStockThreshold);
        int? before;
        int after;
        lock (_gate)
        {
            var prefix = itemId + ":";
            var known = Data.Stock.Where(s => s.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            before = known.Length == 0 ? null : known.Sum(s => s.Value);
            var key = prefix + locationId;
            after = known.Where(s => s.Key != key).Sum(s => s.Value) + available;
            if (!isTest)
            {
                Data.Stock[key] = available;
                Save();
            }
            if (Data.ItemNames.TryGetValue(itemId, out var name)) Name(a, name);
        }
        a["available"] = after;
        a["locationAvailable"] = available;
        a["previousAvailable"] = before is { } b ? b : -1;
        a["threshold"] = threshold;
        if (isTest) return null;
        if (after <= 0) return before is null or > 0 ? ShopifyEventTypes.SoldOut : null;
        if (before is <= 0) return ShopifyEventTypes.Restocked;
        if (threshold > 0 && after <= threshold && (before is null || before > threshold)) return ShopifyEventTypes.LowStock;
        return null;
    }

    private static void Name(Dictionary<string, object?> a, ShopifyItemName name)
    {
        a["productName"] = name.Product;
        a["variantName"] = name.Variant;
        a["sku"] = name.Sku;
        a["item"] = name.Variant.Length > 0 ? $"{name.Product} ({name.Variant})" : name.Product.Length > 0 ? name.Product : name.Sku;
    }

    /// <summary>Names a stock event's item from the API (cached) when it is connected. Never throws.</summary>
    internal async Task NameStockAsync(Dictionary<string, object?> a, CancellationToken ct)
    {
        var id = WebhookFilters.Text(a, "inventoryItemId");
        if (id.Length == 0 || WebhookFilters.Text(a, "productName").Length > 0 || !HasCredentials || NeedsReconnect) return;
        try
        {
            if (await Api.InventoryItemAsync(id, ct).ConfigureAwait(false) is not { } info) return;
            var name = new ShopifyItemName { Product = info.ProductTitle, Variant = info.VariantTitle, Sku = info.Sku };
            lock (_gate)
            {
                Data.ItemNames[id] = name;
                Save();
            }
            Name(a, name);
        }
        catch (Exception ex) when (IsApiError(ex))
        {
            Fail(ex);
        }
    }

    /// <summary>A products/update delivery: re-read the showcase products soon.</summary>
    internal void ProductsChanged() => _productsDirty = true;

    // ---- connection ----

    /// <summary>Reads the shop and the products. Returns a line for the UI.</summary>
    public async Task<string> ConnectAsync(CancellationToken ct = default)
    {
        NeedsReconnect = false;
        LastError = "";
        Api.ForgetToken();
        try
        {
            var shop = await Api.ShopAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                Data.ShopName = shop.Name;
                Data.Currency = shop.Currency;
                if (Data.Sales.Currency.Length == 0) Data.Sales.Currency = shop.Currency;
                Data.CatchUpSince ??= _time.GetUtcNow();
                Save();
            }
            await RefreshProductsAsync(ct).ConfigureAwait(false);
            return Report($"Connected to {shop.Name}: {Products().Count} product(s) for the showcase.");
        }
        catch (Exception ex) when (IsApiError(ex))
        {
            Fail(ex);
            return LastError;
        }
    }

    /// <summary>Forgets the API credentials (the webhooks keep working).</summary>
    public void Disconnect()
    {
        Options.ClientId = "";
        Options.ClientSecret = "";
        Options.AccessToken = "";
        _settings.ScheduleSave();
        Api.ForgetToken();
        NeedsReconnect = false;
        LastError = "";
        lock (_gate)
        {
            Data.ShopName = "";
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Reads the active products for the showcase widget.</summary>
    public async Task<string> RefreshProductsAsync(CancellationToken ct = default)
    {
        if (!HasCredentials) return Report("Connect the Shopify API first.");
        _productsDirty = false;
        var products = await Api.ProductsAsync(50, ct).ConfigureAwait(false);
        lock (_gate)
        {
            Data.Products = [.. products.Select(p => new ShopifyProduct
            {
                Id = p.Id, Name = p.Title, Price = p.Price, Currency = p.Currency, FormattedPrice = p.Price > 0 ? Money.Format(p.Price, p.Currency) : "",
                ImageUrl = p.ImageUrl, SoldOut = p.Tracked && p.TotalInventory <= 0, Stock = p.TotalInventory, Url = p.Url,
            })];
            Data.ProductsRead = _time.GetUtcNow();
            Save();
        }
        Changed?.Invoke();
        return Report($"Read {Products().Count} Shopify product(s) for the showcase.");
    }

    /// <summary>Products whose name contains <paramref name="text"/> (all when empty).</summary>
    public IReadOnlyList<ShopifyProduct> FindProducts(string text)
    {
        var wanted = text.Trim();
        return [.. Products().Where(p => wanted.Length == 0 || p.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))];
    }

    // ---- catch-up ----

    /// <summary>
    /// Asks Shopify for orders paid recently (newer than the first connection, at most 24 hours back) and runs Order Paid
    /// for those no delivery brought (an order already handled is skipped by its id). Returns a line for the UI.
    /// </summary>
    public async Task<string> CatchUpAsync(CancellationToken ct = default)
    {
        if (!HasCredentials) return Report("Connect the Shopify API first.");
        if (Replay is null) return Report("Catch-up is not available.");
        var now = _time.GetUtcNow();
        DateTimeOffset since;
        lock (_gate)
        {
            Data.CatchUpSince ??= now;
            since = Data.CatchUpSince.Value > now - CatchUpWindow ? Data.CatchUpSince.Value : now - CatchUpWindow;
        }
        var search = $"financial_status:paid created_at:>'{since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}'";
        var orders = await Api.OrdersAsync(search, 100, ct).ConfigureAwait(false);
        var replayed = 0;
        foreach (var json in orders.Reverse())
        {
            if (JsonReaders.TryParse(json) is not { } order || order.Bool("test") || order.Has("cancelled_at")) continue;
            var created = ShopifyApi.Time(order.Str("processed_at")) ?? ShopifyApi.Time(order.Str("created_at"));
            if (created is { } at && at <= since) continue;
            if (await Replay(json, created is { } c ? now - c : TimeSpan.Zero, ct).ConfigureAwait(false)) replayed++;
        }
        lock (_gate)
        {
            Data.LastCatchUp = now;
            Data.LastCatchUpCount = replayed;
            Save();
        }
        if (replayed > 0) _log.Info("Shopify", $"Caught up on {replayed} paid Shopify order(s) no delivery brought");
        return Report(replayed == 0 ? "No missed Shopify orders." : $"Ran Order Paid for {replayed} order(s) that never arrived.");
    }

    // ---- totals ----

    /// <summary>Paid orders since <paramref name="since"/> from the API (cancelled and test orders left out).</summary>
    public async Task<ShopifyTotals> TotalsAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var search = $"financial_status:paid created_at:>'{since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}'";
        var orders = (await Api.OrdersAsync(search, 250, ct).ConfigureAwait(false))
            .Select(o => JsonReaders.TryParse(o)).OfType<JsonElement>().Where(o => !o.Bool("test") && !o.Has("cancelled_at")).ToArray();
        var currency = orders.Select(o => o.Str("currency")).FirstOrDefault(c => c.Length > 0) ?? Data.Currency;
        return new ShopifyTotals(Math.Round(orders.Sum(o => o.Number("total_price")), 2), orders.Length,
            orders.Sum(o => o.Items("line_items").Sum(i => Math.Max(1, (int)i.Number("quantity")))), currency, since);
    }

    // ---- periodic ----

    /// <summary>With the API connected: catches up every few minutes and re-reads products after a change or every 30 minutes.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (!HasCredentials || NeedsReconnect || now - _started < FirstWorkDelay || now < _nextTry) return;
        DateTimeOffset? productsRead;
        bool known;
        lock (_gate) (productsRead, known) = (Data.ProductsRead, Data.ShopName.Length > 0);
        var catchUpDue = Options.CatchUpMinutes > 0 && Replay is not null && now >= _nextCatchUp;
        var productsDue = !known || _productsDirty || productsRead is null || now - productsRead >= ProductsEvery;
        if (!catchUpDue && !productsDue) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _nextTry = now + RetryAfter;
        if (catchUpDue) _nextCatchUp = now + TimeSpan.FromMinutes(Math.Max(1, Options.CatchUpMinutes));
        _ = Task.Run(async () =>
        {
            try
            {
                if (!known) await ConnectAsync().ConfigureAwait(false);
                else if (productsDue) await RefreshProductsAsync().ConfigureAwait(false);
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
        ex is HttpRequestException or OperationCanceledException or FormatException or JsonException or ShopifyAuthException;

    private void Fail(Exception ex)
    {
        if (ex is ShopifyAuthException) NeedsReconnect = true;
        LastError = ex switch
        {
            ShopifyAuthException => ex.Message,
            OperationCanceledException => "Shopify did not answer in time.",
            _ => $"Shopify API call failed: {ex.Message}",
        };
        _log.Warn("Shopify", LastError);
        Changed?.Invoke();
    }

    private string Report(string text)
    {
        Changed?.Invoke();
        return text;
    }

    // ---- overlay session data ----

    /// <summary>
    /// <c>shopify</c>: {shop, sales {revenue, orders, items, refunded, currency, since, products {name: count}},
    /// goal {title, measure, current, target, percent, currency, reached}, products [...]}.
    /// </summary>
    public void AddTo(JsonObject data)
    {
        var s = Sales();
        var g = Goal();
        data["shopify"] = new JsonObject
        {
            ["shop"] = ShopName,
            ["sales"] = new JsonObject
            {
                ["revenue"] = s.Revenue, ["orders"] = s.Orders, ["items"] = s.Items, ["refunded"] = s.Refunded, ["currency"] = s.Currency,
                ["since"] = s.Since.ToString("o", CultureInfo.InvariantCulture),
                ["products"] = new JsonObject([.. s.Products.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value))]),
            },
            ["goal"] = new JsonObject
            {
                ["title"] = g.Title, ["measure"] = g.Measure, ["current"] = g.Current, ["target"] = g.Target, ["percent"] = g.Percent,
                ["currency"] = g.Currency, ["reached"] = g.Reached,
            },
            ["products"] = new JsonArray([.. Products().Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Name, ["price"] = p.FormattedPrice, ["amount"] = p.Price, ["currency"] = p.Currency, ["image"] = p.ImageUrl,
                ["soldOut"] = p.SoldOut, ["stock"] = p.Stock, ["url"] = p.Url,
            })]),
        };
    }

    private void Save() => _file?.ScheduleSave();

    public void Dispose() => _file?.Dispose();
}
