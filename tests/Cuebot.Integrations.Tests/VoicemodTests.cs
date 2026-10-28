using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Integrations.Common;
using Cuebot.Integrations.Tests.Support;
using Cuebot.Integrations.Voicemod;

namespace Cuebot.Integrations.Tests;

/// <summary>A scripted Voicemod desktop app speaking the Control API.</summary>
internal sealed class FakeVoicemod
{
    private readonly FakeSocket _socket;

    public FakeVoicemod(FakeSocket socket)
    {
        _socket = socket;
        _ = Task.Run(LoopAsync);
    }

    public const string ValidKey = "VM-client-key-ABCDEFGH12345678";

    public ConcurrentQueue<(string Action, JsonElement Payload)> Received { get; } = new();
    public string CurrentVoice { get; set; } = "nofx";
    public bool VoiceChanger { get; set; } = true;
    public bool HearMyself { get; set; }

    /// <summary>Answer status requests with a null actionID (the docs say this happens).</summary>
    public bool NullIds { get; set; }

    public (string Action, JsonElement Payload)[] Of(string action) => Received.Where(r => r.Action == action).ToArray();

    public void Event(string actionType, object actionObject) =>
        _socket.Push(JsonUtil.Serialize(new { actionType, appVersion = "3.0.0", actionID = (string?)null, actionObject }));

    private void Reply(string actionType, string id, object actionObject) =>
        _socket.Push(JsonUtil.Serialize(new { actionType, appVersion = "3.0.0", actionID = NullIds ? null : id, actionObject }));

    private async Task LoopAsync()
    {
        while (true)
        {
            JsonElement m;
            try
            {
                m = await _socket.NextJsonAsync();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            var action = m.GetProperty("action").GetString()!;
            var id = m.GetProperty("id").GetString()!;
            var payload = m.GetProperty("payload").Clone();
            Received.Enqueue((action, payload));
            switch (action)
            {
                case "registerClient":
                    var ok = payload.GetProperty("clientKey").GetString() == ValidKey;
                    _socket.Push(JsonUtil.Serialize(new
                    {
                        action = "registerClient",
                        id,
                        payload = new { status = new { code = ok ? 200 : 401, description = ok ? "Authorized" : "unauthorized" } },
                    }));
                    break;
                case "getVoices":
                    Reply("getVoices", id, new
                    {
                        voices = new[]
                        {
                            new { id = "nofx", friendlyName = "Clean", enabled = true, favorited = false, isCustom = false },
                            new { id = "baby", friendlyName = "Baby", enabled = true, favorited = true, isCustom = false },
                            new { id = "cave", friendlyName = "Cave", enabled = true, favorited = false, isCustom = false },
                        },
                        currentVoice = CurrentVoice,
                    });
                    break;
                case "getCurrentVoice":
                    Reply("getCurrentVoice", id, new { voiceID = CurrentVoice, parameters = Array.Empty<object>() });
                    break;
                case "getMemes":
                    Reply("getMemes", id, new { listOfMemes = new[] { new { Name = "Airhorn", FileName = "78", Type = "PlayRestart", Image = "" } } });
                    break;
                case "getVoiceChangerStatus":
                    Reply("toggleVoiceChanger", id, new { value = VoiceChanger });
                    break;
                case "getHearMyselfStatus":
                    Reply("toggleHearMyVoice", id, new { value = HearMyself });
                    break;
                case "getBackgroundEffectStatus":
                    Reply("toggleBackground", id, new { value = false });
                    break;
                case "getMuteMicStatus":
                    Reply("toggleMuteMic", id, new { value = false });
                    break;
                case "loadVoice":
                    CurrentVoice = payload.GetProperty("voiceID").GetString()!;
                    Event("voiceChangedEvent", new { voiceID = CurrentVoice });
                    break;
                case "toggleVoiceChanger":
                    VoiceChanger = !VoiceChanger;
                    Event("toggleVoiceChanger", new { value = VoiceChanger });
                    break;
                case "toggleHearMyVoice":
                    HearMyself = !HearMyself;
                    Event("toggleHearMyVoice", new { value = HearMyself });
                    break;
            }
        }
    }
}

public sealed class VoicemodTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private VoicemodService _voicemod = null!;
    private FakeSocket _socket = null!;

