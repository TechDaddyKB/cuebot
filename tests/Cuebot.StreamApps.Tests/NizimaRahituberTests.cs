using System.Text.Json;
using Cuebot.Core;
using Cuebot.StreamApps.Nizima;
using Cuebot.StreamApps.Tests.Support;

namespace Cuebot.StreamApps.Tests;

public sealed class NizimaServiceTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private NizimaService _nizima = null!;
    private FakeSocket _socket = null!;

    private static string Message(string type, string method, string? id, string data) =>
        $$"""{"nLPlugin":"1.0.0","Id":{{(id is null ? "null" : $"\"{id}\"")}},"Type":"{{type}}","Method":"{{method}}","Data":{{data}}}""";

    /// <summary>Reads the next request, checks its method, and answers it.</summary>
    private async Task<JsonElement> AnswerAsync(string method, string data = "{}", string type = "Response")
    {
        var sent = JsonDocument.Parse(await _socket.NextSentAsync()).RootElement;
        Assert.Equal("1.0.0", sent.GetProperty("nLPlugin").GetString());
        Assert.Equal("Request", sent.GetProperty("Type").GetString());
        Assert.Equal(method, sent.GetProperty("Method").GetString());
        _socket.Push(Message(type, method, sent.GetProperty("Id").GetString(), data));
        return sent.GetProperty("Data");
    }

    private Task Event(string method, string data)
    {
        _socket.Push(Message("Event", method, null, data));
        return Task.CompletedTask;
    }

    private async Task AnswerRefreshAsync()
    {
        await AnswerAsync("NotifyCurrentModelChanged");
        await AnswerAsync("NotifyModelsChanged");
        await AnswerAsync("NotifyItemsChanged");
        await AnswerAsync("GetModels", """{"Models":[{"Local":true,"Name":"Hiyori","ModelId":"1","SceneId":"0"},{"Local":true,"Name":"Mao","ModelId":"2","SceneId":"0"}]}""");
        await AnswerAsync("GetCurrentModelId", """{"SceneId":"0","ModelId":"1"}""");
        await AnswerAsync("GetRegisteredModels", """{"RegisteredModels":[{"Name":"Natori","ModelPath":"C:/m/natori.model3.json","IconPath":""}]}""");
        await AnswerAsync("GetRegisteredItems", """{"RegisteredItems":[{"Name":"Cat ears","ItemType":"Still","ItemPath":"C:/i/ears.png"}]}""");
        await AnswerAsync("GetItems", """{"Items":[{"Name":"Cat ears","ItemType":"Still","ItemId":"9","SceneId":"0"}]}""");
        await AnswerAsync("GetEffectGroups", """{"EffectGroups":[{"Name":"Sparkles","GroupId":"g1","SceneId":"0","Target":"","Enabled":false}]}""");
        await AnswerAsync("GetExpressions", """{"Expressions":[{"Name":"Smile","ExpressionPath":"exp/smile.exp3.json","Active":false},{"Name":"Angry","ExpressionPath":"exp/angry.exp3.json","Active":true}]}""");
        await AnswerAsync("GetMotions", """{"Motions":[{"Name":"Wave","MotionPath":"mtn/wave.motion3.json"}]}""");
        await Wait.UntilAsync(() => _nizima.StatusMessage.Contains("motions"), "lists");
    }

    public async Task InitializeAsync()
    {
        _nizima = new NizimaService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, TestTimings.Fast);
        await _nizima.ConnectAsync();
        _socket = await _env.Server.NextAsync();
        Assert.Equal("ws://localhost:22022/", _socket.Url!.ToString());
        // First time: register, keep the token, wait for the user to switch Cuebot on.
        var register = await AnswerAsync("RegisterPlugin", """{"Token":"tok-1"}""");
        Assert.Equal("Cuebot", register.GetProperty("Name").GetString());
        await Wait.UntilAsync(() => _nizima.StatusMessage.Contains("Plugin Manager"), "waiting");
        Assert.Equal("tok-1", _nizima.Settings.Token);
        Assert.False(_nizima.PluginEnabled);
        await Event("NotifyEnabledChanged", """{"Enabled":true}""");
        await AnswerRefreshAsync();
    }

    public async Task DisposeAsync()
    {
        await _nizima.DisposeAsync();
        _env.Dispose();
    }

    [Fact]
    public async Task Registers_once_then_reconnects_with_the_token()
    {
        Assert.Equal("Connected to nizima LIVE at localhost:22022: model Hiyori, 2 expressions, 1 motions", _nizima.StatusMessage);
        Assert.Equal(["Hiyori", "Mao"], _nizima.Models.Select(m => m.Name));
        Assert.Equal(["Cat ears"], _nizima.Items.Select(i => i.Name));

        await _nizima.DisconnectAsync();
        await _nizima.ConnectAsync();
        _socket = await _env.Server.NextAsync();
        var established = await AnswerAsync("EstablishConnection", """{"Enabled":true}""");
        Assert.Equal("tok-1", established.GetProperty("Token").GetString());
        await AnswerRefreshAsync();

        // A token nizima LIVE forgot (plug-in removed) means registering again.
        await _nizima.DisconnectAsync();
        await _nizima.ConnectAsync();
        _socket = await _env.Server.NextAsync();
        await AnswerAsync("EstablishConnection", """{"ErrorType":"InvalidToken"}""", type: "Error");
        await AnswerAsync("RegisterPlugin", """{"Token":"tok-2"}""");
        await Wait.UntilAsync(() => _nizima.Settings.Token == "tok-2", "new token");
    }

    [Fact]
    public async Task Expressions_motions_and_hotkeys_use_paths_and_the_selected_model()
    {
        var on = _nizima.SetExpressionAsync("", "smile", SwitchMode.On, only: true);
        await AnswerAsync("GetExpressions", """{"Expressions":[{"Name":"Smile","ExpressionPath":"exp/smile.exp3.json","Active":false}]}""");
        Assert.Equal("1", (await AnswerAsync("StopAllExpressions")).GetProperty("ModelId").GetString());
        var start = await AnswerAsync("StartExpression");
        Assert.Equal("exp/smile.exp3.json", start.GetProperty("ExpressionPath").GetString());
        Assert.True(await on);

        var toggle = _nizima.SetExpressionAsync("Hiyori", "Smile", SwitchMode.Toggle, only: false);
        await AnswerAsync("GetExpressions", """{"Expressions":[{"Name":"Smile","ExpressionPath":"exp/smile.exp3.json","Active":true}]}""");
        await AnswerAsync("StopExpression");
        Assert.False(await toggle);

        var motion = _nizima.StartMotionAsync("", "WAVE");
        Assert.Equal("mtn/wave.motion3.json", (await AnswerAsync("StartMotion")).GetProperty("MotionPath").GetString());
        Assert.Equal("Wave", await motion);

        var hotkey = _nizima.TriggerHotkeyAsync("Mao", "Ctrl+A");
        var sent = await AnswerAsync("TriggerModelHotkey");
        Assert.Equal("2", sent.GetProperty("ModelId").GetString());
        Assert.Equal("Ctrl+A", sent.GetProperty("Key").GetString());
        await hotkey;

        await Assert.ThrowsAsync<StreamAppRequestException>(() => _nizima.StartMotionAsync("", "Dance"));
        await Assert.ThrowsAsync<StreamAppRequestException>(() => _nizima.TriggerHotkeyAsync("Nobody", "F1"));
    }

    [Fact]
    public async Task Items_models_and_effects_go_by_their_names()
    {
        var add = _nizima.AddItemAsync("", "cat ears");
        var added = await AnswerAsync("AddItem", """{"ItemId":"10"}""");
        Assert.Equal("C:/i/ears.png", added.GetProperty("ItemPath").GetString());
        Assert.Equal("0", added.GetProperty("SceneId").GetString());
        Assert.Equal("10", await add);

        var remove = _nizima.RemoveItemsAsync("Cat ears");
        await AnswerAsync("GetItems", """{"Items":[{"Name":"Cat ears","ItemId":"9","SceneId":"0"},{"Name":"Cat ears","ItemId":"10","SceneId":"0"}]}""");
        Assert.Equal("9", (await AnswerAsync("RemoveItem")).GetProperty("ItemId").GetString());
        Assert.Equal("10", (await AnswerAsync("RemoveItem")).GetProperty("ItemId").GetString());
        Assert.Equal(2, await remove);

        var change = _nizima.ChangeModelAsync("", "Natori");
        Assert.Equal("C:/m/natori.model3.json", (await AnswerAsync("ChangeModel", """{"ModelId":"1"}""")).GetProperty("ModelPath").GetString());
        await change;

        var effect = _nizima.SetEffectAsync("sparkles", SwitchMode.Toggle);
        await AnswerAsync("GetEffectGroups", """{"EffectGroups":[{"Name":"Sparkles","GroupId":"g1","Enabled":false}]}""");
        Assert.Equal("g1", (await AnswerAsync("EnableEffectGroup")).GetProperty("GroupId").GetString());
        Assert.True(await effect);

        var refused = _nizima.StopMotionAsync("");
        await AnswerAsync("StopMotion", """{"ErrorType":"InvalidModelId"}""", type: "Error");
        Assert.Contains("InvalidModelId", (await Assert.ThrowsAsync<StreamAppRequestException>(() => refused)).Message);
    }

    [Fact]
    public async Task Model_changes_are_triggers_and_switching_the_plugin_off_blocks_steps()
    {
        await Event("NotifyCurrentModelChanged", """{"ModelId":"2"}""");
        var changed = await _env.WaitForEventAsync(NizimaEvents.ModelChanged);
        Assert.Equal("Mao", changed.Args["model"]);
        Assert.Equal("Hiyori", changed.Args["previousModel"]);
        await AnswerAsync("GetExpressions", """{"Expressions":[]}""");
        await AnswerAsync("GetMotions", """{"Motions":[]}""");
        await Wait.UntilAsync(() => _nizima.Expressions.Count == 0, "re-read");

        await Event("NotifyEnabledChanged", """{"Enabled":false}""");
        await Wait.UntilAsync(() => !_nizima.PluginEnabled, "disabled");
        var ex = await Assert.ThrowsAsync<StreamAppRequestException>(() => _nizima.TriggerHotkeyAsync("", "F1"));
        Assert.Contains("Plugin Manager", ex.Message);
    }
}
