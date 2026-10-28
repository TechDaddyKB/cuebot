using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.Tests.Support;

/// <summary>A scripted VTube Studio: answers every request on a socket like the real public API.</summary>
internal sealed class FakeVts
{
    public const string IssuedToken = "VTS-issued-plugin-token-0123456789abcdef";

    private readonly FakeSocket _socket;
    private readonly Task _loop;

    /// <summary>Starts answering requests; <paramref name="setup"/> runs first, so the very first request already sees it.</summary>
    public FakeVts(FakeSocket socket, Action<FakeVts>? setup = null)
    {
        _socket = socket;
        setup?.Invoke(this);
        _loop = Task.Run(LoopAsync);
    }

    public ConcurrentQueue<(string Type, JsonElement Data)> Requests { get; } = new();

    /// <summary>Tokens VTube Studio accepts in AuthenticationRequest.</summary>
    public HashSet<string> ValidTokens { get; } = [IssuedToken];

    /// <summary>When true the plugin popup is denied (APIError 50).</summary>
    public bool DenyToken { get; set; }

    /// <summary>Optional override: return (messageType, data) or null for the default answer.</summary>
    public Func<string, JsonElement, (string Type, object Data)?>? Override { get; set; }

    public (string Type, JsonElement Data)[] Of(string type) => Requests.Where(r => r.Type == type).ToArray();

    public void PushEvent(string eventName, object data) => _socket.Push(JsonUtil.Serialize(new
    {
        apiName = "VTubeStudioPublicAPI",
        apiVersion = "1.0",
        timestamp = 1,
        requestID = "event-" + Guid.NewGuid().ToString("N"),
        messageType = eventName,
        data,
    }));

    private async Task LoopAsync()
    {
        while (true)
        {
            JsonElement request;
            try
            {
                request = await _socket.NextJsonAsync();
            }
            catch (OperationCanceledException)
            {
                return; // idle long enough: test is over
            }
            var type = request.GetProperty("messageType").GetString()!;
            var data = request.TryGetProperty("data", out var d) ? d.Clone() : default;
            Requests.Enqueue((type, data));
            var (responseType, responseData) = Override?.Invoke(type, data) ?? Default(type, data);
            _socket.Push(JsonUtil.Serialize(new
            {
                apiName = "VTubeStudioPublicAPI",
                apiVersion = "1.0",
                timestamp = 1,
                requestID = request.GetProperty("requestID").GetString(),
                messageType = responseType,
                data = responseData,
            }));
        }
    }

    public static (string, object) Error(int id, string message) => ("APIError", new { errorID = id, message });

    private (string, object) Default(string type, JsonElement data) => type switch
    {
        "APIStateRequest" => ("APIStateResponse", new { active = true, vTubeStudioVersion = "1.28.0", currentSessionAuthenticated = false }),
        "AuthenticationTokenRequest" => DenyToken
            ? Error(50, "User has denied API access for your plugin.")
            : ("AuthenticationTokenResponse", new { authenticationToken = IssuedToken }),
        "AuthenticationRequest" => ("AuthenticationResponse", ValidTokens.Contains(data.GetProperty("authenticationToken").GetString()!)
            ? new { authenticated = true, reason = "Token valid. The plugin is authenticated for the duration of this session." }
            : new { authenticated = false, reason = "Token invalid." }),
        "EventSubscriptionRequest" => ("EventSubscriptionResponse", new { subscribedEventCount = 1, subscribedEvents = new[] { data.GetProperty("eventName").GetString() } }),
        "CurrentModelRequest" => ("CurrentModelResponse", new
        {
            modelLoaded = true, modelName = "Akari", modelID = "model-akari", vtsModelName = "Akari.vtube.json",
            modelPosition = new { positionX = 0.1, positionY = -0.2, rotation = 5.0, size = -40.0 },
        }),
        "AvailableModelsRequest" => ("AvailableModelsResponse", new
        {
            numberOfModels = 2,
            availableModels = new[]
            {
                new { modelLoaded = true, modelName = "Akari", modelID = "model-akari" },
                new { modelLoaded = false, modelName = "Hiyori", modelID = "model-hiyori" },
            },
        }),
        "HotkeysInCurrentModelRequest" => ("HotkeysInCurrentModelResponse", new
        {
            modelLoaded = true, modelName = "Akari", modelID = "model-akari",
            availableHotkeys = new[]
            {
                new { name = "Wave", type = "TriggerAnimation", description = "Play animation", file = "wave.motion3.json", hotkeyID = "hk-wave" },
                new { name = "Heart Eyes", type = "ToggleExpression", description = "Toggle expression", file = "heart.exp3.json", hotkeyID = "hk-heart" },
            },
        }),
        "ExpressionStateRequest" => ("ExpressionStateResponse", new
        {
            modelLoaded = true, modelName = "Akari", modelID = "model-akari",
            expressions = new[]
            {
                new { name = "Heart", file = "heart.exp3.json", active = true },
                new { name = "Angry", file = "angry.exp3.json", active = false },
            },
        }),
        "InputParameterListRequest" => ("InputParameterListResponse", new
        {
            defaultParameters = new[] { new { name = "FaceAngleX", value = 0.0, min = -30.0, max = 30.0, defaultValue = 0.0 } },
            customParameters = new[] { new { name = "MyParam", value = 0.0, min = 0.0, max = 1.0, defaultValue = 0.0 } },
        }),
        "ItemListRequest" => ("ItemListResponse", new
        {
            itemsInSceneCount = 1,
            itemInstancesInScene = new[] { new { fileName = "hat.png", instanceID = "item-1", order = 5, type = "PNG" } },
            availableItemFiles = new[] { new { fileName = "hat.png", type = "PNG", loadedCount = 1 }, new { fileName = "cat.gif", type = "AnimatedGIF", loadedCount = 0 } },
        }),
        "HotkeyTriggerRequest" => ("HotkeyTriggerResponse", new { hotkeyID = data.GetProperty("hotkeyID").GetString() }),
        "ModelLoadRequest" => ("ModelLoadResponse", new { modelID = data.GetProperty("modelID").GetString() }),
        "ItemLoadRequest" => ("ItemLoadResponse", new { instanceID = "item-new", fileName = data.GetProperty("fileName").GetString() }),
        "ItemUnloadRequest" => ("ItemUnloadResponse", new { unloadedItems = new[] { new { instanceID = "item-1", fileName = "hat.png" } } }),
        "ItemMoveRequest" => ("ItemMoveResponse", new { movedItems = new[] { new { itemInstanceID = "item-1", success = true, errorID = -1 } } }),
        "ColorTintRequest" => ("ColorTintResponse", new { matchedArtMeshes = 42 }),
        _ => (type.Replace("Request", "Response", StringComparison.Ordinal), new { }),
    };
}
