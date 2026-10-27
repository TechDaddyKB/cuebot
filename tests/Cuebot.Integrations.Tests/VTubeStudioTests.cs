using System.Text.Json;
using Cuebot.Core;
using Cuebot.Integrations.Common;
using Cuebot.Integrations.Tests.Support;
using Cuebot.Integrations.VTubeStudio;

namespace Cuebot.Integrations.Tests;

public sealed class VTubeStudioTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private VTubeStudioService _vts = null!;

    public Task InitializeAsync()
    {
        _vts = new VTubeStudioService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, TestTimings.Fast);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _vts.DisposeAsync();
        _env.Dispose();
    }

    private async Task<FakeVts> ConnectAsync(Action<FakeVts>? setup = null)
    {
        await _vts.ConnectAsync();
        _lastSocket = await _env.Server.NextAsync();
        var fake = new FakeVts(_lastSocket);
        setup?.Invoke(fake);
        await Wait.UntilAsync(() => _vts.State == ConnectionState.Connected, "connected");
        await Wait.UntilAsync(() => _vts.ItemFiles.Count > 0, "initial refresh");
        return fake;
    }

    [Fact]
    public async Task First_connection_requests_a_token_saves_it_and_authenticates()
    {
        var fake = await ConnectAsync();
        Assert.Equal("ws://localhost:8001/", _env.Server.Attempts[0].Url.ToString());

        var tokenRequest = Assert.Single(fake.Of("AuthenticationTokenRequest"));
        Assert.Equal("Cuebot", tokenRequest.Data.GetProperty("pluginName").GetString());
        Assert.Equal("Cuebot contributors", tokenRequest.Data.GetProperty("pluginDeveloper").GetString());
        Assert.Equal(FakeVts.IssuedToken, Assert.Single(fake.Of("AuthenticationRequest")).Data.GetProperty("authenticationToken").GetString());
        Assert.Equal(FakeVts.IssuedToken, _vts.Settings.AuthToken);
        Assert.Contains("1.28.0", _vts.StatusMessage);
        Assert.Single(_env.Events(VTubeStudioEvents.Connected));

        // Subscribed to the documented events and cached live data for suggestions.
        var subscribed = fake.Of("EventSubscriptionRequest").Select(r => r.Data.GetProperty("eventName").GetString()).ToArray();
        Assert.Contains("HotkeyTriggeredEvent", subscribed);
        Assert.Contains("ModelLoadedEvent", subscribed);
        Assert.Contains("TrackingStatusChangedEvent", subscribed);
        Assert.Contains("ItemEvent", subscribed);
        Assert.All(fake.Of("EventSubscriptionRequest"), r => Assert.True(r.Data.GetProperty("subscribe").GetBoolean()));
        Assert.Equal("Akari", _vts.CurrentModel!.Name);
        Assert.Equal(["Wave", "Heart Eyes"], _vts.Hotkeys.Select(h => h.Name));
        Assert.Equal(["Akari", "Hiyori"], _vts.Models.Select(m => m.Name));
        Assert.Equal(["Heart", "Angry"], _vts.Expressions.Select(e => e.DisplayName));
        Assert.Equal(["FaceAngleX", "MyParam"], _vts.ParameterNames);
        Assert.Equal("item-1", Assert.Single(_vts.Items).InstanceId);
        Assert.DoesNotContain(FakeVts.IssuedToken, _env.AllLogText());
    }

    [Fact]
    public async Task Saved_token_is_reused_and_a_rejected_token_is_replaced()
    {
        _vts.Settings.AuthToken = "VTS-old-revoked-token-000000000000";
        var fake = await ConnectAsync();
        var auths = fake.Of("AuthenticationRequest");
        Assert.Equal(2, auths.Length);
        Assert.Equal("VTS-old-revoked-token-000000000000", auths[0].Data.GetProperty("authenticationToken").GetString());
        Assert.Single(fake.Of("AuthenticationTokenRequest"));
        Assert.Equal(FakeVts.IssuedToken, _vts.Settings.AuthToken);

        // A valid saved token: no popup.
        await _vts.DisconnectAsync();
        await _vts.ConnectAsync();
        var second = new FakeVts(await _env.Server.NextAsync());
        await Wait.UntilAsync(() => _vts.State == ConnectionState.Connected, "reconnected");
        Assert.Empty(second.Of("AuthenticationTokenRequest"));
        Assert.Equal(2, _env.Events(VTubeStudioEvents.Connected).Length);
        Assert.Single(_env.Events(VTubeStudioEvents.Disconnected));
    }

    [Fact]
    public async Task Denied_plugin_request_stops_with_an_error_and_does_not_reconnect()
    {
        await _vts.ConnectAsync();
        var fake = new FakeVts(await _env.Server.NextAsync()) { DenyToken = true };
        await Wait.UntilAsync(() => _vts.State == ConnectionState.Error, "error");
        Assert.Contains("denied", _vts.StatusMessage);
        Assert.False(_vts.IsRunning);
        await Task.Delay(150);
        Assert.Single(_env.Server.Attempts);
        Assert.Empty(_env.Events(VTubeStudioEvents.Connected));
        Assert.Equal("", _vts.Settings.AuthToken);
        Assert.Single(fake.Of("AuthenticationTokenRequest"));
    }

    [Fact]
    public async Task Unreachable_app_is_retried_until_it_starts()
    {
        var refusals = 2;
        _env.Server.Reject = _ => Interlocked.Decrement(ref refusals) >= 0 ? FakeServer.Refused() : null;
        await _vts.ConnectAsync();
        _ = new FakeVts(await _env.Server.NextAsync());
        await Wait.UntilAsync(() => _vts.State == ConnectionState.Connected, "connected");
        Assert.Equal(3, _env.Server.Attempts.Count);
    }

    [Fact]
    public async Task Events_are_republished_and_tracking_lost_and_found_are_derived()
    {
        var fake = await ConnectAsync();
        fake.PushEvent("HotkeyTriggeredEvent", new
        {
            hotkeyID = "hk-wave", hotkeyName = "Wave", hotkeyAction = "TriggerAnimation", hotkeyFile = "wave.motion3.json",
            hotkeyTriggeredByAPI = false, modelID = "model-akari", modelName = "Akari", isLive2DItem = false,
        });
        var hotkey = await _env.WaitForEventAsync(VTubeStudioEvents.HotkeyTriggered);
        Assert.Equal("Wave", hotkey.Args["hotkeyName"]);
        Assert.Equal("TriggerAnimation", hotkey.Args["hotkeyAction"]);
        Assert.Equal(false, hotkey.Args["triggeredByApi"]);
        Assert.Equal("VTube Studio", hotkey.Args["service"]);

        fake.PushEvent("TrackingStatusChangedEvent", new { faceFound = false, leftHandFound = false, rightHandFound = false });
        await _env.WaitForEventAsync(VTubeStudioEvents.TrackingLost);
        fake.PushEvent("TrackingStatusChangedEvent", new { faceFound = false, leftHandFound = true, rightHandFound = false });
        fake.PushEvent("TrackingStatusChangedEvent", new { faceFound = true, leftHandFound = true, rightHandFound = false });
        await _env.WaitForEventAsync(VTubeStudioEvents.TrackingFound);
        Assert.Single(_env.Events(VTubeStudioEvents.TrackingLost));
        Assert.Equal(3, _env.Events(VTubeStudioEvents.TrackingStatusChanged).Length);

        fake.PushEvent("ModelLoadedEvent", new { modelLoaded = false, modelName = "Akari", modelID = "model-akari" });
        Assert.Equal("Akari", (await _env.WaitForEventAsync(VTubeStudioEvents.ModelUnloaded)).Args["modelName"]);
        fake.PushEvent("ModelLoadedEvent", new { modelLoaded = true, modelName = "Hiyori", modelID = "model-hiyori" });
        Assert.Equal("model-hiyori", (await _env.WaitForEventAsync(VTubeStudioEvents.ModelLoaded)).Args["modelId"]);

        fake.PushEvent("ItemEvent", new { itemEventType = "Added", itemInstanceID = "item-9", itemFileName = "cat.gif", itemPosition = new { x = 0.5, y = -0.5 } });
        var item = await _env.WaitForEventAsync(VTubeStudioEvents.ItemEvent);
        Assert.Equal("Added", item.Args["itemEventType"]);
        Assert.Equal(0.5, item.Args["itemX"]);

        fake.PushEvent("ModelClickedEvent", new { modelLoaded = true, loadedModelID = "m", loadedModelName = "Akari", modelWasClicked = true, mouseButtonID = 1, clickPosition = new { x = 1, y = 2 }, clickedArtMeshCount = 0, artMeshHits = Array.Empty<object>() });
        Assert.Equal("Right", (await _env.WaitForEventAsync(VTubeStudioEvents.ModelClicked)).Args["mouseButton"]);

        Assert.Contains(_env.Events(VTubeStudioEvents.Any), e => (string?)e.Args["eventName"] == "ItemEvent");
        // Model change refreshes hotkeys etc. in the background.
        await Wait.UntilAsync(() => fake.Of("HotkeysInCurrentModelRequest").Length >= 2, "refresh after model load");
    }

    [Fact]
    public async Task Commands_resolve_names_from_live_data()
    {
        var fake = await ConnectAsync();

        Assert.Equal("hk-heart", await _vts.TriggerHotkeyAsync("heart eyes"));
        Assert.Equal("hk-heart", fake.Of("HotkeyTriggerRequest").Last().Data.GetProperty("hotkeyID").GetString());

        Assert.Equal("model-hiyori", await _vts.LoadModelAsync("Hiyori"));
        Assert.Equal("model-hiyori", fake.Of("ModelLoadRequest").Last().Data.GetProperty("modelID").GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => _vts.LoadModelAsync("Nobody"));

        await _vts.MoveModelAsync(0.5, relative: true, positionX: 0.25, positionY: null, rotation: 10, size: null);
        var move = fake.Of("MoveModelRequest").Last().Data;
        Assert.Equal(0.25, move.GetProperty("positionX").GetDouble());
        Assert.False(move.TryGetProperty("positionY", out _));
        Assert.True(move.GetProperty("valuesAreRelativeToModel").GetBoolean());

        // Toggle: "Heart" is active → deactivated.
        Assert.False(await _vts.SetExpressionAsync("Heart", SwitchMode.Toggle));
        var expression = fake.Of("ExpressionActivationRequest").Last().Data;
        Assert.Equal("heart.exp3.json", expression.GetProperty("expressionFile").GetString());
        Assert.False(expression.GetProperty("active").GetBoolean());
        Assert.True(await _vts.SetExpressionAsync("angry", SwitchMode.On));

        Assert.Equal(42, await _vts.ColorTintAsync(VtsProtocol.ParseColor("#FF000080"), ["Hair"], []));
        var tint = fake.Of("ColorTintRequest").Last().Data;
        Assert.Equal(128, tint.GetProperty("colorTint").GetProperty("colorA").GetInt32());
        Assert.False(tint.GetProperty("artMeshMatcher").GetProperty("tintAll").GetBoolean());
        Assert.Equal("Hair", tint.GetProperty("artMeshMatcher").GetProperty("nameContains")[0].GetString());

        Assert.Equal("item-new", await _vts.LoadItemAsync("cat.gif", 0, 0, 0.3, 0, 0.5, null, true));
        await _vts.MoveItemAsync("hat.png", 1, "easeBoth", 0.5, null, null, null);
        Assert.Equal("item-1", fake.Of("ItemMoveRequest").Last().Data.GetProperty("itemsToMove")[0].GetProperty("itemInstanceID").GetString());
        Assert.Equal(1, await _vts.UnloadItemsAsync(["item-1"], [], all: false));

        fake.Override = (type, _) => type == "HotkeyTriggerRequest" ? FakeVts.Error(350, "Hotkey not found") : null;
        var error = await Assert.ThrowsAsync<IntegrationRequestException>(() => _vts.TriggerHotkeyAsync("Wave"));
        Assert.Equal(350, error.ErrorId);
        Assert.Contains("Hotkey not found", error.Message);
    }

    [Fact]
    public async Task Injected_parameters_are_resent_while_held()
    {
        var fake = await ConnectAsync();
        await _vts.InjectParametersAsync(VtsProtocol.ParseParameterValues("FaceAngleX=20, MouthOpen=1@0.5"), add: false, faceFound: false,
            TimeSpan.FromMilliseconds(600));
        var injections = fake.Of("InjectParameterDataRequest");
        Assert.InRange(injections.Length, 3, 20); // every 50 ms for 600 ms, with room for slow machines
        var values = injections[0].Data.GetProperty("parameterValues");
        Assert.Equal("FaceAngleX", values[0].GetProperty("id").GetString());
        Assert.Equal(20, values[0].GetProperty("value").GetDouble());
        Assert.Equal(0.5, values[1].GetProperty("weight").GetDouble());
        Assert.False(values[0].TryGetProperty("weight", out _));
        Assert.Equal("set", injections[0].Data.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Reconnects_after_the_app_closes_and_commands_fail_while_disconnected()
    {
        await ConnectAsync();
        var socket = _env.Server.Attempts.Count;
        _lastSocket!.Close();
        await _env.WaitForEventAsync(VTubeStudioEvents.Disconnected);
        await Assert.ThrowsAsync<IntegrationNotConnectedException>(() => _vts.TriggerHotkeyAsync("Wave"));
        var next = new FakeVts(await _env.Server.NextAsync());
        await Wait.UntilAsync(() => _vts.State == ConnectionState.Connected, "reconnected");
        Assert.Equal(socket + 1, _env.Server.Attempts.Count);
        Assert.Equal(2, _env.Events(VTubeStudioEvents.Connected).Length);
        Assert.Empty(next.Of("AuthenticationTokenRequest"));

        await _vts.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, _vts.State);
        Assert.Equal(2, _env.Events(VTubeStudioEvents.Disconnected).Length);
    }

    private FakeSocket? _lastSocket;

    [Fact]
    public void Parameter_and_color_parsing()
    {
        Assert.Equal([new VtsParameterValue("A", 1, null), new VtsParameterValue("B", -2.5, 0.3)],
            VtsProtocol.ParseParameterValues("A=1; B = -2.5@0.3"));
        Assert.Equal([new VtsParameterValue("FaceAngleX", 10, null)], VtsProtocol.ParseParameterValues("""{"FaceAngleX":10}"""));
        Assert.Equal([new VtsParameterValue("X", 1, 0.5)], VtsProtocol.ParseParameterValues("""[{"id":"X","value":1,"weight":0.5}]"""));
        Assert.Throws<ArgumentException>(() => VtsProtocol.ParseParameterValues("A=abc"));
        Assert.Throws<ArgumentException>(() => VtsProtocol.ParseParameterValues(""));
        Assert.Equal((255, 0, 128, 255), VtsProtocol.ParseColor("#FF0080"));
        Assert.Equal((1, 2, 3, 4), VtsProtocol.ParseColor("1, 2, 3, 4"));
        Assert.Throws<ArgumentException>(() => VtsProtocol.ParseColor("red"));
    }

    [Fact]
    public void Request_envelope_matches_the_public_api()
    {
        using var doc = JsonDocument.Parse(VtsProtocol.Request("HotkeyTriggerRequest", "r1", new { hotkeyID = "x", itemInstanceID = (string?)null }));
        var root = doc.RootElement;
        Assert.Equal("VTubeStudioPublicAPI", root.GetProperty("apiName").GetString());
        Assert.Equal("1.0", root.GetProperty("apiVersion").GetString());
        Assert.Equal("r1", root.GetProperty("requestID").GetString());
        Assert.False(root.GetProperty("data").TryGetProperty("itemInstanceID", out _));
    }
}
