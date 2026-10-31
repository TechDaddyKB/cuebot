using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Quotes;
using Cuebot.Core.Store;

namespace Cuebot.Server.Api;

public sealed partial class ApiOperations
{
    // ---- Quotes ----------------------------------------------------------------------------------------------------

    private QuoteStore QuoteStore => services.Quotes ?? throw ApiException.Unavailable("Quotes are not available");

    /// <summary>Quotes containing <paramref name="search"/> (text, user or game), newest first, at most <paramref name="limit"/>.</summary>
    public JsonObject Quotes(string? search, int limit)
    {
        var list = (string.IsNullOrWhiteSpace(search) ? QuoteStore.All() : QuoteStore.Search(search)).OrderByDescending(q => q.Id).ToList();
        return new JsonObject
        {
            ["quotes"] = new JsonArray([.. list.Take(Math.Clamp(limit, 1, 500)).Select(q => (JsonNode)ToObject(q))]),
            ["count"] = list.Count,
        };
    }

    public JsonObject AddQuote(string text, string? user, string? game, string? platform)
    {
        if (string.IsNullOrWhiteSpace(text)) throw ApiException.BadRequest("The quote needs text");
        var quote = QuoteStore.Add(text, user ?? "", "", platform ?? "", game);
        services.Log.Info(LogSource, $"Quote #{quote.Id} added");
        return new JsonObject { ["quote"] = ToObject(quote) };
    }

    public JsonObject UpdateQuote(int id, string? text, string? user, string? game, string? platform) =>
        QuoteStore.Update(id, text, user, game, platform) is { } quote
            ? new JsonObject { ["quote"] = ToObject(quote) }
            : throw ApiException.NotFound($"Quote #{id} not found");

    public JsonObject DeleteQuote(int id) =>
        QuoteStore.Delete(id) is { } quote
            ? new JsonObject { ["deleted"] = ToObject(quote) }
            : throw ApiException.NotFound($"Quote #{id} not found");

    // ---- Points ----------------------------------------------------------------------------------------------------

    private Cuebot.Core.Points.PointsService PointsService => services.Points ?? throw ApiException.Unavailable("Points are not available");

    /// <summary>The currencies (the main one first) with their names and how they are earned.</summary>
    public JsonObject PointsCurrencies() => new()
    {
        ["main"] = ToObject<Cuebot.Core.Points.PointsCurrency>(PointsService.Settings),
        ["extra"] = JsonSerializer.SerializeToNode(PointsService.Currencies, JsonOptions),
    };

    /// <summary>One viewer's balance (by user name or id on a platform).</summary>
    public JsonObject UserPoints(string platform, string user, string? currency)
    {
        var (id, name) = ResolveUser(platform, user);
        var points = PointsService;
        CheckCurrency(currency);
        return new JsonObject
        {
            ["platform"] = platform, ["userId"] = id, ["user"] = name ?? user, ["currency"] = points.NameOf(currency),
            ["points"] = points.Get(platform, id, currency), ["rank"] = points.Rank(platform, id, currency),
        };
    }

    /// <summary><c>add</c>, <c>remove</c> or <c>set</c> a viewer's points; returns the new balance.</summary>
    public JsonObject ChangeUserPoints(string platform, string user, string command, long amount, string? currency)
    {
        var (id, name) = ResolveUser(platform, user);
        var points = PointsService;
        CheckCurrency(currency);
        var balance = command switch
        {
            "add" => points.Add(platform, id, name, amount, "api", currency),
            "remove" => points.Remove(platform, id, name, amount, currency),
            "set" => points.Set(platform, id, name, amount, currency),
            _ => throw ApiException.BadRequest($"Unknown points command '{command}' (add, remove or set)"),
        };
        services.Log.Info(LogSource, $"API {command} {amount} {points.NameOf(currency)} for {name ?? user} ({platform})");
        return new JsonObject { ["platform"] = platform, ["userId"] = id, ["user"] = name ?? user, ["currency"] = points.NameOf(currency), ["points"] = balance };
    }

