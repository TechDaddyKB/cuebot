using System.Text.Json.Nodes;
using Cuebot.Server.Api;

namespace Cuebot.Server.Tests;

/// <summary>The REST endpoints the companion app uses: the dashboard snapshot and its change events, decks, subathon and goal controls.</summary>
public sealed class CompanionApiTests : IAsyncLifetime
{
    private ApiHost _host = null!;
    private TestBot Bot => _host.Bot;

    public async Task InitializeAsync() => _host = await ApiHost.StartAsync();
    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Dashboard_has_connections_totals_subathon_and_feed()
    {
        var timer = Bot.Subathon.AddTimer("Main");
        Bot.Subathon.AddGoal("Subs");
        Bot.SessionData.SetGoal("follower", 50);

        var (status, body) = await _host.GetAsync("/api/v1/dashboard");

        Assert.Equal(200, status);
        Assert.Equal("v1", (await _host.GetAsync("/api/v1/status")).Body!["app"]!["apiVersion"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(body!["instanceId"]!.GetValue<string>()));
        Assert.Contains(body["connections"]!.AsArray(), c => c!["name"]!.GetValue<string>() == "Twitch" && c["state"]!.GetValue<string>() == "Connected");
        Assert.Equal(timer.Id, body["subathon"]!["timers"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("Subs", body["subathon"]!["goals"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(50, body["totals"]!["follower-goal"]!["amount"]!.GetValue<double>());
        Assert.Null(body["totals"]!["cuebot-subathon"]);   // moved to "subathon", not repeated in totals
        Assert.IsType<JsonArray>(body["feed"]);
    }

    [Fact]
    public async Task Decks_list_has_the_grid_size()
    {
        Bot.Decks.Save(new Cuebot.Core.Decks.DeckDefinition { Name = "Stream", Rows = 2, Columns = 4 });

        var deck = (await _host.GetAsync("/api/v1/decks")).Body!["decks"]![0]!;

        Assert.Equal("Stream", deck["name"]!.GetValue<string>());
        Assert.Equal(2, deck["rows"]!.GetValue<int>());
        Assert.Equal(4, deck["columns"]!.GetValue<int>());
        Assert.Equal(1, deck["pageCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Subathon_timer_is_controlled_by_name()
    {
        Bot.Subathon.AddTimer("Main");

        var (status, body) = await _host.PostAsync("/api/v1/subathon/Main/time", new { set = 3600 });
        Assert.Equal(200, status);
        Assert.Equal(3600, body!["timers"]![0]!["remaining"]!.GetValue<double>());

        await _host.PostAsync("/api/v1/subathon/main/time", new { add = 300 });
        await _host.PostAsync("/api/v1/subathon/Main/multiplier", new { value = 0.5 });
        (_, body) = await _host.PostAsync("/api/v1/subathon/Main/start");

        var timer = body!["timers"]![0]!;
        Assert.Equal("running", timer["state"]!.GetValue<string>());
        Assert.Equal(0.5, timer["multiplier"]!.GetValue<double>());
        Assert.InRange(timer["remaining"]!.GetValue<double>(), 3895, 3900);
        Assert.NotNull(timer["endsAt"]);

        (_, body) = await _host.PostAsync("/api/v1/subathon/Main/pause");
        Assert.Equal("paused", body!["timers"]![0]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Goal_points_are_added_and_set()
    {
        var goal = Bot.Subathon.AddGoal("Subs");

        await _host.PostAsync($"/api/v1/goals/{goal.Id}/points", new { add = 5 });
        var (_, body) = await _host.PostAsync("/api/v1/goals/Subs/points", new { add = 2 });
        Assert.Equal(7, body!["goals"]![0]!["points"]!.GetValue<double>());

        (_, body) = await _host.PostAsync("/api/v1/goals/Subs/points", new { set = 20 });
        Assert.Equal(20, body!["goals"]![0]!["points"]!.GetValue<double>());

        (_, body) = await _host.PostAsync("/api/v1/goals/Subs/reset");
        Assert.Equal(0, body!["goals"]![0]!["points"]!.GetValue<double>());
    }

    [Theory]
    [InlineData("/api/v1/subathon/Nope/start", 404)]
    [InlineData("/api/v1/goals/Nope/reset", 404)]
    [InlineData("/api/v1/subathon/Main/explode", 404)]
    [InlineData("/api/v1/subathon/Main/time", 400)]
    [InlineData("/api/v1/goals/Subs/points", 400)]
    public async Task Bad_controls_are_refused(string path, int expected)
    {
        Bot.Subathon.AddTimer("Main");
        Bot.Subathon.AddGoal("Subs");

        var (status, _) = await _host.PostAsync(path);

        Assert.Equal(expected, status);
    }

    [Fact]
    public async Task Control_endpoints_need_the_token()
    {
        Bot.Subathon.AddTimer("Main");
        using var anonymous = new HttpClient { BaseAddress = _host.Http.BaseAddress };

        using var response = await anonymous.PostAsync("/api/v1/subathon/Main/start", null);

        Assert.Equal(401, (int)response.StatusCode);
        Assert.Equal("stopped", Bot.Subathon.Timers()[0].State.ToString().ToLowerInvariant());
    }

    [Fact]
    public async Task Events_stream_tells_when_the_dashboard_changed()
    {
        Bot.Subathon.AddTimer("Main");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/events?types=dashboard.changed");
        using var response = await _host.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        Assert.Equal(": connected", await reader.ReadLineAsync());

        var before = (await _host.GetAsync("/api/v1/dashboard")).Body!["revision"]!.GetValue<long>();
        for (var i = 0; i < 5; i++) await _host.PostAsync("/api/v1/subathon/Main/time", new { add = 10 });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? type = null;
        JsonNode? data = null;
        while (data is null)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null) break;
            if (line.StartsWith("event: ", StringComparison.Ordinal)) type = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = JsonNode.Parse(line[6..]);
        }
        Assert.Equal("dashboard.changed", type);
        var revision = data!["args"]!["revision"]!.GetValue<long>();
        Assert.True(revision > before, $"revision {revision} should be past {before}");
        Assert.Equal(revision, (await _host.GetAsync("/api/v1/dashboard")).Body!["revision"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_burst_of_changes_sends_one_notice_with_the_last_revision()
    {
        var timer = Bot.Subathon.AddTimer("Main");
        var changes = new DashboardChanges(Bot.Services);
        var notices = new System.Collections.Concurrent.ConcurrentQueue<long>();
        changes.Changed += notices.Enqueue;

        for (var i = 0; i < 20; i++) Bot.Subathon.AddTime(timer.Id, 1);
        await Task.Delay(DashboardChanges.Interval * 4);

        Assert.True(changes.Revision >= 20, $"revision {changes.Revision}");
        Assert.InRange(notices.Count, 1, 2);
        Assert.Equal(changes.Revision, notices.Last());
    }
}
