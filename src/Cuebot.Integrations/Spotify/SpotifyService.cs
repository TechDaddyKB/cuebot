using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Cuebot.Core;
using Cuebot.Core.Auth;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Integrations.Spotify;

/// <summary>A song put in the Spotify queue.</summary>
public sealed record SpotifyQueueResult(SpotifyTrack Track, bool FoundBySearch);

/// <summary>
/// Spotify through the streamer's own Spotify app: PKCE sign-in on a loopback redirect, token refresh, "what is playing"
/// polling (honouring 429 Retry-After), and the player commands song requests need (queue, skip, previous, pause, play,
/// volume). Raises its events on background threads.
/// </summary>
public sealed class SpotifyService : IConnectable, IAsyncDisposable
{
    public const string Category = "Spotify";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ErrorWait = TimeSpan.FromSeconds(15);

    private readonly JsonFile<IntegrationsSettings> _settings;
    private readonly AppLog _log;
    private readonly HttpClient _http;
    private readonly SpotifyEndpoints _endpoints;
    private readonly SpotifyAuth _auth;
    private readonly TimeProvider _time;
    private readonly Func<int, LoopbackReceiver> _receiver;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _pollCts;
    private Task? _poll;
    private SpotifyPlayback? _playback;
    private ConnectionState _state = ConnectionState.Disconnected;
    private string _status = "";
    private string? _lastPollError;

    public SpotifyService(JsonFile<IntegrationsSettings> settings, AppLog log, HttpClient http, SpotifyEndpoints? endpoints = null,
        TimeProvider? time = null, Func<int, LoopbackReceiver>? receiver = null)
    {
        _settings = settings;
        _log = log;
        _http = http;
        _endpoints = endpoints ?? SpotifyEndpoints.Production;
        _auth = new SpotifyAuth(http, _endpoints);
        _time = time ?? TimeProvider.System;
        _receiver = receiver ?? (port => new LoopbackReceiver("Spotify", port, SpotifyAuth.CallbackPath));
        _status = IdleStatus();
    }

    public SpotifySettings Settings => _settings.Value.Spotify;

    public string Name => "Spotify";
    public ConnectionState State { get { lock (_gate) return _state; } }
    public string StatusMessage { get { lock (_gate) return _status; } }
    public bool IsSignedIn => Settings.IsSignedIn;
    public bool IsPolling => _poll is { IsCompleted: false };

    /// <summary>The redirect address to paste into the Spotify app.</summary>
    public string RedirectUri => SpotifyAuth.RedirectUri(Settings.RedirectPort);

    /// <summary>What the account played at the last check (null: nothing, or not signed in).</summary>
    public SpotifyPlayback? Playback { get { lock (_gate) return _playback; } }

    public event Action? StatusChanged;

    /// <summary>The playing song or play/pause state changed.</summary>
    public event Action? PlaybackChanged;

    public void SaveSettings() => _settings.ScheduleSave();

    public Task StartAsync(CancellationToken ct = default)
    {
        if (IsSignedIn) StartPolling();
        return Task.CompletedTask;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (!IsSignedIn) throw new SpotifyException(Settings.ClientId.Trim().Length == 0
            ? "Set up Spotify first: paste your Spotify app's Client ID on the Music page and click Sign in."
            : "Click Sign in on the Music page first.");
        StartPolling();
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        await StopPollingAsync().ConfigureAwait(false);
        SetStatus(ConnectionState.Disconnected, IsSignedIn ? "Not checking what plays (signed in)." : IdleStatus());
    }

    // ---------------------------------------------------------------- sign-in

