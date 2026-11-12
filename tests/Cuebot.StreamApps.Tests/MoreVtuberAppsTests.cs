using System.Text.Json;
using Cuebot.Core;
using Cuebot.StreamApps.Animaze;
using Cuebot.StreamApps.Obsbot;
using Cuebot.StreamApps.PngtuberRemix;
using Cuebot.StreamApps.Tests.Support;
using Cuebot.StreamApps.Vmc;

namespace Cuebot.StreamApps.Tests;

public sealed class PngtuberRemixServiceTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private PngtuberRemixService _remix = null!;
    private FakeSocket _socket = null!;

    private static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement;

    private async Task<JsonElement> NextAsync(string expectedEvent)
    {
        var sent = Parse(await _socket.NextSentAsync());
        Assert.Equal(expectedEvent, sent.GetProperty("event").GetString());
        return sent;
    }

    public async Task InitializeAsync()
    {
        _env.Settings.Value.PngtuberRemix.PollSeconds = 0; // these tests answer by hand: no polling, no ping
        _remix = new PngtuberRemixService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, TestTimings.Fast) { PingInterval = TimeSpan.FromMinutes(10) };
        await _remix.ConnectAsync();
        _socket = await _env.Server.NextAsync();
        Assert.Equal("ws://127.0.0.1:9321/", _socket.Url!.ToString());
        // Answers as PNGTuber Remix 1.4.7 does (captured live).
        await NextAsync("list_states");
        _socket.Push("""{"event":"list_states","result":"success","states":[{"id":1,"is_current":true,"name":"Idle"},{"id":2,"is_current":false,"name":"Smug"}]}""");
        await NextAsync("list_sprites");
        _socket.Push("""{"event":"list_sprites","result":"success","sprites":[{"name":"Head","visible":true,"id":"11","is_group":true,"children":["Hat"],"parent_id":0},{"name":"Hat","visible":false,"id":"12","is_group":false,"children":[],"parent_id":11}]}""");
        await Wait.UntilAsync(() => _remix.StatusMessage.Contains("sprites"), "list");
    }

    public async Task DisposeAsync()
    {
        await _remix.DisposeAsync();
        _env.Dispose();
    }

    [Fact]
    public async Task Lists_states_and_sprites_on_connect()
    {
        Assert.Equal("Connected to PNGTuber Remix at 127.0.0.1:9321: 2 states, 2 sprites", _remix.StatusMessage);
        Assert.Equal(["Idle", "Smug"], _remix.StateNames());
        Assert.Equal(["Head", "Hat"], _remix.SpriteNames());
        Assert.Equal(["Head"], _remix.GroupNames());
        await _env.WaitForEventAsync(PngtuberRemixEvents.Connected);
    }

    [Fact]
    public async Task States_go_by_name_or_number_and_failures_are_explained()
    {
        var byName = _remix.SetStateAsync("smug");
        Assert.Equal("Smug", (await NextAsync("state")).GetProperty("state_name").GetString());   // PNGTuber Remix's own spelling
        _socket.Push("""{"event":"state","result":"success","state_id":2,"state_name":"Smug"}""");
        Assert.Equal("Smug", await byName);
        Assert.Equal("Smug", Assert.Single(_remix.States, st => st.IsCurrent).Name);   // the showing state follows

        var byNumber = _remix.SetStateAsync("1");
        Assert.Equal(1, (await NextAsync("state")).GetProperty("state_id").GetInt32());
        _socket.Push("""{"event":"state","result":"success","state_id":1,"state_name":"Idle"}""");
        Assert.Equal("Idle", await byNumber);

        var missing = _remix.SetStateAsync("zzz");
        await NextAsync("state");
        _socket.Push("""{"available_states":2,"error":"state not found","event":"state","identifier":"zzz","result":"failed"}""");
        var ex = await Assert.ThrowsAsync<StreamAppRequestException>(() => missing);
        Assert.Equal("PNGTuber Remix has no state 'zzz' (capital letters matter); its states are Idle, Smug.", ex.Message);

        var unknown = _remix.ThrowAsync("", null);
        await NextAsync("throw_random");
        _socket.Push("""{"event":"error","message":"Unknown event: throw_random"}""");
        Assert.Contains("Unknown event", (await Assert.ThrowsAsync<StreamAppRequestException>(() => unknown)).Message);
    }

    [Fact]
    public async Task Sprites_are_shown_toggled_and_moved()
    {
        var show = _remix.SetVisibilityAsync("Hat", SwitchMode.On);
        Assert.Equal("Hat", (await NextAsync("show_sprite")).GetProperty("sprite_name").GetString());
        _socket.Push("""{"event":"show_sprite","result":"success","identifier":"Hat"}""");
        Assert.True(await show);

        var toggle = _remix.SetVisibilityAsync("Head", SwitchMode.Toggle);
        Assert.Equal("Head", (await NextAsync("toggle_sprite")).GetProperty("sprite_name").GetString());
        _socket.Push("""{"event":"toggle_sprite","result":"success","identifier":"Head","visibility":"hidden"}""");
        Assert.False(await toggle);

        var shake = _remix.ShakeAsync("Head", null, 2);
        var sent = await NextAsync("shake_sprite");
        Assert.Equal(10, sent.GetProperty("intensity").GetDouble());
        Assert.Equal(2, sent.GetProperty("duration").GetDouble());
        _socket.Push("""{"event":"shake_sprite","result":"success"}""");
        await shake;

        // Position and size are separate requests; only what was set is sent.
        var move = _remix.MoveAsync("Hat", 100, 50, 1.5, null, 0.5, 2);
        var moved = await NextAsync("move_sprite");
        Assert.Equal(100, moved.GetProperty("x").GetDouble());
        Assert.True(moved.GetProperty("reset").GetBoolean());
        Assert.Equal(2, moved.GetProperty("reset_delay").GetDouble());
        _socket.Push("""{"event":"move_sprite","result":"success"}""");
        var scaled = await NextAsync("animate_sprite");
        Assert.Equal(1.5, scaled.GetProperty("scale").GetDouble());
        Assert.Equal(0, scaled.GetProperty("rotation").GetDouble());
        _socket.Push("""{"event":"animate_sprite","result":"success"}""");
        await move;
        await Assert.ThrowsAsync<ArgumentException>(() => _remix.MoveAsync("Hat", null, null, null, null, null, null));
    }

    [Fact]
    public async Task Throwing_and_pausing_send_the_documented_fields()
    {
        var named = _remix.ThrowAsync("Tomato", 3);
        var item = await NextAsync("throw_item");
        Assert.Equal("Tomato", item.GetProperty("item_name").GetString());
        Assert.Equal(3, item.GetProperty("amount").GetInt32());
        _socket.Push("""{"event":"throw_item","result":"success","item_name":"Tomato","amount":3}""");
        await named;

        var random = _remix.ThrowAsync(" ", null);
        Assert.False((await NextAsync("throw_random")).TryGetProperty("amount", out _));   // PNGTuber Remix's own amount
        _socket.Push("""{"event":"throw_random","result":"success"}""");
        await random;

        var pause = _remix.SetThrowingPausedAsync(SwitchMode.On);
        Assert.True((await NextAsync("toggle_throwables_pause")).GetProperty("pause").GetBoolean());
        _socket.Push("""{"event":"toggle_throwables_pause","result":"success","is_paused":true}""");
        Assert.True(await pause);
    }
}

