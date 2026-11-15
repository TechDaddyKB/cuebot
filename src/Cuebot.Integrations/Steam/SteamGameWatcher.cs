using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Games;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Integrations.Steam;

/// <summary>Steam game detection (part of integrations.json).</summary>
public sealed class SteamSettings
{
    /// <summary>Watch Steam for games starting and stopping (the "Steam game started/stopped" triggers).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Steam apps that are never treated as a game: names (with * as a wildcard) or app ids. Streaming tools such as
    /// VTube Studio and VBridger are Steam apps too, and Steam reports them as running just like a game.
    /// </summary>
    public List<string> Excluded { get; set; } = [.. DefaultExcluded];

    public static readonly string[] DefaultExcluded =
    [
        "VTube Studio", "VBridger", "Wallpaper Engine", "SteamVR", "OBS Studio", "Voicemod", "Soundpad", "Lossless Scaling",
        "Live2D Cubism*", "VSeeFace", "Warudo", "VNyan", "Animaze*", "Streamer.bot", "Spout2*",
        // Steam's own helpers start alongside games.
        "Proton*", "Steam Linux Runtime*", "Steamworks Common Redistributables", "Steamworks Shared",
    ];
}

/// <summary>
/// Polls Steam (every few seconds) for the game being played and publishes <see cref="GameEvents.Started"/> and
/// <see cref="GameEvents.Stopped"/>. The game is the most recently started running app that is not excluded; a game
/// counts as stopped only after it was missing twice in a row, so a slow restart is not a stop and a start.
/// </summary>
public sealed class SteamGameWatcher : IPeriodicService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ISteamSource _steam;
    private readonly JsonFile<IntegrationsSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly object _gate = new();
    private DateTimeOffset _nextPoll;
    private HashSet<long> _seen = [];
    private SteamApp? _current;
    private int _missing;

    public SteamGameWatcher(ISteamSource steam, JsonFile<IntegrationsSettings> settings, EventBus bus, AppLog log)
    {
        _steam = steam;
        _settings = settings;
        _bus = bus;
        _log = log;
    }

    /// <summary>The game being played, or null.</summary>
    public SteamApp? Current
    {
        get { lock (_gate) return _current; }
    }

    void IPeriodicService.Tick(DateTimeOffset now)
    {
        if (now < _nextPoll) return;
        _nextPoll = now + PollInterval;
        Poll();
    }

    /// <summary>Checks Steam once (public for tests).</summary>
    public void Poll()
    {
        var settings = _settings.Value.Steam;
        if (!settings.Enabled)
        {
            Stop("detection turned off");
            return;
        }
        IReadOnlyCollection<long> running;
        try
        {
            running = _steam.RunningAppIds();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }
        var games = running.Select(id => new SteamApp(id, _steam.NameOf(id) ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Where(a => !IsExcluded(settings.Excluded, a)).ToList();

        SteamApp? started = null, stopped = null;
        lock (_gate)
        {
            // A newly running game wins over one that was already running.
            var fresh = games.Where(g => !_seen.Contains(g.AppId)).ToList();
            _seen = [.. games.Select(g => g.AppId)];
            if (fresh.Count > 0 && fresh[^1].AppId != _current?.AppId)
            {
                stopped = _current;
                started = _current = fresh[^1];
                _missing = 0;
            }
            else if (_current is not null && games.All(g => g.AppId != _current.AppId))
            {
                if (++_missing >= 2)
                {
                    stopped = _current;
                    _current = games.LastOrDefault();
                    started = _current;
                    _missing = 0;
                }
            }
            else _missing = 0;
        }
        if (stopped is not null) Publish(GameEvents.Stopped, stopped);
        if (started is not null) Publish(GameEvents.Started, started);
    }

    private void Stop(string why)
    {
        SteamApp? stopped;
        lock (_gate)
        {
            stopped = _current;
            _current = null;
            _seen = [];
            _missing = 0;
        }
        if (stopped is not null)
        {
            _log.Debug("Steam", $"Stopped watching {stopped.Name}: {why}");
            Publish(GameEvents.Stopped, stopped);
        }
    }

    private void Publish(string type, SteamApp app)
    {
        _log.Info("Steam", type == GameEvents.Started ? $"Playing {app.Name}" : $"Stopped playing {app.Name}");
        _bus.Publish(new BotEvent(type, new Dictionary<string, object?>
        {
            ["game"] = app.Name,
            ["steamAppId"] = app.AppId,
            ["source"] = "steam",
        }));
    }

    /// <summary>True when the app matches an excluded name (case-insensitive, * as a wildcard) or app id.</summary>
    public static bool IsExcluded(IEnumerable<string> excluded, SteamApp app)
    {
        foreach (var raw in excluded)
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;
            if (long.TryParse(entry, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
            {
                if (id == app.AppId) return true;
                continue;
            }
            if (Wildcard(entry, app.Name)) return true;
        }
        return false;
    }

    private static bool Wildcard(string pattern, string text)
    {
        if (!pattern.Contains('*')) return string.Equals(pattern, text, StringComparison.OrdinalIgnoreCase);
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(text, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(50));
    }
}
