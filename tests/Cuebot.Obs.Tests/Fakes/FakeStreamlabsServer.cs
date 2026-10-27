using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Cuebot.Obs.Tests.Fakes;

/// <summary>
/// In-process Streamlabs Desktop Remote Control API: SockJS websocket transport at /api/{server}/{session}/websocket,
/// JSON-RPC 2.0 with token auth and a small scene/source/streaming model.
/// </summary>
internal sealed class FakeStreamlabsServer : IAsyncDisposable
{
    public const string Token = "slobs-token";

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Task _acceptLoop;
    private TaskCompletionSource<WebSocket> _session = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeStreamlabsServer()
    {
        Port = GetFreePort();
        _listener.Prefixes.Add($"http://localhost:{Port}/api/");
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }
    public ConcurrentQueue<(string Resource, string Method, JsonElement Args)> Calls { get; } = new();
    public string? LastPath { get; private set; }

    public string ActiveScene { get; set; } = "s1";
    public string StreamingStatus { get; set; } = "offline";
    public string RecordingStatus { get; set; } = "offline";
    public string ReplayBufferStatus { get; set; } = "offline";
    public Dictionary<string, bool> Visible { get; } = new() { ["i1"] = true, ["i2"] = false };
    public Dictionary<string, bool> Muted { get; } = new() { ["src-mic"] = false };
    public Dictionary<string, JsonElement> SourceSettings { get; } = new();
    public List<string> FilterCalls { get; } = [];
    public List<string> Transforms { get; } = [];

