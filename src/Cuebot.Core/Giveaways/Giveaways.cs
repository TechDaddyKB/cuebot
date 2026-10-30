using System.Globalization;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Points;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Giveaways;

/// <summary>Someone in the giveaway: their tickets, and whether they were a subscriber when they joined (for luck).</summary>
public sealed class GiveawayEntrant
{
    public string Platform { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public int Tickets { get; set; }
    public bool Subscriber { get; set; }
}

/// <summary>How a giveaway runs; see <see cref="Giveaways.Open"/>.</summary>
public sealed record GiveawayOptions
{
    public string Title { get; init; } = "";
    public string Command { get; init; } = "!join";

    /// <summary>Points per ticket (0: free).</summary>
    public long TicketCost { get; init; }

    /// <summary>Most tickets one viewer may hold.</summary>
    public int MaxTickets { get; init; } = 1;

    /// <summary>Points currency for tickets (empty: the default).</summary>
    public string Currency { get; init; } = "";

    /// <summary>How much more a subscriber's tickets count (1: the same).</summary>
    public double SubscriberLuck { get; init; } = 1;

    public bool SubscribersOnly { get; init; }

    /// <summary>Seconds it takes entries (0: until closed).</summary>
    public int Seconds { get; init; }

    public int Winners { get; init; } = 1;
}

/// <summary>The current giveaway, its entrants (by "platform:userId", in the order they joined) and its winners.</summary>
public sealed class GiveawayRun
{
    public string Id { get; set; } = "";
    public GiveawayOptions Options { get; set; } = new();
    public string Status { get; set; } = Giveaways.OpenStatus;
    public DateTimeOffset? EndsAt { get; set; }
    public Dictionary<string, GiveawayEntrant> Entrants { get; set; } = [];
    public List<GiveawayEntrant> Winners { get; set; } = [];
}

/// <summary>giveaways.json.</summary>
public sealed class GiveawayDocument
{
    /// <summary>Announce winners in chat on every connected platform.</summary>
    public bool AnnounceWinners { get; set; } = true;

    public GiveawayRun? Current { get; set; }
}

/// <summary>Events giveaways publish (triggers "Giveaway opened" and so on).</summary>
public static class GiveawayEvents
{
    /// <summary>Args: giveawayId, title, command, ticketCost, maxTickets, subscribersOnly, duration.</summary>
    public const string Opened = "cuebot.giveawayOpened";

    /// <summary>Args: giveawayId, title, entrants, tickets.</summary>
    public const string Closed = "cuebot.giveawayClosed";

    /// <summary>Args: giveawayId, title, winner, winnerPlatform, winnerUserId, winnerTickets, entrants, tickets.</summary>
    public const string Winner = "cuebot.giveawayWinner";

    /// <summary>Args: giveawayId, title, entrants, refunded (points given back).</summary>
    public const string Canceled = "cuebot.giveawayCanceled";

    /// <summary>Args: giveawayId, title, user, platform, userId, tickets (now held), added.</summary>
    public const string Entered = "cuebot.giveawayEntered";
}

/// <summary>
/// Cuebot giveaways, entered in every platform's chat: the join command (<c>!join</c>, or <c>!join 3</c> for more
/// tickets) takes Cuebot points when tickets cost something, up to the most tickets per viewer; subscribers can be
/// luckier or the only ones allowed. Entries close on time or when told; drawing picks winners at random weighted by
/// tickets (and luck), never the same viewer twice; a redraw adds one more; cancelling refunds every ticket. The
/// running giveaway is kept in giveaways.json. States go to overlay pages as <c>cuebot-giveaway</c>.
/// </summary>
public sealed class Giveaways : IPeriodicService, IDisposable
{
    public const string OpenStatus = "open";
    public const string ClosedStatus = "closed";
    public const string DrawnStatus = "drawn";
    public const string CanceledStatus = "canceled";

    /// <summary>The overlay listener giveaway states are sent on.</summary>
    public const string Listener = "cuebot-giveaway";

    private readonly object _gate = new();
    private readonly JsonFile<GiveawayDocument>? _file;
    private readonly GiveawayDocument _memory = new();
    private readonly EventBus _bus;
    private readonly OverlayFeed _feed;
    private readonly PointsService? _points;
    private readonly ChatHub? _chat;
    private readonly AppLog _log;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly IDisposable _subscription;

