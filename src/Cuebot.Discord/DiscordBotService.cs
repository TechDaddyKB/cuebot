using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Discord.Gateway;

namespace Cuebot.Discord;

/// <summary>
/// The streamer's own Discord bot: a Gateway connection (hello, heartbeat with jitter, identify, resume, reconnect on op 7/9
/// and close codes) that keeps a list of the bot's servers, channels and roles, publishes slash command, message and
/// member-joined events, and answers slash commands (deferred at once, then edited by the reply step or a default text
/// once the actions finished). REST calls go through <see cref="Api"/>.
/// </summary>
public sealed class DiscordBotService : IConnectable, IAsyncDisposable
{
    public const string ServiceName = "Discord bot";

    private readonly JsonFile<DiscordBotSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly Func<IGatewaySocket> _socketFactory;
    private readonly Func<ActionEngine?> _engine;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, DiscordGuild> _guilds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingInteraction> _interactions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, PendingInteraction> _runs = new();
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private volatile IGatewaySocket? _socket;
    private volatile bool _sessionConnected;
    private volatile bool _acked;
    private int _engineHooked;

    // Session state kept across connections so a dropped connection can resume.
    private string? _sessionId;
    private string? _resumeUrl;
    private int? _sequence;
    private string? _gatewayUrl;
    private JsonObject? _presence;

