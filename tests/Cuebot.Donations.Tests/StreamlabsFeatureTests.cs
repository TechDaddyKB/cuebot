using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Donations.Streamlabs;
using Cuebot.Donations.Tests.Support;
using Cuebot.Donations.TreatStream;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Donations.Tests;

/// <summary>The Streamlabs REST API client against the paths and examples of dev.streamlabs.com (v2.0).</summary>
public sealed class StreamlabsApiTests
{
    private const string Access = "SL-SECRET-access-token-0123456789";
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new(StringComparer.Ordinal);
    private readonly FakeHttpHandler _http;
    private readonly StreamlabsApi _api;

    public StreamlabsApiTests()
    {
        _http = new FakeHttpHandler(r => _responses.TryGetValue($"{r.Method.Method} {r.Uri.PathAndQuery}", out var answer)
            ? FakeHttpHandler.Json(answer.Body, answer.Status)
            : FakeHttpHandler.Json("""{"error":"Not Found"}""", HttpStatusCode.NotFound));
        _api = new StreamlabsApi(new HttpClient(_http), new Uri("https://sl.test/api/v2.0/"));
    }

    private void On(string request, string body, HttpStatusCode status = HttpStatusCode.OK) => _responses[request] = (status, body);

    private RecordedRequest Last => _http.Requests[^1];

    private static JsonElement Body(RecordedRequest r) => JsonDocument.Parse(r.Body).RootElement;

    [Fact]
    public void Authorize_url_asks_for_a_code_with_every_scope_but_legacy_token()
    {
        var url = _api.AuthorizeUrl("client-1", "http://localhost:8914/callback", "st4te");
        Assert.Equal("/api/v2.0/authorize", url.AbsolutePath);
        var query = Uri.UnescapeDataString(url.Query);
        Assert.Contains("response_type=code", query);
        Assert.Contains("client_id=client-1", query);
        Assert.Contains("redirect_uri=http://localhost:8914/callback", query);
        Assert.Contains("state=st4te", query);
        Assert.Contains("scope=socket.token donations.read", query);
        Assert.Contains("mediashare.control", query);
        Assert.DoesNotContain("legacy.token", query);
    }

    [Fact]
    public async Task Token_exchange_posts_the_documented_form_fields_and_never_leaks_the_secret()
    {
        On("POST /api/v2.0/token", """{"access_token":"A1","token_type":"Bearer","refresh_token":"R1"}""");
        var tokens = await _api.ExchangeCodeAsync("client-1", "SECRET-xyz-123456", "http://localhost:8914/callback", "code-1", default);
        Assert.Equal(new StreamlabsTokens("A1", "R1"), tokens);
        Assert.Equal("application/x-www-form-urlencoded", Last.ContentType);
        foreach (var field in new[] { "grant_type=authorization_code", "client_id=client-1", "client_secret=SECRET-xyz-123456", "code=code-1" })
            Assert.Contains(field, Last.Body);

        On("POST /api/v2.0/token", """{"error":"invalid_grant","message":"code SECRET-xyz-123456 expired"}""", HttpStatusCode.BadRequest);
        var ex = await Assert.ThrowsAsync<StreamlabsApiException>(() =>
            _api.ExchangeCodeAsync("client-1", "SECRET-xyz-123456", "http://localhost:8914/callback", "code-1", default));
        Assert.DoesNotContain("SECRET-xyz-123456", ex.Message);
        Assert.Contains("refused the sign-in", ex.Message);

        // A refresh answer without a new refresh token keeps the old one.
        On("POST /api/v2.0/token", """{"access_token":"A2","token_type":"Bearer"}""");
        Assert.Equal(new StreamlabsTokens("A2", "R1"), await _api.RefreshAsync("client-1", "SECRET-xyz-123456", "http://x/callback", "R1", default));
        Assert.Contains("grant_type=refresh_token", Last.Body);
    }