    /// <summary>
    /// Opens Spotify's consent page (through <paramref name="openBrowser"/>) and waits up to five minutes for the
    /// redirect on http://127.0.0.1:PORT/callback, then stores the tokens and starts checking what plays.
    /// </summary>
    public async Task SignInAsync(Action<Uri> openBrowser, CancellationToken ct = default)
    {
        var clientId = Settings.ClientId.Trim();
        if (clientId.Length == 0) throw new SpotifyException("Paste your Spotify app's Client ID first.");
        if (!SpotifyAuth.LooksLikeClientId(clientId))
            throw new SpotifyException("That does not look like a Client ID. Copy the Client ID (32 letters and numbers) from your app's settings on developer.spotify.com.");
        var port = Settings.RedirectPort;
        LoopbackReceiver receiver;
        try
        {
            receiver = _receiver(port);
        }
        catch (SocketException)
        {
            throw new SpotifyException($"Another program already uses port {port.ToString(CultureInfo.InvariantCulture)}. Pick another port on the Music page " +
                "and change the redirect address in your Spotify app to match.");
        }
        using (receiver)
        {
            var verifier = Pkce.CreateVerifier();
            var state = Pkce.CreateState();
            SetStatus(ConnectionState.Connecting, "Waiting for you to allow Cuebot in the browser…");
            openBrowser(SpotifyAuth.AuthorizeUri(_endpoints, clientId, receiver.RedirectUri, Pkce.Challenge(verifier), state));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            IReadOnlyDictionary<string, string> result;
            try
            {
                result = await receiver.WaitForRedirectAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SetStatus(ConnectionState.Disconnected, IdleStatus());
                if (ct.IsCancellationRequested) throw;
                throw new SpotifyException("Sign-in took too long. Click Sign in to try again.");
            }
            try
            {
                if (result.GetValueOrDefault("state") != state)
                    throw new SpotifyException("The sign-in answer did not match. Click Sign in to try again.");
                if (result.TryGetValue("error", out var error))
                    throw new SpotifyException(error == "access_denied" ? "Sign-in was cancelled in the browser." : $"Spotify sign-in failed ({error}).");
                var code = result.GetValueOrDefault("code") ?? "";
                var tokens = await _auth.ExchangeCodeAsync(clientId, code, verifier, receiver.RedirectUri, ct).ConfigureAwait(false);
                Store(tokens, clientId);
            }
            catch
            {
                SetStatus(ConnectionState.Disconnected, IdleStatus());
                throw;
            }
        }
        _log.Info(Category, "Signed in to Spotify");
        SetStatus(ConnectionState.Connected, "Signed in to Spotify.");
        StartPolling();
    }

    /// <summary>Forgets the tokens (Spotify has no revoke endpoint; the user can remove the app under Manage apps).</summary>
    public async Task SignOutAsync()
    {
        await StopPollingAsync().ConfigureAwait(false);
        Settings.ClearTokens();
        SaveSettings();
        _log.Info(Category, "Signed out of Spotify");
        SetPlayback(null);
        SetStatus(ConnectionState.Disconnected, IdleStatus());
    }

    private void Store(SpotifyTokens tokens, string clientId)
    {
        var s = Settings;
        s.AccessToken = tokens.AccessToken;
        if (tokens.RefreshToken.Length > 0) s.RefreshToken = tokens.RefreshToken;
        s.AccessTokenExpires = _time.GetUtcNow() + tokens.ExpiresIn;
        s.TokenClientId = clientId;
        SaveSettings();
    }

