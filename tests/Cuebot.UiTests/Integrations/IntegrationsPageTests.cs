using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels.Integrations;
using Cuebot.App.Views.Integrations;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Integrations;
using Cuebot.Integrations.Common;
using Cuebot.Integrations.HeartRate;
using Cuebot.Integrations.HeartRate.HypeRate;
using Cuebot.Integrations.HeartRate.Pulsoid;
using Cuebot.Integrations.VTubeStudio;
using Cuebot.Integrations.Voicemod;
using Cuebot.UiTests.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.UiTests.Integrations;

/// <summary>
/// The Integrations page: secrets are masked and saved, options write through, and the live previews (VTube Studio model and
/// hotkeys with test buttons, Voicemod voice picker, heart rate BPM) render from scripted in-memory apps without binding
/// errors in both themes (screenshots tests/Cuebot.UiTests/screenshots/integrations-*.png).
/// </summary>
public sealed class IntegrationsPageTests : IAsyncDisposable
{
    private const string VoicemodKey = "VM-client-key-ABCDEFGH12345678";
    private const string PulsoidToken = "PULSOID-secret-access-token-WXYZ";
    private const string HypeRateKey = "HYPERATE-secret-api-key-ABCD9876";
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-integrations-").FullName;
    private readonly ServiceProvider _services;

    public IntegrationsPageTests()
    {
        var collection = new ServiceCollection().AddCuebotCore(new AppPaths(_dataDir)).AddIntegrations();
        collection.AddSingleton(sp => new VTubeStudioService(sp.GetRequiredService<JsonFile<IntegrationsSettings>>(), sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>(), () => new ScriptedSocket(AnswerVts), Fast));
        collection.AddSingleton(sp => new VoicemodService(sp.GetRequiredService<JsonFile<IntegrationsSettings>>(), sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>(), () => new ScriptedSocket(AnswerVoicemod), Fast));
        _services = collection.BuildServiceProvider();
    }

    private static readonly IntegrationsTimings Fast = IntegrationsTimings.Default with
    {
        ReconnectDelay = TimeSpan.FromMilliseconds(50),
        RequestTimeout = TimeSpan.FromSeconds(3),
    };

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private IntegrationsViewModel CreateViewModel() =>
        new(Get<VTubeStudioService>(), Get<VoicemodService>(), Get<PulsoidService>(), Get<HypeRateService>());

