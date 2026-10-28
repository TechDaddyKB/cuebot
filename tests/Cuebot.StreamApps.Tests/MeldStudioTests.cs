using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.StreamApps.Meld;
using Cuebot.StreamApps.Tests.Support;
using Ids = Cuebot.StreamApps.Tests.Support.FakeMeld.Ids;

namespace Cuebot.StreamApps.Tests;

public sealed class MeldStudioTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private MeldStudioService _meld = null!;
    private FakeMeld _fake = null!;

    public Task InitializeAsync()
    {
        _meld = new MeldStudioService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, TestTimings.Fast);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _meld.DisposeAsync();
        _env.Dispose();
    }

    private async Task<FakeMeld> ConnectAsync(JsonObject? session = null, bool recording = false, int? version = 2, string[]? methods = null)
    {
        await _meld.ConnectAsync();
        var socket = await _env.Server.NextAsync();
        _fake = new FakeMeld(socket, session ?? FakeMeld.SampleSession(), recording, version: version, methods: methods);
        await Wait.UntilAsync(() => _meld.IsConnected, "connected");
        return _fake;
    }

    [Fact]
    public async Task Handshake_reads_the_published_object_and_reports_connected()
    {
        var fake = await ConnectAsync();

        Assert.Equal(new Uri("ws://127.0.0.1:13376"), fake.Socket.Url);
        Assert.Equal(ConnectionState.Connected, _meld.State);
        Assert.Contains("API v2", _meld.StatusMessage);
        Assert.Equal(2, _meld.ApiVersion);
        Assert.Equal(["Gaming", "Just Chatting"], _meld.Session.Scenes.Select(s => s.Name));
        Assert.Equal("Just Chatting", _meld.Session.CurrentScene!.Name);
        await Wait.UntilAsync(() => fake.Idles >= 1, "idle after init");

        var connected = await _env.WaitForEventAsync(MeldEvents.Connected);
        Assert.Equal("127.0.0.1", connected.Args["meldStudio.host"]);
        Assert.Equal("Meld Studio", connected.Args["meldStudio.name"]);
        Assert.True(Guid.TryParse((string)connected.Args["meldStudio.id"]!, out _));

        // Track gain observation: connect to the pure gainUpdated signal and register every track.
        await Wait.UntilAsync(() => fake.ConnectedSignals.Contains(FakeMeld.GainSignal), "connectToSignal");
        await Wait.UntilAsync(() => fake.CallCount("registerTrackObserver") == 2, "track observers");
        Assert.All(fake.Calls.Where(c => c.Method == "registerTrackObserver"), c => Assert.Equal("cuebot", c.Args[0].GetString()));
    }

    [Fact]
    public async Task Commands_resolve_names_to_ids_and_invoke_by_name_with_matching_argument_counts()
    {
        var fake = await ConnectAsync();

        Assert.Equal(Ids.Gaming, (await _meld.ShowSceneAsync("gaming")).Id);
        var (_, args) = await fake.NextCallAsync("showScene");
        Assert.Equal(Ids.Gaming, args[0].GetString());

        // Visible layer: Hidden toggles it, Visible does nothing.
        Assert.False(await _meld.SetLayerVisibilityAsync("Gaming", "Camera", SwitchMode.Off));
        (_, args) = await fake.NextCallAsync("toggleLayer");
        Assert.Equal([Ids.Gaming, Ids.Camera], args.EnumerateArray().Select(a => a.GetString()));
        Assert.True(await _meld.SetLayerVisibilityAsync("", "Camera", SwitchMode.On));
        Assert.Equal(1, fake.CallCount("toggleLayer"));

        Assert.False(await _meld.SetEffectEnabledAsync("Gaming", "Camera", "Glow Styles", SwitchMode.Toggle));
        (_, args) = await fake.NextCallAsync("toggleEffect");
        Assert.Equal([Ids.Gaming, Ids.Camera, Ids.Glow], args.EnumerateArray().Select(a => a.GetString()));

        // The microphone is muted: Not Muted unmutes, Muted does nothing.
        Assert.False(await _meld.SetTrackMutedAsync("Microphone", SwitchMode.Off));
        Assert.True(await _meld.SetTrackMutedAsync("microphone", SwitchMode.On));
        Assert.Equal(1, fake.CallCount("toggleMute"));
        Assert.Equal(Ids.Mic, (await fake.NextCallAsync("toggleMute")).Args[0].GetString());

        Assert.True(await _meld.SetLayerTrackMutedAsync("Just Chatting", "Spoilers", SwitchMode.On));
        await Wait.UntilAsync(() => fake.Calls.Count(c => c.Method == "toggleMute") == 2);
        Assert.Equal(Ids.SpoilersTrack, fake.Calls.Last(c => c.Method == "toggleMute").Args[0].GetString());

        await _meld.SetBrowserLayerUrlAsync("", "Spoilers", "https://cuebot.example/overlay");
        (_, args) = await fake.NextCallAsync("setProperty");
        Assert.Equal([Ids.Spoilers, "url", "https://cuebot.example/overlay"], args.EnumerateArray().Select(a => a.GetString()));

        await _meld.TakeScreenshotAsync(vertical: true);
        Assert.Equal("meld.screenshot.vertical", (await fake.NextCallAsync("sendCommand")).Args[0].GetString());

        await _meld.RecordClipAsync(30);
        (_, args) = await fake.NextCallAsync("sendCommandWithArgs");
        Assert.Equal("meld.recordClip", args[0].GetString());
        Assert.Equal(30, args[1][0].GetDouble());

        await _meld.SetTrackGainAsync("Microphone", 1.5);
        Assert.Equal(1, (await fake.NextCallAsync("setGain")).Args[1].GetDouble());

        await _meld.SendStreamEventAsync("SUBATHONTIMER_ADDTIME", 120);
        Assert.Equal(120, (await fake.NextCallAsync("sendStreamEvent")).Args[1].GetProperty("amount").GetDouble());
        // Without data the event still goes out with two arguments (false), like Meld's own plugin sends it.
        await _meld.SendStreamEventAsync("CONFETTIPOP_TRIGGER", null);
        await Wait.UntilAsync(() => fake.CallCount("sendStreamEvent") == 2);
        var confetti = fake.Calls.Last(c => c.Method == "sendStreamEvent").Args;
        Assert.Equal("CONFETTIPOP_TRIGGER", confetti[0].GetString());
        Assert.Equal(JsonValueKind.False, confetti[1].ValueKind);

        await _meld.MediaAsync("", "Spoilers", "Seek", 12);
        (_, args) = await fake.NextCallAsync("callFunctionWithArgs");
        Assert.Equal("seekTo", args[1].GetString());

        // Every call named its method (as qwebchannel.js does) and matched an overload by argument count.
        Assert.Equal(0, fake.IndexCalls);
        Assert.Empty(fake.Unmatched);

        var unknown = await Assert.ThrowsAsync<StreamAppRequestException>(() => _meld.ShowSceneAsync("Nope"));
        Assert.Contains("no scene 'Nope'", unknown.Message);
        await Assert.ThrowsAsync<StreamAppRequestException>(() => _meld.SetLayerVisibilityAsync("Gaming", "Spoilers", SwitchMode.On));
    }

    [Fact]
    public async Task Vertical_scene_twins_are_kept_apart_from_the_scene_list_and_cannot_be_staged()
    {
        var fake = await ConnectAsync();

        // "Gaming" by name is the horizontal scene even though its vertical twin has the same name.
        Assert.DoesNotContain(_meld.Session.Scenes, s => s.Id == Ids.GamingVertical);
        Assert.Equal(Ids.GamingVertical, Assert.Single(_meld.Session.VerticalScenes).Id);
        Assert.Equal(Ids.Gaming, (await _meld.StageSceneAsync("Gaming")).Id);
        Assert.Equal(Ids.Gaming, (await fake.NextCallAsync("setStagedScene")).Args[0].GetString());

        // By id the twin can be shown, but staging it would be a silent no-op in Meld.
        await _meld.ShowSceneAsync(Ids.GamingVertical);
        Assert.Equal(Ids.GamingVertical, (await fake.NextCallAsync("showScene")).Args[0].GetString());
        var error = await Assert.ThrowsAsync<StreamAppRequestException>(() => _meld.StageSceneAsync(Ids.GamingVertical));
        Assert.Contains("vertical", error.Message);
    }

    [Fact]
    public async Task Recording_and_streaming_toggle_only_when_the_state_differs_and_updates_raise_events()
    {
        var fake = await ConnectAsync(recording: true);
        Assert.True(_meld.IsRecording);

        Assert.True(await _meld.SetRecordingAsync(SwitchMode.On));
        Assert.Equal(0, fake.CallCount("toggleRecord"));
        Assert.False(await _meld.SetRecordingAsync(SwitchMode.Off));
        await fake.NextCallAsync("toggleRecord");
        Assert.True(await _meld.SetStreamingAsync(SwitchMode.Toggle));
        await fake.NextCallAsync("toggleStream");

        var idles = fake.Idles;
        fake.PushRecording(false);
        await _env.WaitForEventAsync(MeldEvents.RecordingStopped);
        Assert.False(_meld.IsRecording);
        await Wait.UntilAsync(() => fake.Idles > idles, "idle after a property update");
        fake.PushStreaming(true);
        var started = await _env.WaitForEventAsync(MeldEvents.StreamingStarted);
        Assert.Equal("Meld Studio", started.Args["meldStudio.name"]);
        fake.PushStreaming(true);   // unchanged: no second event
        fake.PushStreaming(false);
        await _env.WaitForEventAsync(MeldEvents.StreamingStopped);
        Assert.Single(_env.Events(MeldEvents.StreamingStarted));
    }

    [Fact]
    public async Task Session_updates_become_scene_layer_effect_track_and_object_events()
    {
        var fake = await ConnectAsync();
        var next = FakeMeld.SampleSession();
        next[Ids.Chatting]!["current"] = false;
        next[Ids.Gaming]!["current"] = true;
        next[Ids.Camera]!["visible"] = false;
        next[Ids.Glow]!["enabled"] = false;
        next[Ids.Mic]!["muted"] = false;
        next[Ids.SpoilersTrack]!["monitoring"] = true;
        next[Ids.Spoilers]!["x"] = 42;
        next[Ids.NewLayer] = new JsonObject { ["type"] = "layer", ["name"] = "Image 1", ["parent"] = Ids.Gaming, ["index"] = 1, ["visible"] = true };
        fake.PushSession(next);

        var scene = await _env.WaitForEventAsync(MeldEvents.SceneChanged);
        await Wait.UntilAsync(() => _env.Events(MeldEvents.Event).Length >= 6, "session events");
        Assert.Equal("Gaming", scene.Args["meldStudio.sceneName"]);
        Assert.Equal("Just Chatting", scene.Args["meldStudio.oldSceneName"]);
        Assert.Equal("Gaming", _meld.Session.CurrentScene!.Name);

        BotEventFor(MeldEvents.LayerVisibilityChanged, out var layer);
        Assert.Equal(Ids.Camera, layer.Args["meldStudioEvent.layerId"]);
        Assert.Equal("Camera", layer.Args["meldStudioEvent.layerName"]);
        Assert.Equal(false, layer.Args["meldStudioEvent.visible"]);
        using (var json = JsonDocument.Parse((string)layer.Args["meldStudioEvent._json"]!))
            Assert.False(json.RootElement.GetProperty("visible").GetBoolean());

        BotEventFor(MeldEvents.EffectEnabledStateChanged, out var effect);
        Assert.Equal("Glow Styles", effect.Args["meldStudioEvent.effectName"]);
        Assert.Equal(false, effect.Args["meldStudioEvent.enabled"]);
        BotEventFor(MeldEvents.TrackMutedStateChanged, out var muted);
        Assert.Equal("Microphone", muted.Args["meldStudioEvent.trackName"]);
        Assert.Equal(false, muted.Args["meldStudioEvent.muted"]);
        BotEventFor(MeldEvents.TrackMonitoringStateChanged, out var monitoring);
        Assert.Equal("Spoilers", monitoring.Args["meldStudioEvent.trackName"]);
        BotEventFor(MeldEvents.ObjectAdded, out var added);
        Assert.Equal("layer", added.Args["meldStudioEvent.objectType"]);
        Assert.Equal("Image 1", added.Args["meldStudioEvent.object.name"]);
        Assert.Equal(Ids.Gaming, added.Args["meldStudioEvent.object.parent"]);
        BotEventFor(MeldEvents.ObjectUpdated, out var updated);
        Assert.Equal(Ids.Spoilers, updated.Args["meldStudioEvent.id"]);
        // Only the x change counts as an "other" update; visibility/mute changes have their own events.
        Assert.Single(_env.Events(MeldEvents.Event), e => (string?)e.Args["meldStudioEvent.event"] == MeldEvents.ObjectUpdated);

        // The new track-less layer adds no observer; removing an item raises ObjectRemoved.
        var smaller = FakeMeld.SampleSession();
        smaller.Remove(Ids.Glow);
        fake.PushSession(smaller);
        await _env.WaitForEventAsync(MeldEvents.Event, e => (string?)e.Args["meldStudioEvent.event"] == MeldEvents.ObjectRemoved);
    }

    [Fact]
    public async Task Gain_signal_raises_track_gain_changed_for_gain_and_mute_changes_but_not_the_registration_report()
    {
        var fake = await ConnectAsync();
        // Meld answers each registerTrackObserver with the current gain: that is the baseline, not an event.
        await Wait.UntilAsync(() => _meld.TrackGain(Ids.Mic) == 0.5 && _meld.TrackGain(Ids.SpoilersTrack) == 0.5, "baseline gains");
        Assert.Empty(GainEvents());

        fake.PushGain(Ids.Mic, 0.25, true);
        var e = await _env.WaitForEventAsync(MeldEvents.Event, x => (string?)x.Args["meldStudioEvent.event"] == MeldEvents.TrackGainChanged);
        Assert.Equal("Microphone", e.Args["meldStudioEvent.trackName"]);
        Assert.Equal(0.25, e.Args["meldStudioEvent.gain"]);
        Assert.Equal(0.25, _meld.TrackGain(Ids.Mic));

        // "when the gain or mute status of a track changes": a mute-only change is an event too.
        fake.PushGain(Ids.Mic, 0.25, false);
        await Wait.UntilAsync(() => GainEvents().Length == 2, "mute-only gain event");
        Assert.Equal(false, GainEvents()[1].Args["meldStudioEvent.muted"]);
        fake.PushGain(Ids.Mic, 0.25, false);   // unchanged
        await Task.Delay(100);
        Assert.Equal(2, GainEvents().Length);
    }

    [Fact]
    public async Task Track_observers_are_registered_again_after_every_session_change()
    {
        var fake = await ConnectAsync();
        await Wait.UntilAsync(() => fake.CallCount("registerTrackObserver") == 2, "first registration");

        // Meld drops its observers when it rebuilds the audio tracks; the same track ids come back.
        fake.PushSession(FakeMeld.SampleSession());
        await Wait.UntilAsync(() => fake.CallCount("registerTrackObserver") == 4, "registration after a session change");
        Assert.Empty(GainEvents());
    }

    private Cuebot.Core.Events.BotEvent[] GainEvents() =>
        _env.Events(MeldEvents.Event).Where(x => (string?)x.Args["meldStudioEvent.event"] == MeldEvents.TrackGainChanged).ToArray();

    [Fact]
    public async Task Layer_source_sets_the_documented_image_or_media_property_as_a_file_url()
    {
        // Meld's documented layer properties: "source" (image layer) and "mediaSource" (media layer).
        const string image = "8DB9CC06AFCA152EA30278380B569751", media = "7958A12F7C782F392B08A793218AB7E8";
        var session = FakeMeld.SampleSession();
        session[image] = new JsonObject { ["type"] = "layer", ["name"] = "Camera Frame", ["parent"] = Ids.Chatting, ["index"] = 2, ["visible"] = true, ["source"] = "file:///path/to/image.png" };
        session[media] = new JsonObject { ["type"] = "layer", ["name"] = "Background", ["parent"] = Ids.Chatting, ["index"] = 3, ["visible"] = true, ["mediaSource"] = "file:///path/to/video.mp4" };
        var fake = await ConnectAsync(session);

        await _meld.SetLayerSourceAsync("Just Chatting", "Camera Frame", "https://cuebot.example/frame.png");
        var (_, args) = await fake.NextCallAsync("setProperty");
        Assert.Equal([image, "source", "https://cuebot.example/frame.png"], args.EnumerateArray().Select(a => a.GetString()));

        var file = Path.Combine(Path.GetTempPath(), "cuebot clip.mp4");
        await _meld.SetLayerSourceAsync("", "background", file);
        await Wait.UntilAsync(() => fake.CallCount("setProperty") == 2);
        var last = fake.Calls.Last(c => c.Method == "setProperty").Args;
        Assert.Equal("mediaSource", last[1].GetString());
        Assert.Equal(new Uri(file).AbsoluteUri, last[2].GetString());
        Assert.StartsWith("file:///", last[2].GetString());

        // A browser layer has neither property.
        await Assert.ThrowsAsync<StreamAppRequestException>(() => _meld.SetLayerSourceAsync("", "Spoilers", "x.png"));
    }

    [Fact]
    public async Task Version_1_servers_without_setProperty_get_a_clear_error()
    {
        await ConnectAsync(version: null, methods: FakeMeld.DefaultMethods.Where(m => FakeMeld.BareName(m) != "setProperty").ToArray());
        Assert.Equal(1, _meld.ApiVersion);
        var error = await Assert.ThrowsAsync<StreamAppRequestException>(() => _meld.SetBrowserLayerUrlAsync("", "Spoilers", "https://x"));
        Assert.Contains("API v2", error.Message);
    }

    [Fact]
    public async Task Reconnects_after_Meld_closes_and_reports_disconnected()
    {
        var fake = await ConnectAsync();
        await _env.WaitForEventAsync(MeldEvents.Connected);
        fake.Socket.Close();

        var disconnected = await _env.WaitForEventAsync(MeldEvents.Disconnected);
        Assert.Contains("closed", (string)disconnected.Args["reason"]!);
        var second = await _env.Server.NextAsync();
        _ = new FakeMeld(second, FakeMeld.SampleSession());
        await Wait.UntilAsync(() => _env.Events(MeldEvents.Connected).Length == 2, "second connected event");
        Assert.True(_meld.IsConnected);
    }

    [Fact]
    public async Task Keeps_retrying_while_Meld_is_not_running_and_stops_when_reconnect_is_off()
    {
        _env.Server.Reject = _ => new System.Net.WebSockets.WebSocketException("Unable to connect to the remote server");
        await _meld.ConnectAsync();
        await Wait.UntilAsync(() => _env.Server.Attempts.Count >= 3, "retries");
        Assert.Equal(ConnectionState.Connecting, _meld.State);
        Assert.Contains("not reachable", _meld.StatusMessage);
        await _meld.DisconnectAsync();

        _meld.Settings.AutoReconnect = false;
        var before = _env.Server.Attempts.Count;
        await _meld.ConnectAsync();
        await Wait.UntilAsync(() => !_meld.IsRunning, "loop ends");
        Assert.Equal(before + 1, _env.Server.Attempts.Count);
        Assert.Equal(ConnectionState.Disconnected, _meld.State);
    }

    [Fact]
    public async Task Commands_fail_cleanly_when_not_connected()
    {
        await Assert.ThrowsAsync<StreamAppNotConnectedException>(() => _meld.ShowSceneAsync("Gaming"));
        await Assert.ThrowsAsync<StreamAppNotConnectedException>(() => _meld.SetRecordingAsync(SwitchMode.On));
    }

    [Fact]
    public async Task A_server_without_the_meld_object_is_not_treated_as_connected()
    {
        _meld.Settings.AutoReconnect = false;
        await _meld.ConnectAsync();
        var socket = await _env.Server.NextAsync();
        var init = JsonDocument.Parse(await socket.NextSentAsync()).RootElement;
        Assert.Equal(3, init.GetProperty("type").GetInt32());
        socket.Push("{\"type\":10,\"id\":" + init.GetProperty("id").GetInt64() + ",\"data\":{\"other\":{\"methods\":[],\"properties\":[],\"signals\":[]}}}");
        await Wait.UntilAsync(() => !_meld.IsRunning, "loop ends");
        Assert.Empty(_env.Events(MeldEvents.Connected));
        Assert.Contains("meld", _meld.StatusMessage);
    }

    private void BotEventFor(string eventName, out Cuebot.Core.Events.BotEvent found) =>
        found = _env.Events(MeldEvents.Event).Single(e => (string?)e.Args["meldStudioEvent.event"] == eventName);
}
