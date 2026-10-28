using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Lumia;

/// <summary>Bot event types published by <see cref="LumiaService"/>.</summary>
public static class LumiaEvents
{
    public const string Prefix = "lumia";
    public const string Connected = "lumia.connected";
    public const string Disconnected = "lumia.disconnected";
    public const string ChatMessage = "lumia.chatMessage";
    public const string ChatMessageDeleted = "lumia.chatMessageDeleted";
    public const string Command = "lumia.command";
    public const string Alert = "lumia.alert";
    public const string StatesChanged = "lumia.statesChanged";

    /// <summary>Any other pushed message (premium, modqueue_list, chat_platform_register, ...); <c>lumia.type</c> names it.</summary>
    public const string Event = "lumia.event";
}

/// <summary>
/// Lumia Stream through its Developer API: the websocket (ws://host:39231/api?token=...) is the connection and delivers
/// events; commands go over REST (<c>POST /api/send</c>), which answers on every Lumia version. The token is only ever
/// put into request URLs and is never logged or shown.
/// </summary>
public sealed class LumiaService : StreamAppConnection
{
    private readonly JsonFile<StreamAppsSettings> _settings;
    private readonly HttpClient _http;
    private volatile LumiaInfo _info = LumiaInfo.Empty;

    public LumiaService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, HttpClient http)
        : this(settings, bus, log, http, () => new ClientWebSocketTransport(), StreamAppsTimings.Default)
    {
    }

    internal LumiaService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, HttpClient http,
        Func<IWebSocketTransport> transports, StreamAppsTimings timings)
        : base("Lumia Stream", bus, log, transports, timings)
    {
        _settings = settings;
        _http = http;
    }

    public LumiaSettings Settings => _settings.Value.Lumia;

    public void SaveSettings() => _settings.ScheduleSave();

    public string Host => Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "127.0.0.1";

    public int Port => Settings.Port is > 0 and < 65536 ? Settings.Port : 39231;

    public bool HasToken => Settings.Token.Trim().Length > 0;

    /// <summary>Commands, alerts, lights and voices read from Lumia on connect (and by <see cref="RefreshAsync"/>).</summary>
    public LumiaInfo Info => _info;

    /// <summary>Last core states Lumia pushed (null until the first <c>states</c> event).</summary>
    public (bool On, bool StreamMode, bool Fuze)? States { get; private set; }

    /// <summary>Raised (on a background thread) when <see cref="Info"/> or <see cref="States"/> changed.</summary>
    public event Action? DataChanged;

    protected override bool AutoConnect => Settings.AutoConnect && HasToken;
    protected override bool AutoReconnect => Settings.AutoReconnect;

    private string Token => Settings.Token.Trim();

    private string HostPart => Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host;

    private protected override (Uri Url, string Display) Endpoint()
    {
        if (!HasToken) throw new StreamAppRequestException("no token: copy it from Lumia's Settings → API");
        return (new Uri($"ws://{HostPart}:{Port}/api?token={Uri.EscapeDataString(Token)}&name=Cuebot"), $"{Host}:{Port}");
    }

    private protected override Dictionary<string, object?> ConnectionArgs() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["lumia.host"] = Host,
        ["lumia.port"] = Port,
    };

    private protected override void Publish(string name, Dictionary<string, object?> args)
    {
        foreach (var (key, value) in ConnectionArgs()) args.TryAdd(key, value);
        args.TryAdd("service", Name);
        Bus.Publish(new BotEvent($"{LumiaEvents.Prefix}.{name}", new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)));
    }

    // ---- Session ----

    private protected override async Task RunSessionAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        // Lumia rejects a bad token or a disabled API at the upgrade, so an open socket is a working connection.
        MarkConnected($"Connected to Lumia Stream at {Host}:{Port}");
        var refresh = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is StreamAppRequestException or StreamAppNotConnectedException or TimeoutException or OperationCanceledException)
            {
                if (!ct.IsCancellationRequested) Log.Warn(Name, $"Could not read Lumia's commands and lights: {ex.Message}");
            }
        }, CancellationToken.None);
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text)
                if (JsonUtil.TryParse(text) is { ValueKind: JsonValueKind.Object } message)
                    OnMessage(message);
        }
        finally
        {
            await refresh.ConfigureAwait(false);
        }
    }

    private protected override void OnSessionEnded()
    {
        _info = LumiaInfo.Empty;
        States = null;
        DataChanged?.Invoke();
    }

    private void OnMessage(JsonElement message)
    {
        try
        {
            if (LumiaProtocol.Event(message) is not var (type, args)) return;
            if (type == LumiaEvents.StatesChanged)
            {
                States = ((bool)args["lumia.on"]!, (bool)args["lumia.streamMode"]!, (bool)args["lumia.fuze"]!);
                DataChanged?.Invoke();
            }
            Publish(type[(LumiaEvents.Prefix.Length + 1)..], args);
        }
        catch (Exception ex)
        {
            Log.Error(Name, "Could not process a Lumia Stream event", ex);
        }
    }

    // ---- REST ----

    private Uri ApiUrl(string path, string query = "") =>
        new($"http://{HostPart}:{Port}/api/{path}?token={Uri.EscapeDataString(Token)}{query}");

    /// <summary>Runs one Send API command (<c>{"type":..., "params":...}</c>); returns Lumia's <c>message</c> value.</summary>
    public async Task<object?> SendApiAsync(string type, object? parameters = null, CancellationToken ct = default)
    {
        var body = LumiaProtocol.Send(Steps.Require(type, "Type"), parameters);
        var response = await HttpAsync(HttpMethod.Post, ApiUrl("send"), body, ct).ConfigureAwait(false);
        return response.ValueKind == JsonValueKind.Object && response.TryGetProperty("message", out var m) ? Cuebot.Core.Scripting.ScriptArgs.ToClr(m) : null;
    }

    /// <summary>Re-reads commands, alerts, lights and voices (<c>GET /api/retrieve?slim=true</c>).</summary>
    public async Task<LumiaInfo> RefreshAsync(CancellationToken ct = default)
    {
        var response = await HttpAsync(HttpMethod.Get, ApiUrl("retrieve", "&slim=true"), null, ct).ConfigureAwait(false);
        _info = LumiaProtocol.ParseInfo(response.ValueKind == JsonValueKind.Object && response.TryGetProperty("data", out var d) ? d : default);
        if (IsConnected)
            SetStatus(ConnectionState.Connected,
                $"Connected to Lumia Stream at {Host}:{Port} ({_info.Values("chat-command").Count} commands, {_info.Lights.Count} lights)");
        DataChanged?.Invoke();
        return _info;
    }

    private async Task<JsonElement> HttpAsync(HttpMethod method, Uri url, string? body, CancellationToken ct)
    {
        EnsureConnected();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timings.RequestTimeout);
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        try
        {
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new StreamAppRequestException($"Lumia Stream refused the request: {Scrub(LumiaProtocol.ErrorText(text, (int)response.StatusCode))}");
            return JsonUtil.TryParse(text) ?? default;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Lumia Stream did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new StreamAppRequestException($"Lumia Stream did not answer: {Scrub(ex.Message)}");
        }
    }

    /// <summary>Keeps the token out of anything shown or logged.</summary>
    private string Scrub(string text) =>
        Token.Length == 0 ? text : text.Replace(Token, "***", StringComparison.Ordinal).Replace(Uri.EscapeDataString(Token), "***", StringComparison.Ordinal);

    // ---- Commands ----

    public Task SendCommandAsync(string command, string type = "chat-command", bool hold = false,
        IReadOnlyDictionary<string, string>? extraSettings = null, CancellationToken ct = default) =>
        SendApiAsync(type.Trim().Length > 0 ? type.Trim() : "chat-command",
            LumiaProtocol.CommandParams(Steps.Require(command, "Command"), hold, extraSettings), ct);

    /// <summary>Sets a colour (#RRGGBB or r,g,b) on all lights, or on <paramref name="lights"/> (names, ids or brand:id).</summary>
    public Task SetColorAsync(string color, double? brightness = null, double? transition = null, double? duration = null,
        string lights = "", bool hold = false, CancellationToken ct = default)
    {
        var (type, value) = LumiaProtocol.Color(Steps.Require(color, "Colour"));
        var selected = lights.Trim().Length > 0 ? LumiaProtocol.ResolveLights(lights, _info.Lights) : null;
        return SendApiAsync(type, LumiaProtocol.ColorParams(value, brightness, transition, duration, hold, selected), ct);
    }

    public Task SetToDefaultAsync(CancellationToken ct = default) => SendApiAsync("to-default", null, ct);

    public Task SendAlertAsync(string alert, IReadOnlyDictionary<string, string>? extraSettings = null, CancellationToken ct = default) =>
        SendApiAsync("alert", LumiaProtocol.CommandParams(Steps.Require(alert, "Alert"), false, extraSettings), ct);

    public Task SendChatbotMessageAsync(string message, string platform = "twitch", bool asSelf = false, CancellationToken ct = default)
    {
        var p = new Dictionary<string, object?> { ["value"] = Steps.Require(message, "Message"), ["platform"] = platform.Trim().Length > 0 ? platform.Trim().ToLowerInvariant() : "twitch" };
        if (asSelf) p["userToChatAs"] = "self";
        return SendApiAsync("chatbot-message", p, ct);
    }

    public Task SpeakAsync(string message, string voice = "", double? volume = null, CancellationToken ct = default)
    {
        var p = new Dictionary<string, object?> { ["value"] = Steps.Require(message, "Message") };
        if (voice.Trim().Length > 0) p["voice"] = voice.Trim();
        if (volume is { } v) p["volume"] = Math.Clamp(v, 0, 100);
        return SendApiAsync("tts", p, ct);
    }

    /// <summary>Lumia light control on, off or toggled.</summary>
    public Task SetLumiaStateAsync(SwitchMode mode, CancellationToken ct = default) => mode switch
    {
        SwitchMode.On => SendApiAsync("start-lumia", new { value = true }, ct),
        SwitchMode.Off => SendApiAsync("stop-lumia", new { value = false }, ct),
        _ => SendApiAsync("set-lumia", new Dictionary<string, object?> { ["value"] = null }, ct),
    };

    public Task SetStreamModeAsync(SwitchMode mode, CancellationToken ct = default) =>
        SendApiAsync("toggle-stream-mode", new Dictionary<string, object?> { ["value"] = mode switch { SwitchMode.On => true, SwitchMode.Off => false, _ => null } }, ct);

    public async Task<string> GetVariableAsync(string name, CancellationToken ct = default) =>
        Convert.ToString(await SendApiAsync("get-variable-value", new { name = Steps.Require(name, "Variable") }, ct).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) ?? "";

    public Task SetVariableAsync(string name, string value, CancellationToken ct = default) =>
        SendApiAsync("update-variable-value", new { name = Steps.Require(name, "Variable"), value }, ct);
}