    private static Window Show(IntegrationsViewModel vm)
    {
        var window = new Window
        {
            Content = new IntegrationsView { DataContext = vm },
            Width = 1100,
            Height = 2100,
            Background = Application.Current!.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
        };
        window.Show();
        UiPump.Pump();
        return window;
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(10);
            UiPump.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Page_masks_secrets_saves_options_and_shows_live_previews_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            TestApp.BindingErrors.Clear();
            var vm = CreateViewModel();
            var window = Show(vm);
            Assert.Equal("— BPM", vm.Pulsoid.Bpm);
            Assert.True(vm.VTubeStudio.HasNoHotkeys);
            UiPump.Save(window, "integrations-empty");

            // Secrets: stored trimmed, shown only masked.
            vm.Voicemod.ClientKey = "  " + VoicemodKey + " ";
            vm.Pulsoid.AccessToken = PulsoidToken;
            vm.HypeRate.ApiKey = HypeRateKey;
            vm.HypeRate.SessionId = " internal-testing ";
            var settings = Get<JsonFile<IntegrationsSettings>>().Value;
            Assert.Equal(VoicemodKey, settings.Voicemod.ClientKey);
            Assert.Equal(PulsoidToken, settings.Pulsoid.AccessToken);
            Assert.Equal("internal-testing", settings.HypeRate.SessionId);
            Assert.Equal("Saved client key: ••••••••5678", vm.Voicemod.MaskedKey);
            Assert.Equal("Saved token: ••••••••WXYZ", vm.Pulsoid.MaskedToken);
            UiPump.Pump();
            var secretBoxes = window.GetVisualDescendants().OfType<TextBox>()
                .Where(t => t.Name is "VoicemodKey" or "PulsoidToken" or "HypeRateKey").ToArray();
            Assert.Equal(3, secretBoxes.Length);
            Assert.All(secretBoxes, b => Assert.Equal('•', b.PasswordChar));

            // Options write through (all auto-connect options default to off).
            Assert.False(vm.VTubeStudio.AutoConnect);
            vm.VTubeStudio.AutoConnect = true;
            vm.Pulsoid.UseLegacyV1 = true;
            vm.HypeRate.SubscribeClips = false;
            vm.VTubeStudio.Port = 8002;
            Assert.True(settings.VTubeStudio.AutoConnect);
            Assert.True(settings.Pulsoid.UseLegacyV1);
            Assert.False(settings.HypeRate.SubscribeClips);
            Assert.Equal(8002, settings.VTubeStudio.Port);
            vm.VTubeStudio.Port = 8001;
            vm.Pulsoid.UseLegacyV1 = false;

            // VTube Studio live preview: model, hotkeys with working test buttons, model picker.
            await vm.VTubeStudio.ConnectCommand.ExecuteAsync(null);
            await Until(() => vm.VTubeStudio.IsConnected && vm.VTubeStudio.Hotkeys.Count == 2 && vm.VTubeStudio.Models.Count == 2, "VTube Studio data");
            Assert.StartsWith("Akari", vm.VTubeStudio.CurrentModel);
            Assert.StartsWith("Authorized", vm.VTubeStudio.MaskedToken);
            Assert.DoesNotContain(ScriptedSocket.VtsToken, vm.VTubeStudio.MaskedToken);
            await vm.VTubeStudio.Hotkeys[0].TestCommand.ExecuteAsync(null);
            Assert.Equal("Triggered Wave.", vm.VTubeStudio.Info);

            // Voicemod: current voice and picker.
            await vm.Voicemod.ConnectCommand.ExecuteAsync(null);
            await Until(() => vm.Voicemod.IsConnected && vm.Voicemod.Voices.Count == 2 && vm.Voicemod.CurrentVoice == "Clean", "Voicemod data");
            vm.Voicemod.SelectedVoice = "Baby";
            await vm.Voicemod.ApplyVoiceCommand.ExecuteAsync(null);
            Assert.Equal("Selected Baby.", vm.Voicemod.Info);
            await Until(() => vm.Voicemod.CurrentVoice == "Baby", "voice change");
            Assert.Contains("Voice changer on", vm.Voicemod.Switches);

            // Heart rate preview.
            Get<HeartRateHub>().Report(HeartRateHub.PulsoidSource, 88);
            Get<HeartRateHub>().Report(HeartRateHub.HypeRateSource, 101);
            UiPump.Pump();
            Assert.Equal("88 BPM", vm.Pulsoid.Bpm);
            Assert.Equal("101 BPM", vm.HypeRate.Bpm);
            Assert.Equal("101", Get<Cuebot.Core.Actions.GlobalVariables>().Get("heartRate"));

            UiPump.Pump();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
            Assert.Contains(texts, t => t == "88 BPM");
            Assert.DoesNotContain(texts, t => t.Contains(VoicemodKey, StringComparison.Ordinal) || t.Contains(PulsoidToken, StringComparison.Ordinal));

            foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                UiPump.Pump();
                UiPump.Save(window, $"integrations-{name}");
            }

            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
            var log = string.Join("\n", Get<AppLog>().Recent().Select(e => e.Message));
            Assert.DoesNotContain(VoicemodKey, log);
            Assert.DoesNotContain(ScriptedSocket.VtsToken, log);
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    [AvaloniaFact]
    public async Task Connect_without_credentials_shows_an_error()
    {
        var vm = CreateViewModel();
        await vm.Pulsoid.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("access token", vm.Pulsoid.Error);
        Assert.Equal("Error", vm.Pulsoid.State);
        await vm.HypeRate.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("API key", vm.HypeRate.Error);
        await vm.Voicemod.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("client key", vm.Voicemod.Error);
    }