    [Fact]
    public async Task Calls_use_the_bearer_header_never_a_query_token()
    {
        On("GET /api/v2.0/socket/token", """{"socket_token":"sock-1"}""");
        On("GET /api/v2.0/user", """
            {"streamlabs":{"id":3929217,"display_name":"sunnyding602"},"twitch":{"id":124287875,"display_name":"SunnyDing602","name":"sunnyding602"},
             "youtube":{"id":"UCuDKXjZv4B3F58xv_76gw","title":"runxi@streamlabs.com"},"mixer":{"id":10969035,"name":"Flyfurrysnail"}}
            """);

        Assert.Equal("sock-1", await _api.GetSocketTokenAsync(Access, default));
        var user = await _api.GetUserAsync(Access, default);
        Assert.Equal(("3929217", "sunnyding602", "sunnyding602"), (user.Id, user.DisplayName, user.TwitchLogin));
        Assert.Equal("Twitch: SunnyDing602, YouTube: runxi@streamlabs.com, mixer: Flyfurrysnail", user.LinkedText);
        Assert.All(_http.Requests, r =>
        {
            Assert.Equal("Bearer " + Access, r.Authorization);
            Assert.DoesNotContain("access_token", r.Uri.Query, StringComparison.Ordinal);
            Assert.Equal("XMLHttpRequest", r.Headers["X-Requested-With"]);
        });
    }

    [Fact]
    public async Task Donations_page_by_donation_id_and_map_the_documented_rows()
    {
        On("GET /api/v2.0/donations?limit=25&after=80179000&currency=USD", """
            {"data":[{"donation_id":"80179029","created_at":"1438576556","currency":"USD","amount":"50","name":"Thomas","message":"nice!"},
                     {"donation_id":"80179028","created_at":"1438576500","currency":"usd","amount":"13.3700000000","name":"","message":""}]}
            """);
        var rows = await _api.GetDonationsAsync(Access, 25, "80179000", null, "usd", default);
        Assert.Equal(2, rows.Count);
        Assert.Equal(("80179029", "Thomas", 50d, "USD", "nice!"), (rows[0].Id, rows[0].Name, rows[0].Amount, rows[0].Currency, rows[0].Message));
        Assert.Equal("2015-08-03T04:35:56.0000000+00:00", rows[0].CreatedAt);
        Assert.Equal(("Anonymous", 13.37, "$13.37"), (rows[1].Name, rows[1].Amount, rows[1].FormattedAmount));
    }

    [Fact]
    public async Task Create_donation_validates_before_sending()
    {
        await Assert.ThrowsAsync<StreamlabsApiException>(() => _api.CreateDonationAsync(Access, "A", "", 5, "USD", "", true, default));
        await Assert.ThrowsAsync<StreamlabsApiException>(() => _api.CreateDonationAsync(Access, new string('x', 26), "", 5, "USD", "", true, default));
        await Assert.ThrowsAsync<StreamlabsApiException>(() => _api.CreateDonationAsync(Access, "Anna", "", 5, "USD", new string('m', 255), true, default));
        Assert.Empty(_http.Requests);

        On("POST /api/v2.0/donations", """{"donation_id":"123"}""");
        Assert.Equal("123", await _api.CreateDonationAsync(Access, "Anna", "", 5, "usd", "cash at the meetup", true, default));
        var body = Body(Last);
        Assert.Equal(("Anna", "Anna", 5d, "USD", "yes"), (body.GetProperty("name").GetString(), body.GetProperty("identifier").GetString(),
            body.GetProperty("amount").GetDouble(), body.GetProperty("currency").GetString(), body.GetProperty("skip_alert").GetString()));
    }

