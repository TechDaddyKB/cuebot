using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Integrations.Common;
using Cuebot.Integrations.Tests.Support;
using Cuebot.Integrations.VTubeStudio;

namespace Cuebot.Integrations.Tests;

/// <summary>The VTube Studio API fixes and features from the 2026-11 gap analysis (against the scripted VTube Studio).</summary>
public sealed class VTubeStudioFeatureTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private readonly FakeDiscovery _discovery = new();
    private VTubeStudioService _vts = null!;

    public Task InitializeAsync()
    {
        _vts = new VTubeStudioService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, TestTimings.Fast, _discovery);
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
        var fake = new FakeVts(await _env.Server.NextAsync(), setup);
        await Wait.UntilAsync(() => _vts.State == ConnectionState.Connected, "connected");
        await Wait.UntilAsync(() => _vts.ItemFiles.Count > 0, "initial refresh");
        return fake;
    }

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    [Fact]
    public async Task Subscriptions_spell_out_their_config_and_the_token_request_carries_the_icon()
    {
        var fake = await ConnectAsync();
        var subscriptions = fake.Of("EventSubscriptionRequest").ToDictionary(r => Str(r.Data, "eventName"), r => r.Data.GetProperty("config"));
        Assert.False(subscriptions["ModelClickedEvent"].GetProperty("onlyClicksOnModel").GetBoolean());
        Assert.False(subscriptions["ExpressionToggledEvent"].GetProperty("sendAllActiveStatesOnSubscription").GetBoolean());
        Assert.False(subscriptions["ExpressionToggledEvent"].GetProperty("ignoreLive2DItems").GetBoolean());
        Assert.Contains("Live2DCubismEditorConnectedEvent", subscriptions.Keys);

        var icon = Convert.FromBase64String(Str(Assert.Single(fake.Of("AuthenticationTokenRequest")).Data, "pluginIcon"));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], icon[..4]);
        // 128x128, as VTube Studio requires (PNG IHDR width and height).
        Assert.Equal(128, (icon[16] << 24) | (icon[17] << 16) | (icon[18] << 8) | icon[19]);
        Assert.Equal(128, (icon[20] << 24) | (icon[21] << 16) | (icon[22] << 8) | icon[23]);
    }

    [Fact]
    public async Task Unloading_by_a_file_name_without_a_dot_sends_it_as_a_file_name()
    {
        var fake = await ConnectAsync();
        var (ids, files) = _vts.SplitItemEntries(["item-1", "hat.png", "ANIM_headpat"]);
        Assert.Equal(["item-1", "ANIM_headpat"], ids);
        Assert.Equal(["hat.png", "ANIM_headpat"], files);

        await _vts.UnloadItemsAsync([], [], all: false, onlyLoadedByCuebot: true);
        var unload = fake.Of("ItemUnloadRequest").Last().Data;
        Assert.True(unload.GetProperty("unloadAllLoadedByThisPlugin").GetBoolean());
        Assert.False(unload.GetProperty("allowUnloadingItemsLoadedByUserOrOtherPlugins").GetBoolean());
    }

    [Fact]
    public async Task Moving_an_item_sends_minus_1000_for_what_stays_and_moves_every_copy_of_a_file()
    {
        var fake = await ConnectAsync();
        await _vts.MoveItemsAsync(["hat.png"], 1, "zip", 0.5, null, null, null, null, true);

        var item = Assert.Single(fake.Of("ItemMoveRequest").Last().Data.GetProperty("itemsToMove").EnumerateArray());
        Assert.Equal("item-1", Str(item, "itemInstanceID"));
        Assert.Equal(0.5, item.GetProperty("positionX").GetDouble());
        Assert.Equal(-1000, item.GetProperty("positionY").GetDouble());
        Assert.Equal(-1000, item.GetProperty("size").GetDouble());
        Assert.Equal(-1000, item.GetProperty("rotation").GetDouble());
        Assert.Equal(-1000, item.GetProperty("order").GetInt32());
        Assert.True(item.GetProperty("setFlip").GetBoolean());
        Assert.Equal("zip", Str(item, "fadeMode"));
    }

    [Fact]
    public async Task Expression_snapshots_and_item_clicks_map_as_the_spec_describes()
    {
        var fake = await ConnectAsync();
        fake.PushEvent("ExpressionToggledEvent", new
        {
            modelID = "m", modelName = "Akari", isLive2DItem = true, itemInstanceID = "it-9", justLoaded = true,
            expressionFile = "EyesCry.exp3.json", expressionName = "EyesCry", active = false,
        });
        fake.PushEvent("ItemEvent", new { itemEventType = "ItemClicked", itemInstanceID = "item-1", itemFileName = "hat.png", itemPosition = new { x = 0.1, y = 0.2 } });

        var expression = await _env.WaitForEventAsync(VTubeStudioEvents.ExpressionToggled);
        Assert.Equal(true, expression.Args["justLoaded"]);
        Assert.Equal(true, expression.Args["isLive2DItem"]);
        Assert.Equal("it-9", expression.Args["itemInstanceId"]);
        var trigger = Trigger("vtubestudio.expressionToggled");
        Assert.False(Matches(trigger, expression, ("stateFilter", "Any"), ("source", "Any"), ("ignoreSnapshots", "true")));
        Assert.True(Matches(trigger, expression, ("stateFilter", "Any"), ("source", "Live2DItems"), ("ignoreSnapshots", "false")));
        Assert.False(Matches(trigger, expression, ("stateFilter", "Any"), ("source", "Model"), ("ignoreSnapshots", "false")));

        var click = await _env.WaitForEventAsync(VTubeStudioEvents.ItemEvent);
        Assert.Equal("Clicked", click.Args["itemEventType"]);
    }

    [Fact]
    public async Task Model_clicks_expose_the_clicked_art_mesh_and_pin_items_there()
    {
        var fake = await ConnectAsync();
        fake.PushEvent("ModelClickedEvent", new
        {
            modelLoaded = true, loadedModelID = "model-akari", loadedModelName = "Akari", modelWasClicked = true, mouseButtonID = 0,
            clickPosition = new { x = 0.1, y = 0.3 }, windowSize = new { x = 1920, y = 1080 }, clickedArtMeshCount = 2,
            artMeshHits = new[]
            {
                new { artMeshOrder = 1, isMasked = true, hitInfo = new { modelID = "model-akari", artMeshID = "face_skin", angle = 0.0, size = 1.0, vertexID1 = 1, vertexID2 = 2, vertexID3 = 3, vertexWeight1 = 0.2, vertexWeight2 = 0.3, vertexWeight3 = 0.5 } },
                new { artMeshOrder = 0, isMasked = false, hitInfo = new { modelID = "model-akari", artMeshID = "hair_right6", angle = 0.0, size = 1.0, vertexID1 = 80, vertexID2 = 76, vertexID3 = 75, vertexWeight1 = 0.47, vertexWeight2 = 0.08, vertexWeight3 = 0.45 } },
            },
        });
        var clicked = await _env.WaitForEventAsync(VTubeStudioEvents.ModelClicked);
        Assert.Equal("hair_right6", clicked.Args["artMeshId"]);
        Assert.Equal("hair_right6, face_skin", clicked.Args["artMeshIds"]);
        var trigger = Trigger("vtubestudio.modelClicked");
        Assert.True(Matches(trigger, clicked, ("mouseButton", "Any"), ("onlyOnModel", "true"), ("artMeshes", "FACE_SKIN")));
        Assert.False(Matches(trigger, clicked, ("mouseButton", "Any"), ("onlyOnModel", "true"), ("artMeshes", "arm")));

        await Wait.UntilAsync(() => _vts.LastClick is not null, "last click");
        await _vts.PinItemAsync("item-1", VtsPinPlace.LastClick, "", null, 0.4);
        var pin = fake.Of("ItemPinRequest").Last().Data;
        Assert.Equal("Provided", Str(pin, "vertexPinType"));
        Assert.Equal("RelativeToWorld", Str(pin, "sizeRelativeTo"));
        Assert.Equal("RelativeToCurrentItemRotation", Str(pin, "angleRelativeTo"));
        Assert.Equal("hair_right6", Str(pin.GetProperty("pinInfo"), "artMeshID"));
        Assert.Equal(80, pin.GetProperty("pinInfo").GetProperty("vertexID1").GetInt32());
    }

    [Fact]
    public async Task Hands_report_lost_and_found_after_the_first_status()
    {
        var fake = await ConnectAsync();
        fake.PushEvent("TrackingStatusChangedEvent", new { faceFound = true, leftHandFound = true, rightHandFound = false });
        fake.PushEvent("TrackingStatusChangedEvent", new { faceFound = true, leftHandFound = false, rightHandFound = true });

        var lost = await _env.WaitForEventAsync(VTubeStudioEvents.HandLost);
        var found = await _env.WaitForEventAsync(VTubeStudioEvents.HandFound);
        Assert.Equal("Left", lost.Args["hand"]);
        Assert.Equal("Right", found.Args["hand"]);
        Assert.Single(_env.Events(VTubeStudioEvents.HandLost));
    }

    [Fact]
    public async Task A_timed_expression_goes_back_and_a_second_timed_change_keeps_the_first_state()
    {
        var fake = await ConnectAsync();
        await _vts.SetExpressionAsync("Angry", SwitchMode.On, 0, default, TimeSpan.FromMilliseconds(400));
        await _vts.SetExpressionAsync("Angry", SwitchMode.On, 0, default, TimeSpan.FromMilliseconds(200));

        await Wait.UntilAsync(() => fake.Of("ExpressionActivationRequest").Length == 3, "the expression to go back");
        await Task.Delay(500);
        var requests = fake.Of("ExpressionActivationRequest").Select(r => r.Data.GetProperty("active").GetBoolean()).ToArray();
        Assert.Equal([true, true, false], requests); // back to off (its state before the first change), once
    }

    [Fact]
    public async Task A_timed_undo_is_retried_while_a_window_is_open_in_VTube_Studio()
    {
        var unloads = 0;
        var fake = await ConnectAsync(f => f.Override = (type, _) =>
            type == "ItemUnloadRequest" && Interlocked.Increment(ref unloads) <= 1 ? FakeVts.Error(800, "Cannot currently unload items.") : null);
        await _vts.LoadItemAsync(new VtsItemLoad("hat.png") { UnloadAfter = TimeSpan.FromMilliseconds(50) });
        // The first unload is refused (a menu is open); the undo tries again 2 seconds later.
        await Wait.UntilAsync(() => fake.Of("ItemUnloadRequest").Length == 2, "the unload to be tried again");
        Assert.DoesNotContain(_env.Log.Recent(), e => e.Message.Contains("Could not undo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tints_are_remembered_reapplied_after_reconnecting_and_timed_tints_go_back()
    {
        var fake = await ConnectAsync(Setup);
        await _vts.ColorTintAsync((255, 0, 0, 255), new VtsTintMatcher { Groups = ["Hair"], NameExact = ["eye_l"] }, 1, null);
        var tint = fake.Of("ColorTintRequest").Last().Data.GetProperty("artMeshMatcher");
        Assert.Equal(["group-hair"], tint.GetProperty("artMeshGroupIDExact").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["eye_l"], tint.GetProperty("nameExact").EnumerateArray().Select(e => e.GetString()));

        await _vts.ColorTintAsync((0, 0, 255, 255), VtsTintMatcher.All, 1, TimeSpan.FromMilliseconds(200));
        // Going back: white everywhere, then the remembered hair tint.
        await Wait.UntilAsync(() => fake.Of("ColorTintRequest").Length == 4, "timed tint to go back");
        var last = fake.Of("ColorTintRequest").TakeLast(2).Select(r => r.Data.GetProperty("colorTint").GetProperty("colorG").GetInt32()).ToArray();
        Assert.Equal([255, 0], last);

        _vts.Settings.AuthToken = FakeVts.IssuedToken;
        await _vts.DisconnectAsync();
        await _vts.ConnectAsync();
        var second = new FakeVts(await _env.Server.NextAsync(), Setup);
        await Wait.UntilAsync(() => second.Of("ColorTintRequest").Length == 1, "tint re-applied");
        Assert.Equal(255, second.Of("ColorTintRequest")[0].Data.GetProperty("colorTint").GetProperty("colorR").GetInt32());
        return;

        static void Setup(FakeVts f) => f.Override = ArtMeshes;
    }

    private static (string, object)? ArtMeshes(string type, JsonElement _) => type == "ArtMeshListRequest"
        ? ("ArtMeshListResponse", new
        {
            modelLoaded = true, artMeshNames = new[] { "eye_l", "hair_1" }, artMeshTags = new[] { "hair" },
            artMeshGroups = new[] { new { groupID = "group-hair", groupName = "Hair", artMeshNames = new[] { "hair_1" } } },
        })
        : null;

    [Fact]
    public async Task A_revoked_plugin_reconnects_and_asks_again()
    {
        var fake = await ConnectAsync();
        fake.ValidTokens.Clear();
        fake.Override = (type, _) => type == "HotkeyTriggerRequest" ? FakeVts.Error(50, "User has denied API access for your plugin.") : null;

        await Assert.ThrowsAsync<IntegrationRequestException>(() => _vts.TriggerHotkeyAsync("Wave"));
        // The session ends and Cuebot authenticates again (with a fresh token request if VTube Studio rejects the old one).
        var second = new FakeVts(await _env.Server.NextAsync());
        await Wait.UntilAsync(() => _vts.State == ConnectionState.Connected && second.Of("AuthenticationRequest").Length == 1, "signed in again");
        Assert.Contains(_env.Log.Recent(), e => e.Message.Contains("revoked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Busy_hotkeys_are_retried()
    {
        var calls = 0;
        var fake = await ConnectAsync(f => f.Override = (type, _) =>
            type == "HotkeyTriggerRequest" && Interlocked.Increment(ref calls) == 1 ? FakeVts.Error(203, "Hotkey cooldown not over.") : null);
        Assert.Equal("hk-wave", await _vts.TriggerHotkeyAsync("Wave"));
        Assert.Equal(2, fake.Of("HotkeyTriggerRequest").Length);
    }

    [Fact]
    public async Task Errors_explain_what_to_do()
    {
        await ConnectAsync(f => f.Override = (type, _) => type == "InjectParameterDataRequest" ? FakeVts.Error(454, "Controlled by another plugin.") : null);
        var error = await Assert.ThrowsAsync<IntegrationRequestException>(() =>
            _vts.InjectParametersAsync([new VtsParameterValue("FaceAngleX", 1, null)], false, false, TimeSpan.Zero));
        Assert.Contains("\"add\"", error.Message);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _vts.InjectParametersAsync([new VtsParameterValue("FaceAngleX", 1, 2)], false, false, TimeSpan.Zero));
    }

    [Fact]
    public async Task Custom_parameters_are_saved_created_again_on_connect_and_kept_set()
    {
        var fake = await ConnectAsync();
        await _vts.CreateCustomParameterAsync(new VtsCustomParameter { Name = "CuebotHeartRate", Min = 40, Max = 200, DefaultValue = 70 });
        Assert.Equal("CuebotHeartRate", Str(fake.Of("ParameterCreationRequest").Single().Data, "parameterName"));
        Assert.Contains("CuebotHeartRate", _vts.ParameterNames);
        await Assert.ThrowsAsync<ArgumentException>(() => _vts.CreateCustomParameterAsync(new VtsCustomParameter { Name = "has space" }));

        await _vts.SetParameterAsync("CuebotHeartRate", 92, null, null);
        await Wait.UntilAsync(() => fake.Of("InjectParameterDataRequest").Length >= 3, "kept set");
        Assert.All(fake.Of("InjectParameterDataRequest"), r => Assert.Equal(92, r.Data.GetProperty("parameterValues")[0].GetProperty("value").GetDouble()));
        Assert.Equal(1, _vts.StopParameter(""));

        await _vts.DisconnectAsync();
        await _vts.ConnectAsync();
        var second = new FakeVts(await _env.Server.NextAsync());
        await Wait.UntilAsync(() => second.Of("ParameterCreationRequest").Length == 1, "created again");
    }

    [Fact]
    public async Task Visual_effects_presets_load_and_go_back()
    {
        var fake = await ConnectAsync(f => f.Override = (type, data) => type switch
        {
            "PostProcessingListRequest" => ("PostProcessingListResponse", new
            {
                postProcessingSupported = true, postProcessingActive = true, presetIsActive = true, activePreset = "Calm", activeEffectCount = 2,
                postProcessingPresets = new[] { "Calm", "Rain" },
                postProcessingEffects = new[] { new { enumID = "Bloom", configEntries = new[] { new { enumID = "Bloom_Strength" } } } },
            }),
            "PostProcessingUpdateRequest" => ("PostProcessingUpdateResponse", new { postProcessingActive = true, presetIsActive = true, activePreset = "Rain", activeEffectCount = 3 }),
            _ => null,
        });
        await Wait.UntilAsync(() => _vts.VfxPresets.Count == 2 && _vts.VfxConfigIds.Count > 0, "effects listed");
        Assert.Equal(["Calm", "Rain"], _vts.VfxPresets);
        Assert.Equal(["Bloom_Strength"], _vts.VfxConfigIds);

        var state = await _vts.LoadVfxPresetAsync("Rain", 0.5, TimeSpan.FromMilliseconds(200));
        Assert.Equal("Rain", state.ActivePreset);
        await Wait.UntilAsync(() => fake.Of("PostProcessingUpdateRequest").Length == 2, "effects to go back");
        var back = fake.Of("PostProcessingUpdateRequest")[1].Data;
        Assert.Equal("Calm", Str(back, "presetToSet"));
        Assert.True(back.GetProperty("setPostProcessingPreset").GetBoolean());

        Assert.Equal([KeyValuePair.Create("Bloom_Strength", "0.8"), KeyValuePair.Create("Bloom_StreakColorTint", "220308FF")],
            VTubeStudioService.ParseVfxValues("Bloom_Strength=0.8; Bloom_StreakColorTint=220308FF"));
        Assert.Throws<ArgumentException>(() => VTubeStudioService.ParseVfxValues("A=1, a=2"));
    }

    [Fact]
    public async Task Throwing_flies_items_at_the_head_flinches_and_cleans_up()
    {
        var fake = await ConnectAsync();
        fake.Override = (type, data) =>
        {
            if (type == "EventSubscriptionRequest" && Str(data, "eventName") == "ModelOutlineEvent" && data.GetProperty("subscribe").GetBoolean())
                fake.PushEvent("ModelOutlineEvent", new
                {
                    modelName = "Akari", modelID = "model-akari", convexHullCenter = new { x = 0.2, y = -0.2 },
                    convexHull = new[] { new { x = 0.0, y = 0.8 }, new { x = 0.4, y = 0.8 }, new { x = 0.4, y = -1.2 }, new { x = 0.0, y = -1.2 } },
                    windowSize = new { x = 1920, y = 1080 },
                });
            return null;
        };

        Assert.Equal(2, await _vts.ThrowAsync(new VtsThrow { Files = ["hat.png"], Count = 2, FlightTime = 0.05, Interval = 0 }));

        var loads = fake.Of("ItemLoadRequest");
        Assert.Equal(2, loads.Length);
        Assert.All(loads, l => Assert.True(l.Data.GetProperty("unloadWhenPluginDisconnects").GetBoolean()));
        Assert.All(loads, l => Assert.True(Math.Abs(l.Data.GetProperty("positionX").GetDouble()) > 1)); // off screen
        var flight = fake.Of("ItemMoveRequest").First().Data.GetProperty("itemsToMove")[0];
        Assert.InRange(flight.GetProperty("positionY").GetDouble(), 0.3, 0.7); // the head: above the outline's center
        Assert.All(fake.Of("InjectParameterDataRequest"), r => Assert.Equal("add", Str(r.Data, "mode")));
        Assert.Equal(2, fake.Of("ItemUnloadRequest").Length);
        Assert.Contains(fake.Of("EventSubscriptionRequest"), r => Str(r.Data, "eventName") == "ModelOutlineEvent" && !r.Data.GetProperty("subscribe").GetBoolean());
    }

    [Fact]
    public async Task Images_need_the_permission_and_load_as_custom_data()
    {
        var png = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Cuebot.Integrations", "VTubeStudio", "plugin-icon.png")));
        var fake = await ConnectAsync(f => f.Override = (type, _) => type == "PermissionRequest"
            ? ("PermissionResponse", new { grantSuccess = true, requestedPermission = "LoadCustomImagesAsItems", permissions = new[] { new { name = "LoadCustomImagesAsItems", granted = true } } })
            : null);

        await _vts.LoadItemAsync(new VtsItemLoad("") { Image = png, Pin = VtsPinPlace.Random, UnloadAfter = TimeSpan.FromMilliseconds(100) });
        await _vts.LoadItemAsync(new VtsItemLoad("") { Image = png });

        Assert.Single(fake.Of("PermissionRequest")); // asked once per session
        var load = fake.Of("ItemLoadRequest")[0].Data;
        Assert.Equal(png, Str(load, "customDataBase64"));
        Assert.Matches("^cuebot[0-9a-f]{12}\\.png$", Str(load, "fileName"));
        Assert.Equal("Random", Str(fake.Of("ItemPinRequest").Single().Data, "vertexPinType"));
        await Wait.UntilAsync(() => fake.Of("ItemUnloadRequest").Length == 1, "timed unload");
        await Assert.ThrowsAsync<ArgumentException>(() => _vts.LoadItemAsync(new VtsItemLoad("") { Image = Convert.ToBase64String("not an image"u8.ToArray()) }));
    }

    [Fact]
    public async Task Physics_overrides_are_resent_until_the_time_is_over()
    {
        var fake = await ConnectAsync();
        await _vts.PhysicsOverrideAsync(null, 80, [], TimeSpan.FromSeconds(1));
        var request = fake.Of("SetCurrentModelPhysicsRequest").Single().Data;
        var wind = request.GetProperty("windOverrides")[0];
        Assert.True(wind.GetProperty("setBaseValue").GetBoolean());
        Assert.Equal(80, wind.GetProperty("value").GetDouble());
        Assert.InRange(wind.GetProperty("overrideSeconds").GetDouble(), 0.5, 1);
        Assert.Empty(request.GetProperty("strengthOverrides").EnumerateArray());
    }

    [Fact]
    public async Task Discovered_instances_are_tried_after_the_configured_port()
    {
        _discovery.List = [new VtsInstance("i2", "VTube Studio Window 2", 8002, true, DateTimeOffset.UtcNow)];
        _env.Server.Reject = url => url.Port == 8001 ? FakeServer.Refused() : null;
        await _vts.ConnectAsync();
        _ = new FakeVts(await _env.Server.NextAsync());
        await Wait.UntilAsync(() => _vts.State == ConnectionState.Connected, "connected through discovery");
        Assert.Equal([8001, 8002], _env.Server.Attempts.Select(a => a.Url.Port).Distinct());
        Assert.Contains("VTube Studio Window 2", _vts.StatusMessage);
    }

    [Fact]
    public void Discovery_broadcasts_parse()
    {
        var instance = VtsProtocol.ParseBroadcast("""
            {"apiName":"VTubeStudioPublicAPI","apiVersion":"1.0","timestamp":1,"messageType":"VTubeStudioAPIStateBroadcast",
             "requestID":"VTubeStudioAPIStateBroadcast","data":{"active":true,"port":8002,"instanceID":"abc","windowTitle":"VTube Studio Window 2"}}
            """, DateTimeOffset.UnixEpoch);
        Assert.Equal(new VtsInstance("abc", "VTube Studio Window 2", 8002, true, DateTimeOffset.UnixEpoch), instance);
        Assert.Null(VtsProtocol.ParseBroadcast("{\"messageType\":\"Other\"}", DateTimeOffset.UnixEpoch));
    }

    private ITriggerType Trigger(string typeId) =>
        new VTubeStudioTriggers(_vts).GetTriggers().Single(t => t.TypeId == typeId);

    private static bool Matches(ITriggerType trigger, Cuebot.Core.Events.BotEvent e, params (string Key, string Value)[] settings) =>
        trigger.Matches(e, new TriggerDefinition(), ParameterValues.From(trigger.Parameters, settings.ToDictionary(s => s.Key, s => s.Value)),
            new Dictionary<string, object?>());

    private sealed class FakeDiscovery : IVtsDiscovery
    {
        public IReadOnlyList<VtsInstance> List { get; set; } = [];
        public IReadOnlyList<VtsInstance> Instances => List;
        public void Start() { }
        public void Dispose() { }
    }
}
