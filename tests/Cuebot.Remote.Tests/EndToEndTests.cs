using Cuebot.Core;
using Cuebot.Core.BuiltIn;

namespace Cuebot.Remote.Tests;

/// <summary>Two in-process instances linked over real Kestrel listeners on loopback (random ports).</summary>
public class EndToEndTests
{
    private static async Task<(Instance A, Instance B, RemoteInstanceDefinition Link)> LinkedAsync()
    {
        var a = await Instance.StartAsync("Alpha");
        var b = await Instance.StartAsync("Bravo");
        var link = await a.LinkToAsync(b, "studio");
        await Eventually.TrueAsync(() => b.Hub.Instances.Any(i => i.State == ConnectionState.Connected), "B sees A");
        return (a, b, link);
    }

    [Fact]
    public async Task Instances_pair_with_the_api_token_and_report_status_on_both_sides()
    {
        var (a, b, link) = await LinkedAsync();
        await using (a)
        await using (b)
        {
            var outgoing = Assert.Single(a.Hub.Instances);
            Assert.Equal(ConnectionState.Connected, outgoing.State);
            Assert.True(outgoing.Outgoing);
            Assert.Equal("studio", outgoing.Name);
            Assert.Equal("Bravo", outgoing.Peer!.Name);
            Assert.Equal(b.Hub.Self.Id, outgoing.Peer.Id);
            Assert.Equal(ConnectionState.Connected, a.Hub.State);
            Assert.Contains("studio", a.Hub.StatusMessage);

            var incoming = Assert.Single(b.Hub.Instances);
            Assert.False(incoming.Outgoing);
            Assert.Equal("Alpha", incoming.Name);
            Assert.Null(incoming.DefinitionId);

            var connected = Assert.Single(a.EventsOf(RemoteHub.ConnectedEvent));
            Assert.Equal("Bravo", connected.Args["instanceName"]);
            Assert.Equal("studio", connected.Args["instanceLabel"]);
            Assert.Equal("outgoing", connected.Args["direction"]);
            await Eventually.TrueAsync(() => b.EventsOf(RemoteHub.ConnectedEvent).Length == 1, "B's connected event");
            Assert.Equal("incoming", b.EventsOf(RemoteHub.ConnectedEvent)[0].Args["direction"]);

            // The token never reaches the logs.
            Assert.DoesNotContain(a.Log.Recent(), e => e.Message.Contains(b.Token, StringComparison.Ordinal));

            await a.Hub.DisconnectAsync(link.Id);
            await Eventually.TrueAsync(() => b.Hub.Instances.Count == 0, "B drops A");
            Assert.Single(b.EventsOf(RemoteHub.DisconnectedEvent));
            Assert.Single(a.EventsOf(RemoteHub.DisconnectedEvent));
            Assert.Equal(ConnectionState.Disconnected, Assert.Single(a.Hub.Instances).State);
        }
    }

