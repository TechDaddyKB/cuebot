using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.PngtuberRemix;

/// <summary>Bot event types published by <see cref="PngtuberRemixService"/>.</summary>
public static class PngtuberRemixEvents
{
    public const string Prefix = "pngtuberRemix";
    public const string Connected = "pngtuberRemix.connected";
    public const string Disconnected = "pngtuberRemix.disconnected";

    /// <summary>The showing avatar state changed (seen by polling list_states, or made by Cuebot).</summary>
    public const string StateChanged = "pngtuberRemix.stateChanged";

    /// <summary>Sprites were added, removed or renamed (another model was loaded or the model was edited).</summary>
    public const string SpritesChanged = "pngtuberRemix.spritesChanged";

    /// <summary>Cuebot paused or resumed throwing (PNGTuber Remix has no way to read it otherwise).</summary>
    public const string ThrowingPausedChanged = "pngtuberRemix.throwingPausedChanged";
}

/// <summary>An avatar state (1-based number, as PNGTuber Remix shows it) or a sprite PNGTuber Remix reported.</summary>
public sealed record PngtuberRemixState(int Id, string Name, bool IsCurrent);

/// <summary>A sprite as list_sprites reports it. <see cref="Id"/> and <see cref="ParentId"/> are whole numbers as text.</summary>
public sealed record PngtuberRemixSprite(string Name, string Id, bool Visible, bool IsGroup)
{
    public string ParentId { get; init; } = "";

    /// <summary>The names of the sprites attached directly to this one.</summary>
    public IReadOnlyList<string> Children { get; init; } = [];
}

/// <summary>
/// PNGTuber Remix through its WebSocket API (WebSocket tab; ws://127.0.0.1:9321). Requests are <c>{"event":..}</c>.
/// PNGTuber Remix handles them one by one and answers in order with the same "event" (and "result" "success" or
/// "failed"); an unknown event or bad JSON is answered with "event":"error", a ping with "event":"pong". It pushes
/// nothing. A request it cannot handle (a script error in PNGTuber Remix, for example toggling a sprite that does not
/// exist) gets no answer at all, so answers are matched by their event, not only by their order.
/// </summary>
public sealed partial class PngtuberRemixService : StreamAppConnection
{
    /// <summary>Re-read the sprites on every this-many-th poll (the states are read on every poll).</summary>
    private const int SpritePollEvery = 5;

    private readonly JsonFile<StreamAppsSettings> _settings;
    private readonly object _gate = new();
    private readonly List<PendingRequest> _pending = [];
    /// <summary>Per sprite: completes when the last effect queued for it is over (see RunEffectAsync).</summary>
    private readonly Dictionary<string, Task> _effectDone = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private volatile IReadOnlyList<PngtuberRemixState> _states = [];
    private volatile IReadOnlyList<PngtuberRemixSprite> _sprites = [];
    private volatile bool _statesKnown;
    private volatile bool _spritesKnown;
    private bool? _throwingPaused;

