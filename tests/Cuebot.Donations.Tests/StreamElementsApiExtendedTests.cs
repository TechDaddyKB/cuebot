using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Donations.StreamElements;
using Cuebot.Donations.Tests.Support;

namespace Cuebot.Donations.Tests;

/// <summary>The REST calls beyond points and tips, against paths and bodies of the public OpenAPI document.</summary>
public sealed class StreamElementsApiExtendedTests
{
    private const string Jwt = "jwt-SECRET-value-0123456789";
    private const string C = "5ad23dcc18fff500d78c5348";
    private readonly FakeHttpHandler _http;
    private readonly StreamElementsApi _api;
    private readonly Dictionary<string, string> _responses = new(StringComparer.Ordinal);

    public StreamElementsApiExtendedTests()
    {
        _http = new FakeHttpHandler(r => _responses.TryGetValue($"{r.Method.Method} {r.Uri.PathAndQuery}", out var body)
            ? body.StartsWith('{') || body.StartsWith('[') ? FakeHttpHandler.Json(body) : new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(body) }
            : FakeHttpHandler.Json("""{"statusCode":404,"error":"Not Found","message":"nope"}""", HttpStatusCode.NotFound));
        _api = new StreamElementsApi(new HttpClient(_http), new Uri("https://api.test/kappa/v2/"));
    }

    private RecordedRequest Last => _http.Requests[^1];

    private static JsonElement Body(RecordedRequest r) => JsonDocument.Parse(r.Body).RootElement;

    [Fact]
    public async Task Bot_say_mute_status_and_counters()
    {
        _responses[$"POST /kappa/v2/bot/{C}/say"] = """{"status":200,"channel":"c","message":"ok"}""";
        _responses[$"POST /kappa/v2/bot/{C}/mute"] = """{"status":200}""";
        _responses[$"POST /kappa/v2/bot/{C}/unmute"] = """{"status":200}""";
        _responses[$"GET /kappa/v2/bot/{C}"] = """{"bot":{"enabled":true,"name":"StreamElements","joined":true,"muted":false,"mod":true}}""";
        _responses[$"GET /kappa/v2/bot/{C}/counters/deaths"] = """{"counter":"deaths","value":42}""";

        await _api.SayAsync(Jwt, C, "Hello chat", default);
        Assert.Equal(("POST", "Hello chat", "application/json"), (Last.Method.Method, Body(Last).GetProperty("message").GetString(), Last.ContentType));
        await _api.SetBotMutedAsync(Jwt, C, true, default);
        Assert.EndsWith("/mute", Last.Uri.AbsolutePath, StringComparison.Ordinal);
        await _api.SetBotMutedAsync(Jwt, C, false, default);
        Assert.EndsWith("/unmute", Last.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(new StreamElementsBot(true, true, false, true, "StreamElements"), await _api.GetBotAsync(Jwt, C, default));
        Assert.Equal(42, await _api.GetBotCounterAsync(Jwt, C, "deaths", default));
        Assert.All(_http.Requests, r => Assert.Equal("Bearer " + Jwt, r.Authorization));
    }

    [Fact]
    public async Task Timers_and_commands_are_found_by_name_and_sent_back_with_enabled_changed()
    {
        _responses[$"GET /kappa/v2/bot/timers/{C}"] = """[{"_id":"t1","name":"Discord","enabled":true,"message":"Join!","online":{"enabled":true,"interval":5},"createdAt":"x","channel":"c"}]""";
        _responses[$"PUT /kappa/v2/bot/timers/{C}/t1"] = "Created";
        _responses[$"GET /kappa/v2/bot/commands/{C}"] = """[{"_id":"k1","command":"discord","enabled":false,"reply":"Join!","channel":"c"}]""";
        _responses[$"PUT /kappa/v2/bot/commands/{C}/k1"] = """{"_id":"k1","enabled":true}""";

        var timer = await _api.SetTimerEnabledAsync(Jwt, C, "discord", null, default);
        Assert.Equal(new StreamElementsToggle("t1", "Discord", false), timer);
        var body = Body(Last);
        Assert.Equal((false, "Join!", 5), (body.GetProperty("enabled").GetBoolean(), body.GetProperty("message").GetString(),
            body.GetProperty("online").GetProperty("interval").GetInt32()));
        Assert.False(body.TryGetProperty("_id", out _));
        Assert.False(body.TryGetProperty("createdAt", out _));

        var command = await _api.SetCommandEnabledAsync(Jwt, C, "!discord", true, default);
        Assert.True(command.Enabled);
        Assert.True(Body(Last).GetProperty("enabled").GetBoolean());
        await Assert.ThrowsAsync<StreamElementsApiException>(() => _api.SetCommandEnabledAsync(Jwt, C, "!nothing", true, default));
    }

    [Fact]
    public async Task Overlay_actions_session_and_points_extras()
    {
        _responses[$"PUT /kappa/v2/overlays/{C}/action/pause"] = "Created";
        _responses[$"GET /kappa/v2/sessions/{C}"] = """{"data":{"tip-goal":{"amount":50},"follower-session":{"count":3}},"provider":"twitch","channel":"c"}""";
        _responses[$"PUT /kappa/v2/sessions/{C}"] = """{"data":{"tip-goal":{"amount":100},"follower-session":{"count":3}}}""";
        _responses[$"PUT /kappa/v2/sessions/{C}/reset"] = """{"data":{}}""";
        _responses[$"GET /kappa/v2/points/{C}/alice/rank"] = """{"rank":7}""";
        _responses[$"DELETE /kappa/v2/points/{C}/alice"] = """{}""";
        _responses[$"PUT /kappa/v2/points/{C}"] = """{}""";
        _responses[$"DELETE /kappa/v2/points/{C}/reset/alltime"] = "Created";
        _responses[$"GET /kappa/v2/points/{C}/watchtime?limit=2&offset=0"] = """{"users":[{"username":"anna","minutes":900},{"username":"ben","minutes":30}]}""";

        await _api.OverlayActionAsync(Jwt, C, "pause", default);
        Assert.Equal("PUT", Last.Method.Method);

        await _api.SetSessionValueAsync(Jwt, C, "tip-goal", "amount", JsonValue.Create(100), default);
        var put = Body(Last);
        Assert.Equal(100, put.GetProperty("data").GetProperty("tip-goal").GetProperty("amount").GetInt32());
        Assert.Equal(3, put.GetProperty("data").GetProperty("follower-session").GetProperty("count").GetInt32());   // the rest is kept
        await _api.ResetSessionAsync(Jwt, C, default);

        Assert.Equal(7, await _api.GetUserRankAsync(Jwt, C, "alice", default));
        await _api.ResetUserPointsAsync(Jwt, C, "alice", default);
        Assert.Equal("DELETE", Last.Method.Method);
        await _api.BulkPointsAsync(Jwt, C, ["anna", "ben"], 50, set: false, default);
        var bulk = Body(Last);
        Assert.Equal("add", bulk.GetProperty("mode").GetString());
        Assert.Equal(("anna", 50), (bulk.GetProperty("users")[0].GetProperty("username").GetString(), bulk.GetProperty("users")[0].GetProperty("current").GetInt32()));
        await _api.ResetPointsAsync(Jwt, C, "alltime", default);
        Assert.Equal([new StreamElementsRank("anna", 900), new StreamElementsRank("ben", 30)], await _api.GetPointsBoardAsync(Jwt, C, "watchtime", 2, default));
    }

    [Fact]
    public async Task Points_rank_falls_back_to_the_rank_endpoint()
    {
        _responses[$"GET /kappa/v2/points/{C}/bob"] = """{"username":"bob","points":10,"pointsAlltime":20}""";
        _responses[$"GET /kappa/v2/points/{C}/bob/rank"] = """{"rank":12}""";
        Assert.Equal(new StreamElementsPoints("bob", 10, 20, 12), await _api.GetUserPointsAsync(Jwt, C, "bob", default));
    }

    [Fact]
    public async Task Store_items_and_redemptions()
    {
        _responses[$"GET /kappa/v2/store/{C}/items?limit=100&offset=0"] = """
            [{"_id":"i1","name":"Hydrate!","description":"Drink","enabled":true,"cost":500,"quantity":{"total":10,"current":4},"type":"perk","channel":"c"}]
            """;
        _responses[$"PUT /kappa/v2/store/{C}/items/i1"] = """{"_id":"i1","name":"Hydrate!","enabled":false,"cost":750,"quantity":{"total":20,"current":20}}""";
        _responses[$"GET /kappa/v2/store/{C}/redemptions?limit=5&offset=0&pending=true"] = """
            {"docs":[{"_id":"r1","item":{"_id":"i1","name":"Hydrate!"},"redeemer":{"username":"viewer"},"input":["please"],"completed":false,"createdAt":"t"}]}
            """;
        _responses[$"PUT /kappa/v2/store/{C}/redemptions/r1"] = """{}""";
        _responses[$"DELETE /kappa/v2/store/{C}/redemptions/r1"] = """{}""";

        var item = await _api.UpdateStoreItemAsync(Jwt, C, "hydrate!", false, false, 750, 20, default);
        var body = Body(Last);
        Assert.Equal((false, 750, 20, 20, "Drink"), (body.GetProperty("enabled").GetBoolean(), body.GetProperty("cost").GetInt32(),
            body.GetProperty("quantity").GetProperty("current").GetInt32(), body.GetProperty("quantity").GetProperty("total").GetInt32(),
            body.GetProperty("description").GetString()));
        Assert.Equal(new StreamElementsStoreItem("i1", "Hydrate!", false, 750, 20, 20, ""), item);

        var redemption = Assert.Single(await _api.GetRedemptionsAsync(Jwt, C, true, 5, default));
        Assert.Equal(new StreamElementsRedemption("r1", "Hydrate!", "i1", "viewer", "please", false, "t"), redemption);
        await _api.CompleteRedemptionAsync(Jwt, C, "r1", default);
        Assert.True(Body(Last).GetProperty("completed").GetBoolean());
        await _api.RefundRedemptionAsync(Jwt, C, "r1", default);
        Assert.Equal("DELETE", Last.Method.Method);
    }

    [Fact]
    public async Task Tips_activities_and_leaderboards()
    {
        _responses[$"POST /kappa/v2/tips/{C}"] = """{"_id":"t9","donation":{"user":{"username":"anna"},"amount":5,"currency":"EUR","message":"cash"}}""";
        _responses[$"GET /kappa/v2/tips/{C}/moderation"] = """{"recent":[],"pending":[{"_id":"t8","donation":{"user":{"username":"ben","email":"b@x.y"},"amount":2,"currency":"USD"},"approved":"pending"}]}""";
        _responses[$"POST /kappa/v2/activities/{C}/a1/replay"] = """{"success":true}""";
        _responses[$"GET /kappa/v2/activities/{C}/top?type=cheer&period=weeks&limit=3&offset=0"] = """[{"username":"anna","total":500}]""";

        var tip = await _api.CreateTipAsync(Jwt, C, "anna", 5, "eur", "cash", true, default);
        var body = Body(Last);
        Assert.Equal(("anna", 5.0, "EUR", true), (body.GetProperty("user").GetProperty("username").GetString(), body.GetProperty("amount").GetDouble(),
            body.GetProperty("currency").GetString(), body.GetProperty("imported").GetBoolean()));
        Assert.Equal("t9", tip.Id);

        Assert.Equal("ben", Assert.Single(await _api.GetPendingTipsAsync(Jwt, C, default)).UserName);
        Assert.True(await _api.ReplayActivityAsync(Jwt, C, "a1", default));
        Assert.Equal([new StreamElementsRank("anna", 500)], await _api.GetActivityLeaderboardAsync(Jwt, C, "cheer", "week", 3, default));
    }

    [Fact]
    public void Tips_from_the_list_skip_rejected_pending_and_failed_ones()
    {
        static JsonElement Doc(string json) => JsonDocument.Parse(json).RootElement;
        Assert.True(StreamElementsApi.IsGoodTip(Doc("""{"approved":"allowed","status":"success"}""")));
        Assert.True(StreamElementsApi.IsGoodTip(Doc("""{}""")));
        Assert.False(StreamElementsApi.IsGoodTip(Doc("""{"approved":"rejected","status":"success"}""")));
        Assert.False(StreamElementsApi.IsGoodTip(Doc("""{"approved":"pending","status":"success"}""")));
        Assert.False(StreamElementsApi.IsGoodTip(Doc("""{"approved":"allowed","status":"failed"}""")));
        Assert.False(StreamElementsApi.IsGoodTip(Doc("""{"deleted":true}""")));
    }

    [Fact]
    public async Task Contests_giveaways_on_v3_and_song_requests()
    {
        _responses[$"GET /kappa/v2/contests/{C}"] = """{"active":{"_id":"k1","title":"Win?","state":"running","totalAmount":300,"totalUsers":4,"options":[{"_id":"o1","title":"Yes","command":"1","totalAmount":200},{"_id":"o2","title":"No","command":"2","totalAmount":100}]},"contests":[]}""";
        _responses[$"POST /kappa/v2/contests/{C}"] = """{"_id":"k2","title":"New","state":"created","options":[]}""";
        _responses[$"PUT /kappa/v2/contests/{C}/k1/winner"] = "Created";
        _responses[$"DELETE /kappa/v2/contests/{C}/k1/close"] = "Created";
        _responses[$"GET /kappa/v3/giveaways/{C}"] = """{"active":{"_id":"g1","state":"running","totalUsers":12},"giveaways":[]}""";
        _responses[$"PUT /kappa/v3/giveaways/{C}/g1/winner"] = "";
        _responses[$"DELETE /kappa/v3/giveaways/{C}/g1/refund"] = "Created";
        _responses[$"POST /kappa/v2/songrequest/{C}/player/pause"] = "Created";
        _responses[$"GET /kappa/v2/songrequest/{C}/playing"] = """{"_id":"s1","videoId":"abc","title":"Song","duration":200,"user":{"username":"viewer"},"source":"bot"}""";
        _responses[$"GET /kappa/v2/songrequest/{C}/next"] = """{}""";
        _responses[$"POST /kappa/v2/songrequest/{C}/queue"] = """{"_id":"s2","videoId":"def","title":"Next one"}""";
        _responses[$"DELETE /kappa/v2/songrequest/{C}/queue/s2"] = "Created";

        var contest = await _api.GetContestAsync(Jwt, C, default);
        Assert.Equal(("k1", "running", 2), (contest!.Id, contest.State, contest.Options.Count));
        await _api.CreateContestAsync(Jwt, C, "New", ["Yes", "No"], 10, 100, 3, default);
        var body = Body(Last);
        Assert.Equal(("New", 10, 100, 3, "No", "2"), (body.GetProperty("title").GetString(), body.GetProperty("minBet").GetInt32(), body.GetProperty("maxBet").GetInt32(),
            body.GetProperty("duration").GetInt32(), body.GetProperty("options")[1].GetProperty("title").GetString(), body.GetProperty("options")[1].GetProperty("command").GetString()));
        await _api.ContestActionAsync(Jwt, C, "k1", "winner", "o1", default);
        Assert.Equal("o1", Body(Last).GetProperty("winnerId").GetString());
        await _api.ContestActionAsync(Jwt, C, "k1", "close", "", default);

        Assert.Equal("g1", (await _api.GetGiveawayAsync(Jwt, C, default))!.Id);
        await _api.GiveawayActionAsync(Jwt, C, "g1", "winner", default);
        Assert.Equal(("PUT", "/kappa/v3/giveaways/" + C + "/g1/winner"), (Last.Method.Method, Last.Uri.AbsolutePath));
        await _api.GiveawayActionAsync(Jwt, C, "g1", "refund", default);
        await Assert.ThrowsAsync<ArgumentException>(() => _api.GiveawayActionAsync(Jwt, C, "g1", "explode", default));

        await _api.SetSongPlayerAsync(Jwt, C, play: false, default);
        var playing = await _api.GetPlayingSongAsync(Jwt, C, default);
        Assert.Equal(("Song", "viewer", "https://www.youtube.com/watch?v=abc"), (playing!.Title, playing.UserName, playing.Url));
        Assert.Null(await _api.GetNextSongAsync(Jwt, C, default));
        var added = await _api.AddSongAsync(Jwt, C, "never gonna", default);
        Assert.Equal(("s2", "never gonna"), (added.Id, Body(Last).GetProperty("video").GetString()));
        await _api.RemoveSongAsync(Jwt, C, "s2", default);
    }

    [Fact]
    public async Task Write_errors_never_contain_the_token()
    {
        var ex = await Assert.ThrowsAsync<StreamElementsApiException>(() => _api.SayAsync(Jwt, C, "hi " + Jwt, default));
        Assert.Equal(404, ex.Status);
        Assert.DoesNotContain(Jwt, ex.Message, StringComparison.Ordinal);
    }
}
