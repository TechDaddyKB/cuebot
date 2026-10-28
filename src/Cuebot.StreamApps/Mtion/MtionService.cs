using System.Net;
using System.Text;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.StreamApps.Mtion;

/// <summary>
/// mtion studio (now "mtion worlds") through its local developer API (HTTP, localhost:35393). The API has no push
/// channel, so "connected" means mtion studio answered the last poll of <c>/active-clubhouse</c>; the poll also notices a
/// different clubhouse being opened. Sub-actions call the API directly and report <c>mtion.running</c> whether or not
/// the watcher runs.
/// </summary>
public sealed class MtionService : IConnectionStatus, IAsyncDisposable
{
    public const string ConnectedEvent = "mtion.connected";
    public const string DisconnectedEvent = "mtion.disconnected";
    public const string ClubhouseChangedEvent = "mtion.clubhouseChanged";
    public const string TriggerSentEvent = "mtion.triggerSent";

    private readonly JsonFile<StreamAppsSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly HttpClient _http;
    private readonly StreamAppsTimings _timings;
    private readonly TimeSpan? _pollOverride;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private volatile bool _running;
    private volatile MtionClubhouse? _clubhouse;

    public MtionService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, HttpClient http)
        : this(settings, bus, log, http, StreamAppsTimings.Default)
    {
    }

    internal MtionService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, HttpClient http, StreamAppsTimings timings, TimeSpan? poll = null)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _http = http;
        _timings = timings;
        _pollOverride = poll;
    }

    public string Name => "mtion studio";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not watching";
    public event Action? StatusChanged;

    /// <summary>Raised when the open clubhouse (or its trigger list) changed.</summary>
    public event Action? DataChanged;

    public MtionSettings Settings => _settings.Value.Mtion;

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>mtion studio answered the last request.</summary>
    public bool IsConnected => _running;

    public bool IsRunning => _run is not null && !_loop.IsCompleted;

    /// <summary>The clubhouse open at the last poll (null when none or not running).</summary>
    public MtionClubhouse? Clubhouse => _clubhouse;

    public Uri BaseUrl
    {
        get
        {
            var host = Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "127.0.0.1";
            if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]";
            var port = Settings.Port is > 0 and < 65536 ? Settings.Port : 35393;
            return new Uri($"http://{host}:{port}/");
        }
    }

    private TimeSpan Poll => _pollOverride ?? TimeSpan.FromSeconds(Math.Clamp(Settings.PollSeconds, 1, 3600));

    /// <summary>Starts watching for mtion studio in the background.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Connecting, "Looking for mtion studio...");
            var run = new CancellationTokenSource();
            _run = run;
            _loop = Task.Run(() => LoopAsync(run.Token), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Disconnected, "Not watching");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts watching when auto-connect is on. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!Settings.AutoConnect) return;
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Error(Name, $"Automatic start failed: {ex.Message}");
        }
    }

    // ---- API calls ----

    /// <summary>The open clubhouse: (running, clubhouse or null when none is open).</summary>
    public async Task<(bool Running, MtionClubhouse? Clubhouse)> GetActiveClubhouseAsync(CancellationToken ct = default)
    {
        var (running, json) = await GetAsync(MtionApi.ActiveClubhouse, ct).ConfigureAwait(false);
        var clubhouse = json is { ValueKind: JsonValueKind.Object } j ? MtionApi.ParseClubhouse(j) : null;
        Observe(running, clubhouse);
        return (running, clubhouse);
    }

    /// <summary>External triggers of the open clubhouse (empty when none is open).</summary>
    public async Task<(bool Running, IReadOnlyList<MtionTrigger> Triggers)> GetTriggersAsync(CancellationToken ct = default)
    {
        var (running, json) = await GetAsync(MtionApi.Triggers, ct).ConfigureAwait(false);
        return (running, json is { } j ? MtionApi.ParseTriggers(j) : []);
    }

    public async Task<(bool Running, IReadOnlyList<MtionClubhouse> Clubhouses)> GetClubhousesAsync(CancellationToken ct = default)
    {
        var (running, json) = await GetAsync(MtionApi.Clubhouses, ct).ConfigureAwait(false);
        return (running, json is { } j ? MtionApi.ParseClubhouses(j) : []);
    }

    /// <summary>Fires an external trigger (by id or name) with parameter values keyed by parameter name or index.</summary>
    /// <exception cref="ArgumentException">A parameter does not exist or has the wrong type.</exception>
    public async Task<MtionFireResult> FireTriggerAsync(string idOrName, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        var key = idOrName.Trim().Length > 0 ? idOrName.Trim() : throw new ArgumentException("Trigger is required.");
        var (running, triggers) = await GetTriggersAsync(ct).ConfigureAwait(false);
        if (!running) return new MtionFireResult(false, false, false, null);
        var trigger = MtionApi.Find(triggers, key);
        if (trigger is null)
        {
            _log.Warn(Name, $"No external trigger '{key}' in the open clubhouse.");
            return new MtionFireResult(true, false, false, null);
        }
        var body = MtionApi.FireBody(trigger, parameters);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timings.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri(BaseUrl, MtionApi.FireTrigger(trigger.Id)))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var sent = response.IsSuccessStatusCode;
            if (!sent) _log.Warn(Name, $"mtion did not fire '{trigger.Name}' (HTTP {(int)response.StatusCode}).");
            else
                Publish(TriggerSentEvent, new()
                {
                    ["mtion.trigger.id"] = trigger.Id, ["mtion.trigger.name"] = trigger.Name, ["mtion.trigger.parameters"] = body,
                });
            return new MtionFireResult(true, true, sent, trigger);
        }
        catch (Exception ex) when (IsUnreachable(ex, ct))
        {
            Observe(false, null);
            return new MtionFireResult(false, true, false, trigger);
        }
    }

    /// <summary>GET a path: (false, null) when mtion is not running, (true, null) on 404, else the JSON.</summary>
    private async Task<(bool Running, JsonElement? Json)> GetAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timings.ConnectTimeout);
        try
        {
            using var response = await _http.GetAsync(new Uri(BaseUrl, path), timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return (true, null);
            if (!response.IsSuccessStatusCode)
            {
                _log.Warn(Name, $"GET /{path} answered HTTP {(int)response.StatusCode}.");
                return (true, null);
            }
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return (true, Common.JsonUtil.TryParse(text));
        }
        catch (Exception ex) when (IsUnreachable(ex, ct))
        {
            Observe(false, null);
            return (false, null);
        }
    }

    private static bool IsUnreachable(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    // ---- Watching ----

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await GetActiveClubhouseAsync(ct).ConfigureAwait(false);
                await Task.Delay(Poll, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            _log.Error(Name, $"mtion watcher stopped: {ex.Message}");
            SetStatus(ConnectionState.Error, ex.Message);
        }
    }

    /// <summary>Updates running/clubhouse state from any API answer and raises events on changes.</summary>
    private void Observe(bool running, MtionClubhouse? clubhouse)
    {
        var wasRunning = _running;
        var old = _clubhouse;
        _running = running;
        _clubhouse = running ? clubhouse : null;
        if (running && !wasRunning)
        {
            _log.Info(Name, $"mtion studio found at {BaseUrl}");
            Publish(ConnectedEvent, []);
        }
        else if (!running && wasRunning)
        {
            _log.Info(Name, "mtion studio is no longer reachable");
            Publish(DisconnectedEvent, new() { ["reason"] = "mtion studio is not running" });
        }
        if (running && (old?.Id ?? "") != (clubhouse?.Id ?? "") && (wasRunning || clubhouse is not null))
        {
            var args = ClubhouseArgs(clubhouse);
            args["mtion.oldClubhouse.id"] = old?.Id ?? "";
            args["mtion.oldClubhouse.name"] = old?.Name ?? "";
            Publish(ClubhouseChangedEvent, args);
        }
        if (_run is not null)
        {
            if (running)
                SetStatus(ConnectionState.Connected, clubhouse is null ? "mtion studio is running (no clubhouse open)" : $"mtion studio: {clubhouse.Name}");
            else
                SetStatus(ConnectionState.Connecting, $"mtion studio is not running ({BaseUrl}); checking every {Poll.TotalSeconds:0} s");
        }
        if (wasRunning != running || !SameTriggers(old, _clubhouse)) DataChanged?.Invoke();
    }

    private static bool SameTriggers(MtionClubhouse? a, MtionClubhouse? b) =>
        (a?.Id ?? "") == (b?.Id ?? "") && (a?.Triggers.Select(t => t.Id + t.Name) ?? []).SequenceEqual(b?.Triggers.Select(t => t.Id + t.Name) ?? []);

    /// <summary>Streamer.bot's "Get Active Clubhouse" variables.</summary>
    public static Dictionary<string, object?> ClubhouseArgs(MtionClubhouse? c) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["mtion.clubhouse.found"] = c is not null,
        ["mtion.clubhouse.id"] = c?.Id ?? "",
        ["mtion.clubhouse.name"] = c?.Name ?? "",
        ["mtion.clubhouse.description"] = c?.Description ?? "",
    };

    private void Publish(string type, Dictionary<string, object?> args)
    {
        args.TryAdd("service", Name);
        _bus.Publish(new BotEvent(type, new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)));
    }

    private void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    private async Task StopAsync()
    {
        var run = _run;
        if (run is null) return;
        _run = null;
        await run.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(Name, $"Error while stopping: {ex.Message}");
        }
        run.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
