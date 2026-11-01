using System.Net;
using System.Net.Http.Headers;
using Cuebot.Core.Actions;
using Cuebot.Core.Auth;
using Cuebot.Core;
using Cuebot.Core.Storage;
using Cuebot.Integrations.Spotify;
using Cuebot.Integrations.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Integrations.Tests;

public sealed class SpotifyAuthTests
{
    [Fact]
    public void Builds_the_pkce_sign_in_address()
    {
        // RFC 7636 appendix B: the S256 challenge of this verifier.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

        var uri = SpotifyAuth.AuthorizeUri(SpotifyEndpoints.Production, "0123456789abcdef0123456789abcdef", SpotifyAuth.RedirectUri(43117),
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "st4te");
        Assert.Equal("https://accounts.spotify.com/authorize", uri.GetLeftPart(UriPartial.Path));
        var query = new RecordedRequest(HttpMethod.Get, uri, null, "");
        Assert.Equal("0123456789abcdef0123456789abcdef", query.Query("client_id"));
        Assert.Equal("code", query.Query("response_type"));
        Assert.Equal("http://127.0.0.1:43117/callback", query.Query("redirect_uri"));
        Assert.Equal("S256", query.Query("code_challenge_method"));
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", query.Query("code_challenge"));
        Assert.Equal("st4te", query.Query("state"));
        Assert.Equal("user-read-currently-playing user-read-playback-state user-modify-playback-state", query.Query("scope"));
        Assert.DoesNotContain("client_secret", uri.Query);
    }

    [Fact]
    public void Checks_client_ids_and_redirects()
    {
        Assert.True(SpotifyAuth.LooksLikeClientId(" 0123456789abcdef0123456789abcdef "));
        Assert.False(SpotifyAuth.LooksLikeClientId("https://developer.spotify.com/dashboard"));
        Assert.False(SpotifyAuth.LooksLikeClientId(""));
        Assert.Equal("http://127.0.0.1:5000/callback", SpotifyAuth.RedirectUri(5000));
    }

    [Fact]
    public async Task Loopback_receiver_takes_the_spotify_callback_path_on_a_fixed_port()
    {
        using var receiver = new LoopbackReceiver("Spotify", 0, SpotifyAuth.CallbackPath);
        Assert.Matches(@"^http://127\.0\.0\.1:\d+/callback$", receiver.RedirectUri);
        var wait = receiver.WaitForRedirectAsync(CancellationToken.None);
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(receiver.RedirectUri.Replace("/callback", "/favicon.ico"))).StatusCode);
        var ok = await http.GetAsync(receiver.RedirectUri + "?code=abc&state=xyz");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var query = await wait;
        Assert.Equal("abc", query["code"]);
        Assert.Equal("xyz", query["state"]);
    }
}

public sealed class SpotifyParsingTests
{
    internal const string TrackJson = """
        {"id":"4cOdK2wGLETKBW3PvgPWqT","uri":"spotify:track:4cOdK2wGLETKBW3PvgPWqT","name":"Never Gonna Give You Up","duration_ms":213573,
         "artists":[{"name":"Rick Astley"},{"name":"Guest"}],"album":{"name":"Whenever You Need Somebody","images":[{"url":"https://i.scdn.co/image/big","width":640}]},
         "external_urls":{"spotify":"https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT"}}
        """;

