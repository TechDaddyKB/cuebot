using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Donations.SocketIo;
using Cuebot.Donations.Transport;
using Cuebot.Donations.TreatStream;

namespace Cuebot.Donations.Streamlabs;

/// <summary>
/// The Streamlabs connection: the Socket API (Socket.IO at sockets.streamlabs.com, authenticated with the Socket API
/// token in the query string) for donations, charity donations, merch, loyalty store redemptions, Prime gifts and,
/// optionally, relayed platform events; and, when the user connects their own Streamlabs app (OAuth2), the REST API for
/// the socket token, alerts, the wheel, jar, credits, loyalty points, media share and back-filling missed donations.
/// </summary>
public sealed partial class StreamlabsService : DonationServiceBase
{
    public static readonly Uri Production = new("https://sockets.streamlabs.com");

    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Streamlabs recommends at most 2 POST /alerts and 2 POST /donations per minute per user.</summary>
    private const int AlertsPerMinute = 2;

    private readonly Func<IWebSocketTransport> _transports;
    private readonly Uri _server;
    private readonly DonationsTimings _timings;
    private readonly StreamlabsApi _api;
    private readonly Func<int, IOAuthRedirectReceiver> _receivers;
    private readonly RequestRateLimiter _limiter;
    private readonly RequestRateLimiter _alertLimiter;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _donationGate = new();
    private string _lastDonationId = "";
    private bool _connectedBefore;

    public StreamlabsService(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log, HttpClient http)
        : this(settings, bus, log, () => new ClientWebSocketTransport(DonationsTimings.Default.KeepAlive), Production, DonationsTimings.Default,
            new StreamlabsApi(http), port => new LoopbackRedirectReceiver(port, StreamlabsEventMapper.Service), TimeProvider.System)
    {
    }

    internal StreamlabsService(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports,
        Uri server, DonationsTimings timings, StreamlabsApi? api = null, Func<int, IOAuthRedirectReceiver>? receivers = null,
        TimeProvider? time = null) : base(settings, bus, log)
    {
        _transports = transports;
        _server = server;
        _timings = timings;
        _api = api ?? new StreamlabsApi(new HttpClient());
        _receivers = receivers ?? (port => new LoopbackRedirectReceiver(port, StreamlabsEventMapper.Service));
        var clock = time ?? TimeProvider.System;
        _limiter = new RequestRateLimiter(() => Settings.RequestsPerMinute, clock);
        _alertLimiter = new RequestRateLimiter(() => AlertsPerMinute, clock);
    }

    public override string Name => StreamlabsEventMapper.Service;
    public StreamlabsSettings Settings => SettingsFile.Value.Streamlabs;

    /// <summary>The socket token, or, when only the app is connected, the access token that gets one.</summary>
    protected override string Token => Settings.SocketToken.Trim().Length > 0 ? Settings.SocketToken : Settings.AccessToken;

    protected override string MissingTokenMessage => "Paste your Streamlabs Socket API token (or connect your Streamlabs app) first.";
    protected override bool AutoConnect => Settings.AutoConnect;
    protected override bool RelayPlatformEvents => Settings.RelayPlatformEvents;
    protected override string ConnectedEvent => StreamlabsEventTypes.Connected;
    protected override string DisconnectedEvent => StreamlabsEventTypes.Disconnected;
    public override IReadOnlyList<string> TestEventKinds => [.. StreamlabsEventMapper.Samples.Keys];

    /// <summary>True when an OAuth access token is saved (the REST steps work).</summary>
    public bool IsSignedIn => Settings.AccessToken.Trim().Length > 0;

    /// <summary>The redirect URL to register in the Streamlabs app.</summary>
    public string RedirectUri => $"http://localhost:{Settings.RedirectPort.ToString(CultureInfo.InvariantCulture)}/callback";

    private protected override string RedactSecrets(string? text)
    {
        var result = text ?? "";
        foreach (var secret in new[] { Settings.SocketToken, Settings.AccessToken, Settings.RefreshToken, Settings.ClientSecret })
            result = Secrets.Redact(result, secret);
        return result;
    }

    public override bool SendTestEvent(string kind)
    {
        if (!StreamlabsEventMapper.Samples.TryGetValue(kind, out var sample)) return false;
        using var doc = JsonDocument.Parse(sample);
        foreach (var e in StreamlabsEventMapper.Map(doc.RootElement, isTest: true)) Publish(e);
        return true;
    }

    /// <summary>Handles the payload of one Socket.IO "event" (also used by tests).</summary>
    internal int HandleEvent(JsonElement payload)
    {
        var count = 0;
        foreach (var e in StreamlabsEventMapper.Map(payload))
        {
            if (e.Type == StreamlabsEventTypes.Donation) NoteDonationId(e.Args.GetValueOrDefault("donationId") as string);
            if (Publish(e)) count++;
        }
        return count;
    }