    public Giveaways(AppPaths? paths, AppLog log, EventBus bus, OverlayFeed feed, PointsService? points = null, ChatHub? chat = null,
        TimeProvider? time = null, Random? random = null)
    {
        _log = log;
        _bus = bus;
        _feed = feed;
        _points = points;
        _chat = chat;
        _time = time ?? TimeProvider.System;
        _random = random ?? Random.Shared;
        if (paths is not null) _file = new JsonFile<GiveawayDocument>(paths.File("giveaways.json"), log);
        _subscription = bus.Subscribe(OnEvent);
    }

    private GiveawayDocument Doc => _file?.Value ?? _memory;

    /// <summary>Raised (on the caller's thread) after any change.</summary>
    public event Action? Changed;

    public bool AnnounceWinners
    {
        get { lock (_gate) return Doc.AnnounceWinners; }
        set
        {
            lock (_gate)
            {
                Doc.AnnounceWinners = value;
                Save();
            }
        }
    }

    /// <summary>The current (or last) giveaway's state as JSON, or null when there has been none.</summary>
    public JsonObject? Snapshot()
    {
        lock (_gate) return Doc.Current is { } run ? ToJson(run) : null;
    }

    /// <summary>Whether a giveaway is open or closed but not drawn yet (another cannot start until it is drawn or cancelled).</summary>
    public bool Running
    {
        get { lock (_gate) return Doc.Current is { Status: OpenStatus or ClosedStatus }; }
    }

    public string? Status
    {
        get { lock (_gate) return Doc.Current?.Status; }
    }

    public IReadOnlyList<GiveawayEntrant> Entrants
    {
        get { lock (_gate) return Doc.Current?.Entrants.Values.ToArray() ?? []; }
    }

    public IReadOnlyList<GiveawayEntrant> Winners
    {
        get { lock (_gate) return Doc.Current?.Winners.ToArray() ?? []; }
    }

    /// <summary>Opens a giveaway. Throws when one is still running or the options are bad.</summary>
    public JsonObject Open(GiveawayOptions options)
    {
        var command = options.Command.Trim().Split(' ')[0];
        if (string.IsNullOrWhiteSpace(options.Title)) throw new ArgumentException("A title (the prize) is needed");
        if (command.Length == 0) throw new ArgumentException("A join command is needed");
        if (options.TicketCost < 0 || options.MaxTickets < 1 || options.Winners < 1 || options.SubscriberLuck < 1)
            throw new ArgumentException("Ticket cost, tickets, winners and luck must be positive (luck at least 1)");
        if (options.TicketCost > 0 && _points is null) throw new InvalidOperationException("Tickets that cost points need Cuebot points");
        if (options.Currency.Length > 0 && _points?.CurrencyExists(options.Currency) == false)
            throw new ArgumentException($"There is no points currency '{options.Currency}'");
        JsonObject state;
        lock (_gate)
        {
            if (Doc.Current is { Status: OpenStatus or ClosedStatus }) throw new InvalidOperationException("A giveaway is still running; draw or cancel it first");
            Doc.Current = new GiveawayRun
            {
                Id = Guid.NewGuid().ToString("N"),
                Options = options with { Title = options.Title.Trim(), Command = command },
                EndsAt = options.Seconds > 0 ? _time.GetUtcNow().AddSeconds(options.Seconds) : null,
            };
            Save();
            state = ToJson(Doc.Current);
        }
        Send(state);
        _bus.Publish(new BotEvent(GiveawayEvents.Opened, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["giveawayId"] = state["id"]!.GetValue<string>(), ["title"] = options.Title.Trim(), ["command"] = command,
            ["ticketCost"] = options.TicketCost, ["maxTickets"] = options.MaxTickets, ["subscribersOnly"] = options.SubscribersOnly,
            ["duration"] = Math.Max(0, options.Seconds),
        }));
        return state;
    }