    // ---- Scripted apps ----

    private static IEnumerable<string> AnswerVts(JsonElement request)
    {
        var type = request.GetProperty("messageType").GetString()!;
        var id = request.GetProperty("requestID").GetString();
        object data = type switch
        {
            "APIStateRequest" => new { active = true, vTubeStudioVersion = "1.28.0" },
            "AuthenticationTokenRequest" => new { authenticationToken = ScriptedSocket.VtsToken },
            "AuthenticationRequest" => new { authenticated = true, reason = "ok" },
            "CurrentModelRequest" => new { modelLoaded = true, modelName = "Akari", modelID = "m1", modelPosition = new { positionX = 0.1, positionY = -0.3, rotation = 0, size = -45 } },
            "AvailableModelsRequest" => new { availableModels = new[] { new { modelName = "Akari", modelID = "m1", modelLoaded = true }, new { modelName = "Hiyori", modelID = "m2", modelLoaded = false } } },
            "HotkeysInCurrentModelRequest" => new
            {
                availableHotkeys = new[]
                {
                    new { name = "Wave", type = "TriggerAnimation", hotkeyID = "hk1", file = "", description = "" },
                    new { name = "Heart Eyes", type = "ToggleExpression", hotkeyID = "hk2", file = "", description = "" },
                },
            },
            "HotkeyTriggerRequest" => new { hotkeyID = "hk1" },
            _ => new { },
        };
        yield return JsonSerializer.Serialize(new { apiName = "VTubeStudioPublicAPI", apiVersion = "1.0", requestID = id, messageType = type.Replace("Request", "Response"), data });
    }

    private static IEnumerable<string> AnswerVoicemod(JsonElement request)
    {
        var action = request.GetProperty("action").GetString()!;
        var id = request.GetProperty("id").GetString();
        string Reply(string actionType, object actionObject) => JsonSerializer.Serialize(new { actionType, actionID = id, actionObject });
        switch (action)
        {
            case "registerClient":
                yield return JsonSerializer.Serialize(new { action, id, payload = new { status = new { code = 200, description = "Authorized" } } });
                break;
            case "getVoices":
                yield return Reply("getVoices", new { voices = new[] { new { id = "nofx", friendlyName = "Clean" }, new { id = "baby", friendlyName = "Baby" } }, currentVoice = "nofx" });
                break;
            case "getCurrentVoice":
                yield return Reply("getCurrentVoice", new { voiceID = "nofx" });
                break;
            case "getMemes":
                yield return Reply("getMemes", new { listOfMemes = Array.Empty<object>() });
                break;
            case "getVoiceChangerStatus":
                yield return Reply("toggleVoiceChanger", new { value = true });
                break;
            case "getHearMyselfStatus":
                yield return Reply("toggleHearMyVoice", new { value = false });
                break;
            case "getBackgroundEffectStatus":
                yield return Reply("toggleBackground", new { value = false });
                break;
            case "getMuteMicStatus":
                yield return Reply("toggleMuteMic", new { value = false });
                break;
            case "loadVoice":
                yield return JsonSerializer.Serialize(new { actionType = "voiceChangedEvent", actionID = (string?)null, actionObject = new { voiceID = request.GetProperty("payload").GetProperty("voiceID").GetString() } });
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { /* settings flush may still hold the file briefly */ }
    }
}

/// <summary>An in-memory websocket whose "server" answers each client frame with scripted frames.</summary>
internal sealed class ScriptedSocket(Func<JsonElement, IEnumerable<string>> answer) : IWebSocketTransport
{
    public const string VtsToken = "VTS-ui-test-plugin-token-1234567890";
    private readonly Channel<string?> _inbox = Channel.CreateUnbounded<string?>();

    public string? CloseReason => null;

    public Task ConnectAsync(Uri url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct) => Task.CompletedTask;

    public Task SendAsync(string text, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(text);
        foreach (var reply in answer(document.RootElement)) _inbox.Writer.TryWrite(reply);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct) => await _inbox.Reader.ReadAsync(ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