public sealed class AnimazeServiceTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private AnimazeService _animaze = null!;
    private FakeSocket _socket = null!;

    /// <summary>Answers each request with the reply for its action, repeating its id.</summary>
    private async Task<JsonElement> AnswerAsync(string action, string fields = "")
    {
        var sent = JsonDocument.Parse(await _socket.NextSentAsync()).RootElement;
        Assert.Equal(action, sent.GetProperty("action").GetString());
        var id = sent.GetProperty("id").GetString();
        _socket.Push($$"""{"action":"{{action}}","id":"{{id}}"{{(fields.Length > 0 ? "," + fields : "")}}}""");
        return sent;
    }

    public async Task InitializeAsync()
    {
        _animaze = new AnimazeService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, TestTimings.Fast);
        await _animaze.ConnectAsync();
        _socket = await _env.Server.NextAsync();
        Assert.Equal("ws://127.0.0.1:9000/", _socket.Url!.ToString());
        await AnswerAsync("GetAvatars", """ "avatars":["avatar03","AvatarBomb"] """);
        await AnswerAsync("GetScenes", """ "scenes":["Beach"] """);
        await AnswerAsync("GetQuickscenes", """ "error":"No quickscenes" """);
        await AnswerAsync("GetEmotes", """ "emotes":[{"friendlyName":"Alarm","itemName":"Emote_Alarm"},{"friendlyName":"Angry","itemName":"Emote_Angry"}] """);
        await AnswerAsync("GetSpecialActions", """ "specialActions":[{"animName":"Come here","index":0},{"animName":"Hello","index":1}] """);
        await AnswerAsync("GetPoses", """ "poses":["Sit","Stand"] """);
        await AnswerAsync("GetIdleAnims", """ "idleAnims":[{"name":"Sway","index":4}] """);
        await Wait.UntilAsync(() => _animaze.StatusMessage.Contains("emotes"), "lists");
    }

    public async Task DisposeAsync()
    {
        await _animaze.DisposeAsync();
        _env.Dispose();
    }

    [Fact]
    public void Reads_avatars_scenes_and_animations()
    {
        Assert.Equal("Connected to Animaze at 127.0.0.1:9000: 2 avatars, 2 emotes, 2 special actions", _animaze.StatusMessage);
        Assert.Equal(["avatar03", "AvatarBomb"], _animaze.Avatars);
        Assert.Equal(["Beach"], _animaze.Scenes);
        Assert.Empty(_animaze.Quickscenes);
        Assert.Equal([new AnimazeItem(0, "Sit"), new AnimazeItem(1, "Stand")], _animaze.Poses);   // plain names: numbered in order
        Assert.Equal([new AnimazeItem(4, "Sway")], _animaze.Idles);
    }

    [Fact]
    public async Task Actions_send_the_documented_fields()
    {
        var load = _animaze.LoadAvatarAsync("AvatarBomb");
        Assert.Equal("AvatarBomb", (await AnswerAsync("LoadAvatar")).GetProperty("name").GetString());
        await load;

        var emote = _animaze.TriggerEmoteAsync("angry");   // the shown name becomes the item name
        Assert.Equal("Emote_Angry", (await AnswerAsync("TriggerEmote")).GetProperty("itemName").GetString());
        Assert.Equal("Emote_Angry", await emote);

        var special = _animaze.TriggerSpecialActionAsync("Hello");
        Assert.Equal(1, (await AnswerAsync("TriggerSpecialAction")).GetProperty("index").GetInt32());
        await special;

        var idle = _animaze.TriggerIdleAsync("4");
        Assert.Equal(4, (await AnswerAsync("TriggerIdle")).GetProperty("index").GetInt32());
        await idle;

        var camera = _animaze.SetBroadcastAsync(true);
        Assert.True((await AnswerAsync("Broadcast")).GetProperty("toggle").GetBoolean());
        await camera;

        await Assert.ThrowsAsync<StreamAppRequestException>(() => _animaze.TriggerPoseAsync("Dance"));
    }

    [Fact]
    public async Task Errors_are_reported_and_avatar_changes_are_triggers()
    {
        var load = _animaze.LoadAvatarAsync("Nobody");
        await AnswerAsync("LoadAvatar", """ "error":"Avatar not found" """);
        Assert.Equal("Animaze: Avatar not found.", (await Assert.ThrowsAsync<StreamAppRequestException>(() => load)).Message);

        _socket.Push("""{"event":"AvatarChanged","new_avatar":"AvatarBomb"}""");
        var changed = await _env.WaitForEventAsync(AnimazeEvents.AvatarChanged);
        Assert.Equal("AvatarBomb", changed.Args["avatar"]);
        Assert.Equal("AvatarBomb", _animaze.CurrentAvatar);
        // The new avatar's animations are re-read.
        await AnswerAsync("GetEmotes", """ "emotes":[] """);
        await AnswerAsync("GetSpecialActions", """ "specialActions":[] """);
        await AnswerAsync("GetPoses", """ "poses":[] """);
        await AnswerAsync("GetIdleAnims", """ "idleAnims":[] """);
        await Wait.UntilAsync(() => _animaze.Emotes.Count == 0, "re-read");
    }
}