    internal DiscordBotService(JsonFile<DiscordBotSettings> settings, EventBus bus, AppLog log, HttpClient http,
        Func<IGatewaySocket>? socketFactory = null, DiscordTimings? timings = null, Uri? restBase = null, Func<ActionEngine?>? engine = null)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _socketFactory = socketFactory ?? (() => new ClientGatewaySocket());
        Timings = timings ?? DiscordTimings.Default;
        _engine = engine ?? (() => null);
        Api = new DiscordApi(new DiscordRestClient(http, () => Settings.BotToken, restBase, Timings.MaxRateLimitWait));
    }

    public string Name => ServiceName;
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    /// <summary>Raised (on a background thread) when the server, channel or role lists change.</summary>
    public event Action? DataChanged;

    public DiscordApi Api { get; }
    public DiscordTimings Timings { get; }
    public DiscordBotSettings Settings => _settings.Value;
    public bool IsConnected => State == ConnectionState.Connected;
    public bool IsRunning => _run is not null && !_loop.IsCompleted;

    /// <summary>The bot account, once connected.</summary>
    public DiscordUser? BotUser { get; private set; }

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>The servers the bot is in, by name.</summary>
    public IReadOnlyList<DiscordGuild> Guilds
    {
        get
        {
            lock (_cacheLock) return _guilds.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
    }

    public DiscordGuild? FindGuild(string id)
    {
        lock (_cacheLock) return _guilds.GetValueOrDefault(id);
    }

    private string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var token = Settings.BotToken.Trim();
        return token.Length >= 8 ? text.Replace(token, "***", StringComparison.Ordinal) : text;
    }

    private void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = Redact(message);
        StatusChanged?.Invoke();
    }

    // ---- Connection lifecycle -------------------------------------------------------------------------------------

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (Settings.BotToken.Trim().Length == 0)
        {
            const string missing = "Paste your bot token first (Developer Portal → your app → Bot → Reset Token).";
            SetStatus(ConnectionState.Error, missing);
            throw new InvalidOperationException(missing);
        }
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopAsync(1000).ConfigureAwait(false);
            SetStatus(ConnectionState.Connecting, "Connecting to Discord...");
            var run = new CancellationTokenSource();
            _run = run;
            _loop = Task.Run(() => LoopAsync(run.Token), CancellationToken.None);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync(1000).ConfigureAwait(false);
            SetStatus(ConnectionState.Disconnected, "Disconnected");
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Connects when auto-connect is on and a token is saved. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!Settings.AutoConnect || Settings.BotToken.Trim().Length == 0) return;
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Error(Name, $"Automatic connection failed: {Redact(ex.Message)}");
        }
    }

    private async Task StopAsync(int closeCode)
    {
        var run = _run;
        if (run is null) return;
        _run = null;
        if (_socket is { } socket)
        {
            try { await socket.CloseAsync(closeCode, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.Warn(Name, $"Closing the connection failed: {Redact(ex.Message)}"); }
        }
        await run.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(Name, $"Error while disconnecting: {Redact(ex.Message)}");
        }
        run.Dispose();
        // A closed (1000) session cannot be resumed.
        _sessionId = null;
        _resumeUrl = null;
        _sequence = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var delay = Timings.ReconnectDelay;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var end = await RunOneConnectionAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                if (end.Fatal)
                {
                    _log.Warn(Name, Redact(end.Message));
                    SetStatus(ConnectionState.Error, end.Message);
                    return;
                }
                if (end.HadConnected) delay = Timings.ReconnectDelay;
                var wait = end.Delay ?? delay;
                if (end.HadConnected) _log.Warn(Name, $"Connection lost: {Redact(end.Message)}");
                SetStatus(ConnectionState.Connecting, wait > TimeSpan.Zero
                    ? $"Reconnecting in {Seconds(wait)} ({end.Message})"
                    : $"Reconnecting ({end.Message})");
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
                if (end.Delay is null) delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, Timings.MaxReconnectDelay.Ticks));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            _log.Error(Name, $"Connection loop stopped: {Redact(ex.Message)}");
            SetStatus(ConnectionState.Error, ex.Message);
        }
    }

    private static string Seconds(TimeSpan delay) => delay.TotalSeconds >= 1 ? $"{delay.TotalSeconds:0} s" : $"{delay.TotalMilliseconds:0} ms";

    private sealed record SessionEnd(string Message, bool Fatal = false, bool HadConnected = false, TimeSpan? Delay = null);

    private async Task<Uri> GatewayAddressAsync(CancellationToken ct)
    {
        if (_sessionId is not null && _resumeUrl is { Length: > 0 } resume) return GatewayProtocol.WithQuery(resume);
        if (_gatewayUrl is null)
        {
            try
            {
                var url = await Api.GetGatewayUrlAsync(ct).ConfigureAwait(false);
                _gatewayUrl = url.Length > 0 ? url : null;
            }
            catch (DiscordApiException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new FatalGatewayException(GatewayProtocol.DescribeClose(4004, GatewayIntents.None).Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or DiscordApiException or JsonException)
            {
                _log.Warn(Name, $"Could not ask Discord for the Gateway address ({Redact(ex.Message)}); using the default one");
            }
        }
        return _gatewayUrl is { } cached ? GatewayProtocol.WithQuery(cached) : GatewayProtocol.DefaultUrl;
    }

    private sealed class FatalGatewayException(string message) : Exception(message);

    private async Task<SessionEnd> RunOneConnectionAsync(CancellationToken ct)
    {
        Uri url;
        try
        {
            url = await GatewayAddressAsync(ct).ConfigureAwait(false);
        }
        catch (FatalGatewayException ex)
        {
            return new SessionEnd(ex.Message, Fatal: true);
        }
        var socket = _socketFactory();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timings.ConnectTimeout);
            await socket.ConnectAsync(url, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await socket.DisposeAsync().ConfigureAwait(false);
            // The resume address may be gone; the next attempt starts fresh from /gateway/bot.
            _gatewayUrl = null;
            return new SessionEnd($"Discord not reachable ({(ex is OperationCanceledException ? "timed out" : Redact(ex.Message))})");
        }

        _socket = socket;
        SessionEnd end;
        try
        {
            end = await RunSessionAsync(socket, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            end = new SessionEnd("disconnected");
        }
        catch (Exception ex)
        {
            end = new SessionEnd(Redact(socket.CloseReason ?? ex.Message));
        }
        finally
        {
            _socket = null;
            await socket.DisposeAsync().ConfigureAwait(false);
        }

        var hadConnected = _sessionConnected;
        if (hadConnected)
        {
            _sessionConnected = false;
            Publish(DiscordEventTypes.Disconnected, new Dictionary<string, object?> { ["reason"] = end.Message });
        }
        return end with { HadConnected = hadConnected };
    }

    /// <summary>One Gateway connection: hello → heartbeat + identify/resume → dispatches, until it closes.</summary>
    private async Task<SessionEnd> RunSessionAsync(IGatewaySocket socket, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task heartbeat = Task.CompletedTask;
        var intents = GatewayProtocol.Intents(Settings);
        try
        {
            while (true)
            {
                var text = await socket.ReceiveAsync(session.Token).ConfigureAwait(false);
                if (text is null)
                {
                    var close = GatewayProtocol.DescribeClose(socket.CloseCode, intents);
                    if (!close.Resumable) ForgetSession();
                    if (close.Fatal) return new SessionEnd(close.Message, Fatal: true);
                    return new SessionEnd(socket.CloseCode is null ? Redact(socket.CloseReason ?? close.Message) : close.Message);
                }
                GatewayMessage message;
                try
                {
                    message = GatewayMessage.Parse(text);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    _log.Warn(Name, $"Ignored a Gateway message Cuebot could not read: {ex.Message}");
                    continue;
                }
                if (message.Sequence is { } s) _sequence = s;
                switch (message.Op)
                {
                    case GatewayOp.Hello:
                        var interval = TimeSpan.FromMilliseconds(message.Data.GetProperty("heartbeat_interval").GetDouble());
                        _acked = true;
                        heartbeat = Task.Run(() => HeartbeatAsync(socket, interval, session.Token), CancellationToken.None);
                        var token = Settings.BotToken.Trim();
                        await socket.SendAsync(_sessionId is { } id
                            ? GatewayProtocol.Resume(token, id, _sequence)
                            : GatewayProtocol.Identify(token, intents, _presence), session.Token).ConfigureAwait(false);
                        SetStatus(ConnectionState.Connecting, _sessionId is null ? "Signing in to Discord..." : "Resuming the Discord session...");
                        break;
                    case GatewayOp.HeartbeatAck:
                        _acked = true;
                        break;
                    case GatewayOp.Heartbeat:
                        await socket.SendAsync(GatewayProtocol.Heartbeat(_sequence), session.Token).ConfigureAwait(false);
                        break;
                    case GatewayOp.Reconnect:
                        await socket.CloseAsync(4000, CancellationToken.None).ConfigureAwait(false);
                        return new SessionEnd("Discord asked Cuebot to reconnect", Delay: TimeSpan.Zero);
                    case GatewayOp.InvalidSession:
                        var resumable = message.Data.ValueKind == JsonValueKind.True;
                        if (!resumable) ForgetSession();
                        await socket.CloseAsync(4000, CancellationToken.None).ConfigureAwait(false);
                        return new SessionEnd(resumable ? "Discord asked Cuebot to resume" : "Discord ended the session; signing in again",
                            Delay: resumable ? TimeSpan.Zero : RandomBetween(Timings.InvalidSessionMinDelay, Timings.InvalidSessionMaxDelay));
                    case GatewayOp.Dispatch when message.EventName is { } name:
                        try
                        {
                            OnDispatch(name, message.Data);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _log.Error(Name, $"Handling {name} failed: {Redact(ex.Message)}");
                        }
                        break;
                }
            }
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            try { await heartbeat.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private static TimeSpan RandomBetween(TimeSpan min, TimeSpan max) =>
        min + TimeSpan.FromTicks((long)(Random.Shared.NextDouble() * Math.Max(0, (max - min).Ticks)));

    private void ForgetSession()
    {
        _sessionId = null;
        _resumeUrl = null;
        _sequence = null;
    }

    private async Task HeartbeatAsync(IGatewaySocket socket, TimeSpan interval, CancellationToken ct)
    {
        try
        {
            // Docs: the first heartbeat waits interval * jitter (a random number between 0 and 1).
            await Task.Delay(TimeSpan.FromTicks((long)(interval.Ticks * Random.Shared.NextDouble())), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                if (!_acked)
                {
                    // No ACK since the last heartbeat: a "zombied" connection. Close without 1000 so it can resume.
                    _log.Warn(Name, "Discord stopped answering heartbeats; reconnecting");
                    await socket.CloseAsync(4000, CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                _acked = false;
                await socket.SendAsync(GatewayProtocol.Heartbeat(_sequence), ct).ConfigureAwait(false);
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Warn(Name, $"Heartbeat failed: {Redact(ex.Message)}");
        }
    }

    private void MarkConnected(string message)
    {
        SetStatus(ConnectionState.Connected, message);
        if (_sessionConnected) return;
        _sessionConnected = true;
        _log.Info(Name, message);
        Publish(DiscordEventTypes.Connected, new Dictionary<string, object?>
        {
            ["botName"] = BotUser?.UserName ?? Settings.BotName,
            ["botId"] = BotUser?.Id ?? "",
        });
    }

    private void Publish(string type, Dictionary<string, object?> args)
    {
        args.TryAdd("service", Name);
        _bus.Publish(new BotEvent(type, args));
    }

    // ---- Dispatches -----------------------------------------------------------------------------------------------

    private void OnDispatch(string name, JsonElement d)
    {
        switch (name)
        {
            case "READY":
                _sessionId = DiscordPayloads.Str(d, "session_id");
                _resumeUrl = DiscordPayloads.Str(d, "resume_gateway_url");
                if (d.TryGetProperty("user", out var user)) BotUser = DiscordPayloads.User(user);
                var appId = d.TryGetProperty("application", out var app) ? DiscordPayloads.Str(app, "id") : "";
                var changed = false;
                if (appId.Length > 0 && appId != Settings.ApplicationId) { Settings.ApplicationId = appId; changed = true; }
                if (BotUser is { } bot && bot.UserName != Settings.BotName) { Settings.BotName = bot.UserName; changed = true; }
                if (changed) SaveSettings();
                lock (_cacheLock) _guilds.Clear();
                DataChanged?.Invoke();
                MarkConnected($"Connected as {BotUser?.UserName ?? "your bot"}");
                break;
            case "RESUMED":
                MarkConnected($"Connected as {BotUser?.UserName ?? Settings.BotName} (resumed)");
                break;
            case "GUILD_CREATE":
            case "GUILD_UPDATE":
                if (d.TryGetProperty("unavailable", out var u) && u.ValueKind == JsonValueKind.True) break;
                UpdateGuild(DiscordPayloads.Str(d, "id"), existing =>
                {
                    var parsed = DiscordPayloads.Guild(d);
                    // GUILD_UPDATE carries no channel list; keep the known one.
                    return existing is not null && !d.TryGetProperty("channels", out _) ? parsed with { Channels = existing.Channels } : parsed;
                });
                break;
            case "GUILD_DELETE":
                lock (_cacheLock) _guilds.Remove(DiscordPayloads.Str(d, "id"));
                DataChanged?.Invoke();
                break;
            case "CHANNEL_CREATE":
            case "CHANNEL_UPDATE":
            case "CHANNEL_DELETE":
                var guildId = DiscordPayloads.Str(d, "guild_id");
                if (guildId.Length == 0) break;
                var channel = DiscordPayloads.Channel(d, guildId);
                UpdateGuild(guildId, g => g is null ? null : g with
                {
                    Channels = [.. g.Channels.Where(c => c.Id != channel.Id), .. name == "CHANNEL_DELETE" ? Array.Empty<DiscordChannel>() : [channel]],
                });
                break;
            case "GUILD_ROLE_CREATE":
            case "GUILD_ROLE_UPDATE":
            case "GUILD_ROLE_DELETE":
                var roleGuild = DiscordPayloads.Str(d, "guild_id");
                var roleId = d.TryGetProperty("role", out var roleJson) ? DiscordPayloads.Str(roleJson, "id") : DiscordPayloads.Str(d, "role_id");
                UpdateGuild(roleGuild, g => g is null ? null : g with
                {
                    Roles = [.. g.Roles.Where(r => r.Id != roleId), .. name == "GUILD_ROLE_DELETE" ? Array.Empty<DiscordRole>() : [DiscordPayloads.Role(roleJson, roleGuild)]],
                });
                break;
            case "MESSAGE_CREATE":
                OnMessage(d);
                break;
            case "GUILD_MEMBER_ADD":
                OnMemberAdd(d);
                break;
            case "INTERACTION_CREATE":
                OnInteraction(d);
                break;
        }
    }

    private void UpdateGuild(string id, Func<DiscordGuild?, DiscordGuild?> update)
    {
        if (id.Length == 0) return;
        lock (_cacheLock)
        {
            var next = update(_guilds.GetValueOrDefault(id));
            if (next is null) return;
            _guilds[id] = next;
        }
        DataChanged?.Invoke();
    }

    private (string Name, string ChannelName) Names(string guildId, string channelId)
    {
        var guild = FindGuild(guildId);
        return (guild?.Name ?? "", guild?.Channels.FirstOrDefault(c => c.Id == channelId)?.Name ?? "");
    }

    private static void AddUser(Dictionary<string, object?> args, DiscordUser user, string nick)
    {
        args["user"] = nick.Length > 0 ? nick : user.DisplayName;
        args["userName"] = user.UserName;
        args["userId"] = user.Id;
        args["displayName"] = user.DisplayName;
        args["isBot"] = user.IsBot;
    }

    private void OnMessage(JsonElement d)
    {
        if (!d.TryGetProperty("author", out var authorJson)) return;
        var author = DiscordPayloads.User(authorJson);
        // Never react to bots (including this one): a bot answering itself would loop.
        if (author.IsBot || author.Id == BotUser?.Id) return;
        var guildId = DiscordPayloads.Str(d, "guild_id");
        var channelId = DiscordPayloads.Str(d, "channel_id");
        var (serverName, channelName) = Names(guildId, channelId);
        var nick = d.TryGetProperty("member", out var member) ? DiscordPayloads.Str(member, "nick") : "";
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["message"] = DiscordPayloads.Str(d, "content"),
            ["messageId"] = DiscordPayloads.Str(d, "id"),
            ["discordMessageId"] = DiscordPayloads.Str(d, "id"),
            ["channelId"] = channelId,
            ["channelName"] = channelName,
            ["serverId"] = guildId,
            ["serverName"] = serverName,
            ["messageLink"] = MessageLink(guildId, channelId, DiscordPayloads.Str(d, "id")),
        };
        AddUser(args, author, nick);
        Publish(DiscordEventTypes.MessagePosted, args);
    }

    private void OnMemberAdd(JsonElement d)
    {
        if (!d.TryGetProperty("user", out var userJson)) return;
        var user = DiscordPayloads.User(userJson);
        var guildId = DiscordPayloads.Str(d, "guild_id");
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["serverId"] = guildId,
            ["serverName"] = FindGuild(guildId)?.Name ?? "",
            ["mention"] = $"<@{user.Id}>",
        };
        AddUser(args, user, DiscordPayloads.Str(d, "nick"));
        Publish(DiscordEventTypes.MemberJoined, args);
    }

    /// <summary>Names of the roles a bot can give in a server, highest first (not @everyone or integration roles).</summary>
    public static IEnumerable<string> AssignableRoleNames(DiscordGuild guild) => DiscordTargets.AssignableRoles(guild).Select(r => r.Name);

    public static string MessageLink(string guildId, string channelId, string messageId) =>
        $"https://discord.com/channels/{(guildId.Length > 0 ? guildId : "@me")}/{channelId}/{messageId}";

    // ---- Slash commands -------------------------------------------------------------------------------------------

    /// <summary>A slash command use waiting for (or already given) its answer.</summary>
    internal sealed class PendingInteraction(string id, string token, string applicationId, bool ephemeral, DateTimeOffset expires)
    {
        public string Id { get; } = id;
        public string Token { get; } = token;
        public string ApplicationId { get; } = applicationId;
        public bool Ephemeral { get; } = ephemeral;
        public DateTimeOffset Expires { get; } = expires;
        public Task Deferred { get; set; } = Task.CompletedTask;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Answered { get; set; }
        public int Started;
        public int Finished;
    }

    private void OnInteraction(JsonElement d)
    {
        // Type 2: a slash command. (Buttons, menus and autocomplete are not used.)
        if (DiscordPayloads.Int(d, "type") != 2 || !d.TryGetProperty("data", out var data)) return;
        var id = DiscordPayloads.Str(d, "id");
        var token = DiscordPayloads.Str(d, "token");
        var appId = DiscordPayloads.Str(d, "application_id");
        if (appId.Length == 0) appId = Settings.ApplicationId;
        var command = DiscordPayloads.Str(data, "name");
        var definition = Settings.Commands.FirstOrDefault(c => string.Equals(c.Name.Trim().TrimStart('/'), command, StringComparison.OrdinalIgnoreCase));
        var pending = new PendingInteraction(id, token, appId, definition?.Private == true, DateTimeOffset.UtcNow + Timings.InteractionLifetime);
        PruneInteractions();
        _interactions[id] = pending;
        HookEngine();
        // Acknowledge at once: Discord drops the interaction if it gets no answer within 3 seconds.
        pending.Deferred = DeferAsync(pending);

        var userJson = d.TryGetProperty("member", out var member) && member.TryGetProperty("user", out var mu) ? mu
            : d.TryGetProperty("user", out var du) ? du : default;
        var user = userJson.ValueKind == JsonValueKind.Object ? DiscordPayloads.User(userJson) : new DiscordUser("", "", "", false);
        var guildId = DiscordPayloads.Str(d, "guild_id");
        var channelId = DiscordPayloads.Str(d, "channel_id");
        if (channelId.Length == 0 && d.TryGetProperty("channel", out var ch)) channelId = DiscordPayloads.Str(ch, "id");
        var (serverName, channelName) = Names(guildId, channelId);
        var (optionName, optionValue) = FirstOption(data);
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["command"] = command,
            ["option"] = optionValue,
            ["optionName"] = optionName,
            ["channelId"] = channelId,
            ["channelName"] = channelName,
            ["serverId"] = guildId,
            ["serverName"] = serverName,
            [DiscordEventTypes.InteractionIdArg] = id,
        };
        AddUser(args, user, member.ValueKind == JsonValueKind.Object ? DiscordPayloads.Str(member, "nick") : "");
        Publish(DiscordEventTypes.SlashCommand, args);
        _ = FinishWhenDoneAsync(pending);
    }

    private static (string Name, string Value) FirstOption(JsonElement data)
    {
        if (!data.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array) return ("", "");
        foreach (var o in options.EnumerateArray())
        {
            var value = o.TryGetProperty("value", out var v)
                ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()
                : "";
            return (DiscordPayloads.Str(o, "name"), value);
        }
        return ("", "");
    }

    private async Task DeferAsync(PendingInteraction pending)
    {
        try
        {
            await Api.DeferInteractionAsync(pending.Id, pending.Token, pending.Ephemeral, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn(Name, $"Could not acknowledge a slash command: {Redact(ex.Message)}");
            throw;
        }
    }

    private void PruneInteractions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, value) in _interactions)
            if (value.Expires < now) _interactions.TryRemove(key, out _);
    }

    /// <summary>Counts runs carrying an interaction id, so the default answer goes out once they all finished.</summary>
    private void HookEngine()
    {
        if (Interlocked.Exchange(ref _engineHooked, 1) == 1) return;
        if (_engine() is not { } engine)
        {
            _engineHooked = 0;
            return;
        }
        engine.RunStarted += start =>
        {
            if (start.Args.TryGetValue(DiscordEventTypes.InteractionIdArg, out var raw) && raw?.ToString() is { } id
                && _interactions.TryGetValue(id, out var pending))
            {
                Interlocked.Increment(ref pending.Started);
                _runs[start.RunId] = pending;
            }
        };
        engine.RunCompleted += record =>
        {
            if (_runs.TryRemove(record.RunId, out var pending)) Interlocked.Increment(ref pending.Finished);
        };
    }

    private async Task FinishWhenDoneAsync(PendingInteraction pending)
    {
        try
        {
            var graceEnd = DateTimeOffset.UtcNow + Timings.ReplyGrace;
            while (DateTimeOffset.UtcNow < pending.Expires)
            {
                var started = Volatile.Read(ref pending.Started);
                var finished = Volatile.Read(ref pending.Finished);
                if (pending.Answered) return;
                if (started > 0 && finished >= started) break;
                if (started == 0 && DateTimeOffset.UtcNow >= graceEnd) break;
                await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            }
            var text = Settings.DefaultReply.Trim();
            if (text.Length == 0) text = "Done ✅";
            await AnswerAsync(pending, text, null, onlyIfUnanswered: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn(Name, $"Could not send the default slash command answer: {Redact(ex.Message)}");
        }
    }

    /// <summary>Answers a slash command use: edits the "thinking…" message the first time, then sends follow-ups.</summary>
    public async Task ReplyAsync(string interactionId, string text, DiscordEmbed? embed, CancellationToken ct)
    {
        if (!_interactions.TryGetValue(interactionId.Trim(), out var pending) || pending.Expires < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("There is no slash command to answer here. Use this step in an action started by the " +
                                                "\"Slash command used\" trigger (Discord allows answers for 15 minutes).");
        await AnswerAsync(pending, text, embed, onlyIfUnanswered: false, ct).ConfigureAwait(false);
    }

    private async Task AnswerAsync(PendingInteraction pending, string text, DiscordEmbed? embed, bool onlyIfUnanswered, CancellationToken ct)
    {
        if (text.Trim().Length == 0 && embed is null or { IsEmpty: true }) throw new ArgumentException("Type the answer text.");
        // The answer may only edit the deferred message once Discord has it.
        try { await pending.Deferred.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Discord did not take the slash command in time, so it cannot be answered: {Redact(ex.Message)}", ex);
        }
        await pending.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (pending.Answered)
            {
                if (onlyIfUnanswered) return;
                await Api.CreateFollowupAsync(pending.ApplicationId, pending.Token, text, embed, pending.Ephemeral, ct).ConfigureAwait(false);
                return;
            }
            await Api.EditOriginalResponseAsync(pending.ApplicationId, pending.Token, text, embed, ct).ConfigureAwait(false);
            pending.Answered = true;
        }
        finally
        {
            pending.Gate.Release();
        }
    }

    /// <summary>Registers <see cref="DiscordBotSettings.Commands"/> as guild commands in every server the bot is in.</summary>
    /// <returns>The number of servers updated.</returns>
    public async Task<int> RegisterCommandsAsync(CancellationToken ct = default)
    {
        var payload = DiscordPayloads.Commands(Settings.Commands);
        var appId = await ApplicationIdAsync(ct).ConfigureAwait(false);
        var guilds = Guilds;
        if (guilds.Count == 0) guilds = await RefreshAsync(ct).ConfigureAwait(false);
        if (guilds.Count == 0) throw new InvalidOperationException("The bot is not in any server yet. Click \"Invite the bot to your server\" first.");
        foreach (var guild in guilds) await Api.OverwriteGuildCommandsAsync(appId, guild.Id, payload, ct).ConfigureAwait(false);
        _log.Info(Name, $"Registered {payload.Count} slash command(s) in {guilds.Count} server(s)");
        return guilds.Count;
    }

    /// <summary>The application id: saved, or asked from Discord (GET /applications/@me).</summary>
    public async Task<string> ApplicationIdAsync(CancellationToken ct = default)
    {
        if (Settings.ApplicationId.Length > 0) return Settings.ApplicationId;
        var (id, name) = await Api.GetApplicationAsync(ct).ConfigureAwait(false);
        if (id.Length == 0) throw new InvalidOperationException("Discord did not say which application this bot belongs to.");
        Settings.ApplicationId = id;
        if (Settings.BotName.Length == 0) Settings.BotName = name;
        SaveSettings();
        return id;
    }

    /// <summary>Reloads the servers, channels and roles over REST (works without the Gateway connection).</summary>
    public async Task<IReadOnlyList<DiscordGuild>> RefreshAsync(CancellationToken ct = default)
    {
        var guilds = await Api.GetGuildsAsync(ct).ConfigureAwait(false);
        lock (_cacheLock)
        {
            _guilds.Clear();
            foreach (var g in guilds) _guilds[g.Id] = g;
        }
        DataChanged?.Invoke();
        return Guilds;
    }

    /// <summary>The server list, loading it over REST when nothing is known yet.</summary>
    internal async Task<IReadOnlyList<DiscordGuild>> GuildsAsync(CancellationToken ct)
    {
        var known = Guilds;
        return known.Count > 0 ? known : await RefreshAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Sets the bot's status and activity (Gateway presence update). Kept for the next connection too.</summary>
    public async Task SetPresenceAsync(string status, int? activityType, string text, string? streamUrl, CancellationToken ct)
    {
        var presence = GatewayProtocol.Presence(status, activityType, text, streamUrl);
        _presence = presence;
        if (_socket is not { } socket || !_sessionConnected)
            throw new InvalidOperationException("The Discord bot is not connected. Connect it on the Discord bot page first.");
        await socket.SendAsync(GatewayProtocol.PresenceUpdate(presence), ct).ConfigureAwait(false);
    }

    /// <summary>The invite link (scopes bot + applications.commands) with <see cref="InvitePermissions"/>.</summary>
    public static string InviteUrl(string applicationId) =>
        $"https://discord.com/oauth2/authorize?client_id={Uri.EscapeDataString(applicationId)}&scope=bot%20applications.commands&permissions={InvitePermissions}";

    /// <summary>
    /// View Channels (1&lt;&lt;10), Send Messages (1&lt;&lt;11), Manage Messages (1&lt;&lt;13), Embed Links (1&lt;&lt;14),
    /// Read Message History (1&lt;&lt;16), Manage Roles (1&lt;&lt;28), Use Application Commands (1&lt;&lt;31).
    /// </summary>
    public const long InvitePermissions = (1L << 10) | (1L << 11) | (1L << 13) | (1L << 14) | (1L << 16) | (1L << 28) | (1L << 31);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