    public JsonObject PointsLeaderboard(string? platform, int count, string? currency)
    {
        CheckCurrency(currency);
        return new JsonObject
        {
            ["currency"] = PointsService.NameOf(currency),
            ["leaderboard"] = JsonSerializer.SerializeToNode(PointsService.Leaderboard(string.IsNullOrWhiteSpace(platform) ? null : platform,
                Math.Clamp(count, 1, 100), currency), JsonOptions),
        };
    }

    private void CheckCurrency(string? currency)
    {
        if (!string.IsNullOrWhiteSpace(currency) && !PointsService.CurrencyExists(currency))
            throw ApiException.NotFound($"No points currency '{currency}'");
    }

    /// <summary>A viewer's id and name from a user name or id on <paramref name="platform"/> (seen in chat or holding points).</summary>
    private (string Id, string? Name) ResolveUser(string platform, string user)
    {
        user = user.Trim().TrimStart('@');
        if (platform.Trim().Length == 0 || user.Length == 0) throw ApiException.BadRequest("platform and user are required");
        if (services.Users?.FindByName(platform, user) is { } record) return (record.UserId, record.DisplayName.Length > 0 ? record.DisplayName : record.UserName);
        if (services.Users?.Get(platform, user) is { } byId) return (byId.UserId, byId.DisplayName.Length > 0 ? byId.DisplayName : byId.UserName);
        // Known only by points or variables: no display name to pass on, so the stored one is kept.
        if (services.Points?.FindUserId(platform, user) is { } pointsId) return (pointsId, null);
        if (services.UserVariables?.FindUserId(platform, user) is { } varId) return (varId, null);
        throw ApiException.NotFound($"No viewer '{user}' seen on {platform} yet");
    }

    // ---- User groups -----------------------------------------------------------------------------------------------

    private Cuebot.Core.Users.GroupStore GroupStore => services.Groups ?? throw ApiException.Unavailable("User groups are not available");

    public JsonObject Groups() => new()
    {
        ["groups"] = new JsonArray([.. GroupStore.All().Select(g => (JsonNode)new JsonObject
        {
            ["name"] = g.Name,
            ["members"] = JsonSerializer.SerializeToNode(GroupStore.UsersIn(g.Name), JsonOptions),
        })]),
    };

    /// <summary><c>create</c>, <c>delete</c>, <c>rename</c> (to <paramref name="newName"/>) or <c>clear</c> a group.</summary>
    public JsonObject GroupCommand(string group, string command, string? newName)
    {
        var store = GroupStore;
        var ok = command switch
        {
            "create" => store.Add(group),
            "delete" => store.Remove(group),
            "rename" => store.Rename(group, newName ?? throw ApiException.BadRequest("newName is required")),
            "clear" => store.Clear(group) >= 0 && store.Exists(group),
            _ => throw ApiException.BadRequest($"Unknown group command '{command}' (create, delete, rename or clear)"),
        };
        if (!ok)
            throw command == "create" ? ApiException.Conflict($"Group '{group}' already exists") : ApiException.NotFound($"Group '{group}' not found");
        return Groups();
    }

    /// <summary>Adds or removes a viewer (by name or id on a platform) from a group; adding creates the group if needed.</summary>
    public JsonObject GroupMember(string group, string platform, string user, bool add)
    {
        var (id, name) = ResolveUser(platform, user);
        var shown = name ?? user;
        var ok = add ? GroupStore.AddUser(group, platform, id, shown, createGroup: true) : GroupStore.RemoveUser(group, platform, id, shown);
        if (!ok) throw add ? ApiException.Conflict($"{shown} is already in '{group}'") : ApiException.NotFound($"{shown} is not in '{group}'");
        return Groups();
    }

    // ---- Viewers and their variables -------------------------------------------------------------------------------

