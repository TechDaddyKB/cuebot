using System.Security.Cryptography;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Auth;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Donations.SocketIo;
using Cuebot.Donations.Transport;

namespace Cuebot.Donations.TreatStream;

/// <summary>
/// The TreatStream connection: OAuth2 sign-in with the user's own TreatStream API app (client id + secret, loopback
/// redirect), a socket token from <c>Oauth2/Authorize/socketToken</c>, then Socket.IO v2 (Engine.IO 3) at
/// nodeapi.treatstream.com with <c>token=&lt;socket token&gt;</c>, receiving <c>realTimeTreat</c>.
/// </summary>
public sealed class TreatStreamService : DonationServiceBase
{
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Refresh the access token when it expires within this window.</summary>
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromDays(1);

    private readonly Func<IWebSocketTransport> _transports;
    private readonly Uri _socketServer;
    private readonly DonationsTimings _timings;
    private readonly TreatStreamApi _api;
    private readonly Func<int, IOAuthRedirectReceiver> _receivers;
    private readonly TimeProvider _time;
    private readonly AppCredentials _builtInApp;
    private string _socketToken = "";

    public TreatStreamService(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log, HttpClient http)
        : this(settings, bus, log, () => new ClientWebSocketTransport(DonationsTimings.Default.KeepAlive), TreatStreamApi.SocketServer,
            DonationsTimings.Default, new TreatStreamApi(http), port => new LoopbackRedirectReceiver(port, TreatStreamEventMapper.Service),
            TimeProvider.System)
    {
    }

    internal TreatStreamService(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports,
        Uri socketServer, DonationsTimings timings, TreatStreamApi api, Func<int, IOAuthRedirectReceiver> receivers, TimeProvider time,
        AppCredentials? builtInApp = null)
        : base(settings, bus, log)
    {
        _builtInApp = builtInApp ?? BuiltInApps.TreatStream;
        _transports = transports;
        _socketServer = socketServer;
        _timings = timings;
        _api = api;
        _receivers = receivers;
        _time = time;
    }

    public override string Name => TreatStreamEventMapper.Service;
    public TreatStreamSettings Settings => SettingsFile.Value.TreatStream;
    protected override string Token => Settings.AccessToken;
    protected override string MissingTokenMessage => "Sign in to TreatStream first (or paste an access token).";
    protected override bool AutoConnect => Settings.AutoConnect;
    protected override bool RelayPlatformEvents => false;
    protected override string ConnectedEvent => TreatStreamEventTypes.Connected;
    protected override string DisconnectedEvent => TreatStreamEventTypes.Disconnected;
    public override IReadOnlyList<string> TestEventKinds => [.. TreatStreamEventMapper.Samples.Keys];

    /// <summary>The TreatStream app signed in with: the user's own (Advanced) or the one built into Cuebot.</summary>
    public AppCredentials App => AppCredentials.Choose(Settings.ClientId, Settings.ClientSecret, _builtInApp);

    /// <summary>True when this build has a TreatStream app of its own.</summary>
    public bool HasBuiltInApp => _builtInApp.IsSet;

    /// <summary>Cuebot's app has the default port registered; the user's own app uses the port they chose.</summary>
    public int RedirectPort => App.IsBuiltIn ? TreatStreamSettings.DefaultRedirectPort : Settings.RedirectPort;

    /// <summary>The redirect URL to register in the TreatStream app.</summary>
    public string RedirectUri => $"http://localhost:{RedirectPort}/callback";

    public bool IsSignedIn => Settings.AccessToken.Length > 0;

    private protected override string RedactSecrets(string? text)
    {
        var result = text ?? "";
        foreach (var secret in new[] { Settings.AccessToken, Settings.RefreshToken, App.ClientSecret, _socketToken })
            result = Secrets.Redact(result, secret);
        return result;
    }

    public override bool SendTestEvent(string kind)
    {
        if (!TreatStreamEventMapper.Samples.TryGetValue(kind, out var sample)) return false;
        using var doc = JsonDocument.Parse(sample);
        return TreatStreamEventMapper.Map(doc.RootElement, isTest: true) is { } e && Publish(e);
    }

    /// <summary>Handles one <c>realTimeTreat</c> argument (also used by tests).</summary>
    internal bool HandleTreat(JsonElement payload) => TreatStreamEventMapper.Map(payload) is { } e && Publish(e);

