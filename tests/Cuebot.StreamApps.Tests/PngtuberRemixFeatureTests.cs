using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.StreamApps.Common;
using Cuebot.StreamApps.PngtuberRemix;
using Cuebot.StreamApps.Tests.Support;

namespace Cuebot.StreamApps.Tests;

/// <summary>
/// A scripted PNGTuber Remix 1.4.7, modelled on its own websocket_handler.gd: answers in order, only to the sender;
/// names match exactly and the first match wins; show/hide/toggle act on attached sprites too; a request whose branch
/// hits a script error (general without key, toggling a sprite that does not exist) gets no answer; ids are written
/// as Godot floats.
/// </summary>
internal sealed class FakeRemix
{
    private readonly FakeSocket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public FakeRemix(FakeSocket socket)
    {
        _socket = socket;
        Loop = Task.Run(RunAsync);
    }

    public sealed class Sprite(string name, long id, long parent = 0)
    {
        public string Name { get; set; } = name;
        public long Id { get; } = id;
        public long Parent { get; } = parent;
        public bool Visible { get; set; } = true;
        public string Key { get; init; } = "";
    }

    public List<string> States { get; set; } = ["Idle", "Smug", "Angry"];
    public int Current { get; set; }

    public List<Sprite> Sprites { get; set; } =
    [
        new("Body", 100),
        new("Head", 537693010870, 100),
        new("Hat", 11, 537693010870) { Visible = false, Key = "H" },
        new("Glasses", 12, 537693010870),
    ];

    public HashSet<string> Throwables { get; } = ["Tomato"];
    public bool ThrowablesActive { get; set; } = true;
    public bool Paused { get; set; }

    /// <summary>When set and true for a request, nothing is answered until <see cref="Release"/> (PNGTuber Remix stalls).</summary>
    public Func<JsonElement, bool>? StallOn { get; set; }

    private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _released.TrySetResult();

    /// <summary>Requests whose answer is dropped (as if a script error happened).</summary>
    public Func<JsonElement, bool>? Drop { get; set; }

    /// <summary>A model that load_model switches to, after <see cref="StaleReads"/> more list_states reads.</summary>
    public (List<string> States, List<Sprite> Sprites)? NextModel { get; set; }

    public int StaleReads { get; set; } = 1;
    private int _staleLeft = -1;

    public ConcurrentQueue<(JsonElement Message, double At)> Received { get; } = new();

    public Task Loop { get; }

    public JsonElement[] All(string ev) => [.. Received.Where(r => r.Message.Str("event") == ev).Select(r => r.Message)];

    public double[] Times(string ev) => [.. Received.Where(r => r.Message.Str("event") == ev).Select(r => r.At)];

    public async Task<JsonElement> NextAsync(string ev, int skip = 0)
    {
        await Wait.UntilAsync(() => All(ev).Length > skip, ev);
        return All(ev)[skip];
    }

