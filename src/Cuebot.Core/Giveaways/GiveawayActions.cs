using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Points;
using Cuebot.Core.Store;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Giveaways;

/// <summary>
/// Registration for giveaways (giveaways.json) and the points store (store.json), with their sub-actions and triggers.
/// The app creates both at startup so they hear chat from the start.
/// </summary>
public static class GiveawayFeatures
{
    public const string GiveawayCategory = "Giveaways";
    public const string StoreCategory = "Store";

    public static IServiceCollection AddGiveawayAndStoreFeatures(this IServiceCollection services)
    {
        services.AddSingleton(sp => new Giveaways(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<AppLog>(), sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<OverlayFeed>(), sp.GetService<PointsService>(), sp.GetService<ChatHub>()));
        services.AddSingleton<IPeriodicService>(sp => sp.GetRequiredService<Giveaways>());
        services.AddSingleton(sp => new PointsStore(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<AppLog>(), sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<PointsService>(), sp.GetRequiredService<OverlayFeed>()));
        services.AddSingleton<ISubActionHandler, OpenGiveawaySubAction>();
        services.AddSingleton<ISubActionHandler, GiveawaySubAction>();
        services.AddSingleton<ISubActionHandler, SetStoreItemSubAction>();
        services.AddSingleton<ISubActionHandler, ReviewStoreRedemptionSubAction>();
        services.AddSingleton<ITriggerProvider, GiveawayAndStoreTriggers>();
        return services;
    }
}

