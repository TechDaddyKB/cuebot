using System.Diagnostics;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.StreamApps.Common;
using Cuebot.StreamApps.Tests.Support;
using Cuebot.StreamApps.Warudo;

namespace Cuebot.StreamApps.Tests;

/// <summary>
/// Warudo against an in-memory Stream Deck service that behaves like Warudo's (HakuyaLabs/WarudoPluginExamples,
/// StreamDeck, and the SDK's WebSocketService envelope): answers in order, without request ids, errors as
/// <c>{"action":..,"error":..}</c> with no data, and a <c>toggle</c> broadcast after every change.
/// </summary>
public sealed class WarudoFeatureTests : IAsyncLifetime
{
    private static readonly StreamAppsTimings ShortTimeout = new(
        ConnectTimeout: TimeSpan.FromSeconds(2),
        RequestTimeout: TimeSpan.FromMilliseconds(500),
        ReconnectDelay: TimeSpan.FromMilliseconds(20),
        MaxReconnectDelay: TimeSpan.FromMilliseconds(100));

    private readonly TestEnvironment _env = new();
    private readonly FakeServer _webSocketServer = new();
    private WarudoService _warudo = null!;
    private FakeSocket _socket = null!;

    private async Task ConnectAsync(StreamAppsTimings? timings = null)
    {
        _warudo = new WarudoService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, timings ?? TestTimings.Fast, _webSocketServer.Create);
        await _warudo.ConnectAsync();
        _socket = await _env.Server.NextAsync();
        await AnswerRefreshAsync(["Wave", "Confetti"], ["Hat"], ["Say"], """{"Hat":true}""");
        await Wait.UntilAsync(() => _warudo.StatusMessage.Contains("receivers"), "receivers");
    }

    /// <summary>Answers one refresh: getReceivers for trigger, toggle and message, then getToggles.</summary>
    private async Task AnswerRefreshAsync(string[] triggers, string[] toggles, string[] messages, string states)
    {
        foreach (var (type, names) in new[] { ("trigger", triggers), ("toggle", toggles), ("message", messages) })
        {
            var ask = JsonDocument.Parse(await _socket.NextSentAsync()).RootElement;
            Assert.Equal("getReceivers", ask.GetProperty("action").GetString());
            Assert.Equal(type, ask.GetProperty("data").GetProperty("type").GetString());
            _socket.Push($$"""{"action":"getReceivers","data":{{JsonSerializer.Serialize(names)}}}""");
        }
        Assert.Equal("getToggles", Action(await _socket.NextSentAsync()));
        _socket.Push($$"""{"action":"getToggles","data":{{states}}}""");
    }

    private static string? Action(string sent) => JsonDocument.Parse(sent).RootElement.GetProperty("action").GetString();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_warudo is not null) await _warudo.DisposeAsync();
        _env.Dispose();
    }

    [Fact]
    public async Task A_late_answer_after_a_timeout_does_not_answer_the_next_request()
    {
        await ConnectAsync(ShortTimeout);

        // Warudo is slow (loading a scene): the first getToggles times out.
        var first = _warudo.GetToggleAsync("Hat");
        Assert.Equal("getToggles", Action(await _socket.NextSentAsync()));
        await first; // falls back to the cached state after the timeout
        Assert.Contains(_env.Log.Recent(), e => e.Message.Contains("did not answer getToggles"));

        // A second request goes out, then the late answer to the first arrives, then the second's own answer.
        var second = _warudo.GetToggleAsync("Hat");
        Assert.Equal("getToggles", Action(await _socket.NextSentAsync()));
        _socket.Push("""{"action":"getToggles","data":{"Hat":true}}""");
        _socket.Push("""{"action":"getToggles","data":{"Hat":false}}""");
        Assert.False(await second);

        // And everything after that still lines up.
        var third = _warudo.GetToggleAsync("Hat");
        Assert.Equal("getToggles", Action(await _socket.NextSentAsync()));
        _socket.Push("""{"action":"getToggles","data":{"Hat":true}}""");
        Assert.True(await third);
    }

    [Fact]
    public async Task An_error_answer_fails_the_request_at_once_and_keeps_answers_in_line()
    {
        await ConnectAsync();
        var refresh = _warudo.RefreshAsync();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("getReceivers", Action(await _socket.NextSentAsync()));
            _socket.Push("""{"action":"getReceivers","data":[]}""");
        }
        Assert.Equal("getToggles", Action(await _socket.NextSentAsync()));
        // The SDK's WebSocketService answers a failure with the action and an error, and no data.
        _socket.Push("""{"action":"getToggles","error":"Something went wrong"}""");
        var ex = await Assert.ThrowsAsync<StreamAppRequestException>(() => refresh);
        Assert.Contains("Something went wrong", ex.Message);
        Assert.True(watch.Elapsed < TestTimings.Fast.RequestTimeout, "an error answer must not wait for the timeout");

        // The error used up its request: the next answer belongs to the next request.
        var next = _warudo.GetToggleAsync("Hat");
        Assert.Equal("getToggles", Action(await _socket.NextSentAsync()));
        _socket.Push("""{"action":"getToggles","data":{"Hat":false}}""");
        Assert.False(await next);
    }

    [Fact]
    public async Task On_with_an_unknown_state_reads_the_state_before_flipping()
    {
        await ConnectAsync();

        // "Scarf" was added after the last refresh and is already on: On must not flip it off.
        var on = _warudo.SetToggleAsync("Scarf", SwitchMode.On);
        Assert.Equal("getToggles", Action(await _socket.NextSentAsync()));
        _socket.Push("""{"action":"getToggles","data":{"Hat":true,"Scarf":true}}""");
        Assert.True(await on);
        Assert.False(_socket.TryReadSent(out _));

        // Off then flips it, and returns what Warudo reports.
        var off = _warudo.SetToggleAsync("Scarf", SwitchMode.Off);
        Assert.Equal("""{"action":"toggle","data":{"receiverName":"Scarf"}}""", await _socket.NextSentAsync());
        _socket.Push("""{"action":"toggle","data":{"receiverName":"Scarf","state":false,"isResponse":true}}""");
        Assert.False(await off);
    }

    [Fact]
    public async Task On_flips_when_the_state_is_still_unknown_and_reports_null_without_an_answer()
    {
        await ConnectAsync(ShortTimeout);
        var on = _warudo.SetToggleAsync("Nope", SwitchMode.On);
        Assert.Equal("getToggles", Action(await _socket.NextSentAsync()));
        _socket.Push("""{"action":"getToggles","data":{"Hat":true}}""");
        Assert.Equal("""{"action":"toggle","data":{"receiverName":"Nope"}}""", await _socket.NextSentAsync());
        Assert.Null(await on); // no node, so Warudo reports nothing
    }

    [Fact]
    public async Task A_toggle_report_without_a_state_is_ignored()
    {
        await ConnectAsync();
        _socket.Push("""{"action":"toggle","data":{"receiverName":"Hat","isResponse":false}}""");
        _socket.Push("""{"action":"toggle","data":{"receiverName":"Hat","state":"maybe"}}""");
        await Wait.UntilAsync(() => _env.Log.Recent().Count(e => e.Message.Contains("without an on/off state")) == 2, "ignored");
        Assert.Empty(_env.Events(WarudoEvents.ToggleChanged));
        Assert.True(_warudo.Toggles["Hat"]);
    }

    [Fact]
    public async Task A_toggle_Cuebot_has_not_listed_reloads_the_receivers()
    {
        await ConnectAsync();
        _socket.Push("""{"action":"toggle","data":{"receiverName":"Scarf","state":true,"isResponse":false}}""");
        await _env.WaitForEventAsync(WarudoEvents.ToggleChanged, e => Equals(e.Args["receiver"], "Scarf"));
        await AnswerRefreshAsync(["Wave"], ["Hat", "Scarf"], [], """{"Hat":true,"Scarf":true}""");
        await Wait.UntilAsync(() => _warudo.Receivers(WarudoReceiverKind.Toggle).Contains("Scarf"), "Scarf listed");
    }

    [Fact]
    public async Task Old_suggestions_are_reloaded_in_the_background()
    {
        await ConnectAsync();
        Assert.Equal(["Confetti", "Wave"], _warudo.ReceiverSuggestions(WarudoReceiverKind.Trigger)); // fresh: nothing asked
        Assert.False(_socket.TryReadSent(out _));

        _warudo.SuggestionsMaxAge = TimeSpan.Zero;
        Assert.Equal(["Confetti", "Wave"], _warudo.ReceiverSuggestions(WarudoReceiverKind.Trigger)); // the cache answers at once
        await AnswerRefreshAsync(["Wave", "Confetti", "Hearts"], ["Hat"], ["Say"], """{"Hat":true}""");
        await Wait.UntilAsync(() => _warudo.Receivers(WarudoReceiverKind.Trigger).Contains("Hearts"), "Hearts listed");
    }

    [Fact]
    public async Task An_unknown_receiver_is_sent_anyway_with_one_warning()
    {
        await ConnectAsync();
        await _warudo.TriggerAsync("confetti");
        await _warudo.TriggerAsync("confetti");
        Assert.Equal("""{"action":"trigger","data":{"receiverName":"confetti"}}""", await _socket.NextSentAsync());
        Assert.Equal("""{"action":"trigger","data":{"receiverName":"confetti"}}""", await _socket.NextSentAsync());
        var warnings = _env.Log.Recent().Where(e => e.Level == LogLevel.Warning && e.Message.Contains("'confetti'")).ToArray();
        Assert.Single(warnings);
        Assert.Contains("did you mean 'Confetti'", warnings[0].Message);

        await _warudo.SendMessageAsync("Say", "hi");
        await _warudo.TriggerAsync("Wave");
        Assert.DoesNotContain(_env.Log.Recent(), e => e.Message.Contains("'Say'") || e.Message.Contains("'Wave'"));
    }

    [Fact]
    public async Task Warudo_running_without_a_stream_deck_node_is_told_apart()
    {
        _env.Settings.Value.Warudo.AutoReconnect = false;
        _env.Server.Reject = _ => new System.Net.WebSockets.WebSocketException("Connection refused");
        var probed = new List<(string Host, int Port)>();
        var warudoOpen = true;
        _warudo = new WarudoService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, TestTimings.Fast, _webSocketServer.Create,
            (host, port, _) => { lock (probed) probed.Add((host, port)); return Task.FromResult(warudoOpen); });

        await _warudo.ConnectAsync();
        await Wait.UntilAsync(() => _warudo.State == ConnectionState.Disconnected, "gave up");
        Assert.Contains("Warudo is running, but the open scene has no On Stream Deck node", _warudo.StatusMessage);
        Assert.Contains(("localhost", 19190), probed);

        warudoOpen = false;
        await _warudo.ConnectAsync();
        await Wait.UntilAsync(() => _warudo.State == ConnectionState.Disconnected && _warudo.StatusMessage.Contains("not reachable"), "closed");
        Assert.Contains("Warudo at localhost:19069 not reachable", _warudo.StatusMessage);
    }

    [Fact]
    public async Task WebSocket_messages_go_to_port_19190_and_reconnect_after_a_close()
    {
        _warudo = new WarudoService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, TestTimings.Fast, _webSocketServer.Create);

        // No Stream Deck connection needed.
        await _warudo.SendWebSocketMessageAsync("Zed sent 5 gifts");
        var socket = await _webSocketServer.NextAsync();
        Assert.Equal("ws://localhost:19190/", socket.Url!.ToString());
        Assert.Equal("Zed sent 5 gifts", await socket.NextSentAsync());
        await _warudo.SendWebSocketMessageAsync("""{"user":"Ann"}""");
        Assert.Equal("""{"user":"Ann"}""", await socket.NextSentAsync());

        // Warudo closes it (e.g. restarted): the next message opens a new connection.
        socket.Close();
        await Wait.UntilAsync(() => !_warudo.WebSocketOpen, "noticed the close");
        await _warudo.SendWebSocketMessageAsync("again");
        var again = await _webSocketServer.NextAsync();
        Assert.Equal("again", await again.NextSentAsync());

        await Assert.ThrowsAsync<ArgumentException>(() => _warudo.SendWebSocketMessageAsync(""));
        _webSocketServer.Reject = _ => new System.Net.WebSockets.WebSocketException("Connection refused");
        again.Close();
        await Wait.UntilAsync(() => !_warudo.WebSocketOpen, "noticed the close");
        var ex = await Assert.ThrowsAsync<StreamAppRequestException>(() => _warudo.SendWebSocketMessageAsync("x"));
        Assert.Contains("Is Warudo open?", ex.Message);
    }

    [Fact]
    public async Task Get_toggle_state_step_takes_the_on_or_off_branch()
    {
        await ConnectAsync();
        var step = WarudoSubActions.Create(_warudo).Single(h => h.TypeId == "warudo.getToggle");
        Assert.Equal(["On", "Off"], step.Branches);
        await using var runner = new ActionRunner(WarudoSubActions.Create(_warudo));
        static ActionDefinition Read(string branch) => new()
        {
            Name = "read " + branch,
            SubActions =
            [
                new SubActionDefinition
                {
                    Type = "warudo.getToggle",
                    Parameters = new() { ["receiver"] = "Hat" },
                    Branches = new() { [branch] = [new SubActionDefinition { Type = "test.capture" }] },
                },
            ],
        };

        var off = runner.RunAsync(Read("Off"));
        Assert.Equal("getToggles", Action(await _socket.NextSentAsync()));
        _socket.Push("""{"action":"getToggles","data":{"Hat":false}}""");
        var (ok, args) = await off;
        Assert.True(ok);
        Assert.Equal(false, args!["warudo.toggle"]);

        var on = runner.RunAsync(Read("On"));
        Assert.Equal("getToggles", Action(await _socket.NextSentAsync()));
        _socket.Push("""{"action":"getToggles","data":{"Hat":true}}""");
        Assert.Equal(true, (await on).Args!["warudo.toggle"]);

        var notTaken = runner.RunAsync(Read("On"));
        Assert.Equal("getToggles", Action(await _socket.NextSentAsync()));
        _socket.Push("""{"action":"getToggles","data":{"Hat":false}}""");
        Assert.Null((await notTaken).Args);
    }

    [Fact]
    public async Task Script_functions_drive_warudo()
    {
        await ConnectAsync();
        var functions = new WarudoScriptFunctions(_warudo).GetFunctions().ToDictionary(f => f.Name);
        Assert.Equal(["warudo.getToggle", "warudo.isConnected", "warudo.sendMessage", "warudo.sendWebSocket", "warudo.setToggle", "warudo.trigger"],
            functions.Keys.Order().ToArray());
        static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

        Assert.Equal(true, await functions["warudo.isConnected"].Invoke(null!, Args(new { }), default));
        await functions["warudo.trigger"].Invoke(null!, Args(new { receiver = "Wave" }), default);
        Assert.Equal("""{"action":"trigger","data":{"receiverName":"Wave"}}""", await _socket.NextSentAsync());
        await functions["warudo.sendMessage"].Invoke(null!, Args(new { receiver = "Say", message = "hi" }), default);
        Assert.Equal("""{"action":"message","data":{"receiverName":"Say","message":"hi"}}""", await _socket.NextSentAsync());
        Assert.Equal(true, await functions["warudo.setToggle"].Invoke(null!, Args(new { receiver = "Hat", state = "On" }), default));
        await functions["warudo.sendWebSocket"].Invoke(null!, Args(new { message = "yo" }), default);
        Assert.Equal("yo", await (await _webSocketServer.NextAsync()).NextSentAsync());
    }
}
