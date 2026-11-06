using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Core.Webhooks;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;

namespace Cuebot.Portal.Relay;

/// <summary>Timeouts and delays of <see cref="PortalRelayClient"/> (tests use short ones).</summary>
public sealed record PortalRelayTimings(
    TimeSpan ConnectTimeout,
    TimeSpan ReconnectDelay,
    TimeSpan MaxReconnectDelay,
    TimeSpan PingInterval,
    TimeSpan SilenceTimeout)
{
    public static PortalRelayTimings Default { get; } = new(
        ConnectTimeout: TimeSpan.FromSeconds(15),
        ReconnectDelay: TimeSpan.FromSeconds(2),
        MaxReconnectDelay: TimeSpan.FromSeconds(60),
        PingInterval: TimeSpan.FromSeconds(30),
        SilenceTimeout: TimeSpan.FromSeconds(95));
}

/// <summary>One mod device's encrypted connection, as the link handler sees it.</summary>
public sealed class PortalLinkContext(uint channel, byte[] binding)
{
    /// <summary>The relay's channel number for this device (new on every connection).</summary>
    public uint Channel { get; } = channel;

    /// <summary>The connection's handshake binding (<see cref="Crypto.PortalSession.Binding"/>), which device proofs sign.</summary>
    public byte[] Binding { get; } = binding;

    /// <summary>The member signed in on this connection, once a sign-in or resume succeeded.</summary>
    public Guid? MemberId { get; set; }

    public Guid? DeviceId { get; set; }

    /// <summary>Sign-in attempts on this connection (each one costs a call to the provider).</summary>
    public int SignInAttempts { get; set; }
}

/// <summary>What a mod device's decrypted messages are handed to (<see cref="SignIn.PortalSignInHandler"/> in the app).</summary>
public interface IPortalLinkHandler
{
    /// <summary>The answer to one message, or null for none. Throwing closes the device's connection.</summary>
    Task<byte[]?> HandleAsync(PortalLinkContext link, byte[] message, CancellationToken ct);
}

/// <summary>Only checks the link works: <c>{"op":"ping"}</c> → <c>{"op":"pong"}</c>. The default until the app sets a handler.</summary>
public sealed class PingLinkHandler : IPortalLinkHandler
{
    public Task<byte[]?> HandleAsync(PortalLinkContext link, byte[] message, CancellationToken ct)
    {
        string? op = null;
        try { op = JsonNode.Parse(message)?["op"]?.GetValue<string>(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
        JsonObject answer = op == "ping"
            ? new() { ["op"] = "pong", ["version"] = PortalRelayClient.ProtocolVersion }
            : new() { ["op"] = "error", ["error"] = "This Cuebot does not offer sign-in." };
        return Task.FromResult<byte[]?>(JsonSerializer.SerializeToUtf8Bytes(answer));
    }
}

/// <summary>
/// Cuebot's side of the shared portal relay (portal/RELAY.md): an outbound WebSocket to
/// <c>wss://relay.techdaddydigital.com/connect?id=&lt;relay id&gt;</c>, signed in by proving the identity key behind
/// the relay ID. Carries each mod device's end-to-end encrypted channel (handshake, then frames, handed to an
/// <see cref="IPortalLinkHandler"/>) and webhooks sealed to the identity key. Reconnects with backoff.
/// </summary>
public sealed class PortalRelayClient : IConnectable, IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    public const string ConnectLabel = "cuebot-relay-connect-v1";
    public const string SharedRelay = "wss://relay.techdaddydigital.com";
    public const string SharedHooks = "https://hooks.techdaddydigital.com";
    public const uint HookChannel = 0;

    /// <summary>Close codes (portal/RELAY.md).</summary>
    public const int ReplacedCloseCode = 4000;
    public const int AuthFailedCloseCode = 4001;
    public const int ProtocolCloseCode = 4003;
    public const int PortalClosedCloseCode = 4403;

    /// <summary>Most mod devices queued messages before a device is cut off as too chatty.</summary>
    private const int LinkQueue = 64;
    private const string LogSource = "Mod portal";

    private readonly PortalDirectory _directory;
    private readonly AppLog _log;
    private readonly Func<IPortalRelayTransport> _transportFactory;
    private readonly PortalRelayTimings _timings;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;

    public PortalRelayClient(PortalDirectory directory, AppLog log)
        : this(directory, log, () => new ClientWebSocketPortalTransport(), PortalRelayTimings.Default)
    {
    }

    internal PortalRelayClient(PortalDirectory directory, AppLog log, Func<IPortalRelayTransport> transportFactory, PortalRelayTimings timings)
    {
        _directory = directory;
        _log = log;
        _transportFactory = transportFactory;
        _timings = timings;
    }

    public string Name => "Mod portal relay";

    public PortalDirectory Directory => _directory;
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    public bool IsConnected => State == ConnectionState.Connected;

    /// <summary>Handles mod devices' messages. Replaced when sign-in is built (phase 3).</summary>
    public IPortalLinkHandler LinkHandler { get; set; } = new PingLinkHandler();

    /// <summary>Where opened webhooks go (the app hands them to the webhook relay client's handlers). Called one at a time.</summary>
    public Func<WebhookDelivery, CancellationToken, Task>? WebhookSink { get; set; }

    /// <summary>Mod devices connected right now.</summary>
    public int DeviceCount => _links.Count;

    private readonly ConcurrentDictionary<uint, Link> _links = new();

    /// <summary>Should the relay connection be up: the portal or shared webhooks are on.</summary>
    public bool Wanted => _directory.Enabled || _directory.SharedWebhooks;

    /// <summary>The connect address for a relay setting (empty: the shared relay), or null when it is not a valid address.</summary>
    public static Uri? ConnectUri(string relayUrl, string relayId)
    {
        var baseUri = BaseUri(relayUrl);
        if (baseUri is null) return null;
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttp ? "ws" : "wss",
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/connect",
            Query = "id=" + relayId,
        };
        if (builder.Uri.IsDefaultPort) builder.Port = -1;
        return builder.Uri;
    }