    [Fact]
    public async Task Signals_travel_both_ways_with_arguments_and_instance_details()
    {
        var (a, b, _) = await LinkedAsync();
        await using (a)
        await using (b)
        {
            var atB = b.Hub.WaitForSignalAsync("go-live", null, TimeSpan.FromSeconds(10), CancellationToken.None);
            var sent = await a.Hub.SendSignalAsync("studio", "go-live", "Start show",
                new Dictionary<string, object?> { ["scene"] = "Main", ["count"] = 3, ["live"] = true });
            Assert.Equal(1, sent);
            var signal = await atB;
            Assert.NotNull(signal);
            Assert.Equal("Start show", signal.Sender);
            Assert.Equal("Main", signal.Args["scene"]);
            Assert.Equal(3L, signal.Args["count"]);
            Assert.Equal(true, signal.Args["live"]);
            Assert.Equal("Alpha", signal.From.Name);
            Assert.Equal("Alpha", signal.Label);

            var e = Assert.Single(b.EventsOf(RemoteHub.SignalEvent));
            Assert.Equal("go-live", e.Args["signalName"]);
            Assert.Equal("Main", e.Args["scene"]);
            Assert.Equal(a.Hub.Self.Id, e.Args["instanceId"]);

            // The same link carries signals back (B never configured A).
            var atA = a.Hub.WaitForSignalAsync("done", "studio", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.Equal(1, await b.Hub.SendSignalAsync("Alpha", "done", "", null));
            var back = await atA;
            Assert.NotNull(back);
            Assert.Equal("Bravo", back.From.Name);
            Assert.Equal("studio", back.Label);

            // Unknown instance name: nobody gets it.
            Assert.Equal(0, await a.Hub.SendSignalAsync("nobody", "go-live", "", null));
        }
    }

    [Fact]
    public async Task Sub_actions_send_wait_and_fire_the_trigger_and_local_waiters()
    {
        var (a, b, _) = await LinkedAsync();
        await using (a)
        await using (b)
        {
            // B: one action waits for the remote signal, another is started by the trigger, a third waits locally.
            var waiting = await b.AddActionAsync("Waiter",
                Instance.Step(WaitForRemoteSignalSubAction.Id, ("name", "cue"), ("instance", "Alpha"), ("timeoutMs", "10000")),
                Instance.Step(CaptureSubAction.Id));
            var triggered = await b.AddActionAsync("On cue", Instance.Step(CaptureSubAction.Id));
            triggered.Triggers.Add(new() { Type = RemoteHub.SignalEvent, Settings = { ["signalName"] = "cue", ["instance"] = "" } });
            b.Profile.NotifyChanged();
            var localWait = b.Signals.WaitAsync("cue", TimeSpan.FromSeconds(10), CancellationToken.None);
            var waitRun = b.Engine.RunAsync(waiting, new Dictionary<string, object?> { ["scene"] = "keep me" });
            await Eventually.TrueAsync(() => b.Hub.Instances.Count == 1, "still linked");
            await Task.Delay(200);   // let the waiter subscribe

            var sender = await a.AddActionAsync("Cue",
                Instance.Step(SendRemoteSignalSubAction.Id, ("instance", ""), ("name", "cue"), ("arguments", "scene=Intro\nmode = %mode%")),
                Instance.Step(CaptureSubAction.Id));
            Assert.True(await a.Engine.RunAsync(sender, new Dictionary<string, object?> { ["mode"] = "fast" }));
            Assert.Equal(1, Assert.Single(a.Capture.Runs)["remoteSignalSent"]);

            Assert.True(await waitRun);
            await Eventually.TrueAsync(() => b.Capture.Runs.Count == 2, "both B actions captured");
            var runs = b.Capture.Runs.ToArray();
            var waited = runs.Single(r => r.ContainsKey("signalTimeout"));
            Assert.Equal(true, waited["signalReceived"]);
            Assert.Equal(false, waited["signalTimeout"]);
            Assert.Equal("keep me", waited["scene"]);   // not overwritten without "Overwrite variables"
            Assert.Equal("fast", waited["mode"]);
            Assert.Equal("Cue", waited["signalSender"]);
            Assert.Equal("Alpha", waited["instanceName"]);
            Assert.Equal(true, waited["hasResult"]);

            var fired = runs.Single(r => !r.ContainsKey("signalTimeout"));
            Assert.Equal("Intro", fired["scene"]);
            Assert.Equal("cue", fired["signalName"]);

            var local = await localWait;
            Assert.NotNull(local);
            Assert.Equal("Intro", local["scene"]);
        }
    }

    [Fact]
    public async Task Wait_times_out_and_can_stop_the_action()
    {
        var b = await Instance.StartAsync("Bravo");
        await using (b)
        {
            Assert.Null(await b.Hub.WaitForSignalAsync("never", null, TimeSpan.FromMilliseconds(150), CancellationToken.None));

            var go = await b.AddActionAsync("Continue",
                Instance.Step(WaitForRemoteSignalSubAction.Id, ("name", "never"), ("timeoutMs", "150")), Instance.Step(CaptureSubAction.Id));
            var stop = await b.AddActionAsync("Stop",
                Instance.Step(WaitForRemoteSignalSubAction.Id, ("name", "never"), ("timeoutMs", "150"), ("stopOnTimeout", "true")),
                Instance.Step(CaptureSubAction.Id));
            await b.Engine.RunAsync(go);
            await b.Engine.RunAsync(stop);
            var run = Assert.Single(b.Capture.Runs);
            Assert.Equal(true, run["signalTimeout"]);
            Assert.Equal(false, run["signalReceived"]);
            Assert.False(run.ContainsKey("instanceName"));
        }
    }

    [Fact]
    public async Task Wrong_token_is_refused_and_reported()
    {
        var a = await Instance.StartAsync("Alpha");
        var b = await Instance.StartAsync("Bravo");
        await using (a)
        await using (b)
        {
            await a.LinkToAsync(b, "studio", token: "cb_wrong");
            var status = Assert.Single(a.Hub.Instances);
            Assert.Equal(ConnectionState.Error, status.State);
            Assert.Contains("wrong token", status.Message);
            Assert.Empty(b.Hub.Instances);
            Assert.Equal(0, await a.Hub.SendSignalAsync("", "x", "", null));
            Assert.DoesNotContain(a.Log.Recent(), e => e.Message.Contains("cb_wrong", StringComparison.Ordinal));

            var empty = a.Hub.AddInstance("no token");
            empty.Port = b.Port;
            await a.Hub.ConnectAsync(empty);
            Assert.Contains("No token", a.Hub.Instances.Single(i => i.Name == "no token").Message);
        }
    }

    [Fact]
    public async Task Api_off_on_the_other_side_is_explained()
    {
        var a = await Instance.StartAsync("Alpha");
        var b = await Instance.StartAsync("Bravo");
        await using (a)
        await using (b)
        {
            b.Manager!.Settings.Api.Enabled = false;   // read per request: the listener answers 404
            await a.LinkToAsync(b, "studio");
            Assert.Contains("REST API is off", Assert.Single(a.Hub.Instances).Message);
        }
    }

    [Fact]
    public async Task Link_reconnects_after_the_other_instance_restarts()
    {
        var (a, b, _) = await LinkedAsync();
        await using (a)
        await using (b)
        {
            var port = b.Port;
            await b.StopServerAsync();
            await Eventually.TrueAsync(() => a.Hub.Instances.Single().State != ConnectionState.Connected, "A notices the drop");
            Assert.Single(a.EventsOf(RemoteHub.DisconnectedEvent));

            await b.StartServerAsync(port);
            await Eventually.TrueAsync(() => a.Hub.Instances.Single().State == ConnectionState.Connected, "A reconnects", 15000);
            Assert.Equal(2, a.EventsOf(RemoteHub.ConnectedEvent).Length);

            var atB = b.Hub.WaitForSignalAsync("again", null, TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.Equal(1, await a.Hub.SendSignalAsync("", "again", "", null));
            Assert.NotNull(await atB);
        }
    }

    [Fact]
    public async Task Two_links_to_the_same_instance_deliver_once()
    {
        var (a, b, _) = await LinkedAsync();
        await using (a)
        await using (b)
        {
            await b.LinkToAsync(a, "main");   // now A↔B twice
            await Eventually.TrueAsync(() => a.Hub.Instances.Count(i => i.State == ConnectionState.Connected) == 2, "A has both links");
            var received = 0;
            b.Hub.SignalReceived += _ => Interlocked.Increment(ref received);
            Assert.Equal(1, await a.Hub.SendSignalAsync("", "once", "", null));
            await Task.Delay(300);
            Assert.Equal(1, received);
        }
    }

    [Fact]
    public async Task An_instance_cannot_link_to_itself()
    {
        var a = await Instance.StartAsync("Alpha");
        await using (a)
        {
            await a.LinkToAsync(a, "me");
            await Eventually.TrueAsync(() => a.Hub.Instances.Single().Message.Contains("itself"), "self link refused");
            Assert.NotEqual(ConnectionState.Connected, a.Hub.State);
        }
    }
}