    // ---- OAuth ----

    /// <summary>
    /// Connects the user's own Streamlabs app: listens on <see cref="RedirectUri"/>, opens Streamlabs' authorize page with
    /// <paramref name="openBrowser"/>, exchanges the code (valid 5 minutes, single use), saves the tokens, fetches the
    /// socket token and the account. Throws with a readable message on failure.
    /// </summary>
    public async Task SignInAsync(Action<string> openBrowser, CancellationToken ct = default)
    {
        var (clientId, secret) = (Settings.ClientId.Trim(), Settings.ClientSecret.Trim());
        if (clientId.Length == 0 || secret.Length == 0)
            throw new InvalidOperationException("Enter the Client ID and Client Secret of your Streamlabs app first (Streamlabs dashboard → Settings → API Settings → Register App).");
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var redirect = RedirectUri;
        using var receiver = _receivers(Settings.RedirectPort);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);
        openBrowser(_api.AuthorizeUrl(clientId, redirect, state).AbsoluteUri);
        OAuthRedirect result;
        try
        {
            result = await receiver.WaitAsync(state, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("The Streamlabs sign-in timed out. Try again.");
        }
        if (result.Code is not { } code) throw new InvalidOperationException(result.Error ?? "Streamlabs sign-in failed.");
        try
        {
            var tokens = await _api.ExchangeCodeAsync(clientId, secret, redirect, code, ct).ConfigureAwait(false);
            Settings.AccessToken = tokens.AccessToken;
            Settings.RefreshToken = tokens.RefreshToken;
            SaveSettings();
            await LoadAccountAsync(ct).ConfigureAwait(false);
        }
        catch (StreamlabsApiException ex)
        {
            throw new InvalidOperationException(RedactSecrets(ex.Message));
        }
        Log.Info(Name, $"Connected the Streamlabs app{(Settings.AccountName.Length > 0 ? $" as {Settings.AccountName}" : "")}");
    }

    /// <summary>Forgets the app tokens and the account; the pasted socket token stays.</summary>
    public void SignOut()
    {
        Settings.AccessToken = "";
        Settings.RefreshToken = "";
        Settings.AccountName = "";
        Settings.LinkedAccounts = "";
        SaveSettings();
        SetStatus(State, StatusMessage);
    }

    /// <summary>Fetches the account (GET /user) and, when none is saved, the socket token (GET /socket/token).</summary>
    private async Task LoadAccountAsync(CancellationToken ct)
    {
        var user = await RestAsync((token, c) => _api.GetUserAsync(token, c), ct).ConfigureAwait(false);
        Settings.AccountName = user.DisplayName;
        Settings.LinkedAccounts = user.LinkedText;
        if (Settings.PointsChannel.Trim().Length == 0 && user.TwitchLogin.Length > 0) Settings.PointsChannel = user.TwitchLogin;
        if (Settings.SocketToken.Trim().Length == 0)
            Settings.SocketToken = await RestAsync((token, c) => _api.GetSocketTokenAsync(token, c), ct).ConfigureAwait(false);
        SaveSettings();
    }

    /// <summary>With only the app connected, fetches the socket token before the socket connects.</summary>
    private protected override async Task PrepareAsync(CancellationToken ct)
    {
        if (Settings.SocketToken.Trim().Length > 0 || !IsSignedIn) return;
        try
        {
            Settings.SocketToken = await RestAsync((token, c) => _api.GetSocketTokenAsync(token, c), ct).ConfigureAwait(false);
            SaveSettings();
        }
        catch (StreamlabsApiException ex)
        {
            throw new InvalidOperationException($"Could not get the socket token from Streamlabs: {RedactSecrets(ex.Message)}");
        }
    }

    /// <summary>
    /// Runs one REST call with the access token: waits for the per-minute limit, and on 401 refreshes the token once
    /// (Streamlabs says tokens do not expire, so this should be rare) and retries.
    /// </summary>
    private async Task<T> RestAsync<T>(Func<string, CancellationToken, Task<T>> call, CancellationToken ct, bool alertLimit = false)
    {
        if (!IsSignedIn) throw new StreamlabsApiException("Connect Cuebot to your Streamlabs app first (Donations → Streamlabs → Connect with Streamlabs)", 401);
        if (alertLimit) await _alertLimiter.WaitAsync(ct).ConfigureAwait(false);
        await _limiter.WaitAsync(ct).ConfigureAwait(false);
        var used = Settings.AccessToken.Trim();
        try
        {
            return await call(used, ct).ConfigureAwait(false);
        }
        catch (StreamlabsApiException ex) when (ex.Status == 401 && Settings.RefreshToken.Length > 0 && Settings.ClientSecret.Length > 0)
        {
            await RefreshAsync(used, ct).ConfigureAwait(false);
            await _limiter.WaitAsync(ct).ConfigureAwait(false);
            return await call(Settings.AccessToken.Trim(), ct).ConfigureAwait(false);
        }
    }

