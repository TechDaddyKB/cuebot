using System.Text.Json;
using Cuebot.Obs.Protocol;
using Cuebot.Obs.Tests.Fakes;

namespace Cuebot.Obs.Tests;

public class ObsClientTests
{
    [Fact]
    public async Task Identifies_without_authentication_when_obs_has_no_password()
    {
        await using var server = FakeObsServer.Start();
        await using var client = new ObsClient();

        await client.ConnectAsync(server.Uri, "");

        Assert.True(client.IsConnected);
        var identify = server.LastIdentify!.Value;
        Assert.Equal(1, identify.GetProperty("rpcVersion").GetInt32());
        Assert.Equal(ObsClient.AllLowVolumeEventSubscriptions, identify.GetProperty("eventSubscriptions").GetInt32());
        Assert.False(identify.TryGetProperty("authentication", out _));
    }

    [Fact]
    public async Task Authenticates_with_the_correct_password()
    {
        await using var server = FakeObsServer.Start(password: "supersecretpassword");
        await using var client = new ObsClient();

        await client.ConnectAsync(server.Uri, "supersecretpassword");

        Assert.True(client.IsConnected);
        Assert.Equal("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=", server.LastIdentify!.Value.GetProperty("authentication").GetString());
    }

    [Fact]
    public async Task Wrong_password_is_reported_as_an_authentication_failure()
    {
        await using var server = FakeObsServer.Start(password: "right");
        await using var client = new ObsClient();

        var error = await Assert.ThrowsAsync<ObsAuthenticationException>(() => client.ConnectAsync(server.Uri, "wrong"));

        Assert.Contains("password", error.Message);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Missing_password_is_reported_before_identifying()
    {
        await using var server = FakeObsServer.Start(password: "right");
        await using var client = new ObsClient();

        var error = await Assert.ThrowsAsync<ObsAuthenticationException>(() => client.ConnectAsync(server.Uri, ""));

        Assert.Contains("requires a password", error.Message);
    }

    [Fact]
    public async Task Correlates_responses_answered_out_of_order()
    {
        await using var server = FakeObsServer.Start();
        server.Handler = (type, data) => type == "Slow"
            ? FakeReply.Ok(new { echo = "slow" }) with { Delay = TimeSpan.FromMilliseconds(300) }
            : FakeReply.Ok(new { echo = data.GetProperty("value").GetString() });
        await using var client = await ConnectAsync(server);

        var slow = client.RequestAsync("Slow");
        var fast = client.RequestAsync("Fast", new { value = "fast" });

        Assert.Equal("fast", (await fast).GetProperty("echo").GetString());
        Assert.False(slow.IsCompleted);
        Assert.Equal("slow", (await slow).GetProperty("echo").GetString());
    }

    [Fact]
    public async Task Returns_an_empty_object_when_the_response_has_no_data()
    {
        await using var server = FakeObsServer.Start();
        await using var client = await ConnectAsync(server);

        var response = await client.RequestAsync("SetCurrentProgramScene", new { sceneName = "Main" });

        Assert.Equal(JsonValueKind.Object, response.ValueKind);
        var (type, data) = Assert.Single(server.Requests);
        Assert.Equal("SetCurrentProgramScene", type);
        Assert.Equal("Main", data.GetProperty("sceneName").GetString());
    }

    [Fact]
    public async Task Failed_status_throws_with_code_and_comment()
    {
        await using var server = FakeObsServer.Start();
        server.Handler = (_, _) => FakeReply.Fail(600, "No source was found by the name of `Nope`.");
        await using var client = await ConnectAsync(server);

        var error = await Assert.ThrowsAsync<ObsRequestException>(() => client.RequestAsync("GetSceneItemId"));

        Assert.Equal(600, error.Code);
        Assert.Equal("GetSceneItemId", error.RequestType);
        Assert.Contains("Nope", error.Message);
    }

    [Fact]
    public async Task Unanswered_request_times_out()
    {
        await using var server = FakeObsServer.Start();
        server.Handler = (_, _) => null;
        await using var client = new ObsClient(requestTimeout: TimeSpan.FromMilliseconds(200));
        await client.ConnectAsync(server.Uri, "");

        var error = await Assert.ThrowsAsync<TimeoutException>(() => client.RequestAsync("GetStats"));

        Assert.Contains("GetStats", error.Message);
    }

    [Fact]
    public async Task Raises_events()
    {
        await using var server = FakeObsServer.Start();
        await using var client = new ObsClient();
        var received = new TaskCompletionSource<ObsEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.EventReceived += e => received.TrySetResult(e);
        await client.ConnectAsync(server.Uri, "");

        await server.SendEventAsync("CurrentProgramSceneChanged", new { sceneName = "Gaming" });

        var e = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("CurrentProgramSceneChanged", e.EventType);
        Assert.Equal("Gaming", e.EventData.GetProperty("sceneName").GetString());
    }

    [Fact]
    public async Task Server_close_fails_pending_requests_and_raises_disconnected()
    {
        await using var server = FakeObsServer.Start();
        server.Handler = (_, _) => null;
        await using var client = new ObsClient();
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += reason => disconnected.TrySetResult(reason);
        await client.ConnectAsync(server.Uri, "");
        var pending = client.RequestAsync("GetStats");

        await server.CloseSessionAsync();

        await Assert.ThrowsAsync<ObsConnectionException>(() => pending);
        Assert.Equal("OBS closed the connection.", await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<ObsConnectionException>(() => client.RequestAsync("GetStats"));
    }

    [Fact]
    public async Task Dispose_closes_the_session_gracefully()
    {
        await using var server = FakeObsServer.Start();
        var client = await ConnectAsync(server);
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += reason => disconnected.TrySetResult(reason);

        await client.DisposeAsync();

        Assert.Equal("Disconnected from OBS.", await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static async Task<ObsClient> ConnectAsync(FakeObsServer server)
    {
        var client = new ObsClient();
        await client.ConnectAsync(server.Uri, "");
        return client;
    }
}