    /// <summary>
    /// Signs in through the browser: listens on <see cref="RedirectUri"/>, opens TreatStream's authorize page with
    /// <paramref name="openBrowser"/>, exchanges the code and saves the tokens. Throws with a readable message on failure.
    /// </summary>
    public async Task SignInAsync(Action<string> openBrowser, CancellationToken ct = default)
    {
        var (clientId, secret) = (App.ClientId, App.ClientSecret);
        if (clientId.Length == 0 || secret.Length == 0)
            throw new InvalidOperationException(HasBuiltInApp
                ? "Enter the Client Secret of your TreatStream API app under Advanced, or clear its Client ID to use Cuebot's."
                : "This build has no TreatStream app of its own; enter the Client ID and Client Secret of your TreatStream API app under Advanced.");
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var redirect = RedirectUri;
        using var receiver = _receivers(RedirectPort);
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
            throw new InvalidOperationException("The TreatStream sign-in timed out. Try again.");
        }
        if (result.Code is not { } code) throw new InvalidOperationException(result.Error ?? "TreatStream sign-in failed.");
        try
        {
            Save(await _api.ExchangeCodeAsync(clientId, secret, redirect, code, ct).ConfigureAwait(false));
        }
        catch (TreatStreamApiException ex)
        {
            throw new InvalidOperationException(RedactSecrets(ex.Message));
        }
        Log.Info(Name, "Signed in to TreatStream");
    }

    /// <summary>Forgets the tokens (and disconnects).</summary>
    public async Task SignOutAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        Settings.AccessToken = "";
        Settings.RefreshToken = "";
        Settings.AccessTokenExpiresAt = null;
        SaveSettings();
        SetStatus(ConnectionState.Disconnected, "Signed out");
    }

    private void Save(TreatStreamTokens tokens)
    {
        Settings.AccessToken = tokens.AccessToken;
        Settings.RefreshToken = tokens.RefreshToken;
        Settings.AccessTokenExpiresAt = _time.GetUtcNow() + tokens.ExpiresIn;
        SaveSettings();
    }

    /// <summary>Refreshes the access token when it is about to expire, then gets a socket token.</summary>
    private protected override async Task PrepareAsync(CancellationToken ct)
    {
        var app = App;
        var clientId = app.ClientId;
        if (clientId.Length == 0) throw new InvalidOperationException("Enter the Client ID of your TreatStream API app under Advanced first.");
        if (Settings.AccessTokenExpiresAt is { } expires && expires - _time.GetUtcNow() < RefreshWindow
            && Settings.RefreshToken.Length > 0 && app.ClientSecret.Length > 0)
        {
            try
            {
                Save(await _api.RefreshAsync(clientId, app.ClientSecret, Settings.RefreshToken, ct).ConfigureAwait(false));
                Log.Info(Name, "Refreshed the TreatStream access token");
            }
            catch (TreatStreamApiException ex)
            {
                Log.Warn(Name, $"Could not refresh the access token: {RedactSecrets(ex.Message)}");
            }
        }
        try
        {
            _socketToken = await _api.GetSocketTokenAsync(clientId, Settings.AccessToken.Trim(), ct).ConfigureAwait(false);
        }
        catch (TreatStreamApiException ex) when (ex.Status is 400 or 401 or 403)
        {
            throw new InvalidOperationException("TreatStream rejected the access token. Sign in again.");
        }
        Bus.Publish(new BotEvent(TreatStreamEventTypes.Authenticated, new Dictionary<string, object?> { ["service"] = Name }));
    }

    private protected override async Task RunAsync(CancellationToken ct)
    {
        var token = _socketToken;
        var client = new SocketIoClient(_transports, _timings, Log, Name)
        {
            EngineIoVersion = 3,
            Redact = RedactSecrets,
        };
        client.Connected += _ => OnConnected("Connected to TreatStream");
        client.Disconnected += reason => OnDisconnected(reason, fatal: reason.Contains("refused", StringComparison.Ordinal));
        client.EventReceived += (name, args) =>
        {
            if (name == "realTimeTreat" && args.Count > 0) HandleTreat(args[0]);
        };
        await client.RunAsync(eio => SocketIoFraming.Url(_socketServer, eio, new Dictionary<string, string> { ["token"] = token }), ct)
            .ConfigureAwait(false);
    }
}