    [Fact]
    public void Reads_what_plays()
    {
        var playback = SpotifyJson.ParseCurrentlyPlaying($$"""{"is_playing":true,"progress_ms":1000,"currently_playing_type":"track","item":{{TrackJson}}}""");
        Assert.NotNull(playback);
        Assert.True(playback.IsPlaying);
        var song = playback.ToSession().Song;
        Assert.Equal("Never Gonna Give You Up", song.Title);
        Assert.Equal("Rick Astley, Guest", song.Artist);
        Assert.Equal("Whenever You Need Somebody", song.Album);
        Assert.Equal("https://i.scdn.co/image/big", song.ArtUrl);
        Assert.Equal("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT", song.Link);
        Assert.Equal("3:33", song.DurationText);
        Assert.True(playback.ToSession().FromSpotify);
        Assert.Equal("Spotify", playback.ToSession().Name);

        var episode = SpotifyJson.ParseCurrentlyPlaying("""
            {"is_playing":false,"currently_playing_type":"episode","item":{"uri":"spotify:episode:1","name":"Ep 1","duration_ms":60000,
             "images":[{"url":"https://img/ep"}],"show":{"name":"The Podcast"}}}
            """)!;
        Assert.False(episode.IsPlaying);
        Assert.Equal("The Podcast", episode.Track!.Artist);
        Assert.Equal("https://img/ep", episode.Track.ArtUrl);

        Assert.Null(SpotifyJson.ParseCurrentlyPlaying(""));
        Assert.Null(SpotifyJson.ParseCurrentlyPlaying("""{"is_playing":true,"currently_playing_type":"ad","item":null}"""));
    }

    [Theory]
    [InlineData("spotify:track:4cOdK2wGLETKBW3PvgPWqT", SpotifyLinkKind.Track)]
    [InlineData("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT?si=abc123", SpotifyLinkKind.Track)]
    [InlineData("open.spotify.com/intl-de/track/4cOdK2wGLETKBW3PvgPWqT", SpotifyLinkKind.Track)]
    [InlineData("<https://open.spotify.com/embed/track/4cOdK2wGLETKBW3PvgPWqT>", SpotifyLinkKind.Track)]
    [InlineData("https://open.spotify.com/album/1A2GTWGtFfWp7KSQTwWOyo", SpotifyLinkKind.OtherLink)]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", SpotifyLinkKind.OtherLink)]
    [InlineData("https://spotify.link/abcdef", SpotifyLinkKind.OtherLink)]
    [InlineData("never gonna give you up", SpotifyLinkKind.NotALink)]
    public void Tells_links_from_song_names(string text, SpotifyLinkKind kind)
    {
        Assert.Equal(kind, SpotifyLinks.Parse(text, out var id));
        Assert.Equal(kind == SpotifyLinkKind.Track ? "4cOdK2wGLETKBW3PvgPWqT" : "", id);
    }

    [Fact]
    public void Words_errors_plainly()
    {
        Assert.Equal(SpotifyErrors.NoDevice, SpotifyErrors.From(404,
            """{"error":{"status":404,"message":"Player command failed: No active device found","reason":"NO_ACTIVE_DEVICE"}}""", null, true).Message);
        Assert.Equal(SpotifyErrors.NeedsPremium, SpotifyErrors.From(403,
            """{"error":{"status":403,"message":"Player command failed: Premium required","reason":"PREMIUM_REQUIRED"}}""", null, true).Message);
        Assert.Contains("User Management", SpotifyErrors.From(403,
            """{"error":{"status":403,"message":"Check settings on developer.spotify.com/dashboard, the user may not be registered."}}""", null, false).Message);
        Assert.Contains("volume", SpotifyErrors.From(403,
            """{"error":{"status":403,"message":"Cannot control device volume","reason":"VOLUME_CONTROL_DISALLOW"}}""", null, true).Message);
        var busy = SpotifyErrors.From(429, "", TimeSpan.FromSeconds(7), false);
        Assert.Equal(TimeSpan.FromSeconds(7), busy.RetryAfter);
        Assert.Contains("7 seconds", busy.Message);
        Assert.IsType<SpotifySignInRequiredException>(SpotifyErrors.From(401, "", null, false));
        Assert.Contains("trouble", SpotifyErrors.From(502, "", null, false).Message);
    }
}

public sealed class SpotifyServiceTests : IDisposable
{
    private const string ClientId = "0123456789abcdef0123456789abcdef";
    private readonly TestEnvironment _env = new();
    private readonly List<Func<RecordedRequest, HttpResponseMessage?>> _routes = [];
    private readonly FakeHttpHandler _http;
    private readonly SpotifyService _spotify;
    private readonly FakeClock _clock = new(DateTimeOffset.Parse("2026-10-31T12:00:00Z"));

