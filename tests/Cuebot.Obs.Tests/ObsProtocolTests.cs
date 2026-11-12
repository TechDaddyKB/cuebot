using System.Net.WebSockets;
using System.Text.Json;
using Cuebot.Obs.Protocol;
using Cuebot.Obs.Tests.Fakes;

namespace Cuebot.Obs.Tests;

/// <summary>obs-websocket 5.x protocol details: subscriptions, Reidentify, batch execution and timeouts, RPC version.</summary>
public class ObsProtocolTests
{
    [Fact]
    public async Task Identifies_with_every_low_volume_category_including_canvases()
    {
        await using var server = FakeObsServer.Start();
        await using var client = new ObsClient();

        await client.ConnectAsync(server.Uri, "");

        // General (1 << 0) ... Ui (1 << 10) and Canvases (1 << 11), none of the high-volume bits (1 << 16 and up).
        Assert.Equal(0xFFF, server.Subscriptions);
        Assert.Equal(1, client.NegotiatedRpcVersion);
    }

    [Fact]
    public async Task Reidentify_changes_the_subscriptions_without_reconnecting()
    {
        await using var server = FakeObsServer.Start();
        await using var client = new ObsClient();
        await client.ConnectAsync(server.Uri, "", eventSubscriptions: ObsClient.AllLowVolumeEventSubscriptions | ObsClient.InputVolumeMetersSubscription);
        Assert.Equal(0xFFF | (1 << 16), server.Subscriptions);

        await client.ReidentifyAsync(ObsClient.AllLowVolumeEventSubscriptions | ObsClient.InputActiveStateChangedSubscription);
        await WaitUntil(() => server.ReidentifyCount == 1);

        Assert.Equal(0xFFF | (1 << 17), server.Subscriptions);
        Assert.Equal(1, server.SessionCount);
        Assert.Equal(0xFFF | (1 << 17), client.EventSubscriptions);
    }

    [Fact]
    public async Task Refuses_a_server_that_negotiates_another_rpc_version()
    {
        await using var server = FakeObsServer.Start();
        server.NegotiatedRpcVersion = 2;
        await using var client = new ObsClient();

        var error = await Assert.ThrowsAsync<ObsConnectionException>(() => client.ConnectAsync(server.Uri, ""));

        Assert.Contains("RPC version 2", error.Message);
    }

    [Theory]
    [InlineData(4010, "Update OBS")]
    [InlineData(4011, "Connected clients")]
    [InlineData(4009, "password")]
    public void Close_codes_explain_what_to_do(int code, string expected) =>
        Assert.Contains(expected, ObsCloseCodes.Describe((WebSocketCloseStatus)code, null));

    [Fact]
    public async Task Batch_sends_the_execution_type_only_when_not_the_default()
    {
        await using var server = FakeObsServer.Start();
        await using var client = new ObsClient();
        await client.ConnectAsync(server.Uri, "");

        await client.RequestBatchAsync([new { requestType = "GetVersion" }]);
        Assert.Null(server.LastBatchExecutionType);

        await client.RequestBatchAsync([new { requestType = "GetVersion" }], executionType: ObsBatchExecution.SerialFrame);
        Assert.Equal(1, server.LastBatchExecutionType);

        await client.RequestBatchAsync([new { requestType = "GetVersion" }], executionType: ObsBatchExecution.Parallel);
        Assert.Equal(2, server.LastBatchExecutionType);
    }

    [Fact]
    public async Task Batch_waits_for_its_sleeps_beyond_the_request_timeout()
    {
        await using var server = FakeObsServer.Start();
        await using var client = new ObsClient(requestTimeout: TimeSpan.FromMilliseconds(300));
        await client.ConnectAsync(server.Uri, "");
        using var sleep = JsonDocument.Parse("""{"requestType":"Sleep","requestData":{"sleepMillis":900}}""");

        // Before the fix the batch timed out after 300 ms × 1 request while OBS was still sleeping.
        var results = await client.RequestBatchAsync([sleep.RootElement.Clone()]);

        Assert.True(results[0].GetProperty("requestStatus").GetProperty("result").GetBoolean());
    }

    [Fact]
    public void Batch_timeout_counts_sleep_millis_and_frames()
    {
        var client = new ObsClient(requestTimeout: TimeSpan.FromSeconds(1));
        object[] requests =
        [
            new { requestType = "Sleep", requestData = new { sleepMillis = 15000 } },
            new { requestType = "Sleep", requestData = new { sleepFrames = 100 } },
            new { requestType = "SetCurrentSceneCollection", requestData = new { sceneCollectionName = "Big" } },
        ];

        // 1 s + 15 s, 1 s + 100 frames at 20 fps (5 s), 60 s for the blocking collection switch.
        Assert.Equal(TimeSpan.FromSeconds(82), client.BatchTimeout(requests));
    }

    [Fact]
    public async Task Scene_collection_switch_may_take_longer_than_the_request_timeout()
    {
        await using var server = FakeObsServer.Start();
        server.Handler = (type, _) => FakeReply.Ok() with { Delay = TimeSpan.FromMilliseconds(700) };
        await using var client = new ObsClient(requestTimeout: TimeSpan.FromMilliseconds(300));
        await client.ConnectAsync(server.Uri, "");

        await client.RequestAsync("SetCurrentSceneCollection", new { sceneCollectionName = "Big" });
        await Assert.ThrowsAsync<TimeoutException>(() => client.RequestAsync("GetStats"));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met within 10 s");
            await Task.Delay(20);
        }
    }
}
