using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;

namespace Cuebot.Webhooks.Services;

/// <summary>
/// Sub-actions that use the Fourthwall Open API (connected on the Webhooks page): discount codes, flash drops, the
/// shop's streaming status, members, sales totals, member posts, thank-yous, giveaway links, the showcase products and
/// catch-up. Each sets <c>fourthwallOk</c> and <c>fourthwallResult</c>; a failed call never stops the action.
/// </summary>
internal static class FourthwallSubActions
{
    public const string Category = "Fourthwall";

    public static readonly string[] TypeIds =
    [
        "fourthwall.createPromo", "fourthwall.endPromo", "fourthwall.setAvailability", "fourthwall.setStreaming",
        "fourthwall.getMembers", "fourthwall.getTotals", "fourthwall.createPost", "fourthwall.getThankYous", "fourthwall.sendThankYou",
        "fourthwall.createGiveawayLinks", "fourthwall.refreshProducts", "fourthwall.catchUp",
    ];

    internal const string Percent = "Percent off";
    internal const string Flat = "Amount off";
    internal const string Shipping = "Free shipping";

    private sealed class Step(string typeId, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task<string>> run) : ISubActionHandler
    {
        public string TypeId => typeId;
        public string Category => FourthwallSubActions.Category;
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
            catch (Exception ex) when (FourthwallTracker.IsApiError(ex) || ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                ok = false;
                result = ex is FourthwallAuthException ? ex.Message : $"Fourthwall: {ex.Message}";
                context.Log.Warn("Fourthwall", $"{name}: {result}");
            }
            context.Args["fourthwallOk"] = ok;
            context.Args["fourthwallResult"] = result;
            return SubActionResult.Next;
        }
    }

    /// <summary>"STREAM" and 6 random letters/digits (no look-alikes).</summary>
    internal static string NewCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return "STREAM" + string.Concat(Enumerable.Range(0, 6).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]));
    }

    private static void RequireApi(FourthwallTracker tracker)
    {
        if (!tracker.HasCredentials) throw new FourthwallAuthException("Connect the Fourthwall API on the Webhooks page first.");
    }

    public static IReadOnlyList<ISubActionHandler> Create(FourthwallTracker tracker) =>
    [
        new Step("fourthwall.createPromo", "Create Fourthwall promo code",
            [
                new("code", "Code") { Help = "Leave empty for a random code like STREAM7K2QXP" },
                new("kind", "Discount", ParameterKind.Choice) { DefaultValue = Percent, Choices = [Percent, Flat, Shipping] },
                new("value", "Percent or amount", ParameterKind.Number) { DefaultValue = "10" },
                new("currency", "Currency (amount off)") { DefaultValue = "USD" },
                new("maxUses", "Max uses (0 = no limit)", ParameterKind.Number) { DefaultValue = "10" },
                new("oncePerCustomer", "Once per customer", ParameterKind.Boolean) { DefaultValue = "true" },
            ],
            async (c, p, ct) =>
            {
                RequireApi(tracker);
                var code = p["code"].Trim().Length > 0 ? p["code"].Trim().ToUpperInvariant() : NewCode();
                var kind = p["kind"] switch { Flat => "flat", Shipping => "shipping", _ => "percent" };
                var promo = await tracker.Api.CreatePromotionAsync(code, kind, p.GetDouble("value"), p["currency"], Math.Max(0, p.GetInt("maxUses")),
                    p.GetBool("oncePerCustomer", true), ct).ConfigureAwait(false);
                c.Args["fw.code"] = promo.Code.Length > 0 ? promo.Code : code;
                c.Args["fw.promotionId"] = promo.Id;
                c.Args["fw.promotionStatus"] = promo.Status;
                return $"Created promo code {c.Args["fw.code"]}";
            }),
        new Step("fourthwall.endPromo", "End Fourthwall promo code",
            [
                new("code", "Code or promotion id") { DefaultValue = "%fw.code%", Help = "A code like STREAM20, or the id (prm_...) Create promo code gave" },
                new("live", "Make it live again instead", ParameterKind.Boolean) { DefaultValue = "false" },
            ],
            async (c, p, ct) =>
            {
                RequireApi(tracker);
                var which = p["code"].Trim();
                if (which.Length == 0) throw new ArgumentException("No code given");
                var id = which.StartsWith("prm_", StringComparison.Ordinal) ? which
                    : (await tracker.Api.FindPromotionAsync(which, ct).ConfigureAwait(false))?.Id ?? throw new ArgumentException($"No promotion has the code {which}");
                var promo = await tracker.Api.SetPromotionStatusAsync(id, p.GetBool("live"), ct).ConfigureAwait(false);
                c.Args["fw.promotionStatus"] = promo.Status;
                c.Args["fw.usageCount"] = promo.UsageCount;
                return $"Promo code {(promo.Code.Length > 0 ? promo.Code : which)} is now {promo.Status}";
            }),
        new Step("fourthwall.setAvailability", "Show or hide Fourthwall product or collection",
            [
                new("target", "What", ParameterKind.Choice) { DefaultValue = "Product", Choices = ["Product", "Collection"] },
                new("id", "Product id, or collection id or slug"),
                new("available", "Available", ParameterKind.Boolean) { DefaultValue = "true", Help = "Untick to hide it again" },
            ],
            async (_, p, ct) =>
            {
                RequireApi(tracker);
                var id = p["id"].Trim();
                if (id.Length == 0) throw new ArgumentException("No id given");
                var available = p.GetBool("available", true);
                if (p["target"] == "Collection") await tracker.Api.SetCollectionAvailableAsync(id, available, ct).ConfigureAwait(false);
                else await tracker.Api.SetProductAvailableAsync(id, available, ct).ConfigureAwait(false);
                return $"{p["target"]} {id} is now {(available ? "available" : "hidden")}";
            }),
        new Step("fourthwall.setStreaming", "Set Fourthwall streaming status",
            [
                new("online", "Streaming now", ParameterKind.Boolean) { DefaultValue = "true", Help = "Untick to tell Fourthwall the stream ended" },
                new("broadcasterId", "Twitch user id") { DefaultValue = "%broadcastUserId%" },
                new("broadcasterLogin", "Twitch login") { DefaultValue = "%broadcastUserName%" },
            ],
            async (_, p, ct) =>
            {
                RequireApi(tracker);
                var id = p["broadcasterId"].Trim();
                var login = p["broadcasterLogin"].Trim().ToLowerInvariant();
                if (id.Length == 0 || login.Length == 0) throw new ArgumentException("The Twitch user id and login are needed");
                var online = p.GetBool("online", true);
                if (online) await tracker.Api.StartStreamingAsync(id, login, "", ct).ConfigureAwait(false);
                else await tracker.Api.EndStreamingAsync(id, login, ct).ConfigureAwait(false);
                return online ? "Fourthwall shows the shop as streaming" : "Fourthwall shows the stream as ended";
            }),
        new Step("fourthwall.getMembers", "Get Fourthwall members",
            [new("email", "Check this email (optional)") { Help = "Sets isFourthwallMember for this email, from the last member sync" }],
            (c, p, _) =>
            {
                var (count, perTier) = tracker.MemberCounts();
                c.Args["fourthwallMemberCount"] = count;
                foreach (var t in tracker.Tiers())
                    c.Args[$"fourthwallMembers.{t.Name}"] = perTier.GetValueOrDefault(t.Name);
                c.Args["fourthwallTiers"] = string.Join(", ", tracker.Tiers().Select(t => $"{t.Name} ({perTier.GetValueOrDefault(t.Name)})"));
                var email = p["email"].Trim();
                if (email.Length > 0)
                {
                    var m = tracker.FindMember(email);
                    var active = m is { State: "ACTIVE" or "CANCELLED" };
                    c.Args["isFourthwallMember"] = active;
                    c.Args["fourthwallMemberTier"] = active ? tracker.TierName(m!.TierId) : "";
                    c.Args["fourthwallMemberState"] = m?.State ?? "";
                }
                return Task.FromResult($"{count} Fourthwall member(s)");
            }),
        new Step("fourthwall.getTotals", "Get Fourthwall sales totals",
            [
                new("period", "Since", ParameterKind.Choice) { DefaultValue = "Today", Choices = ["Today", "Last 24 hours", "This stream (hours below)", "This month"] },
                new("hours", "Hours (for this stream)", ParameterKind.Number) { DefaultValue = "4" },
            ],
            async (c, p, ct) =>
            {
                RequireApi(tracker);
                var now = DateTimeOffset.Now;
                var since = p["period"] switch
                {
                    "Last 24 hours" => now.AddHours(-24),
                    "This stream (hours below)" => now.AddHours(-Math.Max(0.1, p.GetDouble("hours", 4))),
                    "This month" => new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset),
                    _ => new DateTimeOffset(now.Date, now.Offset),
                };
                var t = await tracker.TotalsAsync(since, ct).ConfigureAwait(false);
                c.Args["fourthwallRevenue"] = (decimal)t.Revenue;
                c.Args["fourthwallOrders"] = t.Orders;
                c.Args["fourthwallDonations"] = (decimal)t.Donations;
                c.Args["fourthwallDonationCount"] = t.DonationCount;
                c.Args["fourthwallTotal"] = (decimal)(t.Revenue + t.Donations);
                c.Args["fourthwallCurrency"] = t.Currency;
                c.Args["fourthwallFormattedTotal"] = Money.Format(t.Revenue + t.Donations, t.Currency);
                return $"{t.Orders} order(s), {t.DonationCount} donation(s): {Money.Format(t.Revenue + t.Donations, t.Currency)}";
            }),
        new Step("fourthwall.createPost", "Create Fourthwall members post",
            [
                new("title", "Title") { DefaultValue = "I'm live!" },
                new("text", "Text", ParameterKind.Multiline) { DefaultValue = "Come hang out: https://twitch.tv/%broadcastUserName%" },
                new("videoUrl", "Video link (optional)") { Help = "A YouTube or Twitch link makes a video embed post" },
                new("access", "Who can see it", ParameterKind.Choice) { DefaultValue = "MembersOnly", Choices = ["MembersOnly", "Public", "FreeAndPaidMembers"] },
                new("notify", "Notify members", ParameterKind.Boolean) { DefaultValue = "true" },
            ],
            async (c, p, ct) =>
            {
                RequireApi(tracker);
                var title = p["title"].Trim();
                if (title.Length < 3) throw new ArgumentException("The title needs at least 3 characters");
                var id = await tracker.Api.CreatePostAsync(title, p["text"].Trim(), p["videoUrl"], p["access"].Trim().Length > 0 ? p["access"] : "MembersOnly",
                    p.GetBool("notify", true), DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                c.Args["fw.postId"] = id;
                return $"Posted \"{title}\" for members";
            }),
        new Step("fourthwall.getThankYous", "Get Fourthwall thank-yous waiting",
            [new("max", "At most", ParameterKind.Number) { DefaultValue = "10" }],
            async (c, p, ct) =>
            {
                RequireApi(tracker);
                var list = await tracker.Api.WaitingThankYousAsync(Math.Clamp(p.GetInt("max", 10), 1, 100), ct).ConfigureAwait(false);
                c.Args["fourthwallThankYouCount"] = list.Count;
                c.Args["fourthwallThankYouNames"] = string.Join(", ", list.Select(k => k.Name.Length > 0 ? k.Name : FourthwallWebhookHandler.AnonymousName));
                for (var i = 0; i < list.Count; i++)
                {
                    c.Args[$"thankYou{i}.id"] = list[i].Id;
                    c.Args[$"thankYou{i}.name"] = list[i].Name.Length > 0 ? list[i].Name : FourthwallWebhookHandler.AnonymousName;
                    c.Args[$"thankYou{i}.products"] = list[i].Products;
                    c.Args[$"thankYou{i}.message"] = list[i].Message;
                    c.Args[$"thankYou{i}.amount"] = (decimal)list[i].Amount;
                    c.Args[$"thankYou{i}.formattedAmount"] = Money.Format(list[i].Amount, list[i].Currency);
                }
                return $"{list.Count} supporter(s) waiting for a thank-you";
            }),
        new Step("fourthwall.sendThankYou", "Send Fourthwall thank-you video",
            [
                new("contributionId", "Thank-you id") { DefaultValue = "%thankYou0.id%", Help = "From Get thank-yous waiting" },
                new("file", "Video or picture file") { Help = "For example an OBS replay saved by an earlier step" },
            ],
            async (c, p, ct) =>
            {
                RequireApi(tracker);
                var id = p["contributionId"].Trim();
                var file = p["file"].Trim().Trim('"');
                if (id.Length == 0) throw new ArgumentException("No thank-you id given");
                if (!File.Exists(file)) throw new ArgumentException($"File not found: {file}");
                await using var stream = File.OpenRead(file);
                var url = await tracker.Api.SendThankYouAsync(id, file, stream, ct).ConfigureAwait(false);
                c.Args["fw.mediaUrl"] = url;
                return "Thank-you sent";
            }),
        new Step("fourthwall.createGiveawayLinks", "Create Fourthwall giveaway links",
            [
                new("productId", "Product id"),
                new("count", "How many links", ParameterKind.Number) { DefaultValue = "1" },
            ],
            async (c, p, ct) =>
            {
                RequireApi(tracker);
                var product = p["productId"].Trim();
                if (product.Length == 0) throw new ArgumentException("No product id given");
                var links = await tracker.Api.CreateGiveawayLinksAsync(product, Math.Clamp(p.GetInt("count", 1), 1, 100), ct).ConfigureAwait(false);
                c.Args["fw.giveawayPackageId"] = links.PackageId;
                c.Args["fw.giveawayLinks"] = string.Join(" ", links.Links);
                for (var i = 0; i < links.Links.Count; i++) c.Args[$"fw.giveawayLink{i}"] = links.Links[i];
                return $"Made {links.Links.Count} giveaway link(s)";
            }),
        new Step("fourthwall.refreshProducts", "Refresh Fourthwall showcase products", [], (_, _, ct) =>
        {
            RequireApi(tracker);
            return tracker.RefreshProductsAsync(ct);
        }),
        new Step("fourthwall.catchUp", "Catch up on missed Fourthwall events", [], (_, _, ct) =>
        {
            RequireApi(tracker);
            return tracker.CatchUpAsync(ct);
        }),
    ];
}