    public Task InitializeAsync()
    {
        _env.Settings.Value.Voicemod.ClientKey = FakeVoicemod.ValidKey;
        _voicemod = new VoicemodService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, TestTimings.Fast);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _voicemod.DisposeAsync();
        _env.Dispose();
    }

    private async Task<FakeVoicemod> ConnectAsync(Action<FakeVoicemod>? setup = null)
    {
        await _voicemod.ConnectAsync();
        _socket = await _env.Server.NextAsync();
        var fake = new FakeVoicemod(_socket);
        setup?.Invoke(fake);
        await Wait.UntilAsync(() => _voicemod.State == ConnectionState.Connected, "connected");
        await Wait.UntilAsync(() => _voicemod.GetSwitch(VoicemodSwitch.MuteMic) is not null, "initial state");
        return fake;
    }

    private static async Task<(string Action, JsonElement Payload)> SentAsync(FakeVoicemod fake, string action)
    {
        await Wait.UntilAsync(() => fake.Of(action).Length > 0, action);
        return fake.Of(action).Single();
    }

    [Fact]
    public async Task Registers_with_the_client_key_first_and_loads_the_state()
    {
        var fake = await ConnectAsync();
        Assert.Equal("ws://localhost:59129/v1", _socket.Url!.ToString());
        Assert.Equal("registerClient", fake.Received.First().Action);
        Assert.Equal(FakeVoicemod.ValidKey, fake.Received.First().Payload.GetProperty("clientKey").GetString());
        Assert.Equal(["Clean", "Baby", "Cave"], _voicemod.Voices.Select(v => v.DisplayName));
        Assert.Equal("Clean", _voicemod.CurrentVoiceName);
        Assert.True(_voicemod.GetSwitch(VoicemodSwitch.VoiceChanger));
        Assert.False(_voicemod.GetSwitch(VoicemodSwitch.HearMyself));
        Assert.Equal("Airhorn", Assert.Single(_voicemod.Sounds).Name);
        Assert.Single(_env.Events(VoicemodEvents.Connected));
        // Loading the initial state is not a change.
        Assert.Empty(_env.Events(VoicemodEvents.VoiceChanged));
        Assert.Empty(_env.Events(VoicemodEvents.VoiceChangerToggled));
        Assert.Equal(59129, _env.Settings.Value.Voicemod.LastPort);
        Assert.DoesNotContain(FakeVoicemod.ValidKey, _env.AllLogText());
    }

    [Fact]
    public async Task Cycles_through_the_documented_ports()
    {
        _env.Server.Reject = url => url.Port == 20000 ? null : FakeServer.Refused();
        await ConnectAsync();
        Assert.Equal(20000, _socket.Url!.Port);
        Assert.Equal([59129, 20000], _env.Server.Attempts.Select(a => a.Url.Port));
        Assert.Equal(20000, _env.Settings.Value.Voicemod.LastPort);
        Assert.Equal(20000, _voicemod.ConnectedPort);
    }

    [Fact]
    public async Task Rejected_key_stops_with_an_error()
    {
        _env.Settings.Value.Voicemod.ClientKey = "VM-wrong-client-key-000000000000";
        await _voicemod.ConnectAsync();
        _ = new FakeVoicemod(await _env.Server.NextAsync());
        await Wait.UntilAsync(() => _voicemod.State == ConnectionState.Error, "error");
        Assert.Contains("rejected the client key", _voicemod.StatusMessage);
        Assert.DoesNotContain("VM-wrong-client-key-000000000000", _voicemod.StatusMessage);
        Assert.DoesNotContain("VM-wrong-client-key-000000000000", _env.AllLogText());
        await Task.Delay(100);
        Assert.Single(_env.Server.Attempts);
    }

    [Fact]
    public async Task Missing_key_is_reported_before_connecting()
    {
        _env.Settings.Value.Voicemod.ClientKey = " ";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _voicemod.ConnectAsync());
        Assert.Contains("client key", error.Message);
        Assert.Equal(ConnectionState.Error, _voicemod.State);
        Assert.Empty(_env.Server.Attempts);
    }

    [Fact]
    public async Task Commands_and_change_events()
    {
        var fake = await ConnectAsync();

        var voice = await _voicemod.SelectVoiceAsync("baby");
        Assert.Equal("baby", voice.Id);
        Assert.Equal("baby", (await SentAsync(fake, "loadVoice")).Payload.GetProperty("voiceID").GetString());
        var changed = await _env.WaitForEventAsync(VoicemodEvents.VoiceChanged);
        Assert.Equal("Baby", changed.Args["voiceName"]);
        Assert.Equal("Clean", changed.Args["previousVoiceName"]);
        Assert.Equal("baby", changed.Args["voice.id"]);
        Assert.Equal("Baby", changed.Args["voice.name"]);
        await Assert.ThrowsAsync<ArgumentException>(() => _voicemod.SelectVoiceAsync("Robot"));

        // On while already on: no toggle. Off: toggles once.
        Assert.True(await _voicemod.SetSwitchAsync(VoicemodSwitch.VoiceChanger, SwitchMode.On));
        Assert.Empty(fake.Of("toggleVoiceChanger"));
        Assert.False(await _voicemod.SetSwitchAsync(VoicemodSwitch.VoiceChanger, SwitchMode.Off));
        await SentAsync(fake, "toggleVoiceChanger");
        var toggled = await _env.WaitForEventAsync(VoicemodEvents.VoiceChangerToggled);
        Assert.Equal(false, toggled.Args["enabled"]);
        Assert.True(await _voicemod.SetSwitchAsync(VoicemodSwitch.HearMyself, SwitchMode.Toggle));
        await _env.WaitForEventAsync(VoicemodEvents.HearMyselfToggled, e => e.Args["enabled"] is true);

        Assert.Equal("Airhorn", (await _voicemod.PlaySoundAsync("airhorn")).Name);
        var play = (await SentAsync(fake, "playMeme")).Payload;
        Assert.Equal("78", play.GetProperty("FileName").GetString());
        Assert.True(play.GetProperty("IsKeyDown").GetBoolean());
        await _voicemod.StopAllSoundsAsync();
        await _voicemod.SelectRandomVoiceAsync("FavoriteVoices");
        Assert.Equal("FavoriteVoices", (await SentAsync(fake, "selectRandomVoice")).Payload.GetProperty("mode").GetString());
        await _voicemod.SetBeepAsync(true);
        Assert.Equal(1, (await SentAsync(fake, "setBeepSound")).Payload.GetProperty("badLanguage").GetInt32());
        await Wait.UntilAsync(() => fake.Of("stopAllMemeSounds").Length == 1, "stop");

        // Changes made in the app arrive as events.
        fake.Event("toggleMuteMic", new { value = true });
        Assert.Equal(true, (await _env.WaitForEventAsync(VoicemodEvents.MuteToggled)).Args["muted"]);
        fake.Event("getMemes", new { listOfMemes = Array.Empty<object>() });
        await _env.WaitForEventAsync(VoicemodEvents.SoundboardChanged);
        fake.Event("getAllSoundboard", new { soundboards = new[] { new { id = "sb-1", name = "My Test Soundboard", sounds = Array.Empty<object>() } } });
        fake.Event("getActiveSoundboardProfile", new { profileId = "sb-1" });
        var soundboard = await _env.WaitForEventAsync(VoicemodEvents.SoundboardChanged, e => e.Args.ContainsKey("profileId"));
        Assert.Equal("sb-1", soundboard.Args["soundboard.id"]);
        Assert.Equal("My Test Soundboard", soundboard.Args["soundboard.name"]);
        fake.Event("toggleBackground", new { value = true });
        await _env.WaitForEventAsync(VoicemodEvents.BackgroundEffectsToggled);
    }

    [Fact]
    public async Task Answers_without_an_action_id_are_matched_by_kind()
    {
        var fake = await ConnectAsync(f => f.NullIds = true);
        var (id, name) = await _voicemod.GetCurrentVoiceAsync();
        Assert.Equal("nofx", id);
        Assert.Equal("Clean", name);
        Assert.False(await _voicemod.SetSwitchAsync(VoicemodSwitch.HearMyself, SwitchMode.Off));
        Assert.Empty(fake.Of("toggleHearMyVoice"));
    }

    [Fact]
    public async Task Reconnects_and_clears_the_state()
    {
        await ConnectAsync();
        _socket.Close();
        await _env.WaitForEventAsync(VoicemodEvents.Disconnected);
        await Assert.ThrowsAsync<IntegrationNotConnectedException>(() => _voicemod.StopAllSoundsAsync());
        var second = await _env.Server.NextAsync();
        _ = new FakeVoicemod(second);
        await Wait.UntilAsync(() => _voicemod.State == ConnectionState.Connected, "reconnected");
        Assert.Equal(2, _env.Events(VoicemodEvents.Connected).Length);
        // The last port is tried first.
        Assert.Equal(59129, _env.Server.Attempts[^1].Url.Port);
    }

    [Fact]
    public void Parses_documented_message_shapes()
    {
        var register = VoicemodProtocol.Parse("""{"action":"registerClient","id":"x","payload":{"status":{"code":200,"description":"Authorized"}}}""")!;
        Assert.Equal("registerClient", register.Kind);
        Assert.Equal("x", register.Id);
        Assert.True(VoicemodProtocol.RegisterResult(register).Ok);
        var mute = VoicemodProtocol.Parse("""{"actionType":"toggleMute","actionObject":{"value":true}}""")!;
        Assert.Equal("toggleMuteMic", mute.Kind);
        Assert.Null(mute.Id);
        Assert.True(VoicemodProtocol.Value(mute));
        var profile = VoicemodProtocol.Parse("""{"type":"getActiveSoundboardProfile","payload":{"profileId":"p1"}}""")!;
        Assert.Equal("getActiveSoundboardProfile", profile.Kind);
        Assert.Equal("p1", profile.Body.GetProperty("profileId").GetString());
        var voices = VoicemodProtocol.Parse("""{"actionType":"getVoices","actionObject":{"voices":[{"id":"baby","friendlyName":"Baby"}]},"currentVoice":"nofx"}""")!;
        Assert.Equal("nofx", VoicemodProtocol.CurrentVoiceOf(voices));
        Assert.Equal("Baby", Assert.Single(VoicemodProtocol.ParseVoices(voices)).Name);
    }
}