    /// <summary>Viewers seen in chat whose name contains <paramref name="search"/> (most recently seen first).</summary>
    public JsonObject Viewers(string? platform, string? search, int limit)
    {
        var users = services.Users ?? throw ApiException.Unavailable("Viewers are not available");
        var list = users.All()
            .Where(u => string.IsNullOrWhiteSpace(platform) || string.Equals(u.Platform, platform.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(u => string.IsNullOrWhiteSpace(search) || u.UserName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                        || u.DisplayName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(u => u.LastSeen).ToList();
        return new JsonObject
        {
            ["viewers"] = JsonSerializer.SerializeToNode(list.Take(Math.Clamp(limit, 1, 200)), JsonOptions),
            ["count"] = list.Count,
        };
    }

    /// <summary>A viewer's variables (as read by %user...% sub-actions and the "Get/Set user variable" sub-actions).</summary>
    public JsonObject ViewerVariables(string platform, string user)
    {
        var vars = services.UserVariables ?? throw ApiException.Unavailable("Viewer variables are not available");
        var (id, name) = ResolveUser(platform, user);
        return new JsonObject
        {
            ["platform"] = platform, ["userId"] = id, ["user"] = name ?? user,
            ["variables"] = new JsonObject([.. vars.GetAll(platform, id).Select(kv => KeyValuePair.Create(kv.Key, Value(kv.Value)))]),
        };
    }

    public JsonObject SetViewerVariable(string platform, string user, string variable, JsonNode? value, bool persisted)
    {
        var vars = services.UserVariables ?? throw ApiException.Unavailable("Viewer variables are not available");
        if (string.IsNullOrWhiteSpace(variable)) throw ApiException.BadRequest("The variable needs a name");
        var (id, name) = ResolveUser(platform, user);
        vars.Set(platform, id, name, variable.Trim(), value is JsonValue v ? Plain(v) : value?.ToJsonString(), persisted);
        return ViewerVariables(platform, user);
    }

    public JsonObject DeleteViewerVariable(string platform, string user, string variable)
    {
        var vars = services.UserVariables ?? throw ApiException.Unavailable("Viewer variables are not available");
        var (id, _) = ResolveUser(platform, user);
        if (!vars.Delete(platform, id, variable.Trim())) throw ApiException.NotFound($"No variable '{variable}' for {user}");
        return ViewerVariables(platform, user);
    }

    private static object? Plain(JsonValue v) => v.GetValueKind() switch
    {
        JsonValueKind.String => v.GetValue<string>(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => v.TryGetValue<long>(out var l) ? l : v.GetValue<decimal>(),
        _ => null,
    };

    // ---- Store items -----------------------------------------------------------------------------------------------

    private PointsStore StoreService => services.Store ?? throw ApiException.Unavailable("The store is not available");

    /// <summary>Adds an item to the points store (<c>{name, description, cost, currency, stock, needsApproval, ...}</c>).</summary>
    public JsonObject AddStoreItem(JsonObject body)
    {
        var item = FromNode<StoreItem>(body, "item");
        if (string.IsNullOrWhiteSpace(item.Name)) throw ApiException.BadRequest("The item needs a name");
        if (StoreService.Find(item.Name) is not null) throw ApiException.Conflict($"An item named '{item.Name}' already exists");
        item.Id = Guid.NewGuid().ToString("N");
        StoreService.Add(item);
        return Store();
    }

    /// <summary>Changes the given fields of a store item (by id or name).</summary>
    public JsonObject UpdateStoreItem(string idOrName, JsonObject changes)
    {
        var existing = StoreService.Find(idOrName) ?? throw ApiException.NotFound($"Store item '{idOrName}' not found");
        var merged = FromNode<StoreItem>(Merge(ToObject(existing), changes), "changes");
        StoreService.Update(existing.Id, item =>
        {
            item.Name = merged.Name;
            item.Description = merged.Description;
            item.Cost = merged.Cost;
            item.Currency = merged.Currency;
            item.Enabled = merged.Enabled;
            item.Stock = merged.Stock;
            item.UserCooldownSeconds = merged.UserCooldownSeconds;
            item.GlobalCooldownSeconds = merged.GlobalCooldownSeconds;
            item.NeedsApproval = merged.NeedsApproval;
            item.SubscribersOnly = merged.SubscribersOnly;
        });
        return Store();
    }

    public JsonObject DeleteStoreItem(string idOrName)
    {
        var existing = StoreService.Find(idOrName) ?? throw ApiException.NotFound($"Store item '{idOrName}' not found");
        StoreService.Remove(existing.Id);
        return Store();
    }
}