    public void Stop() => _stop.Cancel();

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            string text;
            try
            {
                text = await _socket.NextSentAsync();
            }
            catch (OperationCanceledException)
            {
                continue;
            }
            var message = JsonDocument.Parse(text).RootElement.Clone();
            Received.Enqueue((message, _clock.Elapsed.TotalSeconds));
            if (StallOn?.Invoke(message) == true) await _released.Task;
            if (Drop?.Invoke(message) == true) continue;
            if (Handle(message) is { } answer) _socket.Push(answer.ToJsonString());
        }
    }

    private Sprite? Find(string identifier) =>
        Sprites.FirstOrDefault(s => s.Name == identifier)
        ?? (double.TryParse(identifier, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Sprites.FirstOrDefault(s => s.Id == (long)d) : null);

    private IEnumerable<Sprite> Attached(Sprite parent)
    {
        foreach (var child in Sprites.Where(s => s.Parent == parent.Id))
        {
            yield return child;
            foreach (var grandchild in Attached(child)) yield return grandchild;
        }
    }

    private void SetVisible(Sprite sprite, bool visible, bool attached = true)
    {
        sprite.Visible = visible;
        if (attached) foreach (var c in Attached(sprite)) c.Visible = visible;
    }

    /// <summary>Godot's str() of a JSON value: numbers are floats ("2.0").</summary>
    private static string GodotStr(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString()!,
        JsonValueKind.Number => e.GetDouble().ToString("0.0", CultureInfo.InvariantCulture),
        _ => e.ToString(),
    };

    private static string Ident(JsonElement m, string key) =>
        m.TryGetProperty(key, out var v) ? GodotStr(v) : "";

    private JsonObject? Handle(JsonElement m)
    {
        var ev = m.Str("event");
        JsonObject Ok(JsonObject? more = null)
        {
            var o = new JsonObject { ["event"] = ev, ["result"] = "success" };
            foreach (var (k, v) in more ?? []) o[k] = v?.DeepClone();
            return o;
        }
        JsonObject Failed(string error, string identifier = "") =>
            new() { ["event"] = ev, ["result"] = "failed", ["identifier"] = identifier, ["error"] = error };
        switch (ev)
        {
            case "ping":
                return new JsonObject { ["event"] = "pong" };
            case "state":
            {
                var id = m.TryGetProperty("state_name", out var n) ? GodotStr(n) : Ident(m, "state_id");
                var index = States.IndexOf(id);
                if (index < 0 && double.TryParse(id, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 1 && d <= States.Count)
                    index = (int)d - 1;
                if (index < 0) return Failed("state not found", id);
                Current = index;
                return Ok(new JsonObject { ["state_id"] = index + 1, ["state_name"] = States[index] });
            }
            case "list_states":
                if (_staleLeft == 0 && NextModel is { } model)
                {
                    States = model.States;
                    Sprites = model.Sprites;
                    Current = 0;
                    NextModel = null;
                }
                if (_staleLeft > 0) _staleLeft--;
                if (States.Count == 0) return Failed("no state buttons found");
                return Ok(new JsonObject
                {
                    ["states"] = new JsonArray([.. States.Select((s, i) => (JsonNode)new JsonObject { ["name"] = s, ["id"] = i + 1, ["is_current"] = i == Current })]),
                });
            case "list_sprites":
                return Ok(new JsonObject
                {
                    ["sprites"] = new JsonArray([.. Sprites.Select(s => (JsonNode)new JsonObject
                    {
                        ["name"] = s.Name,
                        ["visible"] = s.Visible,
                        ["id"] = (double)s.Id + 0.0,
                        ["is_group"] = Sprites.Any(c => c.Parent == s.Id),
                        ["children"] = new JsonArray([.. Sprites.Where(c => c.Parent == s.Id).Select(c => (JsonNode)JsonValue.Create(c.Name))]),
                        ["parent_id"] = (double)s.Parent,
                    })]),
                });
            case "general":
            {
                if (!m.TryGetProperty("key", out var key)) return null; // script error: no answer
                var found = new JsonArray();
                foreach (var s in Sprites.Where(s => s.Key == key.GetString()))
                {
                    SetVisible(s, !s.Visible);
                    found.Add(s.Name + " (Toggle)");
                }
                return Ok(new JsonObject { ["key"] = key.GetString(), ["actions_found"] = found });
            }
            case "hide_sprite" or "show_sprite" or "hide_group" or "show_group":
            {
                var id = Ident(m, ev.EndsWith("group", StringComparison.Ordinal) ? "group_name" : "sprite_name");
                if (Find(id) is not { } s) return Failed(ev.EndsWith("group", StringComparison.Ordinal) ? "group not found" : "sprite not found", id);
                SetVisible(s, ev.StartsWith("show", StringComparison.Ordinal));
                return Ok(new JsonObject { ["identifier"] = id });
            }
            case "toggle_sprite" or "toggle_group":
            {
                var group = ev == "toggle_group";
                var id = Ident(m, group ? "group_name" : "sprite_name");
                var match = m.TryGetProperty("match_sprite", out var ms) ? ms.GetString() ?? "" : "";
                var target = Find(id);
                if (match.Length > 0)
                {
                    if (target is null || Find(match) is not { } source) return null; // script error: no answer
                    var reverse = m.TryGetProperty("reverse", out var r) && r.GetBoolean();
                    SetVisible(target, reverse ? !source.Visible : source.Visible, attached: group);
                }
                else
                {
                    if (target is null) return null; // script error: no answer
                    SetVisible(target, !target.Visible);
                }
                return Ok(new JsonObject { ["identifier"] = id, ["visibility"] = target.Visible ? "visible" : "hidden" });
            }
            case "move_sprite" or "animate_sprite" or "shake_sprite" or "bounce_sprite":
            {
                var id = Ident(m, "sprite_name");
                return Find(id) is null ? Failed("sprite not found", id) : Ok(new JsonObject { ["identifier"] = id });
            }
            case "throw_item":
                if (!ThrowablesActive) return Failed("throwable spawner not active");
                var item = m.Str("item_name");
                return Throwables.Contains(item)
                    ? Ok(new JsonObject { ["item_name"] = item, ["amount"] = m.Num("amount") is { } a ? (int)a : 1 })
                    : new JsonObject { ["event"] = ev, ["result"] = "failed", ["item_name"] = item, ["error"] = "item not found" };
            case "throw_random":
                return ThrowablesActive ? Ok() : Failed("throwable spawner not active");
            case "toggle_throwables_pause":
                if (!ThrowablesActive) return Failed("throwable spawner not active");
                Paused = m.TryGetProperty("pause", out var p) ? p.GetBoolean() : !Paused;
                return Ok(new JsonObject { ["is_paused"] = Paused });
            case "load_model":
            {
                var path = m.Str("file_path");
                if (path.Length == 0) return Failed("file_path is required");
                if (!File.Exists(path) || !(path.EndsWith(".pngRemix", StringComparison.Ordinal) || path.EndsWith(".save", StringComparison.Ordinal)))
                    return new JsonObject { ["event"] = ev, ["result"] = "failed", ["file_path"] = path, ["error"] = "failed to load model" };
                _staleLeft = StaleReads;
                return Ok(new JsonObject { ["file_path"] = path });
            }
            default:
                return new JsonObject { ["event"] = "error", ["message"] = "Unknown event: " + ev };
        }
    }
}

/// <summary>The PNGTuber Remix fixes and features from the 2026-11 review, against <see cref="FakeRemix"/>.</summary>
public sealed class PngtuberRemixFeatureTests : IAsyncLifetime
{
    private static readonly StreamAppsTimings Timings = new(
        ConnectTimeout: TimeSpan.FromSeconds(2),
        RequestTimeout: TimeSpan.FromMilliseconds(700),
        ReconnectDelay: TimeSpan.FromMilliseconds(20),
        MaxReconnectDelay: TimeSpan.FromMilliseconds(100));

    private readonly TestEnvironment _env = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-remix-").FullName;
    private PngtuberRemixService _remix = null!;

    public Task InitializeAsync()
    {
        _env.Settings.Value.PngtuberRemix.PollSeconds = 0;
        _remix = new PngtuberRemixService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, Timings) { PingInterval = TimeSpan.FromMinutes(10) };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _remix.DisposeAsync();
        _env.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private async Task<FakeRemix> ConnectAsync(Action<FakeRemix>? setup = null)
    {
        await _remix.ConnectAsync();
        var fake = new FakeRemix(await _env.Server.NextAsync());
        setup?.Invoke(fake);
        await Wait.UntilAsync(() => _remix.StatusMessage.Contains("sprites"), "connected");
        return fake;
    }

    [Fact]
    public async Task A_timed_out_request_does_not_shift_later_answers()
    {
        var fake = await ConnectAsync();
        fake.StallOn = m => m.Str("event") == "list_sprites";

        // A: Remix stalls on it past the timeout.
        await Assert.ThrowsAsync<TimeoutException>(() => _remix.GetVisibilityAsync("Hat"));
        // B is sent while A's answer is still due; A's late answer must not be taken as B's.
        var b = _remix.SetStateAsync("Angry");
        fake.StallOn = null;
        fake.Release();
        Assert.Equal("Angry", await b);
        Assert.Equal("Angry", Assert.Single(_remix.States, s => s.IsCurrent).Name);

        // And the next request after that still gets its own answer.
        Assert.Equal("Idle", await _remix.SetStateAsync("1"));
    }

    [Fact]
    public async Task A_request_Remix_never_answers_fails_as_soon_as_a_later_answer_arrives()
    {
        var fake = await ConnectAsync();
        // Without "key" PNGTuber Remix stops with a script error and answers nothing.
        var unanswered = _remix.RequestAsync(new { @event = "general" }, CancellationToken.None);
        await fake.NextAsync("general");
        var watch = Stopwatch.StartNew();
        Assert.Equal("Smug", await _remix.SetStateAsync("Smug"));
        var ex = await Assert.ThrowsAsync<StreamAppRequestException>(() => unanswered);
        Assert.Contains("did not answer general", ex.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(600), "failed at once, not after the timeout");

        // Same event twice and the first dropped: answers carry no id, so the one answer goes to the first and the
        // second times out; the requests after that are matched correctly again.
        fake.Drop = m => m.Str("event") == "throw_random" && m.TryGetProperty("amount", out _);
        var dropped = _remix.ThrowAsync("", 3);
        await fake.NextAsync("throw_random");
        fake.Drop = null;
        await Assert.ThrowsAsync<TimeoutException>(() => _remix.ThrowAsync("", null));
        Assert.Equal(3, await dropped);
        Assert.Equal("Smug", (await _remix.GetStateAsync())!.Name);
    }

    [Fact]
    public async Task Pings_keep_the_connection_checked_and_a_silent_Remix_is_reconnected()
    {
        _remix.PingInterval = TimeSpan.FromMilliseconds(100);
        var fake = await ConnectAsync();
        await fake.NextAsync("ping", skip: 1);
        fake.Drop = m => m.Str("event") == "ping";
        var disconnected = await _env.WaitForEventAsync(PngtuberRemixEvents.Disconnected);
        Assert.Contains("stopped answering", disconnected.Text("reason"));
        fake.Stop();
        var again = new FakeRemix(await _env.Server.NextAsync());
        await Wait.UntilAsync(() => _env.Events(PngtuberRemixEvents.Connected).Length == 2, "reconnected");
        again.Stop();
    }

    [Fact]
    public async Task Move_needs_both_X_and_Y_and_size_and_turn_go_together()
    {
        var fake = await ConnectAsync();
        var onlyX = await Assert.ThrowsAsync<ArgumentException>(() => _remix.MoveAsync("Hat", 100, null, null, null, null, null));
        Assert.Contains("empty Y would mean 0", onlyX.Message);
        var onlyY = await Assert.ThrowsAsync<ArgumentException>(() => _remix.MoveAsync("Hat", null, 5, null, null, null, null));
        Assert.Contains("empty X", onlyY.Message);
        Assert.Empty(fake.All("move_sprite"));

        await _remix.MoveAsync("Hat", null, null, 2, null, 0, null);
        var grow = await fake.NextAsync("animate_sprite");
        Assert.Equal(2, grow.GetProperty("scale_x").GetDouble());
        Assert.Equal(2, grow.GetProperty("scale_y").GetDouble());
        Assert.Equal(0, grow.GetProperty("rotation").GetDouble());
        Assert.False(grow.GetProperty("reset").GetBoolean());

        // Squash and stretch: one axis only.
        await _remix.MoveAsync("Glasses", null, null, null, null, 0, null, default, scaleX: 1.3, scaleY: 0.7);
        var squash = await fake.NextAsync("animate_sprite", skip: 1);
        Assert.Equal(1.3, squash.GetProperty("scale_x").GetDouble());
        Assert.Equal(0.7, squash.GetProperty("scale_y").GetDouble());
    }

    [Fact]
    public async Task Effects_on_one_sprite_wait_for_each_other_but_not_for_other_sprites()
    {
        var fake = await ConnectAsync();
        await Task.WhenAll(
            _remix.ShakeAsync("Head", 5, 0.3),
            _remix.BounceAsync("Head", 10, 0.2),
            _remix.ShakeAsync("Glasses", 5, 0.3));
        var shake = fake.Times("shake_sprite");
        var bounce = Assert.Single(fake.Times("bounce_sprite"));
        Assert.Equal(2, shake.Length);
        Assert.True(Math.Abs(shake[1] - shake[0]) < 0.2, "another sprite does not wait");
        Assert.True(bounce - shake[0] >= 0.38, $"the bounce waited for the shake and its return ({bounce - shake[0]:0.000} s)");

        // A move with Go back counts its way there, the pause and the way back.
        var move = _remix.MoveAsync("Hat", 10, 20, null, null, 0.1, 0.2);
        await move;
        await _remix.ShakeAsync("Hat", 1, 0);
        var moved = Assert.Single(fake.Times("move_sprite"));
        Assert.True(fake.Times("shake_sprite")[2] - moved >= 0.38, "waited for the move, the pause and the return");
    }

    [Fact]
    public async Task Names_are_sent_with_Remix_spelling_and_duplicates_can_be_picked_by_id()
    {
        var fake = await ConnectAsync(f => f.Sprites.Add(new FakeRemix.Sprite("Hat", 13, 100)));
        await _remix.RefreshAsync();

        Assert.Equal("Angry", await _remix.SetStateAsync("angry"));
        Assert.Equal("Angry", (await fake.NextAsync("state")).GetProperty("state_name").GetString());

        Assert.False(await _remix.SetVisibilityAsync("glasses", SwitchMode.Off));
        Assert.Equal("Glasses", (await fake.NextAsync("hide_sprite")).GetProperty("sprite_name").GetString());

        // Two sprites named Hat: the suggestions offer each by id, and a warning explains the first one is used.
        Assert.Contains("Hat (id 11)", _remix.SpriteNames());
        Assert.Contains("Hat (id 13)", _remix.SpriteNames());
        Assert.Contains("Head (2 attached)", _remix.SpriteNamesWithAttached());
        Assert.Contains("Body (4 attached)", _remix.SpriteNamesWithAttached());
        Assert.True(await _remix.SetVisibilityAsync("Hat (id 13)", SwitchMode.On));
        Assert.Equal("13", (await fake.NextAsync("show_sprite")).GetProperty("sprite_name").GetString());
        Assert.True(await _remix.SetVisibilityAsync("Hat", SwitchMode.On));
        Assert.Contains(_env.Log.Recent(), e => e.Message.Contains("2 sprites are named 'Hat'"));
        Assert.True(await _remix.SetVisibilityAsync("Head (2 attached)", SwitchMode.On));
        Assert.Equal("Head", (await fake.NextAsync("show_sprite", skip: 2)).GetProperty("sprite_name").GetString());

        // Ids are Godot floats in list_sprites; Cuebot uses them as whole numbers.
        Assert.Equal("537693010870", Assert.Single(_remix.Sprites, s => s.Name == "Head").Id);

        // A sprite that does not exist is refused here: PNGTuber Remix would never answer a toggle for it.
        var missing = await Assert.ThrowsAsync<StreamAppRequestException>(() => _remix.SetVisibilityAsync("Hta", SwitchMode.Toggle));
        Assert.Contains("no sprite named 'Hta'", missing.Message);
        Assert.Empty(fake.All("toggle_sprite"));
    }

    [Fact]
    public async Task Show_and_hide_take_the_attached_sprites_along_and_match_can_change_one_sprite_only()
    {
        var fake = await ConnectAsync();
        Assert.False(await _remix.SetVisibilityAsync("Head", SwitchMode.Off));
        Assert.All(fake.Sprites.Where(s => s.Name is "Head" or "Hat" or "Glasses"), s => Assert.False(s.Visible));
        Assert.All(_remix.Sprites.Where(s => s.Name is "Head" or "Hat" or "Glasses"), s => Assert.False(s.Visible));

        // Show only the hat, matching the (visible) body.
        Assert.True(await _remix.MatchVisibilityAsync("Head", "Body", opposite: false, withAttached: false));
        var sent = await fake.NextAsync("toggle_sprite");
        Assert.Equal("Body", sent.GetProperty("match_sprite").GetString());
        Assert.False(sent.GetProperty("reverse").GetBoolean());
        Assert.True(fake.Sprites.Single(s => s.Name == "Head").Visible);
        Assert.False(fake.Sprites.Single(s => s.Name == "Hat").Visible);

        // Opposite of the head, attached sprites following: toggle_group.
        Assert.False(await _remix.MatchVisibilityAsync("Hat", "Head", opposite: true, withAttached: true));
        Assert.True((await fake.NextAsync("toggle_group")).GetProperty("reverse").GetBoolean());

        await Assert.ThrowsAsync<StreamAppRequestException>(() => _remix.MatchVisibilityAsync("Hat", "Nope", false, true));
        Assert.True(await _remix.GetVisibilityAsync("Glasses") is false);
    }

    [Fact]
    public async Task Throwing_is_capped_takes_variance_and_sides_and_explains_a_missing_spawner()
    {
        var fake = await ConnectAsync();
        Assert.Equal(50, await _remix.ThrowAsync("Tomato", 10_000));
        Assert.Equal(50, (await fake.NextAsync("throw_item")).GetProperty("amount").GetInt32());
        Assert.Contains(_env.Log.Recent(), e => e.Message.Contains("Throwing 50 instead of 10000"));

        _remix.Settings.MaxThrowAmount = 5;
        await _remix.ThrowAsync("", 9, variance: 0.5, bothSides: false);
        var random = await fake.NextAsync("throw_random");
        Assert.Equal(5, random.GetProperty("amount").GetInt32());
        Assert.Equal(0.5, random.GetProperty("variance").GetDouble());
        Assert.False(random.GetProperty("both_sides").GetBoolean());
        await _remix.ThrowAsync("", null);
        Assert.False((await fake.NextAsync("throw_random", skip: 1)).TryGetProperty("both_sides", out _));   // app setting

        var item = await Assert.ThrowsAsync<StreamAppRequestException>(() => _remix.ThrowAsync("tomato", 1));
        Assert.Contains("no throwable named 'tomato'", item.Message);
        fake.ThrowablesActive = false;
        var off = await Assert.ThrowsAsync<StreamAppRequestException>(() => _remix.ThrowAsync("", null));
        Assert.Contains("throwing is not set up", off.Message);
    }

    [Fact]
    public async Task Load_model_checks_the_file_and_waits_for_the_new_states()
    {
        var fake = await ConnectAsync();
        Assert.Contains("capital R", (await Assert.ThrowsAsync<ArgumentException>(() => _remix.LoadModelAsync(Path.Combine(_dir, "a.pngremix")))).Message);
        Assert.Contains(".pngRemix (or .save)", (await Assert.ThrowsAsync<ArgumentException>(() => _remix.LoadModelAsync(Path.Combine(_dir, "a.png")))).Message);
        Assert.Contains("no model file", (await Assert.ThrowsAsync<ArgumentException>(() => _remix.LoadModelAsync(Path.Combine(_dir, "gone.pngRemix")))).Message);
        Assert.Empty(fake.All("load_model"));

        var file = Path.Combine(_dir, "Party.save");
        await File.WriteAllTextAsync(file, "{}");
        fake.NextModel = (["Party", "Dance"], [new FakeRemix.Sprite("Confetti", 77)]);
        fake.StaleReads = 2; // the old state buttons are still listed twice after the answer
        await _remix.LoadModelAsync(file);
        Assert.Equal(["Party", "Dance"], _remix.StateNames());
        Assert.Equal(["Confetti"], _remix.SpriteNames());
        var changed = await _env.WaitForEventAsync(PngtuberRemixEvents.SpritesChanged);
        Assert.Equal("Confetti", changed.Text("pngtuberRemix.added"));
        Assert.Contains("Hat", changed.Text("pngtuberRemix.removed"));
    }

    [Fact]
    public async Task Polling_notices_state_and_sprite_changes_made_in_Remix()
    {
        _remix.Settings.PollSeconds = 0.2;
        var fake = await ConnectAsync();
        fake.Current = 2; // the streamer pressed the Angry hotkey
        var changed = await _env.WaitForEventAsync(PngtuberRemixEvents.StateChanged);
        Assert.Equal("Angry", changed.Text("pngtuberRemix.state"));
        Assert.Equal("3", changed.Text("pngtuberRemix.stateNumber"));
        Assert.Equal("Idle", changed.Text("pngtuberRemix.previousState"));
        Assert.Equal("PNGTuber Remix", changed.Text("pngtuberRemix.changedBy"));
        Assert.Single(_env.Events(PngtuberRemixEvents.StateChanged));

        await _remix.SetStateAsync("Smug");
        var byCuebot = await _env.WaitForEventAsync(PngtuberRemixEvents.StateChanged, e => e.Text("pngtuberRemix.state") == "Smug");
        Assert.Equal("Cuebot", byCuebot.Text("pngtuberRemix.changedBy"));

        fake.Sprites.Single(s => s.Name == "Glasses").Name = "Shades";
        var sprites = await _env.WaitForEventAsync(PngtuberRemixEvents.SpritesChanged);
        Assert.Equal("Shades", sprites.Text("pngtuberRemix.added"));
        Assert.Equal("Glasses", sprites.Text("pngtuberRemix.removed"));
        Assert.Equal(2, _env.Events(PngtuberRemixEvents.StateChanged).Length);
    }

    [Fact]
    public async Task Sub_actions_press_keys_match_and_read_visibility()
    {
        var fake = await ConnectAsync();
        await using var runner = new ActionRunner(PngtuberRemixSubActions.Create(_remix));

        var (ok, args) = await runner.RunAsync(("pngtuberRemix.pressKey", new() { ["key"] = "H" }));
        Assert.True(ok, runner.LastError);
        Assert.Equal("Hat (Toggle)", args["pngtuberRemix.actionsFound"]);
        Assert.Equal(1, args["pngtuberRemix.actionsFoundCount"]);
        Assert.Equal("H", (await fake.NextAsync("general")).GetProperty("key").GetString());

        (ok, args) = await runner.RunAsync(("pngtuberRemix.getVisibility", new() { ["sprite"] = "Hat" }));
        Assert.True(ok, runner.LastError);
        Assert.Equal(true, args["pngtuberRemix.visible"]);

        // An action saved with the old "what" = Group still runs (the sprite variant does the same).
        (ok, args) = await runner.RunAsync(("pngtuberRemix.setVisibility", new() { ["sprite"] = "Head", ["what"] = "Group", ["state"] = "Hidden" }));
        Assert.True(ok, runner.LastError);
        Assert.Equal(false, args["pngtuberRemix.visible"]);
        Assert.Equal("Head", (await fake.NextAsync("hide_sprite")).GetProperty("sprite_name").GetString());

        (ok, args) = await runner.RunAsync(("pngtuberRemix.matchVisibility",
            new() { ["sprite"] = "Glasses", ["match"] = "Hat", ["opposite"] = "true", ["attached"] = "false" }));
        Assert.True(ok, runner.LastError);
        Assert.Equal(true, args["pngtuberRemix.visible"]);

        (ok, args) = await runner.RunAsync(("pngtuberRemix.throw", new() { ["amount"] = "2", ["variance"] = "0.2", ["bothSides"] = "Yes" }));
        Assert.True(ok, runner.LastError);
        Assert.Equal(2, args["pngtuberRemix.thrown"]);
        Assert.True((await fake.NextAsync("throw_random")).GetProperty("both_sides").GetBoolean());

        (ok, _) = await runner.RunAsync(("pngtuberRemix.move", new() { ["sprite"] = "Hat", ["x"] = "10" }));
        Assert.False(ok);
        Assert.Contains("Set both X and Y", runner.LastError);
    }

    [Fact]
    public async Task Pausing_runs_the_trigger_when_it_changes()
    {
        await ConnectAsync();
        Assert.True(await _remix.SetThrowingPausedAsync(SwitchMode.On));
        Assert.True(await _remix.SetThrowingPausedAsync(SwitchMode.On));
        Assert.False(await _remix.SetThrowingPausedAsync(SwitchMode.Toggle));
        await Wait.UntilAsync(() => _env.Events(PngtuberRemixEvents.ThrowingPausedChanged).Length == 2, "two changes");
        Assert.Equal([true, false], _env.Events(PngtuberRemixEvents.ThrowingPausedChanged).Select(e => e.Flag("pngtuberRemix.throwingPaused")));

        var trigger = new PngtuberRemixTriggers().GetTriggers().Single(t => t.TypeId == PngtuberRemixEvents.ThrowingPausedChanged);
        Assert.NotNull(trigger);
    }

    [Fact]
    public async Task Script_functions_reach_Remix()
    {
        var fake = await ConnectAsync();
        var functions = new PngtuberRemixScriptFunctions(_remix).GetFunctions().ToDictionary(f => f.Name);
        Cuebot.Core.Actions.ActionRunContext ctx = null!;
        static JsonElement A(object o) => JsonSerializer.SerializeToElement(o);

        Assert.Equal(true, await functions["pngtuberRemix.isConnected"].Invoke(ctx, A(new { }), default));
        Assert.Equal("Smug", await functions["pngtuberRemix.setState"].Invoke(ctx, A(new { state = "smug" }), default));
        Assert.Equal(true, await functions["pngtuberRemix.setVisibility"].Invoke(ctx, A(new { sprite = "Hat", visible = true }), default));
        Assert.Equal(true, await functions["pngtuberRemix.isVisible"].Invoke(ctx, A(new { sprite = "Hat" }), default));
        var pressed = Assert.IsAssignableFrom<IReadOnlyList<string>>(await functions["pngtuberRemix.pressKey"].Invoke(ctx, A(new { key = "H" }), default));
        Assert.Equal(["Hat (Toggle)"], pressed);
        Assert.Equal(3, await functions["pngtuberRemix.throw"].Invoke(ctx, A(new { item = "Tomato", amount = 3 }), default));
        var sprites = JsonSerializer.SerializeToElement(await functions["pngtuberRemix.getSprites"].Invoke(ctx, A(new { }), default));
        Assert.Equal(4, sprites.GetArrayLength());
        Assert.Contains(fake.All("state"), m => m.Str("state_name") == "Smug");
    }
}