    private Task RestAsync(Func<string, CancellationToken, Task> call, CancellationToken ct, bool alertLimit = false) =>
        RestAsync(async (token, c) => { await call(token, c).ConfigureAwait(false); return true; }, ct, alertLimit);

    private async Task RefreshAsync(string rejected, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Settings.AccessToken.Trim() != rejected) return;   // another call refreshed it already
            await _limiter.WaitAsync(ct).ConfigureAwait(false);
            var tokens = await _api.RefreshAsync(Settings.ClientId.Trim(), Settings.ClientSecret.Trim(), RedirectUri, Settings.RefreshToken, ct)
                .ConfigureAwait(false);
            Settings.AccessToken = tokens.AccessToken;
            Settings.RefreshToken = tokens.RefreshToken;
            SaveSettings();
            Log.Info(Name, "Refreshed the Streamlabs access token");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // ---- Socket ----

    private protected override async Task RunAsync(CancellationToken ct)
    {
        var token = Settings.SocketToken.Trim();
        _connectedBefore = false;
        var client = new SocketIoClient(_transports, _timings, Log, Name)
        {
            EngineIoVersion = Settings.EngineIoVersion is 4 ? 4 : 3,
            Redact = RedactSecrets,
        };
        client.Connected += eio =>
        {
            if (Settings.EngineIoVersion != eio)
            {
                Settings.EngineIoVersion = eio;
                SaveSettings();
            }
            OnConnected($"Connected to the Streamlabs Socket API (Socket.IO {(eio >= 4 ? "v4" : "v2")})");
            var reconnected = _connectedBefore;
            _connectedBefore = true;
            if (IsSignedIn && Settings.BackfillMissedDonations) _ = Task.Run(() => BackfillAsync(reconnected, ct), CancellationToken.None);
        };
        // The client says whether the server refused us (CONNECT_ERROR); reasons are not matched as text.
        client.Disconnected += (reason, fatal) => OnDisconnected(reason, fatal);
        client.EventReceived += (name, args) =>
        {
            if (name == "event" && args.Count > 0) HandleEvent(args[0]);
        };
        await client.RunAsync(eio => SocketIoFraming.Url(_server, eio, new Dictionary<string, string> { ["token"] = token }), ct)
            .ConfigureAwait(false);
    }

    // ---- Back-fill ----

    /// <summary>Remembers the newest donation id seen (numeric ids compare as numbers).</summary>
    private void NoteDonationId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_donationGate)
        {
            if (_lastDonationId.Length == 0 || CompareIds(id, _lastDonationId) > 0) _lastDonationId = id;
        }
    }

    private static int CompareIds(string a, string b) =>
        long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) && long.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
            ? x.CompareTo(y)
            : string.CompareOrdinal(a, b);

    internal string LastDonationId
    {
        get { lock (_donationGate) return _lastDonationId; }
    }

    /// <summary>
    /// First connect: remembers the newest donation id as the starting point. Reconnect: publishes the donations made
    /// after it (GET /donations?after=…), oldest first, marked isBackfill; ones the socket already delivered are skipped
    /// (same donation id).
    /// </summary>
    internal async Task<int> BackfillAsync(bool reconnected, CancellationToken ct)
    {
        try
        {
            var since = LastDonationId;
            if (!reconnected || since.Length == 0)
            {
                if (since.Length == 0)
                {
                    var newest = await RestAsync((token, c) => _api.GetDonationsAsync(token, 1, null, null, null, c), ct).ConfigureAwait(false);
                    if (newest.Count > 0) NoteDonationId(newest[0].Id);
                }
                return 0;
            }
            var missed = await RestAsync((token, c) => _api.GetDonationsAsync(token, 25, since, null, null, c), ct).ConfigureAwait(false);
            var count = 0;
            foreach (var donation in missed.Where(d => CompareIds(d.Id, since) > 0).OrderBy(d => d.Id, Comparer<string>.Create(CompareIds)))
            {
                NoteDonationId(donation.Id);
                if (Publish(StreamlabsEventMapper.Backfilled(donation))) count++;
            }
            if (count > 0) Log.Info(Name, $"Published {count} Streamlabs donation(s) missed while reconnecting");
            return count;
        }
        catch (Exception ex) when (ex is StreamlabsApiException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn(Name, $"Could not fetch the donations missed while reconnecting: {RedactSecrets(ex.Message)}");
            return 0;
        }
    }
}