    [Fact]
    public async Task Alerts_custom_queue_status_test_and_profiles()
    {
        On("POST /api/v2.0/alerts", """{"success":true}""");
        await _api.CreateAlertAsync(Access, "Donation", "*Anna* says hi", "", "https://x/img.gif", "", 4000, "#32C3A6", default);
        var alert = Body(Last);
        Assert.Equal("application/json", Last.ContentType);
        Assert.Equal(("donation", "*Anna* says hi", "https://x/img.gif", "4000", "#32C3A6"), (alert.GetProperty("type").GetString(),
            alert.GetProperty("message").GetString(), alert.GetProperty("image_href").GetString(), alert.GetProperty("duration").GetString(),
            alert.GetProperty("special_text_color").GetString()));
        Assert.False(alert.TryGetProperty("sound_href", out _));   // empty = the alert type's own

        foreach (var command in StreamlabsApi.AlertCommands)
        {
            On($"POST /api/v2.0/alerts/{command}", """{"success":true}""");
            await _api.AlertCommandAsync(Access, command.ToUpperInvariant(), default);
            Assert.Equal($"/api/v2.0/alerts/{command}", Last.Uri.AbsolutePath);
        }
        await Assert.ThrowsAsync<StreamlabsApiException>(() => _api.AlertCommandAsync(Access, "explode", default));

        On("GET /api/v2.0/alerts/get_status", """{"paused":true}""");
        Assert.True(await _api.GetAlertQueuePausedAsync(Access, default));

        On("POST /api/v2.0/alerts/send_test_alert", """{"success":true}""");
        await _api.SendTestAlertAsync(Access, "follow", "YouTube", default);
        Assert.Equal("youtube", Body(Last).GetProperty("platform").GetString());

        On("GET /api/v2.0/alert_profiles/get", """{"data":[{"id":"p1","name":"Default","active":true},{"id":"p2","name":"Just Chatting"}]}""");
        var profiles = await _api.GetAlertProfilesAsync(Access, default);
        Assert.Equal([new StreamlabsAlertProfile("p1", "Default", true), new StreamlabsAlertProfile("p2", "Just Chatting", false)], profiles);
    }

    [Fact]
    public async Task Points_calls_send_username_channel_and_points()
    {
        On("GET /api/v2.0/points?username=anna&channel=streamer", """{"id":1,"platform":"twitch","channel":"streamer","username":"anna","exp":0,"points":470}""");
        Assert.Equal(470, await _api.GetPointsAsync(Access, "anna", "streamer", default));

        On("POST /api/v2.0/points/subtract", """{"username":"anna","points":460}""");
        Assert.Equal(460, await _api.SubtractPointsAsync(Access, "anna", "streamer", 10, default));
        Assert.Equal(10, Body(Last).GetProperty("points").GetInt64());

        On("POST /api/v2.0/points/subtract", """{"message":"User does not have enough points"}""", HttpStatusCode.BadRequest);
        var ex = await Assert.ThrowsAsync<StreamlabsApiException>(() => _api.SubtractPointsAsync(Access, "anna", "streamer", 1000, default));
        Assert.Contains("not have enough points", ex.Message);

        On("POST /api/v2.0/points/import", """{"message":"Success"}""");
        await _api.ImportPointsAsync(Access, "streamer", new Dictionary<string, long> { ["anna"] = 25 }, default);
        Assert.Equal(25, Body(Last).GetProperty("users").GetProperty("anna").GetInt64());

        On("GET /api/v2.0/points/user_points?limit=2&page=1&sort=points&order=desc", """{"data":[{"username":"b","points":5},{"username":"a","points":50},{"username":"c","points":1}]}""");
        var board = await _api.GetPointsLeaderboardAsync(Access, 2, default);
        Assert.Equal([new StreamlabsPointsRow("a", 50), new StreamlabsPointsRow("b", 5)], board);
    }

    [Fact]
    public async Task Media_share_tries_the_other_verb_when_the_first_is_not_found()
    {
        // The reference shows PATCH for enable/disable, the scopes page PUT for all: the other one is the fallback.
        On("PUT /api/v2.0/media-share/enable-requests", """{"success":true}""");
        await _api.MediaShareAsync(Access, "enable-requests", null, default);
        Assert.Equal(["PATCH", "PUT"], _http.Requests.Select(r => r.Method.Method));

        On("PUT /api/v2.0/media-share/volume-up", """{"success":true}""");
        await _api.MediaShareAsync(Access, "volume-up", 150, default);
        Assert.Equal(100, Body(Last).GetProperty("volume").GetInt32());
        Assert.Equal(3, _http.Requests.Count);
    }