    /// <summary>Stops taking entries; null when no giveaway is open.</summary>
    public JsonObject? Close()
    {
        JsonObject state;
        lock (_gate)
        {
            if (Doc.Current is not { Status: OpenStatus } run) return null;
            run.Status = ClosedStatus;
            Save();
            state = ToJson(run);
        }
        Send(state);
        _bus.Publish(new BotEvent(GiveawayEvents.Closed, Args(state)));
        return state;
    }

    /// <summary>
    /// Closes entries and draws the winners (the number set when opened); with <paramref name="oneMore"/> draws one more
    /// (a redraw when a winner does not answer). Null when there is no giveaway to draw from or nobody left to pick.
    /// </summary>
    public JsonObject? Draw(bool oneMore = false)
    {
        JsonObject state;
        List<GiveawayEntrant> picked = [];
        bool announce;
        lock (_gate)
        {
            if (Doc.Current is not { } run || run.Status == CanceledStatus || (run.Status == DrawnStatus && !oneMore)) return null;
            var count = oneMore ? 1 : Math.Max(1, run.Options.Winners) - run.Winners.Count;
            for (var i = 0; i < count; i++)
            {
                var pool = run.Entrants.Values.Where(e => !run.Winners.Contains(e)).ToArray();
                if (pool.Length == 0) break;
                var weights = pool.Select(e => e.Tickets * (e.Subscriber ? run.Options.SubscriberLuck : 1)).ToArray();
                var roll = _random.NextDouble() * weights.Sum();
                var at = 0;
                while (at < pool.Length - 1 && roll >= weights[at]) roll -= weights[at++];
                run.Winners.Add(pool[at]);
                picked.Add(pool[at]);
            }
            if (picked.Count == 0 && run.Status == DrawnStatus) return null;
            run.Status = DrawnStatus;
            Save();
            state = ToJson(run);
            announce = Doc.AnnounceWinners;
        }
        Send(state);
        foreach (var winner in picked)
        {
            var args = Args(state);
            args["winner"] = winner.UserName;
            args["winnerPlatform"] = winner.Platform;
            args["winnerUserId"] = winner.UserId;
            args["winnerTickets"] = winner.Tickets;
            _bus.Publish(new BotEvent(GiveawayEvents.Winner, args));
        }
        if (announce && picked.Count > 0 && _chat is not null)
        {
            var names = string.Join(", ", picked.Select(w => "@" + w.UserName));
            _ = AnnounceAsync($"🎉 {names} won {state["title"]!.GetValue<string>()}!");
        }
        return state;
    }

    /// <summary>Cancels and gives every ticket's points back; null when there is no giveaway to cancel.</summary>
    public JsonObject? Cancel()
    {
        JsonObject state;
        List<GiveawayEntrant> refunds;
        GiveawayOptions options;
        lock (_gate)
        {
            if (Doc.Current is not { Status: OpenStatus or ClosedStatus } run) return null;
            run.Status = CanceledStatus;
            refunds = run.Entrants.Values.ToList();
            options = run.Options;
            Save();
            state = ToJson(run);
        }
        long refunded = 0;
        if (options.TicketCost > 0 && _points is not null)
        {
            foreach (var e in refunds)
            {
                var amount = e.Tickets * options.TicketCost;
                _points.Add(e.Platform, e.UserId, e.UserName, amount, "giveaway refund", Currency(options.Currency));
                refunded += amount;
            }
        }
        Send(state);
        var args = Args(state);
        args["refunded"] = refunded;
        _bus.Publish(new BotEvent(GiveawayEvents.Canceled, args));
        return state;
    }

    /// <summary>Closes entries once the time is up.</summary>
    public void Tick(DateTimeOffset now)
    {
        bool close;
        lock (_gate) close = Doc.Current is { Status: OpenStatus, EndsAt: { } ends } && now >= ends;
        if (close) Close();
    }

    // ---- Chat -------------------------------------------------------------------------------------------------------