/// <summary>Opens a giveaway entered with a chat command on every platform.</summary>
public sealed class OpenGiveawaySubAction(Giveaways giveaways) : ISubActionHandler
{
    public string TypeId => "giveaways.open";
    public string Category => GiveawayFeatures.GiveawayCategory;
    public string DisplayName => "Open giveaway";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("title", "Prize"),
        new("command", "Join command") { DefaultValue = "!join" },
        new("ticketCost", "Points per ticket (0: free)", ParameterKind.Number) { DefaultValue = "0" },
        new("maxTickets", "Most tickets per viewer", ParameterKind.Number) { DefaultValue = "1", Help = "Viewers buy more with !join 3" },
        new("currency", "Points currency") { Help = "Leave empty for the default currency" },
        new("subscriberLuck", "Subscriber luck", ParameterKind.Number) { DefaultValue = "1", Help = "2 makes a subscriber's tickets count double" },
        new("subscribersOnly", "Subscribers only", ParameterKind.Boolean) { DefaultValue = "false" },
        new("seconds", "Seconds taking entries (0: until closed)", ParameterKind.Number) { DefaultValue = "0" },
        new("winners", "Winners", ParameterKind.Number) { DefaultValue = "1" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var state = giveaways.Open(new GiveawayOptions
        {
            Title = p["title"], Command = p["command"], TicketCost = p.GetInt("ticketCost"), MaxTickets = p.GetInt("maxTickets", 1),
            Currency = p["currency"].Trim(), SubscriberLuck = p.GetDouble("subscriberLuck", 1), SubscribersOnly = p.GetBool("subscribersOnly"),
            Seconds = p.GetInt("seconds"), Winners = p.GetInt("winners", 1),
        });
        context.Args["giveawayId"] = state["id"]!.GetValue<string>();
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Closes entries, draws winners, draws one more, or cancels (refunding tickets).</summary>
public sealed class GiveawaySubAction(Giveaways giveaways) : ISubActionHandler
{
    internal const string Close = "Close entries";
    internal const string Draw = "Draw winners";
    internal const string Redraw = "Draw one more";
    internal const string Cancel = "Cancel and refund";

    public string TypeId => "giveaways.control";
    public string Category => GiveawayFeatures.GiveawayCategory;
    public string DisplayName => "Close, draw or cancel giveaway";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [new("mode", "Do", ParameterKind.Choice) { DefaultValue = Draw, Choices = [Close, Draw, Redraw, Cancel] }];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var state = p["mode"] switch
        {
            Close => giveaways.Close(),
            Redraw => giveaways.Draw(oneMore: true),
            Cancel => giveaways.Cancel(),
            _ => giveaways.Draw(),
        };
        context.Args["success"] = state is not null;
        var winners = giveaways.Winners;
        context.Args["winner"] = winners.Count > 0 ? winners[^1].UserName : "";
        context.Args["winners"] = string.Join(", ", winners.Select(w => w.UserName));
        context.Args["entrants"] = giveaways.Entrants.Count;
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Turns a store item on or off, or changes its stock.</summary>
public sealed class SetStoreItemSubAction(PointsStore store) : ISubActionHandler
{
    public string TypeId => "store.setItem";
    public string Category => GiveawayFeatures.StoreCategory;
    public string DisplayName => "Set store item";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("item", "Item") { Suggestions = () => store.Items.Select(i => i.Name) },
        new("enabled", "Available", ParameterKind.Choice) { DefaultValue = "Keep", Choices = ["Keep", "Enable", "Disable", "Toggle"] },
        new("stock", "Stock") { Help = "Empty keeps it; 10 sets it, +5 or -5 changes it, -1 is unlimited" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var item = store.Find(p["item"]) ?? throw new InvalidOperationException($"There is no store item '{p["item"]}'");
        var stockText = p["stock"].Trim();
        store.Update(item.Id, i =>
        {
            i.Enabled = p["enabled"] switch { "Enable" => true, "Disable" => false, "Toggle" => !i.Enabled, _ => i.Enabled };
            if (stockText.Length == 0) return;
            if (!int.TryParse(stockText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
                throw new InvalidOperationException($"'{stockText}' is not a stock amount");
            i.Stock = stockText[0] is '+' || (stockText[0] == '-' && n != -1) ? Math.Max(0, (i.Stock < 0 ? 0 : i.Stock) + n) : n;
        });
        var now = store.Find(item.Id)!;
        context.Args["itemEnabled"] = now.Enabled;
        context.Args["itemStock"] = now.Stock;
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Approves or rejects (refunds) a pending store redemption: the oldest, or one by id.</summary>
public sealed class ReviewStoreRedemptionSubAction(PointsStore store) : ISubActionHandler
{
    public string TypeId => "store.review";
    public string Category => GiveawayFeatures.StoreCategory;
    public string DisplayName => "Approve or reject store redemption";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("mode", "Do", ParameterKind.Choice) { DefaultValue = "Approve", Choices = ["Approve", "Reject"], Help = "Rejecting refunds the points" },
        new("redemption", "Redemption id") { Help = "Leave empty for the oldest pending one (%redemptionId% from a Store redemption pending trigger)" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var id = p["redemption"].Trim();
        if (id.Length == 0 || id.Contains('%')) id = store.Pending.FirstOrDefault()?.Id ?? "";
        context.Args["success"] = id.Length > 0 && (p["mode"] == "Reject" ? store.Reject(id) : store.Approve(id));
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Giveaway and store triggers.</summary>
internal sealed class GiveawayAndStoreTriggers(PointsStore store) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType(GiveawayEvents.Opened, GiveawayFeatures.GiveawayCategory, "Giveaway opened", GiveawayEvents.Opened),
        new EventTriggerType(GiveawayEvents.Entered, GiveawayFeatures.GiveawayCategory, "Giveaway entered", GiveawayEvents.Entered),
        new EventTriggerType(GiveawayEvents.Closed, GiveawayFeatures.GiveawayCategory, "Giveaway closed", GiveawayEvents.Closed),
        new EventTriggerType(GiveawayEvents.Winner, GiveawayFeatures.GiveawayCategory, "Giveaway winner drawn", GiveawayEvents.Winner),
        new EventTriggerType(GiveawayEvents.Canceled, GiveawayFeatures.GiveawayCategory, "Giveaway cancelled", GiveawayEvents.Canceled),
        Item(StoreEvents.Redeemed, "Store item redeemed"),
        Item(StoreEvents.Pending, "Store redemption waiting for approval"),
        Item(StoreEvents.Rejected, "Store redemption rejected"),
    ];

    private EventTriggerType Item(string eventType, string name) =>
        new(eventType, GiveawayFeatures.StoreCategory, name, eventType,
            [new ParameterDescriptor("item", "Item") { Help = "Leave empty for any item", Suggestions = () => store.Items.Select(i => i.Name) }],
            (e, s, _) => s["item"].Trim().Length == 0 || string.Equals(e.Args.GetString("item"), s["item"].Trim(), StringComparison.OrdinalIgnoreCase));
}
