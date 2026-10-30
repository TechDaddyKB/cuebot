using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Quotes;

/// <summary>One saved quote.</summary>
public sealed class Quote
{
    /// <summary>Number shown to viewers ("quote #12"). Unique; never reused unless the quotes are re-indexed.</summary>
    public int Id { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Who added (or said) the quote.</summary>
    public string User { get; set; } = "";

    public string UserId { get; set; } = "";
    public string Platform { get; set; } = "";

    /// <summary>Game / category being streamed when the quote was added.</summary>
    public string Game { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>quotes.json.</summary>
public sealed class QuotesFile
{
    public int Version { get; set; } = 1;
    public int NextId { get; set; } = 1;
    public List<Quote> Quotes { get; set; } = [];
}

public static class QuoteEvents
{
    public const string Added = "quotes.added";
    public const string Shown = "quotes.shown";
    public const string Removed = "quotes.removed";
    public const string Updated = "quotes.updated";
}

/// <summary>
/// The quote database (quotes.json). Publishes quotes.added / quotes.updated / quotes.removed, and quotes.shown when
/// <see cref="MarkShown"/> is called (by "Get quote"). Remembers the last game/category reported by any platform event
/// (arguments gameName, categoryName or game) so new quotes record what was being streamed. Thread-safe.
/// </summary>
public sealed class QuoteStore : Users.IViewerDataStore, IDisposable
{
    private static readonly string[] GameKeys = ["gameName", "categoryName", "game"];

    private readonly JsonFile<QuotesFile>? _file;
    private readonly QuotesFile _data;
    private readonly EventBus? _bus;
    private readonly TimeProvider _time;
    private readonly System.Random _random;
    private readonly IDisposable? _subscription;
    private readonly object _gate = new();
    private string _currentGame = "";

    public QuoteStore(AppPaths? paths, AppLog log, EventBus? bus = null, TimeProvider? time = null, System.Random? random = null)
    {
        _bus = bus;
        _time = time ?? TimeProvider.System;
        _random = random ?? new System.Random();
        if (paths is not null)
        {
            _file = new JsonFile<QuotesFile>(paths.File("quotes.json"), log);
            _data = _file.Value;
        }
        else
        {
            _data = new QuotesFile();
        }
        _data.Quotes.Sort((a, b) => a.Id.CompareTo(b.Id));
        _data.NextId = Math.Max(_data.NextId, _data.Quotes.Count == 0 ? 1 : _data.Quotes.Max(q => q.Id) + 1);
        _subscription = bus?.Subscribe(OnEvent);
    }

    /// <summary>Raised after any change (add, edit, delete, import, re-index).</summary>
    public event Action? Changed;

    /// <summary>Last game/category seen on the bus; used when a quote is added without one.</summary>
    public string CurrentGame
    {
        get { lock (_gate) return _currentGame; }
        set { lock (_gate) _currentGame = value?.Trim() ?? ""; }
    }

    public int Count
    {
        get { lock (_gate) return _data.Quotes.Count; }
    }

    public IReadOnlyList<Quote> All()
    {
        lock (_gate) return Json.Clone(_data.Quotes);
    }

    public Quote? Get(int id)
    {
        lock (_gate) return _data.Quotes.FirstOrDefault(q => q.Id == id) is { } q ? Json.Clone(q) : null;
    }

    /// <summary>Quotes whose text, user or game contains <paramref name="query"/> (or whose id equals it), by id.</summary>
    public IReadOnlyList<Quote> Search(string? query)
    {
        var q = query?.Trim() ?? "";
        lock (_gate)
        {
            if (q.Length == 0) return Json.Clone(_data.Quotes);
            var id = ParseId(q);
            return Json.Clone(_data.Quotes.Where(x => x.Id == id || Matches(x, q)).ToList());
        }
    }

    /// <summary>A random quote, optionally only among those matching <paramref name="search"/>. Null when none.</summary>
    public Quote? RandomQuote(string? search = null)
    {
        var candidates = Search(search);
        if (candidates.Count == 0) return null;
        lock (_gate) return candidates[_random.Next(candidates.Count)];
    }

    /// <summary>Adds a quote; an empty <paramref name="game"/> uses <see cref="CurrentGame"/>.</summary>
    public Quote Add(string text, string user = "", string userId = "", string platform = "", string? game = null,
        DateTimeOffset? createdAt = null)
    {
        text = text.Trim();
        if (text.Length == 0) throw new ArgumentException("The quote text is empty");
        Quote copy;
        lock (_gate)
        {
            var quote = new Quote
            {
                Id = _data.NextId++,
                Text = text,
                User = user.Trim().TrimStart('@'),
                UserId = userId.Trim(),
                Platform = platform.Trim(),
                Game = string.IsNullOrWhiteSpace(game) ? _currentGame : game.Trim(),
                CreatedAt = createdAt ?? _time.GetUtcNow(),
            };
            _data.Quotes.Add(quote);
            copy = Json.Clone(quote);
            Save();
        }
        Raise(QuoteEvents.Added, copy);
        return copy;
    }

    /// <summary>Edits a quote; null values keep the current field. Returns the updated quote, or null when missing.</summary>
    public Quote? Update(int id, string? text = null, string? user = null, string? game = null, string? platform = null,
        DateTimeOffset? createdAt = null)
    {
        Quote copy;
        lock (_gate)
        {
            var quote = _data.Quotes.FirstOrDefault(q => q.Id == id);
            if (quote is null) return null;
            if (text is not null)
            {
                if (text.Trim().Length == 0) throw new ArgumentException("The quote text is empty");
                quote.Text = text.Trim();
            }
            if (user is not null) quote.User = user.Trim().TrimStart('@');
            if (game is not null) quote.Game = game.Trim();
            if (platform is not null) quote.Platform = platform.Trim();
            if (createdAt is { } at) quote.CreatedAt = at;
            copy = Json.Clone(quote);
            Save();
        }
        Raise(QuoteEvents.Updated, copy);
        return copy;
    }

    /// <summary>Deletes a quote and returns it, or null when there is no such id.</summary>
    public Quote? Delete(int id)
    {
        Quote? removed;
        lock (_gate)
        {
            removed = _data.Quotes.FirstOrDefault(q => q.Id == id);
            if (removed is null) return null;
            _data.Quotes.Remove(removed);
            Save();
        }
        Raise(QuoteEvents.Removed, removed);
        return removed;
    }

    /// <summary>Renumbers the quotes 1..n in their current order. Returns how many ids changed.</summary>
    public int Reindex()
    {
        int changed = 0;
        lock (_gate)
        {
            for (var i = 0; i < _data.Quotes.Count; i++)
            {
                if (_data.Quotes[i].Id != i + 1) changed++;
                _data.Quotes[i].Id = i + 1;
            }
            _data.NextId = _data.Quotes.Count + 1;
            Save();
        }
        Changed?.Invoke();
        return changed;
    }

    /// <summary>
    /// Imports quotes. Appending gives every imported quote a new id after the existing ones; replacing clears the
    /// database first and keeps the imported ids where they are positive and unique. Returns how many were imported.
    /// </summary>
    public int Import(IEnumerable<Quote> quotes, bool replace)
    {
        var incoming = quotes.Where(q => !string.IsNullOrWhiteSpace(q.Text)).Select(Json.Clone).ToList();
        lock (_gate)
        {
            if (replace)
            {
                _data.Quotes.Clear();
                _data.NextId = 1;
                var used = new HashSet<int>();
                var keep = incoming.Where(q => q.Id > 0 && used.Add(q.Id)).ToHashSet();
                foreach (var q in incoming.Where(q => !keep.Contains(q))) q.Id = 0;
                var next = keep.Count == 0 ? 1 : keep.Max(q => q.Id) + 1;
                foreach (var q in incoming.Where(q => q.Id == 0)) q.Id = next++;
                _data.NextId = next;
            }
            else
            {
                foreach (var q in incoming) q.Id = _data.NextId++;
            }
            var now = _time.GetUtcNow();
            foreach (var q in incoming)
            {
                q.Text = q.Text.Trim();
                if (q.CreatedAt == default) q.CreatedAt = now;
                _data.Quotes.Add(q);
            }
            _data.Quotes.Sort((a, b) => a.Id.CompareTo(b.Id));
            Save();
        }
        Changed?.Invoke();
        return incoming.Count;
    }

    /// <summary>Publishes quotes.shown for a quote a sub-action or script displayed.</summary>
    public void MarkShown(Quote quote) => _bus?.Publish(new BotEvent(QuoteEvents.Shown, ToArgs(quote)));

    /// <summary>Arguments describing a quote: quoteId, quote, quoteUser, quoteUserId, quotePlatform, quoteGame, quoteDate, quoteTimestamp.</summary>
    public static Dictionary<string, object?> ToArgs(Quote quote) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["quoteId"] = quote.Id,
        ["quote"] = quote.Text,
        ["quoteUser"] = quote.User,
        ["quoteUserId"] = quote.UserId,
        ["quotePlatform"] = quote.Platform,
        ["quoteGame"] = quote.Game,
        ["quoteDate"] = quote.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd", ExtrasText.Inv),
        ["quoteTimestamp"] = quote.CreatedAt.ToString("o", ExtrasText.Inv),
        ["quoteTime"] = quote.CreatedAt.LocalDateTime,
    };

    /// <summary>Parses "12" or "#12"; 0 when not a positive number.</summary>
    public static int ParseId(string? text)
    {
        var t = ExtrasText.Clean(text).TrimStart('#');
        return int.TryParse(t, System.Globalization.NumberStyles.None, ExtrasText.Inv, out var id) && id > 0 ? id : 0;
    }

    public void Flush() => _file?.Flush();

    private static bool Matches(Quote q, string query) =>
        q.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
        || q.User.Contains(query, StringComparison.OrdinalIgnoreCase)
        || q.Game.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void OnEvent(BotEvent e)
    {
        if (e.Type.StartsWith("quotes.", StringComparison.Ordinal) || e.Type == Chat.ChatHub.MessageEvent) return;
        foreach (var key in GameKeys)
        {
            var value = e.Args.GetString(key).Trim();
            if (value.Length == 0) continue;
            CurrentGame = value;
            return;
        }
    }

    string Users.IViewerDataStore.Name => "quotes";

    /// <summary>Keeps the quotes (the stream's content) but removes who said or added them.</summary>
    int Users.IViewerDataStore.Forget(Func<Users.ViewerRef, bool> match)
    {
        lock (_gate)
        {
            var quotes = _data.Quotes.Where(q => q.Platform.Length > 0 && match(new Users.ViewerRef(q.Platform, q.UserId, q.User))).ToList();
            foreach (var q in quotes)
            {
                q.User = "";
                q.UserId = "";
            }
            if (quotes.Count > 0) Save();
            return quotes.Count;
        }
    }

    private void Save() => _file?.ScheduleSave();

    private void Raise(string type, Quote quote)
    {
        Changed?.Invoke();
        _bus?.Publish(new BotEvent(type, ToArgs(quote)));
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _file?.Dispose();
    }
}