    private void OnEvent(BotEvent e)
    {
        if (e.Type != ChatHub.MessageEvent || e.Args.GetValueOrDefault("isInternal") is true) return;
        var words = e.Args.GetString("message").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is 0 or > 2) return;
        var platform = e.Args.GetString("platform");
        var user = e.Args.GetString("user");
        var userId = e.Args.GetString("userId") is { Length: > 0 } id ? id : user.ToLowerInvariant();
        if (platform.Length == 0 || userId.Length == 0) return;
        var subscriber = e.Args.GetValueOrDefault("isSubscriber") is true || e.Args.GetValueOrDefault("isBroadcaster") is true;
        JsonObject? state = null;
        int held = 0, added = 0;
        string giveawayId = "", title = "";
        lock (_gate)
        {
            if (Doc.Current is not { Status: OpenStatus } run || !words[0].Equals(run.Options.Command, StringComparison.OrdinalIgnoreCase)) return;
            if (run.Options.SubscribersOnly && !subscriber) return;
            var wanted = words.Length == 2 ? int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0 : 1;
            var key = $"{platform.ToLowerInvariant()}:{userId}";
            run.Entrants.TryGetValue(key, out var entrant);
            var room = run.Options.MaxTickets - (entrant?.Tickets ?? 0);
            added = Math.Min(Math.Max(0, wanted), room);
            if (added <= 0) return;
            if (run.Options.TicketCost > 0)
            {
                if (_points is null || !_points.TrySpend(platform, userId, user, added * run.Options.TicketCost, out _, Currency(run.Options.Currency))) return;
            }
            entrant ??= run.Entrants[key] = new GiveawayEntrant { Platform = platform, UserId = userId, UserName = user };
            entrant.Tickets += added;
            entrant.Subscriber |= subscriber;
            held = entrant.Tickets;
            giveawayId = run.Id;
            title = run.Options.Title;
            Save();
            state = ToJson(run);
        }
        Send(state);
        _bus.Publish(new BotEvent(GiveawayEvents.Entered, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["giveawayId"] = giveawayId, ["title"] = title, ["user"] = user, ["platform"] = platform, ["userId"] = userId,
            ["tickets"] = held, ["added"] = added,
        }));
    }

    // ---- State ------------------------------------------------------------------------------------------------------

    private JsonObject ToJson(GiveawayRun run)
    {
        var o = run.Options;
        return new JsonObject
        {
            ["id"] = run.Id,
            ["title"] = o.Title,
            ["status"] = run.Status,
            ["command"] = o.Command,
            ["ticketCost"] = o.TicketCost,
            ["maxTickets"] = o.MaxTickets,
            ["currency"] = _points is null ? o.Currency : _points.NameOf(Currency(o.Currency)),
            ["subscribersOnly"] = o.SubscribersOnly,
            ["subscriberLuck"] = o.SubscriberLuck,
            ["endsAt"] = run.EndsAt?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["entrants"] = run.Entrants.Count,
            ["tickets"] = run.Entrants.Values.Sum(e => e.Tickets),
            ["recent"] = new JsonArray([.. run.Entrants.Values.TakeLast(8).Reverse().Select(e => (JsonNode)e.UserName)]),
            ["names"] = new JsonArray([.. run.Entrants.Values.Take(200).Select(e => (JsonNode)e.UserName)]),
            ["winners"] = new JsonArray([.. run.Winners.Select(w => (JsonNode)new JsonObject { ["name"] = w.UserName, ["platform"] = w.Platform })]),
        };
    }

    private static Dictionary<string, object?> Args(JsonObject state) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["giveawayId"] = state["id"]!.GetValue<string>(),
        ["title"] = state["title"]!.GetValue<string>(),
        ["entrants"] = state["entrants"]!.GetValue<int>(),
        ["tickets"] = state["tickets"]!.GetValue<int>(),
    };

    private void Send(JsonObject state)
    {
        var json = new JsonObject { ["type"] = "event", ["listener"] = Listener, ["event"] = state }.ToJsonString();
        _feed.Publish(new OverlayMessage(json, Droppable: state["status"]!.GetValue<string>() == OpenStatus));
        Changed?.Invoke();
    }

    private async Task AnnounceAsync(string text)
    {
        try
        {
            await _chat!.SendAsync(ChatHub.AllPlatforms, text).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("Giveaways", "Could not announce the winner in chat: " + ex.Message);
        }
    }

    private static string? Currency(string currency) => currency.Length == 0 ? null : currency;

    private void Save() => _file?.ScheduleSave();

    public void Dispose()
    {
        _subscription.Dispose();
        _file?.Dispose();
    }
}