    /// <summary>
    /// The address to paste into a service's webhook settings: <c>https://hooks.techdaddydigital.com/&lt;relay id&gt;/&lt;route
    /// secret&gt;/&lt;service&gt;</c> (another relay: its own address with <c>/h/</c>), or null while shared webhooks are off.
    /// </summary>
    public string? WebhookUrl(string service)
    {
        if (!_directory.SharedWebhooks) return null;
        var tail = $"{_directory.RelayId}/{_directory.RouteSecret}/{service}";
        if (_directory.RelayUrl.Length == 0) return $"{SharedHooks}/{tail}";
        return BaseUri(_directory.RelayUrl) is { } b ? $"{b.GetLeftPart(UriPartial.Authority)}{b.AbsolutePath.TrimEnd('/')}/h/{tail}" : null;
    }

    private static Uri? BaseUri(string relayUrl)
    {
        var text = relayUrl.Trim();
        if (text.Length == 0) text = SharedRelay;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme switch
        {
            "https" or "wss" => new UriBuilder(uri) { Scheme = "https", Port = uri.IsDefaultPort ? -1 : uri.Port, Query = "" }.Uri,
            "http" or "ws" => new UriBuilder(uri) { Scheme = "http", Port = uri.IsDefaultPort ? -1 : uri.Port, Query = "" }.Uri,
            _ => null,
        };
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Connecting, "Connecting to the portal relay...");
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
            await StopAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Disconnected, "Disconnected");
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Connects when the portal or shared webhooks are on and disconnects otherwise. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        try
        {
            if (Wanted) { if (_run is null) await ConnectAsync(ct).ConfigureAwait(false); }
            else if (_run is not null) await DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Error(LogSource, $"Could not start the portal relay connection: {Redact(ex.Message)}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
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
            _log.Error(LogSource, $"Error while disconnecting from the portal relay: {Redact(ex.Message)}");
        }
        run.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var delay = _timings.ReconnectDelay;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (reason, fatal, hadConnected) = await RunOnceAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                if (fatal)
                {
                    _log.Warn(LogSource, reason);
                    SetStatus(ConnectionState.Error, reason);
                    return;
                }
                if (hadConnected)
                {
                    delay = _timings.ReconnectDelay;
                    _log.Warn(LogSource, $"Portal relay connection lost: {reason}");
                }
                SetStatus(ConnectionState.Connecting, $"Reconnecting in {Math.Max(1, (int)delay.TotalSeconds)} s ({reason})");
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _timings.MaxReconnectDelay.Ticks));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, $"Portal relay connection stopped: {Redact(ex.Message)}");
            SetStatus(ConnectionState.Error, Redact(ex.Message));
        }
    }

    private async Task<(string Reason, bool Fatal, bool HadConnected)> RunOnceAsync(CancellationToken ct)
    {
        var identity = _directory.Identity;
        var uri = ConnectUri(_directory.RelayUrl, identity.RelayId);
        if (uri is null) return ("The portal relay address is not valid", true, false);
        var transport = _transportFactory();
        var hadConnected = false;
        try
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_timings.ConnectTimeout);
                await transport.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
                await AuthenticateAsync(transport, identity, uri, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (transport.CloseCode == AuthFailedCloseCode) return ("The portal relay did not accept this Cuebot's identity key", true, false);
                return ($"portal relay not reachable: {(ex is OperationCanceledException ? "timed out" : Describe(ex))}", false, false);
            }

            hadConnected = true;
            SetStatus(ConnectionState.Connected, "Connected to the portal relay");
            _log.Info(LogSource, $"Connected to the portal relay ({uri.Host})");
            string reason;
            var fatal = false;
            try
            {
                reason = await RunSessionAsync(transport, identity, ct).ConfigureAwait(false);
                if (transport.CloseCode == ReplacedCloseCode)
                {
                    reason = "Another Cuebot with this identity key connected to the portal relay and took over; click Connect to take it back";
                    fatal = true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                reason = "disconnected";
            }
            catch (Exception ex)
            {
                reason = Describe(ex);
            }
            return (Redact(reason), fatal, hadConnected);
        }
        finally
        {
            foreach (var link in _links.Values) link.Dispose();
            _links.Clear();
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Answers the relay's challenge with the identity key and registers the webhook route secret's hash.</summary>
    private async Task AuthenticateAsync(IPortalRelayTransport transport, IdentityKey identity, Uri uri, CancellationToken ct)
    {
        var challenge = await transport.ReceiveAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidDataException($"the relay closed the connection ({transport.CloseCode} {transport.CloseReason})");
        using var doc = JsonDocument.Parse(challenge.Data);
        if (challenge.Binary || Str(doc.RootElement, "type") != "challenge") throw new InvalidDataException("the relay did not send a challenge");
        var nonce = PortalKeys.FromBase64Url(Str(doc.RootElement, "nonce"));
        if (nonce.Length < 16) throw new InvalidDataException("the relay's challenge is too short");
        var signature = identity.Sign([.. PortalKeys.Utf8(ConnectLabel), .. nonce, .. PortalKeys.Utf8(uri.Authority)]);
        var auth = new JsonObject
        {
            ["type"] = "auth",
            ["key"] = PortalKeys.Base64Url(identity.PublicKey),
            ["signature"] = PortalKeys.Base64Url(signature),
            ["hooks"] = PortalKeys.Base64Url(SHA256.HashData(PortalKeys.Utf8(_directory.RouteSecret))),
        };
        await transport.SendTextAsync(auth.ToJsonString(), ct).ConfigureAwait(false);
        var ready = await transport.ReceiveAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidDataException($"the relay closed the connection ({transport.CloseCode} {transport.CloseReason})");
        using var readyDoc = JsonDocument.Parse(ready.Data);
        if (ready.Binary || Str(readyDoc.RootElement, "type") != "ready") throw new InvalidDataException("the relay did not confirm the connection");
    }

    private async Task<string> RunSessionAsync(IPortalRelayTransport transport, IdentityKey identity, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var hooks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        var lastHeard = Environment.TickCount64;
        var silent = false;
        var webhooks = Task.Run(() => WebhookLoopAsync(identity, hooks.Reader, session.Token), CancellationToken.None);
        var heartbeat = Task.Run(async () =>
        {
            while (!session.Token.IsCancellationRequested)
            {
                await Task.Delay(_timings.PingInterval, session.Token).ConfigureAwait(false);
                if (Environment.TickCount64 - Interlocked.Read(ref lastHeard) > (long)_timings.SilenceTimeout.TotalMilliseconds)
                {
                    silent = true;
                    await session.CancelAsync().ConfigureAwait(false);
                    return;
                }
                await transport.SendTextAsync("""{"type":"ping"}""", session.Token).ConfigureAwait(false);
            }
        }, CancellationToken.None);

        try
        {
            while (true)
            {
                RelayMessage? message;
                try
                {
                    message = await transport.ReceiveAsync(session.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (silent && !ct.IsCancellationRequested)
                {
                    return "the portal relay stopped answering";
                }
                if (message is not { } m)
                {
                    var code = transport.CloseCode is { } c ? $" ({c}{(string.IsNullOrEmpty(transport.CloseReason) ? "" : " " + transport.CloseReason)})" : "";
                    return $"the portal relay closed the connection{code}";
                }
                Interlocked.Exchange(ref lastHeard, Environment.TickCount64);
                if (m.Binary) await OnFrameAsync(transport, identity, m.Data, hooks.Writer, session.Token).ConfigureAwait(false);
                else await OnControlAsync(transport, m.Data, session.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            hooks.Writer.TryComplete();
            await session.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(Quiet(webhooks), Quiet(heartbeat)).ConfigureAwait(false);
        }
    }

    private static async Task Quiet(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* the session is over */ }
    }

    private async Task OnControlAsync(IPortalRelayTransport transport, byte[] data, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(data); }
        catch (JsonException) { return; }
        using (doc)
        {
            var root = doc.RootElement;
            var ch = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ch", out var c) && c.TryGetUInt32(out var n) ? n : HookChannel;
            switch (Str(root, "type"))
            {
                case "open" when ch != HookChannel:
                    if (!_directory.Enabled)
                    {
                        await CloseChannelAsync(transport, ch, PortalClosedCloseCode, "The streamer has not opened the mod portal", ct).ConfigureAwait(false);
                        return;
                    }
                    var link = new Link(this, transport, ch);
                    _links[ch] = link;
                    link.Start(ct);
                    break;
                case "close":
                    if (_links.TryRemove(ch, out var gone)) gone.Dispose();
                    break;
            }
        }
    }

    private async Task OnFrameAsync(IPortalRelayTransport transport, IdentityKey identity, byte[] data, ChannelWriter<byte[]> hooks, CancellationToken ct)
    {
        if (data.Length < 4) return;
        var ch = BinaryPrimitives.ReadUInt32BigEndian(data);
        var payload = data[4..];
        if (ch == HookChannel)
        {
            hooks.TryWrite(payload);
            return;
        }
        if (!_links.TryGetValue(ch, out var link)) return;
        if (!link.Receive(identity, payload, out var serverHello, out var error))
        {
            _links.TryRemove(ch, out _);
            link.Dispose();
            await CloseChannelAsync(transport, ch, ProtocolCloseCode, error, ct).ConfigureAwait(false);
        }
        else if (serverHello is not null)
        {
            await transport.SendBinaryAsync(Frame(ch, serverHello), ct).ConfigureAwait(false);
        }
    }

    private static Task CloseChannelAsync(IPortalRelayTransport transport, uint ch, int code, string reason, CancellationToken ct) =>
        transport.SendTextAsync(new JsonObject { ["type"] = "close", ["ch"] = ch, ["code"] = code, ["reason"] = reason }.ToJsonString(), ct);

    private async Task WebhookLoopAsync(IdentityKey identity, ChannelReader<byte[]> reader, CancellationToken ct)
    {
        await foreach (var sealedBytes in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (!_directory.SharedWebhooks || WebhookSink is not { } sink) continue;
            WebhookDelivery? delivery;
            try
            {
                delivery = ParseDelivery(identity.OpenSealed(sealedBytes));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
            {
                delivery = null;
            }
            if (delivery is null)
            {
                _log.Warn(LogSource, "Ignored a webhook from the portal relay that could not be opened");
                continue;
            }
            try
            {
                await sink(delivery, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.Error(LogSource, $"A shared-relay webhook for \"{delivery.Service}\" failed: {Redact(ex.Message)}");
            }
        }
    }

    /// <summary>The delivery JSON the relay sealed (the same shape as the self-hosted relay's), or null when malformed.</summary>
    internal static WebhookDelivery? ParseDelivery(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Str(root, "id").Length == 0) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
            foreach (var p in h.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) headers[p.Name.ToLowerInvariant()] = p.Value.GetString() ?? "";
        var receivedAt = DateTimeOffset.TryParse(Str(root, "receivedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at : DateTimeOffset.UtcNow;
        var method = Str(root, "method");
        return new WebhookDelivery(Str(root, "id"), Str(root, "service").ToLowerInvariant(), Str(root, "path"),
            method.Length == 0 ? "POST" : method.ToUpperInvariant(), Str(root, "query"), headers,
            Convert.FromBase64String(Str(root, "bodyBase64")), receivedAt);
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    /// <summary>Removes the webhook route secret from text that may be logged or shown.</summary>
    private string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var secret = _directory.SharedWebhooks || _directory.Enabled ? _directory.RouteSecret : "";
        return secret.Length >= 16 ? text.Replace(secret, "***", StringComparison.Ordinal) : text;
    }

    private string Describe(Exception ex) => Redact(ex is System.Net.WebSockets.WebSocketException { InnerException: { } inner } ? inner.Message : ex.Message);

    /// <summary>
    /// One mod device: the first frame is its handshake hello; after that, frames are decrypted in order here and
    /// handled one at a time on a worker, whose answers are encrypted in order.
    /// </summary>
    private sealed class Link(PortalRelayClient client, IPortalRelayTransport transport, uint ch) : IDisposable
    {
        private readonly Channel<byte[]> _inbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(LinkQueue) { SingleReader = true, SingleWriter = true });
        private CancellationTokenSource? _stop;
        private PortalSession? _session;
        private PortalLinkContext? _context;
        private bool _disposed;

        public void Start(CancellationToken ct)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _stop.Token;
            _ = Task.Run(() => WorkAsync(token), CancellationToken.None);
        }

        /// <summary>
        /// False (with the reason) when the device broke the protocol and must be closed. The first frame is the
        /// device's hello; <paramref name="serverHello"/> is then the answer to send back.
        /// </summary>
        public bool Receive(IdentityKey identity, byte[] payload, out byte[]? serverHello, out string error)
        {
            serverHello = null;
            error = "";
            try
            {
                if (_session is null)
                {
                    (serverHello, _session) = PortalHandshake.Accept(identity, payload);
                    _context = new PortalLinkContext(ch, _session.Binding);
                    return true;
                }
                if (_inbox.Writer.TryWrite(_session.Decrypt(payload))) return true;
                error = "Too many messages at once";
                return false;
            }
            catch (CryptographicException)
            {
                error = "The secure connection failed";
                return false;
            }
        }

        private async Task WorkAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var message in _inbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    var answer = await client.LinkHandler.HandleAsync(_context!, message, ct).ConfigureAwait(false);
                    if (answer is not null && _session is { } s) await transport.SendBinaryAsync(Frame(ch, s.Encrypt(answer)), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The device left.
            }
            catch (Exception ex) when (!_disposed)
            {
                client._log.Warn(LogSource, $"A mod device's connection failed: {client.Redact(ex.Message)}");
                if (client._links.TryRemove(ch, out _))
                {
                    try { await CloseChannelAsync(transport, ch, ProtocolCloseCode, "Cuebot could not handle a message", CancellationToken.None).ConfigureAwait(false); }
                    catch { /* the relay connection is gone too */ }
                }
                Dispose();
            }
            catch
            {
                // Closed while handling a message.
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _inbox.Writer.TryComplete();
            _stop?.Cancel();
            _stop?.Dispose();
            _session?.Dispose();
        }
    }

    internal static byte[] Frame(uint ch, byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, ch);
        payload.CopyTo(frame, 4);
        return frame;
    }
}