    public SpotifyServiceTests()
    {
        _http = new FakeHttpHandler(r =>
        {
            lock (_routes)
                foreach (var route in _routes)
                    if (route(r) is { } response) return response;
            return FakeHttpHandler.Json("""{"error":{"status":500,"message":"no route"}}""", HttpStatusCode.InternalServerError);
        });
        var endpoints = new SpotifyEndpoints(new Uri("https://accounts.test/"), new Uri("https://api.test/v1/"));
        _spotify = new SpotifyService(_env.Settings, _env.Log, new HttpClient(_http), endpoints, _clock);
    }

    private void Route(Func<RecordedRequest, HttpResponseMessage?> route)
    {
        lock (_routes) _routes.Insert(0, route);
    }

    private void SignedIn(string access = "old-access", bool expired = false)
    {
        var s = _spotify.Settings;
        s.ClientId = ClientId;
        s.TokenClientId = ClientId;
        s.AccessToken = access;
        s.RefreshToken = "refresh-1";
        s.AccessTokenExpires = _clock.GetUtcNow() + (expired ? TimeSpan.FromSeconds(-1) : TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Refreshes_an_expired_token_and_keeps_the_new_refresh_token()
    {
        SignedIn(expired: true);
        Route(r => r.Path == "/api/token"
            ? FakeHttpHandler.Json("""{"access_token":"new-access","token_type":"Bearer","expires_in":3600,"refresh_token":"refresh-2","scope":"x"}""")
            : null);
        Route(r => r.Path == "/v1/me/player/currently-playing"
            ? FakeHttpHandler.Json($$"""{"is_playing":true,"item":{{SpotifyParsingTests.TrackJson}}}""")
            : null);

        var playback = await _spotify.GetCurrentlyPlayingAsync();
        Assert.Equal("Never Gonna Give You Up", playback!.Track!.Title);
        var token = Assert.Single(_http.To("/api/token"));
        Assert.Equal("refresh_token", token.Form("grant_type"));
        Assert.Equal("refresh-1", token.Form("refresh_token"));
        Assert.Equal(ClientId, token.Form("client_id"));
        Assert.Equal("", token.Form("client_secret"));
        Assert.Equal("new-access", _http.To("/v1/me/player/currently-playing").Single().Bearer);
        Assert.Equal("additional_types=episode", _http.To("/v1/me/player/currently-playing").Single().Uri.Query.TrimStart('?'));
        Assert.Equal("refresh-2", _spotify.Settings.RefreshToken);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromHours(1), _spotify.Settings.AccessTokenExpires);

        // A valid token is reused; a 401 refreshes once and retries.
        Route(r => r.Path == "/v1/me/player/currently-playing" && r.Bearer == "new-access"
            ? FakeHttpHandler.Json("""{"error":{"status":401,"message":"The access token expired"}}""", HttpStatusCode.Unauthorized)
            : null);
        Route(r => r.Path == "/api/token" ? FakeHttpHandler.Json("""{"access_token":"third","expires_in":3600}""") : null);
        Assert.NotNull(await _spotify.GetCurrentlyPlayingAsync());
        Assert.Equal("refresh-2", _spotify.Settings.RefreshToken);   // no new refresh token sent: the old one stays
        Assert.Equal("third", _http.Requests[^1].Bearer);
    }

    [Fact]
    public async Task An_expired_sign_in_asks_to_sign_in_again()
    {
        SignedIn(expired: true);
        Route(r => r.Path == "/api/token"
            ? FakeHttpHandler.Json("""{"error":"invalid_grant","error_description":"Refresh token revoked"}""", HttpStatusCode.BadRequest)
            : null);
        var ex = await Assert.ThrowsAsync<SpotifySignInRequiredException>(() => _spotify.GetCurrentlyPlayingAsync());
        Assert.Contains("Sign in", ex.Message);
        Assert.False(_spotify.IsSignedIn);
        Assert.Equal("", _spotify.Settings.RefreshToken);
        Assert.Equal(ConnectionState.Error, _spotify.State);
        Assert.Empty(_http.To("/v1/me/player/currently-playing"));
    }

    [Fact]
    public async Task Queues_a_search_result()
    {
        SignedIn();
        Route(r => r.Path == "/v1/search"
            ? FakeHttpHandler.Json($$$"""{"tracks":{"items":[{{{SpotifyParsingTests.TrackJson}}}],"limit":1}}""")
            : null);
        Route(r => r.Path == "/v1/me/player/queue" ? FakeHttpHandler.Empty(HttpStatusCode.OK) : null);

        var result = await _spotify.AddToQueueAsync("  never gonna give you up  ");
        Assert.True(result.FoundBySearch);
        Assert.Equal("Rick Astley, Guest", result.Track.Artist);
        var search = Assert.Single(_http.To("/v1/search"));
        Assert.Equal("never gonna give you up", search.Query("q"));
        Assert.Equal("track", search.Query("type"));
        Assert.Equal("1", search.Query("limit"));
        var queue = Assert.Single(_http.To("/v1/me/player/queue"));
        Assert.Equal(HttpMethod.Post, queue.Method);
        Assert.Equal("spotify:track:4cOdK2wGLETKBW3PvgPWqT", queue.Query("uri"));
        Assert.Equal("old-access", queue.Bearer);
    }

    [Fact]
    public async Task Queues_a_link_without_searching()
    {
        SignedIn();
        Route(r => r.Path == "/v1/tracks/4cOdK2wGLETKBW3PvgPWqT" ? FakeHttpHandler.Json(SpotifyParsingTests.TrackJson) : null);
        Route(r => r.Path == "/v1/me/player/queue" ? FakeHttpHandler.Empty() : null);
        var result = await _spotify.AddToQueueAsync("https://open.spotify.com/intl-de/track/4cOdK2wGLETKBW3PvgPWqT?si=xyz");
        Assert.False(result.FoundBySearch);
        Assert.Equal("Never Gonna Give You Up", result.Track.Title);
        Assert.Empty(_http.To("/v1/search"));
        Assert.Equal("spotify:track:4cOdK2wGLETKBW3PvgPWqT", _http.To("/v1/me/player/queue").Single().Query("uri"));

        // When Spotify will not describe the track (development-mode limits), the link is still queued.
        Route(r => r.Path.StartsWith("/v1/tracks/", StringComparison.Ordinal)
            ? FakeHttpHandler.Json("""{"error":{"status":403,"message":"Forbidden"}}""", HttpStatusCode.Forbidden)
            : null);
        var bare = await _spotify.AddToQueueAsync("spotify:track:0000000000000000000000");
        Assert.Equal("spotify:track:0000000000000000000000", bare.Track.Uri);
        Assert.Equal("a Spotify song", bare.Track.Title);

        await Assert.ThrowsAsync<SpotifyException>(() => _spotify.AddToQueueAsync("https://open.spotify.com/album/1A2GTWGtFfWp7KSQTwWOyo"));
        await Assert.ThrowsAsync<SpotifyException>(() => _spotify.AddToQueueAsync("   "));
    }

    [Fact]
    public async Task Player_errors_come_back_in_plain_words()
    {
        SignedIn();
        Route(r => r.Path == "/v1/search" ? FakeHttpHandler.Json("""{"tracks":{"items":[]}}""") : null);
        Assert.Contains("found no song", (await Assert.ThrowsAsync<SpotifyException>(() => _spotify.AddToQueueAsync("zzzz"))).Message);

        Route(r => r.Path == "/v1/me/player/next"
            ? FakeHttpHandler.Json("""{"error":{"status":404,"message":"Player command failed: No active device found","reason":"NO_ACTIVE_DEVICE"}}""", HttpStatusCode.NotFound)
            : null);
        Assert.Equal(SpotifyErrors.NoDevice, (await Assert.ThrowsAsync<SpotifyException>(() => _spotify.NextAsync())).Message);

        Route(r => r.Path == "/v1/me/player/pause"
            ? FakeHttpHandler.Json("""{"error":{"status":403,"message":"Player command failed: Premium required","reason":"PREMIUM_REQUIRED"}}""", HttpStatusCode.Forbidden)
            : null);
        Assert.Equal(SpotifyErrors.NeedsPremium, (await Assert.ThrowsAsync<SpotifyException>(() => _spotify.PauseAsync())).Message);

        Route(r => r.Path == "/v1/me/player/volume"
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12)) } }
            : null);
        var busy = await Assert.ThrowsAsync<SpotifyException>(() => _spotify.SetVolumeAsync(150));
        Assert.Equal(TimeSpan.FromSeconds(12), busy.RetryAfter);
        Assert.Equal("100", _http.To("/v1/me/player/volume").Single().Query("volume_percent"));   // clamped

        Route(r => r.Path == "/v1/me/player" ? FakeHttpHandler.Json("""{"is_playing":true,"device":{"name":"PC","volume_percent":40}}""") : null);
        Route(r => r.Path == "/v1/me/player/pause" ? FakeHttpHandler.Empty() : null);
        Assert.False(await _spotify.TogglePlayAsync());
        Assert.Equal(HttpMethod.Put, _http.To("/v1/me/player/pause").Last().Method);
    }

    [Fact]
    public async Task Polling_honours_retry_after_and_reports_playback()
    {
        SignedIn();
        _spotify.Settings.PollSeconds = 2;
        var calls = 0;
        Route(r =>
        {
            if (r.Path != "/v1/me/player/currently-playing") return null;
            return Interlocked.Increment(ref calls) == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)) } }
                : FakeHttpHandler.Json($$"""{"is_playing":true,"item":{{SpotifyParsingTests.TrackJson}}}""");
        });
        await _spotify.StartAsync();
        await Wait.UntilAsync(() => calls == 1, "first poll");
        await Task.Delay(300);
        Assert.Equal(1, calls);   // waits as asked instead of hammering
        _spotify.WakePoll();      // e.g. after a skip
        await Wait.UntilAsync(() => _spotify.Playback?.Track?.Title == "Never Gonna Give You Up", "playback");
        Assert.Equal(ConnectionState.Connected, _spotify.State);
        await _spotify.DisconnectAsync();
        Assert.False(_spotify.IsPolling);
    }

    [Fact]
    public async Task Sign_in_needs_a_client_id_and_a_free_port()
    {
        await Assert.ThrowsAsync<SpotifyException>(() => _spotify.SignInAsync(_ => { }));
        _spotify.Settings.ClientId = "not a client id";
        Assert.Contains("Client ID", (await Assert.ThrowsAsync<SpotifyException>(() => _spotify.SignInAsync(_ => { }))).Message);

        using var busy = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        _spotify.Settings.ClientId = ClientId;
        _spotify.Settings.RedirectPort = ((IPEndPoint)busy.LocalEndpoint).Port;
        Assert.Contains("already uses port", (await Assert.ThrowsAsync<SpotifyException>(() => _spotify.SignInAsync(_ => { }))).Message);
    }

    [Fact]
    public async Task Signs_in_through_the_loopback_redirect()
    {
        _spotify.Settings.ClientId = ClientId;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            _spotify.Settings.RedirectPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        Route(r => r.Path == "/api/token"
            ? FakeHttpHandler.Json("""{"access_token":"a1","expires_in":3600,"refresh_token":"r1","scope":"user-read-currently-playing"}""")
            : null);
        Route(r => r.Path == "/v1/me/player/currently-playing" ? FakeHttpHandler.Empty() : null);
        Uri? opened = null;
        using var browser = new HttpClient();
        await _spotify.SignInAsync(uri =>
        {
            opened = uri;
            var q = new RecordedRequest(HttpMethod.Get, uri, null, "");
            // The "browser" follows Spotify's redirect back to Cuebot.
            _ = browser.GetAsync($"{q.Query("redirect_uri")}?code=the-code&state={Uri.EscapeDataString(q.Query("state"))}");
        });
        Assert.NotNull(opened);
        var exchange = Assert.Single(_http.To("/api/token"));
        Assert.Equal("authorization_code", exchange.Form("grant_type"));
        Assert.Equal("the-code", exchange.Form("code"));
        Assert.Equal(ClientId, exchange.Form("client_id"));
        Assert.Equal(_spotify.RedirectUri, exchange.Form("redirect_uri"));
        Assert.Equal(new RecordedRequest(HttpMethod.Get, opened!, null, "").Query("code_challenge"), Pkce.Challenge(exchange.Form("code_verifier")));
        Assert.True(_spotify.IsSignedIn);
        Assert.Equal("r1", _spotify.Settings.RefreshToken);
        Assert.Equal(ConnectionState.Connected, _spotify.State);
        await _spotify.SignOutAsync();
        Assert.False(_spotify.IsSignedIn);
        Assert.Equal("", _spotify.Settings.AccessToken);
    }

    public void Dispose()
    {
        _spotify.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _env.Dispose();
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>The song request step through the real engine: never fails, takes the Added / Not added branch.</summary>
public sealed class SpotifyStepTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-spotify-steps-").FullName;
    private readonly ServiceProvider _services;
    private readonly Capture _added = new("test.added");
    private readonly Capture _notAdded = new("test.notAdded");

    public SpotifyStepTests()
    {
        var http = new FakeHttpHandler(r => r.Path switch
        {
            "/v1/search" => FakeHttpHandler.Json($$$"""{"tracks":{"items":[{{{SpotifyParsingTests.TrackJson}}}]}}"""),
            "/v1/me/player/queue" => FakeHttpHandler.Json(
                """{"error":{"status":404,"message":"Player command failed: No active device found","reason":"NO_ACTIVE_DEVICE"}}""", HttpStatusCode.NotFound),
            _ => FakeHttpHandler.Empty(),
        });
        var collection = new ServiceCollection().AddCuebotCore(new AppPaths(_dir)).AddIntegrations();
        collection.AddSingleton(sp => new SpotifyService(sp.GetRequiredService<JsonFile<IntegrationsSettings>>(), sp.GetRequiredService<Cuebot.Core.Logging.AppLog>(),
            new HttpClient(http), new SpotifyEndpoints(new Uri("https://accounts.test/"), new Uri("https://api.test/v1/"))));
        collection.AddSingleton<ISubActionHandler>(_added);
        collection.AddSingleton<ISubActionHandler>(_notAdded);
        _services = collection.BuildServiceProvider();
    }

    private ActionDefinition Request(string song)
    {
        var step = new SubActionDefinition { Type = "spotify.addToQueue", Parameters = new() { ["song"] = song } };
        step.Branches["Added"] = [new SubActionDefinition { Type = "test.added" }];
        step.Branches["Not added"] = [new SubActionDefinition { Type = "test.notAdded" }];
        return new ActionDefinition { Name = "sr", SubActions = [step] };
    }

    [Fact]
    public async Task Not_signed_in_and_no_device_take_the_not_added_branch()
    {
        var engine = _services.GetRequiredService<ActionEngine>();
        Assert.True(await engine.RunAsync(Request("never gonna give you up")));
        Assert.Null(_added.Args);
        Assert.Equal(false, _notAdded.Args!["spotify.added"]);
        Assert.Contains("Sign in", (string)_notAdded.Args["spotify.error"]!);

        var s = _services.GetRequiredService<JsonFile<IntegrationsSettings>>().Value.Spotify;
        s.ClientId = s.TokenClientId = "0123456789abcdef0123456789abcdef";
        s.AccessToken = "token";
        s.RefreshToken = "refresh";
        s.AccessTokenExpires = DateTimeOffset.UtcNow.AddHours(1);
        Assert.True(await engine.RunAsync(Request("never gonna give you up")));
        Assert.Equal(SpotifyErrors.NoDevice, _notAdded.Args!["spotify.error"]);
        Assert.Null(_added.Args);
    }

    private sealed class Capture(string typeId) : ISubActionHandler
    {
        public Dictionary<string, object?>? Args { get; private set; }
        public string TypeId => typeId;
        public string Category => "Test";
        public string DisplayName => typeId;
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            Args = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(SubActionResult.Next);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
