using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;

namespace Cuebot.Webhooks.Services;

/// <summary>
/// Shopify sub-actions: the merch counter and goal Cuebot keeps from paid orders (no API needed), and with the Admin API
/// connected sales totals, the showcase products and catch-up. Each sets <c>shopifyOk</c> and <c>shopifyResult</c>; a
/// failed call never stops the action.
/// </summary>
internal static class ShopifySubActions
{
    public const string Category = "Shopify";

    public static readonly string[] TypeIds =
    [
        "shopify.getSales", "shopify.resetSales", "shopify.setGoal", "shopify.getTotals", "shopify.getProducts", "shopify.refreshProducts",
        "shopify.catchUp",
    ];

    internal const string Keep = "Keep";
    internal const string SinceStream = "This stream (the merch counter's start)";
    internal const string SinceToday = "Today";
    internal const string SinceHours = "The last hours below";

    private sealed class Step(string typeId, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task<string>> run) : ISubActionHandler
    {
        public string TypeId => typeId;
        public string Category => ShopifySubActions.Category;
        public string DisplayName => name;
        public IReadOnlyList<ParameterDescriptor> Parameters => parameters;

        public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
        {
            bool ok;
            string result;
            try
            {
                result = await run(context, p, ct).ConfigureAwait(false);
                ok = true;
            }
            catch (Exception ex) when (ShopifyTracker.IsApiError(ex) || ex is ArgumentException)
            {
                ok = false;
                result = ex is ShopifyAuthException ? ex.Message : $"Shopify: {ex.Message}";
                context.Log.Warn("Shopify", $"{name}: {result}");
            }
            context.Args["shopifyOk"] = ok;
            context.Args["shopifyResult"] = result;
            return SubActionResult.Next;
        }
    }

    private static void RequireApi(ShopifyTracker tracker)
    {
        if (!tracker.HasCredentials) throw new ShopifyAuthException("Connect the Shopify API on the Webhooks page first.");
    }

    public static IReadOnlyList<ISubActionHandler> Create(ShopifyTracker tracker) =>
    [
        new Step("shopify.getSales", "Get Shopify merch counter", [], (c, _, _) =>
        {
            StoreSales(c.Args, tracker.Sales(), tracker.Goal());
            return Task.FromResult($"{tracker.Sales().Items} item(s) sold this stream");
        }),
        new Step("shopify.resetSales", "Reset Shopify merch counter", [], (c, _, _) =>
        {
            StoreSales(c.Args, tracker.ResetSales(), tracker.Goal());
            return Task.FromResult("The merch counter starts again from zero.");
        }),
        new Step("shopify.setGoal", "Set Shopify merch goal",
            [
                new("title", "Title") { Help = "Leave empty to keep the title" },
                new("target", "Target") { Help = "Items or money, depending on what it counts. Leave empty to keep it; 0 = no goal" },
                new("measure", "Counts", ParameterKind.Choice)
                {
                    DefaultValue = Keep, Choices = [Keep, ShopifyGoalSettings.Items, ShopifyGoalSettings.Revenue],
                },
            ],
            (c, p, _) =>
            {
                var goal = tracker.SetGoal(Optional(p["title"]), OptionalNumber(p["target"]), p["measure"] is Keep or "" ? null : p["measure"]);
                StoreSales(c.Args, tracker.Sales(), goal);
                return Task.FromResult($"Merch goal: {goal.Current:0.##} / {goal.Target:0.##}");
            }),
        new Step("shopify.getTotals", "Get Shopify sales totals",
            [
                new("since", "Since", ParameterKind.Choice) { DefaultValue = SinceStream, Choices = [SinceStream, SinceToday, SinceHours] },
                new("hours", "Hours", ParameterKind.Number) { DefaultValue = "24" },
            ],
            async (c, p, ct) =>
            {
                RequireApi(tracker);
                var now = tracker.Time.GetUtcNow();
                var since = p["since"] switch
                {
                    SinceToday => new DateTimeOffset(now.ToLocalTime().Date, now.ToLocalTime().Offset),
                    SinceHours => now - TimeSpan.FromHours(Math.Max(1, p.GetDouble("hours", 24))),
                    _ => tracker.Sales().Since,
                };
                var totals = await tracker.TotalsAsync(since, ct).ConfigureAwait(false);
                c.Args["shopifyRevenue"] = (decimal)totals.Revenue;
                c.Args["shopifyFormattedRevenue"] = Money.Format(totals.Revenue, totals.Currency);
                c.Args["shopifyOrders"] = totals.Orders;
                c.Args["shopifyItems"] = totals.Items;
                c.Args["shopifyCurrency"] = totals.Currency;
                return $"{totals.Orders} paid order(s), {Money.Format(totals.Revenue, totals.Currency)}";
            }),
        new Step("shopify.getProducts", "Get Shopify products",
            [new("search", "Name contains") { Help = "Leave empty for all. The list comes from the last product refresh (Shopify API)" }],
            (c, p, _) =>
            {
                var found = tracker.FindProducts(p["search"]);
                c.Args["shopifyProductCount"] = found.Count;
                c.Args["shopifyProductsText"] = string.Join("; ", found.Select(x => x.SoldOut ? $"{x.Name} (sold out)" : $"{x.Name} {x.FormattedPrice}".Trim()));
                for (var i = 0; i < found.Count; i++)
                {
                    c.Args[$"product{i}.name"] = found[i].Name;
                    c.Args[$"product{i}.price"] = found[i].FormattedPrice;
                    c.Args[$"product{i}.url"] = found[i].Url;
                    c.Args[$"product{i}.image"] = found[i].ImageUrl;
                    c.Args[$"product{i}.soldOut"] = found[i].SoldOut;
                    c.Args[$"product{i}.stock"] = found[i].Stock;
                }
                return Task.FromResult($"{found.Count} product(s)");
            }),
        new Step("shopify.refreshProducts", "Refresh Shopify products", [], (_, _, ct) =>
        {
            RequireApi(tracker);
            return tracker.RefreshProductsAsync(ct);
        }),
        new Step("shopify.catchUp", "Catch up on Shopify orders", [], (_, _, ct) =>
        {
            RequireApi(tracker);
            return tracker.CatchUpAsync(ct);
        }),
    ];