    public Task<WebSocket> WaitForSessionAsync() => _session.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public async Task SendEventAsync(string resourceId, object? data)
    {
        var session = await WaitForSessionAsync();
        var message = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = (int?)null, result = new { _type = "EVENT", emitter = "STREAM", resourceId, data } });
        await SendAsync(session, "a" + JsonSerializer.Serialize(new[] { message }));
    }

    public async Task CloseSessionAsync()
    {
        var session = await WaitForSessionAsync();
        _session = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendAsync(session, "c[3000,\"Go away!\"]");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Close();
        try { await _acceptLoop; } catch (Exception) { /* closed */ }
        _stop.Dispose();
    }

    private object Scenes() => new object[]
    {
        new
        {
            id = "s1", name = "Main", resourceId = "Scene[\"s1\"]",
            nodes = new object[]
            {
                new { id = "i1", sceneItemId = "i1", sourceId = "src-cam", name = "Cam", visible = Visible["i1"], sceneNodeType = "item", resourceId = "SceneItem[\"s1\",\"i1\",\"src-cam\"]" },
                new { id = "i2", sceneItemId = "i2", sourceId = "src-text", name = "Title", visible = Visible["i2"], sceneNodeType = "item", resourceId = "SceneItem[\"s1\",\"i2\",\"src-text\"]" },
            },
        },
        new { id = "s2", name = "BRB", resourceId = "Scene[\"s2\"]", nodes = Array.Empty<object>() },
    };

    private object? Answer(string resource, string method, JsonElement args, out string? error)
    {
        error = null;
        Calls.Enqueue((resource, method, args));
        switch (resource, method)
        {
            case ("TcpServerService", "auth"):
                if (args[0].GetString() == Token) return true;
                error = "INTERNAL_JSON_RPC_ERROR Invalid token";
                return null;
            case ("ScenesService", "getScenes"): return Scenes();
            case ("ScenesService", "activeSceneId"): return ActiveScene;
            case ("ScenesService", "activeScene"): return new { id = ActiveScene, name = ActiveScene == "s1" ? "Main" : "BRB" };
            case ("ScenesService", "makeSceneActive"):
                ActiveScene = args[0].GetString()!;
                return true;
            case ("SourcesService", "getSources"):
                return new object[]
                {
                    new { sourceId = "src-cam", id = "src-cam", name = "Cam", type = "dshow_input", resourceId = "Source[\"src-cam\"]", muted = false },
                    new { sourceId = "src-text", id = "src-text", name = "Title", type = "text_gdiplus", resourceId = "Source[\"src-text\"]", muted = false },
                    new { sourceId = "src-mic", id = "src-mic", name = "Mic", type = "wasapi_input_capture", resourceId = "Source[\"src-mic\"]", muted = Muted["src-mic"] },
                };
            case ("StreamingService", "getModel"):
                return new { streamingStatus = StreamingStatus, recordingStatus = RecordingStatus, replayBufferStatus = ReplayBufferStatus };
            case ("StreamingService", "toggleStreaming"):
                StreamingStatus = StreamingStatus == "offline" ? "live" : "offline";
                return null;
            case ("StreamingService", "toggleRecording"):
                RecordingStatus = RecordingStatus == "offline" ? "recording" : "offline";
                return null;
            case ("StreamingService", "startReplayBuffer"): ReplayBufferStatus = "running"; return null;
            case ("StreamingService", "stopReplayBuffer"): ReplayBufferStatus = "offline"; return null;
            case ("StreamingService", "saveReplay"): return null;
            case ("SourceFiltersService", "setVisibility"):
                FilterCalls.Add($"{args[0].GetString()}|{args[1].GetString()}|{args[2].GetBoolean()}");
                return null;
            case (_, "setVisibility") when resource.StartsWith("SceneItem[", StringComparison.Ordinal):
                Visible[resource.Split('"')[3]] = args[0].GetBoolean();
                return null;
            case (_, "getModel") when resource.StartsWith("SceneItem[", StringComparison.Ordinal):
                return new { transform = new { scale = new { x = 1.0, y = 2.0 }, rotation = 0.0 } };
            case (_, "setTransform") when resource.StartsWith("SceneItem[", StringComparison.Ordinal):
                Transforms.Add(args[0].GetRawText());
                return null;
            case (_, "setMuted") when resource.StartsWith("AudioSource[", StringComparison.Ordinal):
                Muted[resource.Split('"')[1]] = args[0].GetBoolean();
                return null;
            case (_, "updateSettings") when resource.StartsWith("Source[", StringComparison.Ordinal):
                SourceSettings[resource.Split('"')[1]] = args[0].Clone();
                return null;
            case (_, _) when method.EndsWith("Change", StringComparison.Ordinal) || method.StartsWith("scene", StringComparison.Ordinal) ||
                             method.StartsWith("item", StringComparison.Ordinal) || method.StartsWith("source", StringComparison.Ordinal) ||
                             method == "replayBufferFileWrite":
                return new { _type = "SUBSCRIPTION", resourceId = $"{resource}.{method}", emitter = "STREAM" };
            default:
                error = $"Unknown {resource}.{method}";
                return null;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }
            LastPath = context.Request.Url!.AbsolutePath;
            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            _ = Task.Run(() => RunSessionAsync(socket));
        }
    }

    private async Task RunSessionAsync(WebSocket socket)
    {
        try
        {
            await SendAsync(socket, "o");
            _session.TrySetResult(socket);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (socket.State == WebSocketState.CloseReceived)
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Bye", CancellationToken.None);
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                foreach (var text in JsonSerializer.Deserialize<string[]>(message.ToArray())!)
                {
                    using var request = JsonDocument.Parse(text);
                    var root = request.RootElement;
                    var id = root.GetProperty("id").GetInt32();
                    var p = root.GetProperty("params");
                    var value = Answer(p.GetProperty("resource").GetString()!, root.GetProperty("method").GetString()!,
                        p.TryGetProperty("args", out var a) ? a.Clone() : JsonDocument.Parse("[]").RootElement, out var error);
                    var reply = error is null
                        ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = value })
                        : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32603, message = error } });
                    await SendAsync(socket, "a" + JsonSerializer.Serialize(new[] { reply }));
                }
            }
        }
        catch (Exception)
        {
            // Client went away.
        }
    }

    private async Task SendAsync(WebSocket socket, string text)
    {
        await _sendLock.WaitAsync();
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _sendLock.Release(); }
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
