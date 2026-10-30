using System.Globalization;
using System.Text.Json.Nodes;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Points;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Store;

/// <summary>Something viewers can buy with Cuebot points.</summary>
public sealed class StoreItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public long Cost { get; set; } = 100;

    /// <summary>Points currency (empty: the default).</summary>
    public string Currency { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>How many are left (-1: unlimited).</summary>
    public int Stock { get; set; } = -1;

    public int UserCooldownSeconds { get; set; }
    public int GlobalCooldownSeconds { get; set; }

    /// <summary>Redemptions wait for a moderator to approve them (rejecting refunds the points).</summary>
    public bool NeedsApproval { get; set; }

    public bool SubscribersOnly { get; set; }
}

/// <summary>One purchase.</summary>
public sealed class StoreRedemption
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ItemId { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string Platform { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public long Cost { get; set; }
    public string Currency { get; set; } = "";

    /// <summary>What the viewer wrote after the item's name (a song, a message...).</summary>
    public string Input { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public string Status { get; set; } = Pending;
}

/// <summary>store.json.</summary>
public sealed class StoreDocument
{
    public string Command { get; set; } = "!redeem";
    public List<StoreItem> Items { get; set; } = [];
    public List<StoreRedemption> Pending { get; set; } = [];

    /// <summary>The latest finished redemptions, newest first.</summary>
    public List<StoreRedemption> History { get; set; } = [];
}

/// <summary>Events the store publishes.</summary>
public static class StoreEvents
{
    /// <summary>
    /// An item was bought (and approved when it needs approval). Args: redemptionId, item, itemId, cost, currency, user,
    /// platform, userId, input (also as rawInput).
    /// </summary>
    public const string Redeemed = "cuebot.storeRedeemed";

    /// <summary>A redemption waits for approval. Same args as <see cref="Redeemed"/>.</summary>
    public const string Pending = "cuebot.storePending";

    /// <summary>A pending redemption was rejected and refunded. Same args as <see cref="Redeemed"/>.</summary>
    public const string Rejected = "cuebot.storeRejected";
}

/// <summary>
/// The Cuebot points store: items viewers buy in any platform's chat with the redeem command (<c>!redeem item name
/// anything else</c>; the rest is the redemption's input), paid in Cuebot points, limited by stock, per-viewer and
/// global cooldowns and subscribers-only; items can wait for approval (rejecting refunds). Each purchase fires the
/// "Store item redeemed" trigger (link an action to it) and shows on overlays as a <c>redemption-latest</c> alert with
/// <c>store: true</c>. Items, pending redemptions and the history are kept in store.json.
/// </summary>
public sealed class PointsStore : IDisposable
{
    public const int HistorySize = 100;

    private readonly object _gate = new();
    private readonly JsonFile<StoreDocument>? _file;
    private readonly StoreDocument _memory = new();
    private readonly EventBus _bus;
    private readonly OverlayFeed? _feed;
    private readonly PointsService _points;
    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;
    private readonly Dictionary<string, DateTimeOffset> _lastByItem = [];
    private readonly Dictionary<string, DateTimeOffset> _lastByUser = [];

    public PointsStore(AppPaths? paths, AppLog log, EventBus bus, PointsService points, OverlayFeed? feed = null, TimeProvider? time = null)
    {
        _bus = bus;
        _points = points;
        _feed = feed;
        _time = time ?? TimeProvider.System;
        if (paths is not null) _file = new JsonFile<StoreDocument>(paths.File("store.json"), log);
        _subscription = bus.Subscribe(OnEvent);
    }

    private StoreDocument Doc => _file?.Value ?? _memory;

    /// <summary>Raised (on the caller's thread) after any change.</summary>
    public event Action? Changed;

    public string Command
    {
        get { lock (_gate) return Doc.Command; }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            lock (_gate)
            {
                Doc.Command = value.Trim().Split(' ')[0];
                Save();
            }
            Changed?.Invoke();
        }
    }

    public IReadOnlyList<StoreItem> Items
    {
        get { lock (_gate) return Doc.Items.Select(Copy).ToArray(); }
    }

    public IReadOnlyList<StoreRedemption> Pending
    {
        get { lock (_gate) return Doc.Pending.ToArray(); }
    }

    public IReadOnlyList<StoreRedemption> History
    {
        get { lock (_gate) return Doc.History.ToArray(); }
    }

    // ---- Items ------------------------------------------------------------------------------------------------------

    /// <summary>Adds an item; its name must be new (names are how viewers ask for items).</summary>
    public StoreItem Add(StoreItem item)
    {
        Check(item, null);
        var copy = Copy(item);
        lock (_gate)
        {
            if (Doc.Items.Any(i => i.Name.Equals(copy.Name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"There is already an item called '{copy.Name}'");
            if (Doc.Items.Any(i => i.Id == copy.Id)) copy.Id = Guid.NewGuid().ToString("N");
            Doc.Items.Add(copy);
            Save();
        }
        Changed?.Invoke();
        return Copy(copy);
    }

    /// <summary>Edits an item; false when it does not exist. Throws when the edit leaves it invalid (nothing is changed then).</summary>
    public bool Update(string id, Action<StoreItem> edit)
    {
        lock (_gate)
        {
            if (Doc.Items.FirstOrDefault(i => i.Id == id) is not { } item) return false;
            var changed = Copy(item);
            edit(changed);
            changed.Id = id;
            Check(changed, id);
            if (Doc.Items.Any(i => i.Id != id && i.Name.Equals(changed.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"There is already an item called '{changed.Name}'");
            Doc.Items[Doc.Items.IndexOf(item)] = changed;
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = Doc.Items.RemoveAll(i => i.Id == id) > 0;
            if (removed) Save();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>An item by id or name (case-insensitive).</summary>
    public StoreItem? Find(string idOrName)
    {
        lock (_gate)
        {
            var item = Doc.Items.FirstOrDefault(i => i.Id == idOrName)
                       ?? Doc.Items.FirstOrDefault(i => i.Name.Equals(idOrName.Trim(), StringComparison.OrdinalIgnoreCase));
            return item is null ? null : Copy(item);
        }
    }

    // ---- Redeeming --------------------------------------------------------------------------------------------------

    /// <summary>Why a redemption did not go through.</summary>
    public enum Refusal { None, NoSuchItem, Disabled, SoldOut, Cooldown, SubscribersOnly, NotEnoughPoints }

    /// <summary>
    /// Buys an item for a viewer (what the redeem command does): checks it, takes the points and completes it, or puts it
    /// in the pending list when it needs approval. Returns the redemption, or null with the reason.
    /// </summary>
    public (StoreRedemption? Redemption, Refusal Refusal) Redeem(string itemName, string platform, string userId, string userName, string input = "",
        bool subscriber = false)
    {
        StoreRedemption redemption;
        bool pending;
        lock (_gate)
        {
            var item = Doc.Items.FirstOrDefault(i => i.Name.Equals(itemName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (item is null) return (null, Refusal.NoSuchItem);
            if (!item.Enabled) return (null, Refusal.Disabled);
            if (item.Stock == 0) return (null, Refusal.SoldOut);
            if (item.SubscribersOnly && !subscriber) return (null, Refusal.SubscribersOnly);
            var now = _time.GetUtcNow();
            var userKey = $"{item.Id}:{platform.ToLowerInvariant()}:{userId}";
            if (_lastByItem.TryGetValue(item.Id, out var lastAny) && now - lastAny < TimeSpan.FromSeconds(item.GlobalCooldownSeconds)) return (null, Refusal.Cooldown);
            if (_lastByUser.TryGetValue(userKey, out var lastMine) && now - lastMine < TimeSpan.FromSeconds(item.UserCooldownSeconds)) return (null, Refusal.Cooldown);
            if (!_points.TrySpend(platform, userId, userName, item.Cost, out _, Currency(item.Currency))) return (null, Refusal.NotEnoughPoints);
            _lastByItem[item.Id] = now;
            _lastByUser[userKey] = now;
            if (item.Stock > 0) item.Stock--;
            redemption = new StoreRedemption
            {
                ItemId = item.Id, ItemName = item.Name, Platform = platform, UserId = userId, UserName = userName, Cost = item.Cost,
                Currency = item.Currency, Input = input.Trim(), Time = now,
            };
            pending = item.NeedsApproval;
            if (pending) Doc.Pending.Add(redemption);
            else Finish(redemption, StoreRedemption.Approved);
            Save();
        }
        Changed?.Invoke();
        if (pending) _bus.Publish(new BotEvent(StoreEvents.Pending, Args(redemption)));
        else Completed(redemption);
        return (redemption, Refusal.None);
    }

    /// <summary>Approves a pending redemption (it completes as if it had needed none); false when it is not pending.</summary>
    public bool Approve(string redemptionId)
    {
        StoreRedemption? redemption;
        lock (_gate)
        {
            redemption = Doc.Pending.FirstOrDefault(r => r.Id == redemptionId);
            if (redemption is null) return false;
            Doc.Pending.Remove(redemption);
            Finish(redemption, StoreRedemption.Approved);
            Save();
        }
        Changed?.Invoke();
        Completed(redemption);
        return true;
    }

    /// <summary>Rejects a pending redemption and refunds it (and gives the stock back); false when it is not pending.</summary>
    public bool Reject(string redemptionId)
    {
        StoreRedemption? redemption;
        lock (_gate)
        {
            redemption = Doc.Pending.FirstOrDefault(r => r.Id == redemptionId);
            if (redemption is null) return false;
            Doc.Pending.Remove(redemption);
            if (Doc.Items.FirstOrDefault(i => i.Id == redemption.ItemId) is { Stock: >= 0 } item) item.Stock++;
            Finish(redemption, StoreRedemption.Rejected);
            Save();
        }
        _points.Add(redemption.Platform, redemption.UserId, redemption.UserName, redemption.Cost, "store refund", Currency(redemption.Currency));
        Changed?.Invoke();
        _bus.Publish(new BotEvent(StoreEvents.Rejected, Args(redemption)));
        return true;
    }

    private void Finish(StoreRedemption redemption, string status)
    {
        redemption.Status = status;
        Doc.History.Insert(0, redemption);
        if (Doc.History.Count > HistorySize) Doc.History.RemoveRange(HistorySize, Doc.History.Count - HistorySize);
    }

    private void Completed(StoreRedemption redemption)
    {
        _bus.Publish(new BotEvent(StoreEvents.Redeemed, Args(redemption)));
        _feed?.Publish(OverlayMessage.Event("redemption-latest", new JsonObject
        {
            ["name"] = redemption.UserName,
            ["displayName"] = redemption.UserName,
            ["amount"] = redemption.Cost,
            ["redemption"] = redemption.ItemName,
            ["message"] = redemption.Input,
            ["type"] = "redemption",
            ["service"] = redemption.Platform.ToLowerInvariant(),
            ["store"] = true,
            ["_id"] = redemption.Id,
            ["createdAt"] = redemption.Time.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
        }));
    }

    private void OnEvent(BotEvent e)
    {
        if (e.Type != ChatHub.MessageEvent || e.Args.GetValueOrDefault("isInternal") is true) return;
        var text = e.Args.GetString("message").Trim();
        string command;
        StoreItem[] items;
        lock (_gate)
        {
            command = Doc.Command;
            items = Doc.Items.ToArray();
        }
        if (!text.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase)) return;
        var rest = text[(command.Length + 1)..].Trim();
        // The longest item name the rest starts with (names can have spaces); what follows is the input.
        var item = items.Where(i => rest.Equals(i.Name, StringComparison.OrdinalIgnoreCase)
                                    || rest.StartsWith(i.Name + " ", StringComparison.OrdinalIgnoreCase))
            .MaxBy(i => i.Name.Length);
        if (item is null) return;
        var user = e.Args.GetString("user");
        var userId = e.Args.GetString("userId") is { Length: > 0 } id ? id : user.ToLowerInvariant();
        var subscriber = e.Args.GetValueOrDefault("isSubscriber") is true || e.Args.GetValueOrDefault("isBroadcaster") is true;
        Redeem(item.Name, e.Args.GetString("platform"), userId, user, rest[item.Name.Length..], subscriber);
    }

    private static Dictionary<string, object?> Args(StoreRedemption r) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["redemptionId"] = r.Id, ["item"] = r.ItemName, ["itemId"] = r.ItemId, ["cost"] = r.Cost, ["currency"] = r.Currency,
        ["user"] = r.UserName, ["platform"] = r.Platform, ["userId"] = r.UserId, ["input"] = r.Input, ["rawInput"] = r.Input,
    };

    private void Check(StoreItem item, string? id)
    {
        if (string.IsNullOrWhiteSpace(item.Name)) throw new ArgumentException("An item needs a name");
        item.Name = item.Name.Trim();
        if (item.Cost < 0 || item.Stock < -1 || item.UserCooldownSeconds < 0 || item.GlobalCooldownSeconds < 0)
            throw new ArgumentException("Cost, stock and cooldowns cannot be negative (stock -1 is unlimited)");
        item.Currency = item.Currency.Trim();
        if (item.Currency.Length > 0 && !_points.CurrencyExists(item.Currency)) throw new ArgumentException($"There is no points currency '{item.Currency}'");
    }

    private static StoreItem Copy(StoreItem i) => new()
    {
        Id = i.Id, Name = i.Name, Description = i.Description, Cost = i.Cost, Currency = i.Currency, Enabled = i.Enabled, Stock = i.Stock,
        UserCooldownSeconds = i.UserCooldownSeconds, GlobalCooldownSeconds = i.GlobalCooldownSeconds, NeedsApproval = i.NeedsApproval,
        SubscribersOnly = i.SubscribersOnly,
    };

    private static string? Currency(string currency) => currency.Length == 0 ? null : currency;

    private void Save() => _file?.ScheduleSave();

    public void Dispose()
    {
        _subscription.Dispose();
        _file?.Dispose();
    }
}