/// <summary>The Fourthwall sub-actions, made once so each DI registration hands out the same instance.</summary>
internal sealed class FourthwallSubActionSet(IReadOnlyList<ISubActionHandler> steps)
{
    public ISubActionHandler Get(string typeId) => steps.Single(s => s.TypeId == typeId);
}

/// <summary>
/// Script functions: <c>fourthwall.shop</c>, <c>fourthwall.tiers</c>, <c>fourthwall.members</c>,
/// <c>fourthwall.products</c>, <c>fourthwall.totals</c>, <c>fourthwall.createPromo</c>, <c>fourthwall.catchUp</c>.
/// </summary>
internal sealed class FourthwallScriptFunctions(FourthwallTracker tracker) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("fourthwall.shop", "The Fourthwall shop Cuebot is connected to: name, address, connected, giftingPlatform, lastCatchUp", [], (_, _, _) =>
            Task.FromResult<object?>(new
            {
                name = tracker.ShopName, address = tracker.ShopAddress, connected = tracker.Connected, giftingPlatform = tracker.GiftingPlatform,
                lastCatchUp = tracker.LastCatchUp.At, lastError = tracker.LastError,
            })),
        new("fourthwall.tiers", "The Fourthwall membership tiers: id, name, monthly, currency, members", [], (_, _, _) =>
        {
            var (_, perTier) = tracker.MemberCounts();
            return Task.FromResult<object?>(tracker.Tiers().Select(t => new
            {
                id = t.Id, name = t.Name, monthly = t.Monthly, currency = t.Currency, members = perTier.GetValueOrDefault(t.Name),
            }).ToArray());
        }),
        new("fourthwall.members", "How many Fourthwall members Cuebot knows (count, perTier, lastSync)", [], (_, _, _) =>
        {
            var (count, perTier) = tracker.MemberCounts();
            return Task.FromResult<object?>(new { count, perTier, lastSync = tracker.LastMemberSync });
        }),
        new("fourthwall.products", "The products the showcase shows: name, price, image, soldOut, url", [], (_, _, _) =>
            Task.FromResult<object?>(tracker.Products().Select(p => new
            {
                name = p.Name, price = p.FormattedPrice, amount = p.Price, currency = p.Currency, image = p.ImageUrl, soldOut = p.SoldOut, url = p.Url,
            }).ToArray())),
        new("fourthwall.totals", "Fourthwall revenue, orders and donations over the last N hours (default 24); needs the API", ["hours"],
            async (_, a, ct) =>
            {
                var hours = a.GetElement("hours") is { ValueKind: JsonValueKind.Number } h ? h.GetDouble()
                    : double.TryParse(a.GetString("hours"), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 24;
                try
                {
                    var t = await tracker.TotalsAsync(DateTimeOffset.UtcNow.AddHours(-Math.Max(0.1, hours)), ct).ConfigureAwait(false);
                    return new { revenue = t.Revenue, orders = t.Orders, donations = t.Donations, donationCount = t.DonationCount, currency = t.Currency };
                }
                catch (Exception ex) when (FourthwallTracker.IsApiError(ex))
                {
                    return null;
                }
            }),
        new("fourthwall.createPromo", "Create a single-use-per-customer percent-off code (code empty = random); returns the code or null",
            ["code", "percent", "maxUses"], async (_, a, ct) =>
            {
                var code = a.GetString("code").Trim() is { Length: > 0 } c ? c.ToUpperInvariant() : FourthwallSubActions.NewCode();
                var percent = a.GetElement("percent") is { ValueKind: JsonValueKind.Number } p ? p.GetDouble() : 10;
                var max = a.GetElement("maxUses") is { ValueKind: JsonValueKind.Number } m ? m.GetInt32() : 0;
                try
                {
                    var promo = await tracker.Api.CreatePromotionAsync(code, "percent", percent, "", max, true, ct).ConfigureAwait(false);
                    return promo.Code.Length > 0 ? promo.Code : code;
                }
                catch (Exception ex) when (FourthwallTracker.IsApiError(ex))
                {
                    return null;
                }
            }),
        new("fourthwall.catchUp", "Replay Fourthwall events that never arrived (needs the API); returns a status line", [], async (_, _, ct) =>
        {
            try { return await tracker.CatchUpAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (FourthwallTracker.IsApiError(ex))
            {
                return ex is FourthwallAuthException ? ex.Message : $"Fourthwall catch-up failed: {ex.Message}";
            }
        }),
    ];
}