    public PngtuberRemixService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(), StreamAppsTimings.Default)
    {
    }

    internal PngtuberRemixService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports, StreamAppsTimings timings)
        : base("PNGTuber Remix", bus, log, transports, timings)
    {
        _settings = settings;
    }

    public PngtuberRemixSettings Settings => _settings.Value.PngtuberRemix;

    public void SaveSettings() => _settings.ScheduleSave();

    public string Host => Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "127.0.0.1";

    public int Port => Settings.Port is > 0 and < 65536 ? Settings.Port : 9321;

    /// <summary>How often a ping is sent when polling is off (tests shorten it).</summary>
    internal TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How often the states (and every 5th time the sprites) are re-read; zero when polling is off.</summary>
    internal TimeSpan PollInterval => Settings.PollSeconds is > 0 and var s ? TimeSpan.FromSeconds(Math.Max(0.2, Math.Min(s, 3600))) : TimeSpan.Zero;

    /// <summary>The most items one throw sends (PNGTuber Remix spawns one physics body per item).</summary>
    public int MaxThrowAmount => Settings.MaxThrowAmount > 0 ? Settings.MaxThrowAmount : 50;

    /// <summary>Avatar states read on connect, by polling and by <see cref="RefreshAsync"/>.</summary>
    public IReadOnlyList<PngtuberRemixState> States => _states;

    /// <summary>Sprites read on connect, by polling and by <see cref="RefreshAsync"/>.</summary>
    public IReadOnlyList<PngtuberRemixSprite> Sprites => _sprites;

    /// <summary>Whether throwing is paused, as far as Cuebot knows (only its own pause/resume answers tell).</summary>
    public bool? ThrowingPaused
    {
        get { lock (_gate) return _throwingPaused; }
    }

    public IReadOnlyList<string> StateNames() => [.. _states.Select(s => s.Name)];

    /// <summary>Sprite names for suggestions; a name used by more than one sprite is offered once per sprite id.</summary>
    public IReadOnlyList<string> SpriteNames() => SpriteSuggestions(false);

    /// <summary>Like <see cref="SpriteNames"/>, with "(3 attached)" after sprites that have others attached.</summary>
    public IReadOnlyList<string> SpriteNamesWithAttached() => SpriteSuggestions(true);

    public IReadOnlyList<string> GroupNames() => [.. _sprites.Where(s => s.IsGroup).Select(s => s.Name).Distinct(StringComparer.Ordinal)];

    /// <summary>How many sprites are attached to this one, directly or through others.</summary>
    public int AttachedCount(PngtuberRemixSprite sprite)
    {
        var sprites = _sprites;
        var seen = new HashSet<string>(StringComparer.Ordinal) { sprite.Id };
        var queue = new Queue<string>([sprite.Id]);
        var count = 0;
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var child in sprites.Where(s => s.ParentId == parent && s.Id.Length > 0 && seen.Add(s.Id)))
            {
                count++;
                queue.Enqueue(child.Id);
            }
        }
        return count;
    }

    private List<string> SpriteSuggestions(bool withAttached)
    {
        var sprites = _sprites;
        var result = new List<string>();
        foreach (var group in sprites.GroupBy(s => s.Name, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                result.Add(group.Key);
                result.AddRange(group.Where(s => s.Id.Length > 0).Select(s => $"{s.Name} (id {s.Id})"));
                continue;
            }
            var sprite = group.First();
            var attached = withAttached ? AttachedCount(sprite) : 0;
            result.Add(attached > 0 ? $"{sprite.Name} ({attached} attached)" : sprite.Name);
        }
        return result;
    }

    /// <summary>Raised (on a background thread) when the states or sprites changed.</summary>
    public event Action? DataChanged;

    protected override bool AutoConnect => Settings.AutoConnect;
    protected override bool AutoReconnect => Settings.AutoReconnect;

    private protected override (Uri Url, string Display) Endpoint() => (new Uri($"ws://{Host}:{Port}/"), $"PNGTuber Remix at {Host}:{Port}");

    private protected override Dictionary<string, object?> ConnectionArgs() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["pngtuberRemix.host"] = Host,
        ["pngtuberRemix.port"] = Port,
    };

    private protected override void Publish(string name, Dictionary<string, object?> args)
    {
        foreach (var (key, value) in ConnectionArgs()) args.TryAdd(key, value);
        args.TryAdd("service", Name);
        Bus.Publish(new BotEvent($"{PngtuberRemixEvents.Prefix}.{name}", new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)));
    }

    // ---- Session ----

    private protected override async Task RunSessionAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = PumpAsync(transport, session.Token);
        var watch = Task.CompletedTask;
        try
        {
            MarkConnected($"Connected to PNGTuber Remix at {Host}:{Port}");
            try
            {
                await RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or StreamAppRequestException)
            {
                Log.Warn(Name, $"Could not read the states and sprites: {ex.Message}");
            }
            watch = WatchAsync(session.Token);
            if (await Task.WhenAny(pump, watch).ConfigureAwait(false) == watch && watch.IsFaulted)
                await watch.ConfigureAwait(false); // PNGTuber Remix stopped answering: end the session (reconnects when on).
            await pump.ConfigureAwait(false);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            foreach (var task in new[] { pump, watch })
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Session over; the first failure was already handled above.
                }
            }
        }
    }

    private protected override void OnSessionEnded()
    {
        _states = [];
        _sprites = [];
        _statesKnown = false;
        _spritesKnown = false;
        lock (_gate)
        {
            _effectDone.Clear();
            _throwingPaused = null;
        }
        _warned.Clear();
        DataChanged?.Invoke();
    }

    private async Task PumpAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text)
            {
                if (JsonUtil.TryParse(text) is not { ValueKind: JsonValueKind.Object } message) continue;
                Answer(message.Clone());
            }
        }
        finally
        {
            List<PendingRequest> pending;
            lock (_gate)
            {
                pending = [.. _pending];
                _pending.Clear();
            }
            foreach (var p in pending) p.Done.TrySetException(new StreamAppNotConnectedException(Name));
        }
    }

    /// <summary>
    /// Matches an answer to its request. PNGTuber Remix answers in order, so an answer belongs to the oldest open request
    /// with the same event; older requests before it will never be answered and fail at once. "error" answers the
    /// oldest request. A request that timed out keeps its place, so its late answer is used up there.
    /// </summary>
    private void Answer(JsonElement message)
    {
        var ev = message.Str("event");
        PendingRequest request;
        List<PendingRequest> skipped;
        lock (_gate)
        {
            var index = ev == "error" ? (_pending.Count > 0 ? 0 : -1) : _pending.FindIndex(p => p.Expected == ev);
            if (index < 0)
            {
                if (ev == "error") Log.Warn(Name, $"PNGTuber Remix reported an error: {message.Str("message")}");
                return;
            }
            skipped = _pending.GetRange(0, index);
            request = _pending[index];
            _pending.RemoveRange(0, index + 1);
        }
        foreach (var s in skipped.Where(s => s.AbandonedAt is null))
            s.Done.TrySetException(new StreamAppRequestException(
                $"PNGTuber Remix did not answer {s.Event} (it skips requests it cannot carry out, such as a key name it does not know)."));
        if (request.AbandonedAt is not null) return; // the late answer to a request that already timed out
        request.Done.TrySetResult(message);
    }

    /// <summary>
    /// While connected: re-reads the states (and now and then the sprites) every poll interval, or pings every 15 s
    /// when polling is off. Two missed answers in a row mean PNGTuber Remix hangs or the connection is half-open: the
    /// session ends (and reconnects when that is on).
    /// </summary>
    private async Task WatchAsync(CancellationToken ct)
    {
        var missed = 0;
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            var poll = PollInterval;
            await Task.Delay(poll > TimeSpan.Zero ? poll : PingInterval, ct).ConfigureAwait(false);
            try
            {
                if (poll > TimeSpan.Zero) await PollAsync(++tick % SpritePollEvery == 0, ct).ConfigureAwait(false);
                else await RequestAsync(new { @event = "ping" }, ct).ConfigureAwait(false);
                missed = 0;
            }
            catch (TimeoutException) when (!ct.IsCancellationRequested)
            {
                if (++missed >= 2) throw new TimeoutException("PNGTuber Remix stopped answering");
            }
            catch (StreamAppRequestException)
            {
                missed = 0; // it answered
            }
            catch (StreamAppNotConnectedException)
            {
                return;
            }
        }
    }

    private async Task PollAsync(bool sprites, CancellationToken ct)
    {
        try
        {
            UpdateStates(ParseStates(await RequestAsync(new { @event = "list_states" }, ct).ConfigureAwait(false)), "PNGTuber Remix");
        }
        catch (StreamAppRequestException)
        {
            // "no state buttons found" while a model loads.
        }
        if (sprites || !_spritesKnown)
            UpdateSprites(ParseSprites(await RequestAsync(new { @event = "list_sprites" }, ct).ConfigureAwait(false)));
    }

    // ---- Requests ----

    /// <summary>
    /// Sends one request and returns its answer (see <see cref="Answer"/> for the matching). Throws
    /// <see cref="StreamAppRequestException"/> with a plain-words reason when PNGTuber Remix refused it.
    /// </summary>
    internal async Task<JsonElement> RequestAsync(object request, CancellationToken ct)
    {
        EnsureConnected();
        var text = JsonUtil.Serialize(request);
        var ev = JsonUtil.TryParse(text)?.Str("event") ?? "";
        var pending = new PendingRequest(ev);
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Queue and send under one lock so the queue is in the order PNGTuber Remix receives them.
            lock (_gate)
            {
                DropForgotten();
                _pending.Add(pending);
            }
            await SendAsync(text, ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate) _pending.Remove(pending);
            throw;
        }
        finally
        {
            _sendGate.Release();
        }
        JsonElement answer;
        try
        {
            answer = await pending.Done.Task.WaitAsync(Timings.RequestTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            lock (_gate) pending.AbandonedAt ??= Environment.TickCount64;
            if (ex is TimeoutException) throw new TimeoutException($"PNGTuber Remix did not answer {ev} in time.");
            throw;
        }
        if (answer.Str("event") == "error")
            throw new StreamAppRequestException($"PNGTuber Remix refused the request: {answer.Str("message")}");
        if (answer.Str("result") == "failed") throw new StreamAppRequestException(Explain(ev, answer));
        return answer;
    }

    /// <summary>Drops timed-out requests whose answer never came (under <see cref="_gate"/>).</summary>
    private void DropForgotten()
    {
        var limit = Environment.TickCount64 - (long)(Timings.RequestTimeout.TotalMilliseconds * 4);
        var removed = _pending.RemoveAll(p => p.AbandonedAt is { } at && at < limit);
        if (removed > 0) Log.Warn(Name, $"PNGTuber Remix never answered {removed} earlier request(s); no longer waiting for them.");
    }

    /// <summary>A one-line hint for a "failed" answer.</summary>
    internal string Explain(string ev, JsonElement answer)
    {
        var error = answer.Str("error");
        var id = answer.Str("identifier");
        return error switch
        {
            "throwable spawner not active" =>
                "PNGTuber Remix: throwing is not set up. Turn on throwables in PNGTuber Remix and add at least one image first.",
            "item not found" =>
                $"PNGTuber Remix has no throwable named '{answer.Str("item_name")}' (capital letters matter).",
            "no state buttons found" => "PNGTuber Remix has no states yet (is a model still loading?).",
            "failed to load model" =>
                $"PNGTuber Remix could not open '{answer.Str("file_path")}'. Check that the file exists on that computer and ends in .pngRemix or .save.",
            "state not found" =>
                $"PNGTuber Remix has no state '{id}' (capital letters matter){(StateNames() is { Count: > 0 } names ? $"; its states are {string.Join(", ", names)}" : "")}.",
            "sprite not found" or "group not found" => $"PNGTuber Remix has no sprite '{id}' (capital letters matter).",
            "" => $"PNGTuber Remix: {ev} failed.",
            _ => $"PNGTuber Remix: {error}{(id.Length > 0 ? $" ({id})" : "")}.",
        };
    }

    /// <summary>Re-reads the avatar states and sprites.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PngtuberRemixState> states;
        try
        {
            states = ParseStates(await RequestAsync(new { @event = "list_states" }, ct).ConfigureAwait(false));
        }
        catch (StreamAppRequestException)
        {
            states = []; // "no state buttons found" while a model is loading.
        }
        var sprites = ParseSprites(await RequestAsync(new { @event = "list_sprites" }, ct).ConfigureAwait(false));
        UpdateStates(states, "PNGTuber Remix", force: true);
        UpdateSprites(sprites, force: true);
        if (IsConnected)
            SetStatus(Cuebot.Core.ConnectionState.Connected,
                $"Connected to PNGTuber Remix at {Host}:{Port}: {_states.Count} states, {_sprites.Count} sprites");
    }

    private static List<PngtuberRemixState> ParseStates(JsonElement answer) =>
        answer.TryGetProperty("states", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object)
                .Select(s => new PngtuberRemixState((int)(s.Num("id") ?? 0), s.Str("name"), s.Bool("is_current") ?? false))]
            : [];

    private static List<PngtuberRemixSprite> ParseSprites(JsonElement answer) =>
        answer.TryGetProperty("sprites", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object)
                .Select(s => new PngtuberRemixSprite(s.Str("name"), WholeNumber(s.Str("id")), s.Bool("visible") ?? false, s.Bool("is_group") ?? false)
                {
                    ParentId = WholeNumber(s.Str("parent_id")),
                    Children = s.TryGetProperty("children", out var c) && c.ValueKind == JsonValueKind.Array
                        ? [.. c.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())]
                        : [],
                })
                .Where(s => s.Name.Length > 0)]
            : [];

    /// <summary>Godot writes ids as floats ("537693010870.0"); PNGTuber Remix matches them as whole numbers.</summary>
    internal static string WholeNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(d) < 9e15 && d == Math.Floor(d)
            ? ((long)d).ToString(CultureInfo.InvariantCulture)
            : text;

    /// <summary>Stores new states; publishes stateChanged when the showing state changed (not on the first read).</summary>
    private void UpdateStates(IReadOnlyList<PngtuberRemixState> states, string changedBy, bool force = false)
    {
        var old = _states;
        _states = states;
        var before = old.FirstOrDefault(s => s.IsCurrent);
        var now = states.FirstOrDefault(s => s.IsCurrent);
        if (_statesKnown && now is not null && (before is null || before.Id != now.Id || before.Name != now.Name))
        {
            Publish("stateChanged", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["pngtuberRemix.state"] = now.Name,
                ["pngtuberRemix.stateNumber"] = now.Id,
                ["pngtuberRemix.previousState"] = before?.Name ?? "",
                ["pngtuberRemix.previousStateNumber"] = before?.Id.ToString(CultureInfo.InvariantCulture) ?? "",
                ["pngtuberRemix.changedBy"] = changedBy,
            });
        }
        if (states.Count > 0) _statesKnown = true;
        if (force || !old.SequenceEqual(states)) DataChanged?.Invoke();
    }

    /// <summary>Stores new sprites; publishes spritesChanged when sprites were added, removed or renamed.</summary>
    private void UpdateSprites(IReadOnlyList<PngtuberRemixSprite> sprites, bool force = false)
    {
        var old = _sprites;
        _sprites = sprites;
        static string Key(PngtuberRemixSprite s) => $"{s.Id}\n{s.Name}";
        var oldKeys = old.Select(Key).ToHashSet(StringComparer.Ordinal);
        var newKeys = sprites.Select(Key).ToHashSet(StringComparer.Ordinal);
        if (_spritesKnown && !oldKeys.SetEquals(newKeys))
        {
            var oldNames = old.Select(s => s.Name).ToList();
            var newNames = sprites.Select(s => s.Name).ToList();
            Publish("spritesChanged", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["pngtuberRemix.spriteCount"] = sprites.Count,
                ["pngtuberRemix.previousSpriteCount"] = old.Count,
                ["pngtuberRemix.added"] = string.Join(", ", newNames.Except(oldNames, StringComparer.Ordinal)),
                ["pngtuberRemix.removed"] = string.Join(", ", oldNames.Except(newNames, StringComparer.Ordinal)),
            });
            _warned.Clear();
        }
        _spritesKnown = true;
        if (force || !SameSprites(old, sprites)) DataChanged?.Invoke();
    }

    private static bool SameSprites(IReadOnlyList<PngtuberRemixSprite> a, IReadOnlyList<PngtuberRemixSprite> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Name == p.Second.Name && p.First.Id == p.Second.Id
                                                && p.First.Visible == p.Second.Visible && p.First.IsGroup == p.Second.IsGroup
                                                && p.First.ParentId == p.Second.ParentId
                                                && p.First.Children.SequenceEqual(p.Second.Children, StringComparer.Ordinal));

    // ---- Name resolution ----

    [GeneratedRegex(@"^(?<name>.*?)\s*\((?:id\s+(?<id>-?[0-9]+(?:\.0+)?)|(?<n>[0-9]+)\s+attached)\)$")]
    private static partial Regex SuggestionSuffix();

    /// <summary>
    /// The identifier to send for a sprite name typed by the user: its exact name (PNGTuber Remix matches names
    /// case-sensitively), or a sprite id. Accepts the suggestion forms "Hat (id 123)" and "Head (3 attached)". Fails
    /// with a clear message when the sprite list is known and has no such sprite (PNGTuber Remix leaves some requests
    /// for missing sprites unanswered).
    /// </summary>
    internal async Task<string> ResolveSpriteAsync(string text, string label, CancellationToken ct)
    {
        var wanted = Steps.Require(text, label);
        if (Resolve(_sprites, wanted) is { } found) return found;
        if (_sprites.Count == 0 && !_spritesKnown) return wanted; // not read yet: let PNGTuber Remix decide
        // The list may be out of date (a sprite was just added): read it again once.
        UpdateSprites(ParseSprites(await RequestAsync(new { @event = "list_sprites" }, ct).ConfigureAwait(false)));
        return Resolve(_sprites, wanted)
               ?? throw new StreamAppRequestException($"PNGTuber Remix has no sprite named '{wanted}'{Hint(wanted)}.");
    }

    private string? Resolve(IReadOnlyList<PngtuberRemixSprite> sprites, string wanted)
    {
        var exact = sprites.Where(s => s.Name == wanted).ToList();
        if (exact.Count > 0)
        {
            if (exact.Count > 1 && _warned.TryAdd($"dup\n{wanted}", 0))
                Log.Warn(Name, $"{exact.Count} sprites are named '{wanted}'; PNGTuber Remix uses the first one. " +
                               $"To pick another, use its id: {string.Join(", ", exact.Select(s => $"'{wanted} (id {s.Id})'"))}.");
            return wanted;
        }
        if (WholeNumber(wanted) is var id && sprites.Any(s => s.Id.Length > 0 && s.Id == id)) return id;
        if (SuggestionSuffix().Match(wanted) is { Success: true } m)
        {
            if (m.Groups["id"].Success) return WholeNumber(m.Groups["id"].Value);
            return Resolve(sprites, m.Groups["name"].Value.Trim());
        }
        var close = sprites.Select(s => s.Name).Where(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal).ToList();
        return close.Count == 1 ? close[0] : null;
    }

    private string Hint(string wanted)
    {
        var names = _sprites.Select(s => s.Name).Distinct(StringComparer.Ordinal).ToList();
        var similar = names.Where(n => n.Contains(wanted, StringComparison.OrdinalIgnoreCase) || wanted.Contains(n, StringComparison.OrdinalIgnoreCase))
            .Take(3).ToList();
        return similar.Count > 0 ? $" (did you mean {string.Join(" or ", similar.Select(n => $"'{n}'"))}?)" : " (press Reload on its card after changing the model)";
    }

    /// <summary>The cached sprite an identifier names (exact name or id), if any.</summary>
    private PngtuberRemixSprite? Find(string identifier) =>
        _sprites.FirstOrDefault(s => s.Name == identifier) ?? _sprites.FirstOrDefault(s => s.Id.Length > 0 && s.Id == identifier);

    // ---- States ----

    /// <summary>The current avatar state, asked fresh.</summary>
    public async Task<PngtuberRemixState?> GetStateAsync(CancellationToken ct = default)
    {
        var states = ParseStates(await RequestAsync(new { @event = "list_states" }, ct).ConfigureAwait(false));
        UpdateStates(states, "PNGTuber Remix");
        return states.FirstOrDefault(s => s.IsCurrent);
    }

    /// <summary>
    /// Switches the avatar state, by name or by its number (1 = the first). A name is sent with the exact spelling of
    /// a known state (PNGTuber Remix compares names case-sensitively). Returns the state's name.
    /// </summary>
    public async Task<string> SetStateAsync(string state, CancellationToken ct = default)
    {
        var wanted = Steps.Require(state, "State");
        var states = _states;
        var known = states.FirstOrDefault(s => s.Name == wanted);
        if (known is null)
        {
            var close = states.Where(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (close.Count == 1) known = close[0];
        }
        object request = known is not null
            ? new { @event = "state", state_name = known.Name }
            : int.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? new { @event = "state", state_id = number }
                : new { @event = "state", state_name = wanted };
        var answer = await RequestAsync(request, ct).ConfigureAwait(false);
        var name = answer.Str("state_name") is { Length: > 0 } n ? n : known?.Name ?? wanted;
        if (answer.Num("state_id") is { } id)
            UpdateStates([.. _states.Select(s => s with { IsCurrent = s.Id == (int)id })], "Cuebot");
        return name;
    }

    // ---- Sprites ----

    /// <summary>
    /// Shows, hides or toggles a sprite. PNGTuber Remix always does the same to every sprite attached to it. Returns
    /// whether it is now visible, when known.
    /// </summary>
    public async Task<bool?> SetVisibilityAsync(string name, SwitchMode mode, CancellationToken ct = default)
    {
        var target = await ResolveSpriteAsync(name, "Sprite", ct).ConfigureAwait(false);
        var verb = mode switch { SwitchMode.On => "show", SwitchMode.Off => "hide", _ => "toggle" };
        var answer = await RequestAsync(new Dictionary<string, object?> { ["event"] = $"{verb}_sprite", ["sprite_name"] = target }, ct)
            .ConfigureAwait(false);
        bool? visible = mode switch
        {
            SwitchMode.On => true,
            SwitchMode.Off => false,
            _ => answer.Str("visibility") switch { "visible" => true, "hidden" => false, _ => null },
        };
        if (visible is { } v) SetCachedVisibility(target, v, withAttached: true);
        return visible;
    }

    /// <summary>
    /// Makes a sprite visible when another one is (or hidden, with <paramref name="opposite"/>). With
    /// <paramref name="withAttached"/> the sprites attached to it follow. Returns whether it is now visible.
    /// </summary>
    public async Task<bool?> MatchVisibilityAsync(string sprite, string source, bool opposite, bool withAttached, CancellationToken ct = default)
    {
        var target = await ResolveSpriteAsync(sprite, "Sprite", ct).ConfigureAwait(false);
        var match = await ResolveSpriteAsync(source, "Match sprite", ct).ConfigureAwait(false);
        var request = withAttached
            ? new Dictionary<string, object?> { ["event"] = "toggle_group", ["group_name"] = target, ["match_sprite"] = match, ["reverse"] = opposite }
            : new Dictionary<string, object?> { ["event"] = "toggle_sprite", ["sprite_name"] = target, ["match_sprite"] = match, ["reverse"] = opposite };
        var answer = await RequestAsync(request, ct).ConfigureAwait(false);
        bool? visible = answer.Str("visibility") switch { "visible" => true, "hidden" => false, _ => null };
        if (visible is { } v) SetCachedVisibility(target, v, withAttached);
        return visible;
    }

    /// <summary>Whether a sprite is visible now (asked fresh).</summary>
    public async Task<bool> GetVisibilityAsync(string sprite, CancellationToken ct = default)
    {
        var wanted = Steps.Require(sprite, "Sprite");
        UpdateSprites(ParseSprites(await RequestAsync(new { @event = "list_sprites" }, ct).ConfigureAwait(false)));
        var target = Resolve(_sprites, wanted) is { } id ? Find(id) : null;
        return target?.Visible ?? throw new StreamAppRequestException($"PNGTuber Remix has no sprite named '{wanted}'{Hint(wanted)}.");
    }

    private void SetCachedVisibility(string identifier, bool visible, bool withAttached)
    {
        if (Find(identifier) is not { } sprite) return;
        var ids = new HashSet<string>(StringComparer.Ordinal) { sprite.Id };
        if (withAttached)
        {
            var added = true;
            while (added)
            {
                added = false;
                foreach (var s in _sprites.Where(s => s.ParentId.Length > 0 && ids.Contains(s.ParentId) && !ids.Contains(s.Id)))
                    added |= ids.Add(s.Id);
            }
        }
        var updated = _sprites.Select(s => (ReferenceEquals(s, sprite) || ids.Contains(s.Id)) && s.Visible != visible ? s with { Visible = visible } : s).ToList();
        if (SameSprites(updated, _sprites)) return;
        _sprites = updated;
        DataChanged?.Invoke();
    }

    /// <summary>
    /// Presses a key the way PNGTuber Remix's key binds would ("Trigger key binds"): every sprite bound to the key
    /// toggles, or hides for a hide key. Returns what it did, like "Hat (Toggle)"; empty when no sprite uses that key.
    /// </summary>
    public async Task<IReadOnlyList<string>> PressKeyAsync(string key, CancellationToken ct = default)
    {
        // Always send "key": without it PNGTuber Remix stops with a script error and never answers.
        var answer = await RequestAsync(new { @event = "general", key = Steps.Require(key, "Key") }, ct).ConfigureAwait(false);
        return answer.TryGetProperty("actions_found", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())]
            : [];
    }

    // ---- Effects ----

    /// <summary>
    /// Runs one effect at a time per sprite. PNGTuber Remix remembers where a sprite was when an effect starts and
    /// returns it there at the end, saving that into the current state; an effect that starts while another still
    /// runs would remember a mid-way spot and leave the sprite (and the state) out of place. So a second effect on
    /// the same sprite waits until the first is over. "Over" counts from when the first was actually sent (not from
    /// when it was asked for), so a slow send never lets the next effect start early.
    /// </summary>
    private async Task RunEffectAsync(string identifier, double seconds, Func<Task> send, CancellationToken ct)
    {
        var key = Find(identifier)?.Id is { Length: > 0 } id ? id : identifier;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_gate)
        {
            previous = _effectDone.GetValueOrDefault(key) ?? Task.CompletedTask;
            _effectDone[key] = done.Task;
        }
        var length = TimeSpan.FromMilliseconds(Math.Ceiling(Math.Max(0, seconds) * 1000) + 50);
        var sending = false;
        try
        {
            await previous.WaitAsync(ct).ConfigureAwait(false);
            sending = true;
            await send().ConfigureAwait(false);
        }
        catch
        {
            // Not sent (cancelled while waiting): the next effect waits only for the ones before. Failed while
            // sending: part of it may be running, so count it as if it ran.
            if (sending) _ = Task.Delay(length).ContinueWith(_ => done.TrySetResult(), TaskScheduler.Default);
            else _ = previous.ContinueWith(_ => done.TrySetResult(), TaskScheduler.Default);
            throw;
        }
        _ = Task.Delay(length).ContinueWith(_ => done.TrySetResult(), TaskScheduler.Default);
    }

    private static double NotNegative(double? value, string label) =>
        value is < 0 ? throw new ArgumentException($"{label} cannot be less than 0.") : value ?? 0;

    /// <summary>Shakes a sprite (strength in pixels, default 10) for some seconds (default 1).</summary>
    public async Task ShakeAsync(string sprite, double? strength, double? seconds, CancellationToken ct = default)
    {
        var name = await ResolveSpriteAsync(sprite, "Sprite", ct).ConfigureAwait(false);
        var duration = seconds is null ? 1 : NotNegative(seconds, "Seconds");
        await RunEffectAsync(name, duration + 0.1, () => RequestAsync(new { @event = "shake_sprite", sprite_name = name, intensity = strength ?? 10, duration }, ct), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Bounces a sprite (height in pixels, default 20) once over some seconds (default 0.5).</summary>
    public async Task BounceAsync(string sprite, double? height, double? seconds, CancellationToken ct = default)
    {
        var name = await ResolveSpriteAsync(sprite, "Sprite", ct).ConfigureAwait(false);
        var duration = seconds is null ? 0.5 : NotNegative(seconds, "Seconds");
        // Up (half), a small dip (an eighth), down (half).
        await RunEffectAsync(name, duration * 1.125, () => RequestAsync(new { @event = "bounce_sprite", sprite_name = name, height = height ?? 20, duration }, ct), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a sprite to x/y (both needed: PNGTuber Remix moves to an exact spot) and/or sets its size and turn, over
    /// some seconds; with <paramref name="returnAfter"/> it goes back that many seconds later. Size and turn are set
    /// together: an empty size means 1 (normal) and an empty turn 0 degrees. <paramref name="scaleX"/> and
    /// <paramref name="scaleY"/> stretch one way only.
    /// </summary>
    public async Task MoveAsync(string sprite, double? x, double? y, double? scale, double? rotation, double? seconds, double? returnAfter,
        CancellationToken ct = default, double? scaleX = null, double? scaleY = null)
    {
        Steps.Require(sprite, "Sprite");
        var resize = scale is not null || rotation is not null || scaleX is not null || scaleY is not null;
        if (x is null && y is null && !resize)
            throw new ArgumentException("Set a position (X and Y), a size or a turn.");
        if ((x is null) != (y is null))
            throw new ArgumentException($"Set both X and Y: PNGTuber Remix moves the sprite to an exact spot, so an empty {(x is null ? "X" : "Y")} would mean 0.");
        var duration = seconds is null ? 1 : NotNegative(seconds, "Seconds");
        var delay = NotNegative(returnAfter, "Go back after");
        var name = await ResolveSpriteAsync(sprite, "Sprite", ct).ConfigureAwait(false);
        var reset = returnAfter is not null;
        var total = reset ? duration * 2 + delay : duration;
        await RunEffectAsync(name, total, async () =>
        {
            if (x is not null)
                await RequestAsync(new
                {
                    @event = "move_sprite", sprite_name = name, x, y, duration, reset, reset_delay = delay,
                }, ct).ConfigureAwait(false);
            if (resize)
            {
                var sx = scaleX ?? scale ?? 1;
                var sy = scaleY ?? scale ?? 1;
                await RequestAsync(new
                {
                    @event = "animate_sprite", sprite_name = name, scale = scale ?? sx, scale_x = sx, scale_y = sy, rotation = rotation ?? 0,
                    duration, reset, reset_delay = delay,
                }, ct).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    // ---- Throwing and models ----

    /// <summary>
    /// Throws items at the avatar: a named item from its throwables, or random ones when the name is empty. The
    /// amount is capped at <see cref="MaxThrowAmount"/>. <paramref name="variance"/> and <paramref name="bothSides"/>
    /// left null use PNGTuber Remix's own settings. Returns the amount sent (null = PNGTuber Remix's own amount).
    /// </summary>
    public async Task<int?> ThrowAsync(string item, int? amount, CancellationToken ct = default, double? variance = null, bool? bothSides = null)
    {
        if (amount is <= 0) throw new ArgumentException("Amount must be at least 1.");
        if (variance is < 0) throw new ArgumentException("Variance cannot be less than 0.");
        if (amount > MaxThrowAmount)
        {
            Log.Info(Name, $"Throwing {MaxThrowAmount} instead of {amount} (the most per throw, set on the PNGTuber Remix card).");
            amount = MaxThrowAmount;
        }
        var request = new Dictionary<string, object?>();
        if (item.Trim().Length > 0)
        {
            request["event"] = "throw_item";
            request["item_name"] = item.Trim();
            request["amount"] = amount ??= 1;
        }
        else
        {
            request["event"] = "throw_random";
            if (amount is { } n) request["amount"] = n;
        }
        if (variance is { } v) request["variance"] = v;
        if (bothSides is { } b) request["both_sides"] = b;
        await RequestAsync(request, ct).ConfigureAwait(false);
        return amount;
    }

    /// <summary>Pauses or resumes throwing (Toggle flips). Returns whether throwing is now paused.</summary>
    public async Task<bool?> SetThrowingPausedAsync(SwitchMode mode, CancellationToken ct = default)
    {
        object request = mode == SwitchMode.Toggle
            ? new { @event = "toggle_throwables_pause" }
            : new { @event = "toggle_throwables_pause", pause = mode == SwitchMode.On };
        var paused = (await RequestAsync(request, ct).ConfigureAwait(false)).Bool("is_paused");
        bool changed;
        lock (_gate)
        {
            changed = paused is not null && _throwingPaused != paused;
            if (paused is not null) _throwingPaused = paused;
        }
        if (changed)
            Publish("throwingPausedChanged", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["pngtuberRemix.throwingPaused"] = paused,
            });
        return paused;
    }

    /// <summary>
    /// Opens a saved model (.pngRemix or .save file; PNGTuber Remix checks the ending with capital letters as written)
    /// and re-reads its states and sprites.
    /// </summary>
    public async Task LoadModelAsync(string path, CancellationToken ct = default)
    {
        var file = Steps.Require(path, "Model file").Trim('"');
        if (!file.EndsWith(".pngRemix", StringComparison.Ordinal) && !file.EndsWith(".save", StringComparison.Ordinal))
        {
            throw new ArgumentException(file.EndsWith(".pngremix", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".save", StringComparison.OrdinalIgnoreCase)
                ? $"The model file must end in .pngRemix or .save written exactly like that (capital R): '{file}'."
                : $"The model file must be a .pngRemix (or .save) file: '{file}'.");
        }
        if (IsLocalHost && !File.Exists(file))
            throw new ArgumentException($"There is no model file at '{file}'.");
        var oldStates = _states;
        var oldSprites = _sprites;
        await RequestAsync(new { @event = "load_model", file_path = file }, ct).ConfigureAwait(false);
        try
        {
            // The new state buttons and sprites appear a moment after the answer; the old ones may still be listed
            // right after it. Re-read until the lists change (about 2 s at most; reloading the same model never does).
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
                await RefreshAsync(ct).ConfigureAwait(false);
                if (_states.Count > 0 && (!oldStates.Select(s => s.Name).SequenceEqual(_states.Select(s => s.Name)) || !SameSprites(oldSprites, _sprites)))
                    break;
            }
        }
        catch (Exception ex) when (ex is TimeoutException or StreamAppRequestException)
        {
            Log.Warn(Name, $"Loaded the model but could not re-read it: {ex.Message}");
        }
    }

    private bool IsLocalHost =>
        Host is "localhost" || (IPAddress.TryParse(Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    private sealed class PendingRequest(string ev)
    {
        public string Event { get; } = ev;

        /// <summary>The event of the answer: the request's own, "pong" for a ping.</summary>
        public string Expected { get; } = ev == "ping" ? "pong" : ev;

        public TaskCompletionSource<JsonElement> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>When the request timed out (Environment.TickCount64); its late answer is then thrown away.</summary>
        public long? AbandonedAt { get; set; }
    }
}