public sealed class VmcServiceTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private readonly FakeOsc _osc = new();
    private VmcService _vmc = null!;

    private sealed class FakeOsc : IOscTransport
    {
        private readonly List<OscMessage> _sent = [];
        public (string Host, int Port, int ListenPort)? Opened { get; private set; }
        public string Display => "fake";
        public string? CloseReason => null;

        public OscMessage[] Sent
        {
            get
            {
                lock (_sent) return [.. _sent];
            }
        }

        public Task OpenAsync(string host, int port, int listenPort, CancellationToken ct)
        {
            Opened = (host, port, listenPort);
            return Task.CompletedTask;
        }

        public Task SendAsync(OscMessage message, CancellationToken ct)
        {
            lock (_sent) _sent.Add(message);
            return Task.CompletedTask;
        }

        public Task<OscMessage?> ReceiveAsync(CancellationToken ct) => Task.FromResult<OscMessage?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        _vmc = new VmcService(_env.Settings, _env.Log, () => _osc);
        await _vmc.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        await _vmc.DisposeAsync();
        _env.Dispose();
    }

    [Fact]
    public async Task Expressions_are_a_value_then_apply()
    {
        Assert.Equal(("127.0.0.1", 39539, 0), _osc.Opened);   // sends only: no fixed listen port
        Assert.Equal(ConnectionState.Connected, _vmc.State);
        Assert.Equal(0.5f, await _vmc.SetExpressionAsync("Joy", VmcService.ParseValue("50%"), null));
        Assert.Equal(
            [new OscMessage("/VMC/Ext/Blend/Val", "Joy", 0.5f).ToString(), new OscMessage("/VMC/Ext/Blend/Apply").ToString()],
            _osc.Sent.Select(m => m.ToString()));
        Assert.IsType<float>(_osc.Sent[0].Args[1]);

        await _vmc.ClearExpressionsAsync(["Joy", " Angry ", ""]);
        Assert.Equal(["/VMC/Ext/Blend/Val \"Joy\" 0", "/VMC/Ext/Blend/Val \"Angry\" 0", "/VMC/Ext/Blend/Apply"],
            _osc.Sent.Skip(2).Select(m => m.ToString()));

        Assert.Throws<ArgumentException>(() => VmcService.ParseValue("2"));
        Assert.Equal(1f, VmcService.ParseValue(""));
    }

    [Fact]
    public async Task A_held_expression_is_resent_then_set_back_to_zero()
    {
        await _vmc.SetExpressionAsync("Surprised", 1, 0.35);
        await Wait.UntilAsync(() => _osc.Sent.Any(m => m.ToString() == "/VMC/Ext/Blend/Val \"Surprised\" 0"), "release");
        var values = _osc.Sent.Where(m => m.Address == VmcService.BlendValueAddress).Select(m => m.ToString()).ToList();
        Assert.True(values.Count(v => v.EndsWith(" 1", StringComparison.Ordinal)) >= 2, string.Join(" | ", values));   // re-sent while held
        Assert.EndsWith(" 0", values[^1]);

        await _vmc.DisconnectAsync();
        await Assert.ThrowsAsync<StreamAppNotConnectedException>(() => _vmc.SetExpressionAsync("Joy", 1, null));
    }
}
