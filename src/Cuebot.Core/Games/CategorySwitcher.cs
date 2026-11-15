using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Requests;
using Cuebot.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Games;

/// <summary>Bus events for a game starting and stopping (published by game detection, e.g. Steam).</summary>
public static class GameEvents
{
    public const string Started = "steam.gameStarted";
    public const string Stopped = "steam.gameStopped";
    public const string Category = "Games";
}

/// <summary>
/// A platform whose stream category Cuebot can set (Twitch's game, Kick's category, YouTube's video category).
/// Implemented by the platform modules.
/// </summary>
public interface IStreamCategoryTarget
{
    /// <summary>"Twitch", "Kick", "YouTube".</summary>
    string Platform { get; }

    /// <summary>True when signed in so a change can succeed.</summary>
    bool CanSet { get; }

    /// <summary>The current category's name, or null when unknown.</summary>
    Task<string?> CurrentAsync(CancellationToken ct = default);

    /// <summary>The platform's category for a game (exact name first), or null when it has none.</summary>
    Task<string?> FindAsync(string game, CancellationToken ct = default);

    /// <summary>Sets the category; returns the name that was set.</summary>
    Task<string> SetAsync(string category, CancellationToken ct = default);
}

/// <summary>Comparing a game's name with a platform's category name.</summary>
public static class CategoryNames
{
    /// <summary>
    /// Same letters and digits, ignoring case, spaces and punctuation, or one starting with the other
    /// ("HELLDIVERS™ 2" is "Helldivers 2"). Platforms' searches return their best guess for anything, so only a close
    /// name counts as the game's category.
    /// </summary>
    public static bool Similar(string a, string b)
    {
        static string Key(string s) => new([.. s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
        var ka = Key(a);
        var kb = Key(b);
        return ka.Length > 0 && kb.Length > 0 && (ka == kb || ka.StartsWith(kb, StringComparison.Ordinal) || kb.StartsWith(ka, StringComparison.Ordinal));
    }
}

/// <summary>games.json: switching the stream category to the game being played, after someone allows it.</summary>
public sealed class CategorySettings
{
    /// <summary>Ask to switch the category when a game starts.</summary>
    public bool Enabled { get; set; } = true;

    public bool Twitch { get; set; } = true;
    public bool Kick { get; set; } = true;

    /// <summary>YouTube only has broad categories: a game sets "Gaming". Off at first.</summary>
    public bool YouTube { get; set; }

    /// <summary>Only ask while a platform is live (off: also before you go live, so the category is right from the start).</summary>
    public bool OnlyWhileLive { get; set; }

    /// <summary>How long a request waits for an answer before it expires (nothing changes then).</summary>
    public int RequestMinutes { get; set; } = 10;

    /// <summary>Games that switch at once, without asking (filled by "Always for this game").</summary>
    public List<string> AlwaysSwitch { get; set; } = [];

    /// <summary>Games that never ask.</summary>
    public List<string> NeverAsk { get; set; } = [];

    /// <summary>Game name → the category to use when the platform's name differs ("Helldivers 2" → "HELLDIVERS 2").</summary>
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When the game closes, ask to switch back to this category ("" = do not ask).</summary>
    public string ReturnCategory { get; set; } = "";
}

/// <summary>
/// When a game starts, finds its category on each platform that is turned on and signed in, and asks (through
/// <see cref="StreamRequests"/>: the desktop, the mod portal and the companion app) to switch to it. Nothing changes
/// until someone allows it, unless the game is on the "always" list. When the game stops, a request still waiting is
/// withdrawn, and switching back to <see cref="CategorySettings.ReturnCategory"/> is asked for when one is set.
/// </summary>
public sealed class CategorySwitcher : IDisposable
{
    public const string RequestKind = "category";

    private readonly JsonFile<CategorySettings> _file;
    private readonly StreamRequests _requests;
    private readonly Func<IEnumerable<IStreamCategoryTarget>> _targets;
    private readonly Func<IEnumerable<IStreamLiveStatus>> _live;
    private readonly AppLog _log;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private string? _game;
    private bool _switched;

    public CategorySwitcher(JsonFile<CategorySettings> file, StreamRequests requests, EventBus bus, AppLog log,
        Func<IEnumerable<IStreamCategoryTarget>> targets, Func<IEnumerable<IStreamLiveStatus>>? live = null)
    {
        _file = file;
        _requests = requests;
        _targets = targets;
        _live = live ?? (() => []);
        _log = log;
        _subscription = bus.Subscribe(OnEvent);
    }

    public CategorySettings Settings
    {
        get { lock (_gate) return Json.Clone(_file.Value); }
    }

    public void UpdateSettings(Action<CategorySettings> change)
    {
        lock (_gate)
        {
            change(_file.Value);
            _file.Value.Overrides = new Dictionary<string, string>(_file.Value.Overrides ?? [], StringComparer.OrdinalIgnoreCase);
            _file.SaveNow();
        }
    }

    /// <summary>The task of the last reaction to a game event (tests wait for it).</summary>
    internal Task Last { get; private set; } = Task.CompletedTask;

    private void OnEvent(BotEvent e)
    {
        if (e.Type == GameEvents.Started) Last = Run(() => GameStartedAsync(e.Args.GetString("game")));
        else if (e.Type == GameEvents.Stopped) Last = Run(() => GameStoppedAsync(e.Args.GetString("game")));
    }

    private async Task Run(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("Games", "Could not look up the stream category: " + ex.Message);
        }
    }

    private IReadOnlyList<IStreamCategoryTarget> Enabled(CategorySettings s) =>
        [.. _targets().Where(t => t.CanSet && t.Platform.ToLowerInvariant() switch
        {
            "twitch" => s.Twitch,
            "kick" => s.Kick,
            "youtube" => s.YouTube,
            _ => false,
        })];

    private static bool Listed(IEnumerable<string> list, string game) => list.Any(g => string.Equals(g.Trim(), game, StringComparison.OrdinalIgnoreCase));

    internal async Task GameStartedAsync(string game)
    {
        game = game.Trim();
        if (game.Length == 0) return;
        var s = Settings;
        lock (_gate)
        {
            _game = game;
            _switched = false;
        }
        if (!s.Enabled || Listed(s.NeverAsk, game)) return;
        if (s.OnlyWhileLive && !_live().Any(l => l.IsLive == true)) return;

        var wanted = s.Overrides.TryGetValue(game, out var o) && o.Trim().Length > 0 ? o.Trim() : game;
        var plan = await PlanAsync(Enabled(s), wanted).ConfigureAwait(false);
        if (plan.Count == 0)
        {
            _log.Info("Games", $"{game}: no category to switch to (no match, or already set)");
            return;
        }
        lock (_gate)
            if (_game != game) return;   // another game started while looking up
        var detail = string.Join(" · ", plan.Select(p => $"{p.Target.Platform}: {p.Category}"));
        if (Listed(s.AlwaysSwitch, game))
        {
            _log.Info("Games", $"{game} is set to always switch: {await SetAsync(game, plan, CancellationToken.None).ConfigureAwait(false)}");
            return;
        }
        _requests.Add(RequestKind, $"Switch the stream category to {game}?", detail, TimeSpan.FromMinutes(Math.Max(1, s.RequestMinutes)),
            async (always, ct) =>
            {
                if (always) UpdateSettings(c => { if (!Listed(c.AlwaysSwitch, game)) c.AlwaysSwitch.Add(game); });
                return await SetAsync(game, plan, ct).ConfigureAwait(false);
            },
            acceptLabel: "Switch", remember: "Always for this game");
    }

    internal async Task GameStoppedAsync(string game)
    {
        bool switched;
        lock (_gate)
        {
            if (!string.Equals(_game, game.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            switched = _switched;
            _game = null;
            _switched = false;
        }
        _requests.Withdraw(RequestKind);
        var s = Settings;
        if (!s.Enabled || !switched || s.ReturnCategory.Trim().Length == 0) return;
        var back = s.ReturnCategory.Trim();
        var plan = await PlanAsync(Enabled(s), back).ConfigureAwait(false);
        if (plan.Count == 0) return;
        _requests.Add(RequestKind, $"{game} closed. Switch the stream category back to {back}?",
            string.Join(" · ", plan.Select(p => $"{p.Target.Platform}: {p.Category}")), TimeSpan.FromMinutes(Math.Max(1, s.RequestMinutes)),
            (_, ct) => SetAsync(back, plan, ct), acceptLabel: "Switch back");
    }

    /// <summary>Each platform's category for the name, leaving out platforms with no match or already on it.</summary>
    private async Task<List<(IStreamCategoryTarget Target, string Category)>> PlanAsync(IReadOnlyList<IStreamCategoryTarget> targets, string name)
    {
        var plan = new List<(IStreamCategoryTarget, string)>();
        foreach (var target in targets)
        {
            try
            {
                var found = await target.FindAsync(name).ConfigureAwait(false);
                if (found is null) continue;
                var current = await target.CurrentAsync().ConfigureAwait(false);
                if (string.Equals(current, found, StringComparison.OrdinalIgnoreCase)) continue;
                plan.Add((target, found));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _log.Warn("Games", $"{target.Platform}: could not look up the category for {name}: {e.Message}");
            }
        }
        return plan;
    }

    private async Task<string> SetAsync(string game, IReadOnlyList<(IStreamCategoryTarget Target, string Category)> plan, CancellationToken ct)
    {
        var done = new List<string>();
        var failed = new List<string>();
        foreach (var (target, category) in plan)
        {
            try
            {
                var set = await target.SetAsync(category, ct).ConfigureAwait(false);
                done.Add($"{target.Platform} ({set})");
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failed.Add($"{target.Platform}: {e.Message}");
            }
        }
        lock (_gate)
            if (string.Equals(_game, game, StringComparison.OrdinalIgnoreCase)) _switched |= done.Count > 0;
        if (done.Count == 0) throw new InvalidOperationException(string.Join("; ", failed));
        var message = "Category switched on " + string.Join(", ", done) + ".";
        if (failed.Count > 0) message += " Not on " + string.Join("; ", failed) + ".";
        _log.Info("Games", message);
        return message;
    }

    public void Dispose() => _subscription.Dispose();
}

/// <summary>Registration: the request queue, the category switcher (games.json) and the game triggers.</summary>
public static class GameFeatures
{
    public static IServiceCollection AddGameFeatures(this IServiceCollection services)
    {
        services.AddSettingsFile<CategorySettings>("games.json");
        services.AddSingleton(sp => new StreamRequests(sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IPeriodicService>(sp => new RequestTicker(sp.GetRequiredService<StreamRequests>()));
        services.AddSingleton(sp => new CategorySwitcher(sp.GetRequiredService<JsonFile<CategorySettings>>(), sp.GetRequiredService<StreamRequests>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>(), () => sp.GetServices<IStreamCategoryTarget>(),
            () => sp.GetServices<IStreamLiveStatus>()));
        services.AddSingleton<ITriggerProvider, GameTriggers>();
        return services;
    }

    private sealed class RequestTicker(StreamRequests requests) : IPeriodicService
    {
        public void Tick(DateTimeOffset now) => requests.Tick();
    }
}

/// <summary>"Game started" / "Game stopped" (Steam) and "Request answered" triggers.</summary>
public sealed class GameTriggers : ITriggerProvider
{
    private static readonly ParameterDescriptor GameFilter =
        new("game", "Game") { Help = "Only this game (empty = any). Case does not matter." };

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType("steam.gameStarted", GameEvents.Category, "Game started (Steam)", GameEvents.Started, [GameFilter], Filter),
        new EventTriggerType("steam.gameStopped", GameEvents.Category, "Game stopped (Steam)", GameEvents.Stopped, [GameFilter], Filter),
        new EventTriggerType("requests.decided", GameEvents.Category, "Request answered", StreamRequests.DecidedEvent),
    ];

    private static bool Filter(BotEvent e, ParameterValues s, IDictionary<string, object?> _) =>
        s["game"].Trim().Length == 0 || string.Equals(e.Args.GetString("game"), s["game"].Trim(), StringComparison.OrdinalIgnoreCase);
}