    [Fact]
    public async Task Errors_are_readable_and_never_carry_the_token()
    {
        On("POST /api/v2.0/credits/roll", $$"""{"message":"not live for {{Access}}"}""", (HttpStatusCode)422);
        var ex = await Assert.ThrowsAsync<StreamlabsApiException>(() => _api.RollCreditsAsync(Access, default));
        Assert.Equal(422, ex.Status);
        Assert.DoesNotContain(Access, ex.Message);

        On("POST /api/v2.0/wheel/spin", "{}", HttpStatusCode.TooManyRequests);
        Assert.Contains("5 requests a minute", (await Assert.ThrowsAsync<StreamlabsApiException>(() => _api.SpinWheelAsync(Access, default))).Message);
        await Assert.ThrowsAsync<StreamlabsApiException>(() => _api.EmptyJarAsync("", default));
    }

    [Fact]
    public async Task Rate_limiter_lets_the_limit_through_then_waits()
    {
        var limiter = new RequestRateLimiter(() => 2, TimeProvider.System);
        await limiter.WaitAsync(default);
        await limiter.WaitAsync(default);
        Assert.Equal(TimeSpan.Zero, limiter.LastWait);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.WaitAsync(cts.Token));
        Assert.True(limiter.LastWait > TimeSpan.FromSeconds(55));
    }

    [Fact]
    public void Top_donors_add_up_per_name_and_currency()
    {
        var rows = StreamlabsSubActions.TopDonors(
        [
            new("1", "Anna", 5, "USD", "", ""), new("2", "anna", 10, "USD", "", ""), new("3", "Bob", 12, "USD", "", ""), new("4", "Cy", 1, "USD", "", ""),
        ], 2);
        Assert.Equal([("Anna", 15d, "USD"), ("Bob", 12d, "USD")], rows);
    }
}