    internal static void StoreSales(Dictionary<string, object?> a, ShopifySales s, ShopifyGoalState g)
    {
        a["shopifySalesRevenue"] = (decimal)s.Revenue;
        a["shopifySalesFormattedRevenue"] = Money.Format(s.Revenue, s.Currency);
        a["shopifySalesOrders"] = s.Orders;
        a["shopifySalesItems"] = s.Items;
        a["shopifySalesRefunded"] = (decimal)s.Refunded;
        a["shopifySalesCurrency"] = s.Currency;
        a["shopifySalesSince"] = s.Since.LocalDateTime;
        a["shopifyTopProduct"] = s.Products.OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault() ?? "";
        a["shopifySalesProductsText"] = string.Join(", ", s.Products.OrderByDescending(p => p.Value).Select(p => $"{p.Value} x {p.Key}"));
        ShopifyTracker.AddGoal(a, g);
    }

    private static string? Optional(string text) => text.Trim().Length == 0 ? null : text.Trim();

    private static double? OptionalNumber(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
}

/// <summary>The Shopify sub-actions, made once so each DI registration hands out the same instance.</summary>
internal sealed class ShopifySubActionSet(IReadOnlyList<ISubActionHandler> steps)
{
    public ISubActionHandler Get(string typeId) => steps.Single(s => s.TypeId == typeId);
}

/// <summary>Script functions: <c>shopify.sales</c>, <c>shopify.resetSales</c>, <c>shopify.goal</c>, <c>shopify.setGoal</c>, <c>shopify.products</c>, <c>shopify.totals</c>.</summary>
internal sealed class ShopifyScriptFunctions(ShopifyTracker tracker) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("shopify.sales", "This stream's Shopify merch counter: revenue, orders, items, refunded, currency, since, products {name: count}", [],
            (_, _, _) => Task.FromResult<object?>(Sales(tracker.Sales()))),
        new("shopify.resetSales", "Start the Shopify merch counter (and goal) again from zero", [],
            (_, _, _) => Task.FromResult<object?>(Sales(tracker.ResetSales()))),
        new("shopify.goal", "The Shopify merch goal: title, measure, current, target, percent, currency, reached", [],
            (_, _, _) => Task.FromResult<object?>(Goal(tracker.Goal()))),
        new("shopify.setGoal", "Change the merch goal's title, target or measure (\"Items sold\" or \"Revenue\"); leave any out to keep it",
            ["title", "target", "measure"], (_, a, _) => Task.FromResult<object?>(Goal(tracker.SetGoal(
                a.GetElement("title") is not null ? a.GetString("title") : null, NumberOrNull(a, "target"),
                a.GetElement("measure") is not null ? a.GetString("measure") : null)))),
        new("shopify.products", "Shopify products from the last refresh (Shopify API), optionally only names containing search", ["search"],
            (_, a, _) => Task.FromResult<object?>(tracker.FindProducts(a.GetString("search")).Select(p => new
            {
                name = p.Name, price = p.Price, formattedPrice = p.FormattedPrice, currency = p.Currency, url = p.Url, image = p.ImageUrl,
                soldOut = p.SoldOut, stock = p.Stock,
            }).ToArray())),
        new("shopify.totals", "Paid Shopify orders in the last hours (default 24) from the Shopify API: revenue, orders, items, currency",
            ["hours"], async (_, a, ct) =>
            {
                var hours = NumberOrNull(a, "hours") is { } h && h > 0 ? h : 24;
                var t = await tracker.TotalsAsync(tracker.Time.GetUtcNow() - TimeSpan.FromHours(hours), ct).ConfigureAwait(false);
                return new { revenue = t.Revenue, orders = t.Orders, items = t.Items, currency = t.Currency, since = t.Since };
            }),
    ];

    private static object Sales(ShopifySales s) => new
    {
        revenue = s.Revenue, orders = s.Orders, items = s.Items, refunded = s.Refunded, currency = s.Currency, since = s.Since, products = s.Products,
    };

    private static object Goal(ShopifyGoalState g) => new
    {
        title = g.Title, measure = g.Measure, current = g.Current, target = g.Target, percent = g.Percent, currency = g.Currency, reached = g.Reached,
    };

    private static double? NumberOrNull(JsonElement a, string name) =>
        a.GetElement(name) is { } v ? v.ValueKind == JsonValueKind.Number ? v.GetDouble()
            : double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null : null;
}
