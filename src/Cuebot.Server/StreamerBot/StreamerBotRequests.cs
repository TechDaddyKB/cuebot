using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Scripting;
using Cuebot.Core.Variables;
using Cuebot.Scripting.Compat;

namespace Cuebot.Server.StreamerBot;

/// <summary>A request failed; becomes <c>{status: "error", error: message}</c>.</summary>
public sealed class RequestException(string message) : Exception(message);

/// <summary>
/// Streamer.bot WebSocket/HTTP requests answered from Cuebot's state. Shared by the WebSocket server, the HTTP API
/// and (for DoAction) the UDP server. Each method returns the response body without <c>status</c>/<c>id</c>.
/// </summary>
public sealed class StreamerBotRequests(ServerServices services)
{
    /// <summary>The requests answered here (Subscribe, UnSubscribe and Authenticate belong to the WebSocket session).</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "GetInfo", "GetEvents", "GetActions", "DoAction", "GetBroadcaster", "GetActiveViewers", "GetCodeTriggers",
        "ExecuteCodeTrigger", "GetCommands", "GetGlobals", "GetGlobal", "GetUserGlobals", "GetUserGlobal",
        "TwitchGetUserGlobals", "TwitchGetUserGlobal", "YouTubeGetUserGlobals", "YouTubeGetUserGlobal",
        "KickGetUserGlobals", "KickGetUserGlobal", "GetCredits", "TestCredits", "ClearCredits", "SendMessage",
        "TwitchGetEmotes", "YouTubeGetEmotes", "GetMonitoredYouTubeBroadcasts", "GetUserPronouns", "ClearFirstWordsCache",
    ];

    /// <summary>Requests that need authentication even when it is not enforced for everything.</summary>
    public static readonly IReadOnlySet<string> Privileged =
        new HashSet<string>(["SendMessage", "DoAction", "ExecuteCodeTrigger", "ClearCredits", "ClearFirstWordsCache"],
            StringComparer.OrdinalIgnoreCase);

    public static readonly string InstanceId = Guid.NewGuid().ToString();

    public static bool IsKnown(string request) => Names.Contains(request, StringComparer.OrdinalIgnoreCase);

    public async Task<JsonObject> HandleAsync(string request, JsonElement message, CancellationToken ct = default)
    {
        switch (request.ToLowerInvariant())
        {
            case "getinfo": return new JsonObject { ["info"] = Info() };
            case "getevents": return new JsonObject { ["events"] = Catalog() };
            case "getactions": return GetActions();
            case "doaction": return DoAction(message);
            case "getbroadcaster": return GetBroadcaster();
            case "getactiveviewers": return GetActiveViewers();
            case "getcodetriggers": return GetCodeTriggers();
            case "executecodetrigger": return ExecuteCodeTrigger(message);
            case "getcommands": return GetCommands();
            case "getglobals": return GetGlobals(message);
            case "getglobal": return GetGlobal(message);
            case "getuserglobals": return GetUserGlobals(Str(message, "platform", "twitch"), message);
            case "getuserglobal": return GetUserGlobal(Str(message, "platform", "twitch"), message);
            case "twitchgetuserglobals": return GetUserGlobals("twitch", message);
            case "twitchgetuserglobal": return GetUserGlobal("twitch", message);
            case "youtubegetuserglobals": return GetUserGlobals("youtube", message);
            case "youtubegetuserglobal": return GetUserGlobal("youtube", message);
            case "kickgetuserglobals": return GetUserGlobals("kick", message);
            case "kickgetuserglobal": return GetUserGlobal("kick", message);
            case "getcredits":
            case "testcredits": return new JsonObject { ["credits"] = new JsonObject() };   // Cuebot has no credits roll yet
            case "clearcredits": return [];
            case "sendmessage": return await SendMessageAsync(message, ct).ConfigureAwait(false);
            case "twitchgetemotes":
            case "youtubegetemotes":
                return new JsonObject { ["emotes"] = new JsonObject { ["platform"] = new JsonArray(), ["bttv"] = new JsonArray(), ["ffz"] = new JsonArray(), ["sevenTv"] = new JsonArray() } };
            case "getmonitoredyoutubebroadcasts": return new JsonObject { ["broadcasts"] = new JsonArray() };
            case "getuserpronouns":
                return new JsonObject
                {
                    ["pronoun"] = new JsonObject { ["userLogin"] = Str(message, "userLogin"), ["userFound"] = false },
                };
            case "clearfirstwordscache":
                services.Users?.ResetFirstWords();
                return [];
            default: throw new RequestException($"Unknown request '{request}'");
        }
    }

    public static JsonObject Info() => new()
    {
        ["instanceId"] = InstanceId,
        ["name"] = "Cuebot",
        ["os"] = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other",
        ["osVersion"] = RuntimeInformation.OSDescription,
        ["version"] = "1.0.0",
        ["cuebotVersion"] = typeof(StreamerBotRequests).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        ["source"] = "cuebot",
    };

    public static JsonObject Catalog()
    {
        var events = new JsonObject();
        foreach (var (source, types) in StreamerBotEventMap.Catalog.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            events[source] = new JsonArray(types.Select(t => (JsonNode)t).ToArray());
        }
        return events;
    }

    private JsonObject GetActions()
    {
        var actions = services.Profile.Snapshot.Actions;
        return new JsonObject
        {
            ["actions"] = new JsonArray(actions.Select(a => (JsonNode)new JsonObject
            {
                ["id"] = a.Id.ToString(),
                ["name"] = a.Name,
                ["group"] = a.Group,
                ["enabled"] = a.Enabled,
                ["subaction_count"] = a.SubActions.Count,
            }).ToArray()),
            ["count"] = actions.Count,
        };
    }

    /// <summary>Queues an action by id or name with the given args (the response does not wait for it).</summary>
    public JsonObject DoAction(JsonElement message, string source = "WebSocket")
    {
        var action = FindAction(message.ValueKind == JsonValueKind.Object && message.TryGetProperty("action", out var a) ? a : default)
                     ?? throw new RequestException("Action not found");
        if (!action.Enabled) throw new RequestException($"Action '{action.Name}' is disabled");
        var args = ToArgs(message.ValueKind == JsonValueKind.Object && message.TryGetProperty("args", out var raw) ? raw : default);
        _ = RunAsync(action, args, source);
        return [];
    }

    private async Task RunAsync(ActionDefinition action, Dictionary<string, object?> args, string source)
    {
        try
        {
            await services.Engine.RunAsync(action, args, CancellationToken.None, source).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            services.Log.Error("Servers", $"{source} DoAction '{action.Name}' failed", ex);
        }
    }

    public ActionDefinition? FindAction(JsonElement reference)
    {
        var snapshot = services.Profile.Snapshot;
        if (reference.ValueKind == JsonValueKind.String) return Find(reference.GetString());
        if (reference.ValueKind != JsonValueKind.Object) return null;
        if (reference.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            && Guid.TryParse(id.GetString(), out var guid) && snapshot.FindAction(guid) is { } byId)
        {
            return byId;
        }
        return reference.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? Find(name.GetString()) : null;

        ActionDefinition? Find(string? text) =>
            string.IsNullOrWhiteSpace(text) ? null
            : Guid.TryParse(text, out var g) ? snapshot.FindAction(g) ?? snapshot.FindAction(text) : snapshot.FindAction(text);
    }

    public static Dictionary<string, object?> ToArgs(JsonElement args)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (args.ValueKind != JsonValueKind.Object) return result;
        foreach (var p in args.EnumerateObject()) result[p.Name] = ScriptArgs.ToClr(p.Value);
        return result;
    }

    private JsonObject GetBroadcaster()
    {
        var platforms = new JsonObject();
        var connected = new JsonArray();
        var disconnected = new JsonArray();
        foreach (var status in services.Connections)
        {
            var key = status.Name.ToLowerInvariant();
            platforms[key] = new JsonObject { ["name"] = status.Name, ["state"] = status.State.ToString(), ["status"] = status.StatusMessage };
            (status.State == ConnectionState.Connected ? connected : disconnected).Add(key);
        }
        return new JsonObject { ["platforms"] = platforms, ["connected"] = connected, ["disconnected"] = disconnected };
    }

    private JsonObject GetActiveViewers()
    {
        var viewers = services.Users?.PresentUsers() ?? [];
        return new JsonObject
        {
            ["viewers"] = new JsonArray(viewers.Select(u => (JsonNode)new JsonObject
            {
                ["id"] = u.UserId,
                ["login"] = u.UserName,
                ["display"] = string.IsNullOrEmpty(u.DisplayName) ? u.UserName : u.DisplayName,
                ["platform"] = u.Platform.ToLowerInvariant(),
                ["role"] = Role(u.Roles),
                ["subscribed"] = u.Roles.HasFlag(ChatRoles.Subscriber),
                ["previousActive"] = u.LastSeen.ToString("o"),
                ["channelPointsUsed"] = 0,
                ["exempt"] = false,
                ["groups"] = new JsonArray(),
            }).ToArray()),
            ["count"] = viewers.Count,
        };
    }

    private static string Role(ChatRoles roles) =>
        roles.HasFlag(ChatRoles.Broadcaster) ? "Broadcaster"
        : roles.HasFlag(ChatRoles.Moderator) ? "Moderator"
        : roles.HasFlag(ChatRoles.Vip) ? "VIP"
        : "Viewer";

    private JsonObject GetCodeTriggers()
    {
        var triggers = services.CodeTriggers.All;
        return new JsonObject
        {
            ["triggers"] = new JsonArray(triggers.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name,
                ["eventName"] = t.EventName,
                ["category"] = string.Join("/", t.Categories),
            }).ToArray()),
            ["count"] = triggers.Count,
        };
    }

    private JsonObject ExecuteCodeTrigger(JsonElement message)
    {
        var name = Str(message, "triggerName");
        if (name.Length == 0) throw new RequestException("triggerName is required");
        var args = ToArgs(message.ValueKind == JsonValueKind.Object && message.TryGetProperty("args", out var raw) ? raw : default);
        services.Bus.Publish(CodeTriggerRegistry.CreateEvent(name, args));
        return [];
    }

    private JsonObject GetCommands()
    {
        var commands = services.Commands?.All() ?? [];
        return new JsonObject
        {
            ["commands"] = new JsonArray(commands.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id.ToString(),
                ["name"] = c.Name,
                ["enabled"] = c.Enabled,
                ["group"] = c.Group,
                ["mode"] = c.Mode.ToString(),
                ["location"] = c.Location.ToString(),
                ["commands"] = new JsonArray(c.Commands.Select(x => (JsonNode)x).ToArray()),
                ["caseSensitive"] = c.CaseSensitive,
                ["globalCooldown"] = c.GlobalCooldownSeconds,
                ["userCooldown"] = c.UserCooldownSeconds,
                ["sources"] = new JsonArray(c.Sources.Select(x => (JsonNode)x).ToArray()),
            }).ToArray()),
            ["count"] = commands.Count,
        };
    }

    private JsonObject GetGlobals(JsonElement message)
    {
        var persisted = Bool(message, "persisted", true);
        var variables = new JsonObject();
        var list = services.Globals.Snapshot(persisted);
        foreach (var v in list) variables[v.Name] = Variable(v.Name, v.Value, v.LastWrite);
        return new JsonObject { ["variables"] = variables, ["count"] = list.Count };
    }

    private JsonObject GetGlobal(JsonElement message)
    {
        var name = Str(message, "variable");
        if (name.Length == 0) throw new RequestException("variable is required");
        var persisted = Bool(message, "persisted", true);
        var found = services.Globals.Snapshot(persisted).FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new RequestException($"Variable '{name}' not found");
        return new JsonObject { ["variable"] = Variable(found.Name, found.Value, found.LastWrite) };
    }

    private JsonObject GetUserGlobals(string platform, JsonElement message)
    {
        var name = Str(message, "variable");
        var persisted = Bool(message, "persisted", true);
        var list = (services.UserVariables?.Snapshot(platform, persisted: persisted) ?? [])
            .Where(v => name.Length == 0 || string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return new JsonObject { ["variables"] = UserVariables(list), ["count"] = list.Length };
    }

    private JsonObject GetUserGlobal(string platform, JsonElement message)
    {
        var userId = Str(message, "userId");
        if (userId.Length == 0) throw new RequestException("userId is required");
        var name = Str(message, "variable");
        var persisted = Bool(message, "persisted", true);
        var list = (services.UserVariables?.Snapshot(platform, userId, persisted) ?? [])
            .Where(v => name.Length == 0 || string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return new JsonObject { ["variables"] = UserVariables(list), ["count"] = list.Length };
    }

    private static JsonArray UserVariables(IEnumerable<UserVariableInfo> list) =>
        new(list.Select(v => (JsonNode)new JsonObject
        {
            ["userId"] = v.UserId,
            ["userLogin"] = v.UserName.ToLowerInvariant(),
            ["userName"] = v.UserName,
            ["platform"] = v.Platform,
            ["name"] = v.Name,
            ["value"] = JsonSerializer.SerializeToNode(v.Value),
            ["lastWrite"] = v.LastWrite.ToString("o"),
        }).ToArray());

    private static JsonObject Variable(string name, object? value, DateTimeOffset lastWrite) => new()
    {
        ["name"] = name,
        ["value"] = JsonSerializer.SerializeToNode(value),
        ["lastWrite"] = lastWrite.ToString("o"),
    };

    private async Task<JsonObject> SendMessageAsync(JsonElement message, CancellationToken ct)
    {
        var text = Str(message, "message");
        if (text.Trim().Length == 0) throw new RequestException("message is required");
        var chat = services.Chat ?? throw new RequestException("Chat is not available");
        var platform = Str(message, "platform", "twitch");
        try
        {
            var sent = await chat.SendAsync(platform, text, ct).ConfigureAwait(false);
            return new JsonObject { ["platforms"] = new JsonArray(sent.Select(p => (JsonNode)p).ToArray()) };
        }
        catch (InvalidOperationException ex)
        {
            throw new RequestException(ex.Message);
        }
    }

    internal static string Str(JsonElement message, string name, string fallback = "") =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : v.ValueKind is JsonValueKind.Null ? fallback : v.ToString()
            : fallback;

    internal static bool Bool(JsonElement message, string name, bool fallback) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : fallback,
                _ => fallback,
            }
            : fallback;
}