/// <summary>The service side: app sign-in, the socket token from OAuth, refresh on 401, back-fill, the fatal flag and steps.</summary>
public sealed class StreamlabsFeatureTests : IAsyncLifetime
{
    private const string Access = "SL-SECRET-access-token-0123456789";
    private const string Secret = "SL-SECRET-client-secret-987654321";
    private const string Socket = "SL-SECRET-socket-token-555555555";
    private const string Open = """0{"sid":"s1","upgrades":[],"pingInterval":25000,"pingTimeout":20000}""";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cuebot-sl-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly ConcurrentQueue<BotEvent> _published = new();
    private readonly FakeServer _server = new();
    private readonly FakeReceiver _receiver = new();
    private readonly ConcurrentDictionary<string, Func<RecordedRequest, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
    private FakeHttpHandler _http = null!;
    private JsonFile<DonationsSettings> _settings = null!;
    private StreamlabsService _service = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        _settings = new JsonFile<DonationsSettings>(Path.Combine(_dataDir, "donations.json"), _log);
        _settings.Value.Streamlabs.ClientId = "client-1";
        _settings.Value.Streamlabs.ClientSecret = Secret;
        _settings.Value.Streamlabs.RequestsPerMinute = 100;
        _http = new FakeHttpHandler(r => _routes.TryGetValue($"{r.Method.Method} {r.Uri.AbsolutePath}", out var route)
            ? route(r)
            : FakeHttpHandler.Json("""{"error":"Not Found"}""", HttpStatusCode.NotFound));
        var bus = new EventBus(_log);
        bus.Subscribe(_published.Enqueue);
        _service = new StreamlabsService(_settings, bus, _log, _server.Create, new Uri("https://sockets.test"), TestTimings.Fast,
            new StreamlabsApi(new HttpClient(_http), new Uri("https://sl.test/api/v2.0/")), _ => _receiver, TimeProvider.System);
        _routes["GET /api/v2.0/socket/token"] = _ => FakeHttpHandler.Json($$"""{"socket_token":"{{Socket}}"}""");
        _routes["GET /api/v2.0/user"] = _ => FakeHttpHandler.Json("""{"streamlabs":{"id":1,"display_name":"Streamer"},"twitch":{"id":2,"display_name":"Streamer","name":"streamer"}}""");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _settings.Dispose();
        Directory.Delete(_dataDir, recursive: true);
    }

    private BotEvent[] Events(string type) => _published.Where(e => e.Type == type).ToArray();

    private async Task<FakeSocket> ConnectEio3Async()
    {
        await _service.ConnectAsync();
        var socket = await _server.NextAsync();
        Assert.Contains("EIO=3", socket.Url!.Query);
        socket.Push(Open);   // Socket.IO v2 connects the namespace on its own
        await Wait.UntilAsync(() => _service.State == ConnectionState.Connected, "connected");
        return socket;
    }

    [Fact]
    public async Task Fresh_installs_try_socket_io_v2_first_as_the_docs_load_it()
    {
        _settings.Value.Streamlabs.SocketToken = Socket;
        await ConnectEio3Async();
        Assert.Contains("v2", _service.StatusMessage);
        Assert.Equal(3, _service.Settings.EngineIoVersion);
    }

    [Fact]
    public async Task Sign_in_saves_tokens_account_points_channel_and_the_socket_token()
    {
        _routes["POST /api/v2.0/token"] = _ => FakeHttpHandler.Json($$"""{"access_token":"{{Access}}","token_type":"Bearer","refresh_token":"R1"}""");
        string? opened = null;
        await _service.SignInAsync(url =>
        {
            opened = url;
            var state = Uri.UnescapeDataString(new Uri(url).Query).Split('&').Single(p => p.StartsWith("state=", StringComparison.Ordinal))[6..];
            _receiver.ExpectedState = state;
            _receiver.Result = new OAuthRedirect("the-code", null);
        });

        Assert.StartsWith("https://sl.test/api/v2.0/authorize?", opened, StringComparison.Ordinal);
        var s = _service.Settings;
        Assert.Equal((Access, "R1", "Streamer", "streamer", Socket), (s.AccessToken, s.RefreshToken, s.AccountName, s.PointsChannel, s.SocketToken));
        Assert.Equal("Twitch: Streamer", s.LinkedAccounts);
        Assert.True(_service.IsSignedIn);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A8914%2Fcallback", _http.Requests[0].Body);

        _service.SignOut();
        Assert.Equal(("", "", Socket), (s.AccessToken, s.RefreshToken, s.SocketToken));   // the socket token keeps working
    }

    [Fact]
    public async Task Sign_in_needs_the_app_and_reports_a_cancelled_consent()
    {
        _settings.Value.Streamlabs.ClientSecret = "";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SignInAsync(_ => { }));
        _settings.Value.Streamlabs.ClientSecret = Secret;
        _receiver.Result = new OAuthRedirect(null, "Sign-in was cancelled on Streamlabs.");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SignInAsync(url =>
            _receiver.ExpectedState = Uri.UnescapeDataString(new Uri(url).Query).Split('&').Single(p => p.StartsWith("state=", StringComparison.Ordinal))[6..]));
        Assert.Contains("cancelled", ex.Message);
    }

    [Fact]
    public async Task With_only_the_app_connected_the_socket_token_is_fetched_before_connecting()
    {
        _settings.Value.Streamlabs.AccessToken = Access;
        _settings.Value.Streamlabs.BackfillMissedDonations = false;
        var socket = await ConnectEio3Async();
        Assert.Contains($"token={Socket}", socket.Url!.Query);
        Assert.Equal(Socket, _service.Settings.SocketToken);
        Assert.DoesNotContain(Socket, _service.StatusMessage);
    }

    [Fact]
    public async Task A_rejected_access_token_is_refreshed_once_and_the_call_retried()
    {
        _settings.Value.Streamlabs.AccessToken = "OLD-access-token-1234567";
        _settings.Value.Streamlabs.RefreshToken = "R1";
        _routes["POST /api/v2.0/token"] = _ => FakeHttpHandler.Json($$"""{"access_token":"{{Access}}","token_type":"Bearer","refresh_token":"R2"}""");
        _routes["POST /api/v2.0/wheel/spin"] = r => r.Authorization == "Bearer " + Access
            ? FakeHttpHandler.Json("""{"success":true}""")
            : FakeHttpHandler.Json("""{"error":"Unauthorized"}""", HttpStatusCode.Unauthorized);

        await _service.SpinWheelAsync();

        Assert.Equal((Access, "R2"), (_service.Settings.AccessToken, _service.Settings.RefreshToken));
        Assert.Equal(["POST /api/v2.0/wheel/spin", "POST /api/v2.0/token", "POST /api/v2.0/wheel/spin"],
            _http.Requests.Select(r => $"{r.Method.Method} {r.Uri.AbsolutePath}"));
        Assert.Contains("grant_type=refresh_token", _http.Requests[1].Body);
    }

    [Fact]
    public async Task Rest_steps_say_to_connect_the_app_first()
    {
        var ex = await Assert.ThrowsAsync<StreamlabsApiException>(() => _service.AlertCommandAsync("skip"));
        Assert.Contains("Connect Cuebot to your Streamlabs app", ex.Message);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Alert_profile_is_activated_by_name_and_credits_explain_the_422()
    {
        _settings.Value.Streamlabs.AccessToken = Access;
        _routes["GET /api/v2.0/alert_profiles/get"] = _ => FakeHttpHandler.Json("""[{"id":"p1","name":"Default"},{"id":"p2","name":"Just Chatting"}]""");
        _routes["POST /api/v2.0/alert_profiles/activate"] = _ => FakeHttpHandler.Json("""{"success":true}""");
        _routes["POST /api/v2.0/credits/roll"] = _ => FakeHttpHandler.Json("""{"message":"Not live"}""", (HttpStatusCode)422);

        Assert.Equal("p2", (await _service.ActivateAlertProfileAsync("just chatting")).Id);
        Assert.Equal("p2", JsonDocument.Parse(_http.Requests[^1].Body).RootElement.GetProperty("id").GetString());
        var missing = await Assert.ThrowsAsync<StreamlabsApiException>(() => _service.ActivateAlertProfileAsync("Gaming"));
        Assert.Contains("Default, Just Chatting", missing.Message);
        Assert.Contains("only roll while you are live", (await Assert.ThrowsAsync<StreamlabsApiException>(() => _service.RollCreditsAsync())).Message);
    }

    [Fact]
    public async Task Points_use_the_channel_and_add_subtract_by_sign()
    {
        _settings.Value.Streamlabs.AccessToken = Access;
        _settings.Value.Streamlabs.PointsChannel = "streamer";
        var points = 100L;
        _routes["GET /api/v2.0/points"] = _ => FakeHttpHandler.Json($$"""{"username":"anna","points":{{points}}}""");
        _routes["POST /api/v2.0/points/import"] = r =>
        {
            points += JsonDocument.Parse(r.Body).RootElement.GetProperty("users").GetProperty("anna").GetInt64();
            return FakeHttpHandler.Json("""{"message":"Success"}""");
        };
        _routes["POST /api/v2.0/points/subtract"] = r =>
        {
            points -= JsonDocument.Parse(r.Body).RootElement.GetProperty("points").GetInt64();
            return FakeHttpHandler.Json($$"""{"username":"anna","points":{{points}}}""");
        };

        Assert.Equal(150, await _service.AddPointsAsync("@anna", 50));
        Assert.Equal(130, await _service.AddPointsAsync("anna", -20));
        Assert.Contains("channel=streamer", _http.Requests.First(r => r.Method == HttpMethod.Get).Uri.Query);
        _settings.Value.Streamlabs.PointsChannel = "";
        await Assert.ThrowsAsync<StreamlabsApiException>(() => _service.GetPointsAsync("anna"));
    }

    [Fact]
    public async Task Custom_alerts_and_added_donations_are_held_to_two_a_minute()
    {
        _settings.Value.Streamlabs.AccessToken = Access;
        _routes["POST /api/v2.0/alerts"] = _ => FakeHttpHandler.Json("""{"success":true}""");
        await _service.CreateAlertAsync("donation", "one", "", "", "", 0, "");
        await _service.CreateAlertAsync("donation", "two", "", "", "", 0, "");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.CreateAlertAsync("donation", "three", "", "", "", 0, "", cts.Token));
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task Missed_donations_are_backfilled_after_a_reconnect_once_and_marked()
    {
        _settings.Value.Streamlabs.SocketToken = Socket;
        _settings.Value.Streamlabs.AccessToken = Access;
        _routes["GET /api/v2.0/donations"] = r => r.Uri.Query.Contains("after=", StringComparison.Ordinal)
            ? FakeHttpHandler.Json("""
                {"data":[{"donation_id":"103","created_at":"1700000300","currency":"USD","amount":"3","name":"Cy","message":"c"},
                         {"donation_id":"102","created_at":"1700000200","currency":"USD","amount":"2","name":"Bo","message":"b"},
                         {"donation_id":"101","created_at":"1700000100","currency":"USD","amount":"1","name":"Al","message":"a"}]}
                """)
            : FakeHttpHandler.Json("""{"data":[{"donation_id":"100","created_at":"1700000000","currency":"USD","amount":"9","name":"Old","message":""}]}""");

        var socket = await ConnectEio3Async();
        await Wait.UntilAsync(() => _service.LastDonationId == "100", "baseline");
        Assert.Empty(Events(StreamlabsEventTypes.Donation));   // the first connect replays nothing

        socket.Push("""42["event",{"type":"donation","message":[{"id":101,"name":"Al","amount":"1","currency":"USD","message":"a","_id":"x101"}],"event_id":"e1"}]""");
        await Wait.UntilAsync(() => Events(StreamlabsEventTypes.Donation).Length == 1, "live donation");

        socket.Close("transport error");
        var second = await _server.NextAsync();
        second.Push(Open);
        await Wait.UntilAsync(() => Events(StreamlabsEventTypes.Donation).Length == 3, "back-filled donations");
        var donations = Events(StreamlabsEventTypes.Donation);
        Assert.Equal(["Al", "Bo", "Cy"], donations.Select(d => d.Args["user"]));
        Assert.Equal([false, true, true], donations.Select(d => d.Args["isBackfill"]));
        Assert.Contains("after=101", _http.Requests[^1].Uri.Query);

        // The socket delivering one of them late is a duplicate.
        second.Push("""42["event",{"type":"donation","message":[{"id":102,"name":"Bo","amount":"2","currency":"USD","message":"b","_id":"x102"}],"event_id":"e2"}]""");
        await Task.Delay(150);
        Assert.Equal(3, Events(StreamlabsEventTypes.Donation).Length);
        Assert.Equal("103", _service.LastDonationId);
    }

    [Fact]
    public async Task A_closed_transport_mentioning_refused_is_not_a_token_rejection_but_connect_error_is()
    {
        _settings.Value.Streamlabs.SocketToken = Socket;
        var socket = await ConnectEio3Async();
        socket.Close("No connection could be made because the target machine actively refused it");
        await Wait.UntilAsync(() => Events(StreamlabsEventTypes.Disconnected).Length == 1, "disconnected");
        Assert.Equal(ConnectionState.Connecting, _service.State);   // reconnects, not "token rejected"

        var second = await _server.NextAsync();
        second.Push(Open);
        second.Push("""44{"message":"invalid token"}""");
        await Wait.UntilAsync(() => _service.State == ConnectionState.Error, "error");
    }

    [Fact]
    public async Task Steps_run_through_the_engine_and_write_their_args()
    {
        var dir = Path.Combine(_dataDir, "app");
        var services = new ServiceCollection().AddCuebotCore(new AppPaths(dir)).AddDonations();
        services.AddSingleton(new HttpClient(_http));
        var capture = new Capture();
        services.AddSingleton<ISubActionHandler>(capture);
        await using var provider = services.BuildServiceProvider();
        var sl = provider.GetRequiredService<StreamlabsService>();
        sl.Settings.AccessToken = Access;
        sl.Settings.PointsChannel = "streamer";
        _routes["POST /api/v2.0/alerts/skip"] = _ => FakeHttpHandler.Json("""{"success":true}""");
        _routes["GET /api/v2.0/alerts/get_status"] = _ => FakeHttpHandler.Json("""{"paused":false}""");
        _routes["GET /api/v2.0/points"] = _ => FakeHttpHandler.Json("""{"username":"anna","points":470}""");
        _routes["GET /api/v2.0/donations"] = _ => FakeHttpHandler.Json("""{"data":[{"donation_id":"7","created_at":"1438576556","currency":"EUR","amount":"5","name":"Eve","message":"hi"}]}""");
        var engine = provider.GetRequiredService<ActionEngine>();

        async Task<Dictionary<string, object?>> Run(string type, Dictionary<string, string> parameters)
        {
            capture.Args = null;
            await engine.RunAsync(new ActionDefinition
            {
                Name = "test " + type,
                SubActions = [new SubActionDefinition { Type = type, Parameters = parameters }, new SubActionDefinition { Type = Capture.Id }],
            }, new Dictionary<string, object?> { ["userName"] = "anna" });
            return capture.Args ?? throw new Xunit.Sdk.XunitException($"{type} failed: {engine.History.Entries.FirstOrDefault(e => e.Error is not null)?.Error}");
        }

        await Run("streamlabs.alertQueue", new() { ["command"] = "skip" });
        Assert.Equal(false, (await Run("streamlabs.getAlertQueueStatus", new()))["slAlertsPaused"]);
        Assert.Equal(470L, (await Run("streamlabs.getPoints", new() { ["user"] = "%userName%" }))["slPoints"]);
        var donations = await Run("streamlabs.getDonations", new() { ["count"] = "3" });
        Assert.Equal((1, "Eve", "€5.00"), (donations["slDonationCount"], donations["slDonation0User"], donations["slDonation0FormattedAmount"]));

        var steps = StreamlabsSubActions.Create(sl);
        Assert.Equal(StreamlabsSubActions.TypeIds, steps.Select(s => s.TypeId));
        Assert.All(steps, s => Assert.NotEqual(StepRisk.Never, StepRiskCatalog.ForStep(s.TypeId)));
        Assert.Equal(StepRisk.ShowCritical, StepRiskCatalog.ForStep("streamlabs.resetPoints"));
    }

    private sealed class Capture : ISubActionHandler
    {
        public const string Id = "test.capture";
        public Dictionary<string, object?>? Args { get; set; }
        public string TypeId => Id;
        public string Category => "Test";
        public string DisplayName => "Capture";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            Args = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(SubActionResult.Next);
        }
    }

    private sealed class FakeReceiver : IOAuthRedirectReceiver
    {
        public OAuthRedirect Result { get; set; } = new(null, "not set");
        public string ExpectedState { get; set; } = "";

        public Task<OAuthRedirect> WaitAsync(string expectedState, CancellationToken ct) =>
            Task.FromResult(expectedState == ExpectedState ? Result : new OAuthRedirect(null, "state mismatch"));

        public void Dispose()
        {
        }
    }
}