    /// <summary>A valid access token, refreshed when it is about to run out (or when <paramref name="force"/>).</summary>
    internal async Task<string> AccessTokenAsync(bool force, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var s = Settings;
            if (!s.IsSignedIn) throw new SpotifySignInRequiredException("Sign in to Spotify first on Cuebot's Music page.", "not_signed_in");
            if (!force && s.AccessToken.Length > 0 && s.AccessTokenExpires - RefreshMargin > _time.GetUtcNow()) return s.AccessToken;
            if (s.RefreshToken.Length == 0) throw new SpotifySignInRequiredException(SpotifyErrors.SignInAgain);
            var clientId = s.TokenClientId.Length > 0 ? s.TokenClientId : s.ClientId.Trim();
            try
            {
                Store(await _auth.RefreshAsync(clientId, s.RefreshToken, ct).ConfigureAwait(false), clientId);
            }
            catch (SpotifySignInRequiredException ex)
            {
                s.ClearTokens();
                SaveSettings();
                _log.Warn(Category, "The Spotify sign-in ran out or was removed; sign in again on the Music page.");
                SetPlayback(null);
                SetStatus(ConnectionState.Error, ex.Message);
                throw;
            }
            return s.AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // ---------------------------------------------------------------- Web API

    /// <summary>Sends a request with the access token (refreshing once on 401). Returns the status and body of a 2xx answer.</summary>
    private async Task<(int Status, string Body)> SendAsync(HttpMethod method, string path, bool player, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await AccessTokenAsync(force: attempt > 0, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, new Uri(_endpoints.Api, path));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (method != HttpMethod.Get) request.Content = new ByteArrayContent([]);   // Spotify wants a Content-Length
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == (int)HttpStatusCode.Unauthorized && attempt == 0) continue;
            if (response.IsSuccessStatusCode) return (status, body);
            throw SpotifyErrors.From(status, body, RetryAfter(response), player);
        }
    }

    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } retry)
        {
            if (retry.Delta is { } delta) return delta;
            if (retry.Date is { } date) return date - DateTimeOffset.UtcNow is { } d && d > TimeSpan.Zero ? d : TimeSpan.Zero;
        }
        return null;
    }

    public async Task<SpotifyPlayback?> GetCurrentlyPlayingAsync(CancellationToken ct = default)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "me/player/currently-playing?additional_types=episode", false, ct).ConfigureAwait(false);
        return status == (int)HttpStatusCode.NoContent ? null : SpotifyJson.ParseCurrentlyPlaying(body);
    }

    /// <summary>
    /// Adds a song to the queue: a Spotify track link or URI, or words to search for (the first match, like typing them
    /// into Spotify's search).
    /// </summary>
    public async Task<SpotifyQueueResult> AddToQueueAsync(string request, CancellationToken ct = default)
    {
        var text = request.Trim();
        if (text.Length == 0) throw new SpotifyException("Say which song to add, for example a song name or a Spotify song link.");
        SpotifyTrack track;
        var bySearch = false;
        switch (SpotifyLinks.Parse(text, out var id))
        {
            case SpotifyLinkKind.Track:
                track = await TrackDetailsAsync(id, ct).ConfigureAwait(false);
                break;
            case SpotifyLinkKind.OtherLink:
                throw new SpotifyException("That link is not a song. Send a song link (open.spotify.com/track/…) or the song's name.");
            default:
                var (_, body) = await SendAsync(HttpMethod.Get, "search?type=track&limit=1&q=" + Uri.EscapeDataString(text), false, ct).ConfigureAwait(false);
                track = SpotifyJson.ParseSearch(body) ?? throw new SpotifyException($"Spotify found no song for \"{text}\".", 404, "NOT_FOUND");
                bySearch = true;
                break;
        }
        await SendAsync(HttpMethod.Post, "me/player/queue?uri=" + Uri.EscapeDataString(track.Uri), true, ct).ConfigureAwait(false);
        _log.Info(Category, $"Added to the Spotify queue: {track.Artist} - {track.Title}");
        return new SpotifyQueueResult(track, bySearch);
    }

    /// <summary>The track's name and artist; a bare URI when Spotify will not say (the queue still takes it).</summary>
    private async Task<SpotifyTrack> TrackDetailsAsync(string id, CancellationToken ct)
    {
        var uri = "spotify:track:" + id;
        try
        {
            var (_, body) = await SendAsync(HttpMethod.Get, "tracks/" + id, false, ct).ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var track = SpotifyJson.ParseTrack(doc.RootElement);
            return track with { Uri = track.Uri.Length > 0 ? track.Uri : uri, Id = id };
        }
        catch (SpotifyException ex) when (ex is not SpotifySignInRequiredException && ex.Status is 403 or 404 or 400)
        {
            if (ex.Status == 404) throw new SpotifyException("Spotify has no song with that link.", 404, "NOT_FOUND");
            return new SpotifyTrack(uri, id, "a Spotify song", "", "", "https://open.spotify.com/track/" + id, "", null);
        }
    }

    public Task NextAsync(CancellationToken ct = default) => PlayerAsync(HttpMethod.Post, "me/player/next", ct);

    public Task PreviousAsync(CancellationToken ct = default) => PlayerAsync(HttpMethod.Post, "me/player/previous", ct);

    public Task PauseAsync(CancellationToken ct = default) => PlayerAsync(HttpMethod.Put, "me/player/pause", ct);

    public Task ResumeAsync(CancellationToken ct = default) => PlayerAsync(HttpMethod.Put, "me/player/play", ct);

    /// <summary>Pauses when it plays, plays when it is paused. Returns true when it now plays.</summary>
    public async Task<bool> TogglePlayAsync(CancellationToken ct = default)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "me/player", true, ct).ConfigureAwait(false);
        if (status == (int)HttpStatusCode.NoContent) throw new SpotifyException(SpotifyErrors.NoDevice, 404, "NO_ACTIVE_DEVICE");
        var playing = SpotifyJson.ParsePlayer(body).IsPlaying;
        if (playing) await PauseAsync(ct).ConfigureAwait(false);
        else await ResumeAsync(ct).ConfigureAwait(false);
        return !playing;
    }

    /// <summary>Sets the volume of the device Spotify plays on, 0–100.</summary>
    public Task SetVolumeAsync(int percent, CancellationToken ct = default) =>
        PlayerAsync(HttpMethod.Put, "me/player/volume?volume_percent=" + Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture), ct);

    private async Task PlayerAsync(HttpMethod method, string path, CancellationToken ct)
    {
        await SendAsync(method, path, true, ct).ConfigureAwait(false);
        WakePoll();
    }

    // ---------------------------------------------------------------- polling

    private void StartPolling()
    {
        lock (_gate)
        {
            if (_poll is { IsCompleted: false }) return;
            _pollCts = new CancellationTokenSource();
            var token = _pollCts.Token;
            _poll = Task.Run(() => PollLoopAsync(token), CancellationToken.None);
        }
    }

    private async Task StopPollingAsync()
    {
        Task? poll;
        lock (_gate)
        {
            _pollCts?.Cancel();
            poll = _poll;
            _poll = null;
        }
        if (poll is not null)
        {
            try { await poll.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        lock (_gate)
        {
            _pollCts?.Dispose();
            _pollCts = null;
        }
    }

    /// <summary>Checks again soon (after a skip, the song changes right away).</summary>
    internal void WakePoll()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var wait = TimeSpan.FromSeconds(Math.Clamp(Settings.PollSeconds, 2, 60));
            try
            {
                SetPlayback(await GetCurrentlyPlayingAsync(ct).ConfigureAwait(false));
                _lastPollError = null;
                if (State != ConnectionState.Connected) SetStatus(ConnectionState.Connected, "Signed in to Spotify.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SpotifySignInRequiredException ex)
            {
                SetPlayback(null);
                SetStatus(ConnectionState.Error, ex.Message);
                return;
            }
            catch (SpotifyException ex) when (ex.RetryAfter is { } retry)
            {
                wait = retry > wait ? retry : wait;
                _log.Debug(Category, $"Spotify asked Cuebot to wait {retry.TotalSeconds:0} s before checking again");
            }
            catch (Exception ex) when (ex is SpotifyException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            {
                var message = ex is SpotifyException ? ex.Message : "Cannot reach Spotify right now. Cuebot keeps trying.";
                if (message != _lastPollError) _log.Warn(Category, $"Could not check what Spotify plays: {(ex is SpotifyException ? ex.Message : ex.GetType().Name + ": " + ex.Message)}");
                _lastPollError = message;
                if (ex is not SpotifyException) SetStatus(ConnectionState.Error, message);
                wait = wait > ErrorWait ? wait : ErrorWait;
            }
            try
            {
                await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void SetPlayback(SpotifyPlayback? playback)
    {
        bool changed;
        lock (_gate)
        {
            changed = _playback?.IsPlaying != playback?.IsPlaying || _playback?.Track != playback?.Track;
            _playback = playback;
        }
        if (changed) PlaybackChanged?.Invoke();
    }

    private string IdleStatus() => Settings.ClientId.Trim().Length == 0
        ? "Not set up. Paste your Spotify app's Client ID, then sign in."
        : Settings.IsSignedIn ? "Signed in to Spotify." : "Click Sign in to connect your Spotify account.";

    private void SetStatus(ConnectionState state, string message)
    {
        lock (_gate)
        {
            if (_state == state && _status == message) return;
            _state = state;
            _status = message;
        }
        StatusChanged?.Invoke();
    }

    /// <summary>Re-reads the idle status line after the Client ID changed.</summary>
    public void RefreshStatus()
    {
        if (State is ConnectionState.Disconnected) SetStatus(ConnectionState.Disconnected, IdleStatus());
    }

    public async ValueTask DisposeAsync()
    {
        await StopPollingAsync().ConfigureAwait(false);
        _tokenLock.Dispose();
        _wake.Dispose();
    }
}
