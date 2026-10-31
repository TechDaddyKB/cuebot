using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Credits;
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
        "KickGetUserGlobals", "KickGetUserGlobal", "TrovoGetUserGlobals", "TrovoGetUserGlobal", "GetCredits", "TestCredits",
        "ClearCredits", "SendMessage",
        "TwitchGetEmotes", "YouTubeGetEmotes", "GetMonitoredYouTubeBroadcasts", "GetUserPronouns", "ClearFirstWordsCache",
    ];

    /// <summary>Requests that need authentication even when it is not enforced for everything.</summary>
    public static readonly IReadOnlySet<string> Privileged =
        new HashSet<string>(["SendMessage", "DoAction", "ExecuteCodeTrigger", "ClearCredits", "ClearFirstWordsCache"],
            StringComparer.OrdinalIgnoreCase);

    public static readonly string InstanceId = Guid.NewGuid().ToString();

    public static bool IsKnown(string request) => Names.Contains(request, StringComparer.OrdinalIgnoreCase);

    /// <summary>The services the event payloads read (cached pronouns, loaded emote sets).</summary>
    public StreamerBotPayloadServices PayloadServices => _payloadServices ??= new StreamerBotPayloadServices(services.Pronouns, services.Emotes);

    private StreamerBotPayloadServices? _payloadServices;

    public async Task<JsonObject> HandleAsync(string request, JsonElement message, CancellationToken ct = default)
    {
        switch (request.ToLowerInvariant())
        {
            case "getinfo": return new JsonObject { ["info"] = Info() };
            case "getevents": return new JsonObject { ["events"] = Catalog(services.Registry) };
            case "getactions": return GetActions();
            case "doaction": return DoAction(message);
            case "getbroadcaster": return await GetBroadcasterAsync(ct).ConfigureAwait(false);
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
            case "trovogetuserglobals": return GetUserGlobals("trovo", message);
            case "trovogetuserglobal": return GetUserGlobal("trovo", message);
            case "getcredits": return CreditsResponse(services.Credits?.Snapshot());
            case "testcredits": return CreditsResponse(services.Credits?.Test());
            case "clearcredits": services.Credits?.Reset(); return [];
            case "sendmessage": return await SendMessageAsync(message, ct).ConfigureAwait(false);
            case "twitchgetemotes": return GetEmotes(EmoteProviders.Twitch);
            case "youtubegetemotes": return GetEmotes(EmoteProviders.YouTube);
            case "getmonitoredyoutubebroadcasts": return await GetMonitoredBroadcastsAsync(ct).ConfigureAwait(false);
            case "getuserpronouns": return await GetUserPronounsAsync(message, ct).ConfigureAwait(false);
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
        ["cuebotVersion"] = Cuebot.Core.AppVersion.Current,
        ["mode"] = "ui",
        ["darkMode"] = true,
        ["source"] = "websocketServer",
        ["app"] = "cuebot",
    };

    /// <summary>
    /// GetEvents: every Streamer.bot name the loaded modules' triggers can produce (what the server actually sends),
    /// or the built-in catalog without a registry.
    /// </summary>
    public static JsonObject Catalog(Registry? registry = null)
    {
        var catalog = registry is null
            ? StreamerBotEventMap.Catalog
            : StreamerBotEventMap.BuildCatalog(registry.Triggers.SelectMany(t => t.EventTypes));
        var events = new JsonObject();
        foreach (var (source, types) in catalog.OrderBy(p => p.Key, StringComparer.Ordinal))
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
                ["trigger_count"] = a.Triggers.Count,
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

    /// <summary>The broadcaster platforms GetBroadcaster reports (the client's <c>BroadcasterPlatform</c>s Cuebot has).</summary>
    private static readonly string[] BroadcasterPlatforms = ["twitch", "youtube", "kick"];

    /// <summary>
    /// GetBroadcaster: the signed-in broadcaster and bot accounts per platform, in Streamer.bot's field names, from the
    /// platform modules' account functions; <c>connected</c>/<c>disconnected</c> from their connection state. Cuebot's
    /// state (<c>name</c>, <c>state</c>, <c>status</c>) is kept on each platform, and other connections (OBS, ...) are in
    /// <c>cuebotConnections</c>.
    /// </summary>
    private async Task<JsonObject> GetBroadcasterAsync(CancellationToken ct)
    {
        var platforms = new JsonObject();
        var connected = new JsonArray();
        var disconnected = new JsonArray();
        var others = new JsonObject();
        foreach (var status in services.Connections)
        {
            var key = status.Name.ToLowerInvariant();
            if (BroadcasterPlatforms.Contains(key)) continue;
            others[key] = new JsonObject { ["name"] = status.Name, ["state"] = status.State.ToString(), ["status"] = status.StatusMessage };
        }
        foreach (var platform in BroadcasterPlatforms)
        {
            var status = services.Connections.FirstOrDefault(c => c.Name.Equals(platform, StringComparison.OrdinalIgnoreCase));
            var account = platform switch
            {
                "twitch" => await TwitchAccountAsync(ct).ConfigureAwait(false),
                "youtube" => await YouTubeAccountAsync(ct).ConfigureAwait(false),
                _ => await KickAccountAsync(ct).ConfigureAwait(false),
            };
            if (status is null && account is null) continue;
            account ??= new JsonObject();
            account["name"] = status?.Name ?? platform;
            account["state"] = (status?.State ?? ConnectionState.Disconnected).ToString();
            account["status"] = status?.StatusMessage ?? "";
            platforms[platform] = account;
            (status?.State == ConnectionState.Connected ? connected : disconnected).Add(platform);
        }
        return new JsonObject
        {
            ["platforms"] = platforms, ["connected"] = connected, ["disconnected"] = disconnected, ["cuebotConnections"] = others,
        };
    }

    private async Task<JsonObject?> TwitchAccountAsync(CancellationToken ct)
    {
        var broadcaster = await CallAsync("twitch.getBroadcaster", null, ct).ConfigureAwait(false);
        var id = Prop(broadcaster, "userId");
        if (id.Length == 0) return null;
        var login = Prop(broadcaster, "userName");
        // Display name and partner status need a Helix lookup; it is cached for the session.
        var user = await CachedAsync("twitch:" + id, () => CallAsync("twitch.getUser", new { user = "id:" + id }, ct)).ConfigureAwait(false);
        var display = Prop(user, "displayName") is { Length: > 0 } d ? d : login;
        var type = Prop(user, "broadcasterType");
        var bot = await CallAsync("twitch.getBot", null, ct).ConfigureAwait(false);
        var botLogin = Prop(bot, "userName");
        return new JsonObject
        {
            ["broadcastUser"] = display,
            ["broadcastUserName"] = login,
            ["broadcastUserId"] = id,
            ["broadcastIsAffiliate"] = type == "affiliate",
            ["broadcastIsPartner"] = type == "partner",
            // The request reference spells these broadcaster*, the client's types broadcast*: both are sent.
            ["broadcasterIsAffiliate"] = type == "affiliate",
            ["broadcasterIsPartner"] = type == "partner",
            ["broadcastUserProfileImage"] = Prop(user, "profileImageUrl"),
            ["botUser"] = botLogin,
            ["botUserName"] = botLogin.ToLowerInvariant(),
            ["botUserId"] = Prop(bot, "userId"),
        };
    }

    private async Task<JsonObject?> YouTubeAccountAsync(CancellationToken ct)
    {
        var broadcaster = await CallAsync("youtube.getBroadcaster", null, ct).ConfigureAwait(false);
        if (Prop(broadcaster, "userId").Length == 0) return null;
        var bot = await CallAsync("youtube.getBot", null, ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["broadcastUserName"] = Prop(broadcaster, "userName"),
            ["broadcastUserId"] = Prop(broadcaster, "userId"),
            ["broadcastUserProfileImage"] = Prop(broadcaster, "profileImageUrl"),
            ["botUserName"] = Prop(bot, "userName"),
            ["botUserId"] = Prop(bot, "userId"),
            ["botUserProfileImage"] = Prop(bot, "profileImageUrl"),
        };
    }

    private async Task<JsonObject?> KickAccountAsync(CancellationToken ct)
    {
        var info = await CallAsync("kick.getBroadcaster", null, ct).ConfigureAwait(false);
        var id = Prop(info, "broadcastUserId");
        if (id.Length == 0) return null;
        var name = Prop(info, "broadcastUser");
        var profile = await CachedAsync("kick:" + id, () => CallAsync("kick.getUserInfo", new { user = id }, ct)).ConfigureAwait(false);
        var botId = Prop(info, "botUserId");
        var botProfile = botId.Length == 0 ? null
            : await CachedAsync("kick:" + botId, () => CallAsync("kick.getUserInfo", new { user = botId }, ct)).ConfigureAwait(false);
        var slug = Prop(info, "broadcasterChannelSlug");
        return new JsonObject
        {
            ["broadcasterLogin"] = slug.Length > 0 ? slug : name.ToLowerInvariant(),
            ["broadcasterUserName"] = name,
            ["broadcasterUserId"] = id,
            ["broadcasterProfileUrl"] = Prop(profile, "ProfilePicture"),
            ["botLogin"] = Prop(info, "botUser").ToLowerInvariant(),
            ["botUserName"] = Prop(info, "botUser"),
            ["botUserId"] = botId,
            ["botProfileUrl"] = Prop(botProfile, "ProfilePicture"),
        };
    }

    /// <summary>Account lookups that need the network, cached (successful ones only) for the life of the server.</summary>
    private readonly ConcurrentDictionary<string, JsonElement> _lookups = new(StringComparer.Ordinal);

    private async Task<JsonElement?> CachedAsync(string key, Func<Task<JsonElement?>> lookup)
    {
        if (_lookups.TryGetValue(key, out var hit)) return hit;
        var found = await lookup().ConfigureAwait(false);
        if (found is { ValueKind: JsonValueKind.Object } value) _lookups[key] = value;
        return found;
    }

    /// <summary>
    /// Calls a platform module's script function (twitch.getBroadcaster, youtube.getMonitoredBroadcasts, ...) outside
    /// an action run; null when no loaded module provides it or it fails (the request then reports less).
    /// </summary>
    private async Task<JsonElement?> CallAsync(string function, object? args, CancellationToken ct)
    {
        var api = services.Scripts;
        if (api is null || !api.Functions.Any(f => f.Name.Equals(function, StringComparison.OrdinalIgnoreCase))) return null;
        try
        {
            var context = services.Engine.CreateDetachedContext("Streamer.bot WebSocket request");
            var result = await api.CallAsync(context, function, args, ct).ConfigureAwait(false);
            return result is null ? null : JsonSerializer.SerializeToElement(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            services.Log.Warn("Servers", $"Streamer.bot request: {function} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>A property of a function result as text, by name in any case ("" when missing).</summary>
    private static string Prop(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } e) return "";
        foreach (var p in e.EnumerateObject())
        {
            if (!p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => p.Value.ToString(),
            };
        }
        return "";
    }

    // ---- Emotes, broadcasts, pronouns ----------------------------------------------------------------------------

    /// <summary>
    /// TwitchGetEmotes / YouTubeGetEmotes from Cuebot's emote sets: native emotes Cuebot has seen in chat
    /// (<c>userEmotes</c>) and the loaded BTTV, FFZ and 7TV sets. Empty lists without the emote service.
    /// </summary>
    private JsonObject GetEmotes(string platform)
    {
        var lists = new Dictionary<string, JsonArray>
        {
            ["userEmotes"] = [], ["bttvEmotes"] = [], ["ffzEmotes"] = [], ["sevenTvEmotes"] = [],
        };
        foreach (var e in services.Emotes?.GetEmotes(platform) ?? [])
        {
            var (list, type) = e.Provider switch
            {
                EmoteProviders.Bttv => ("bttvEmotes", "BTTV"),
                EmoteProviders.Ffz => ("ffzEmotes", "FFZ"),
                EmoteProviders.SevenTv => ("sevenTvEmotes", "7TV"),
                _ => ("userEmotes", platform.ToLowerInvariant()),
            };
            var json = new JsonObject
            {
                ["name"] = e.Code,
                ["type"] = list == "userEmotes" ? type : type + (e.Scope == "channel" ? "Channel" : "Global"),
                ["imageUrl"] = e.ImageUrl,
            };
            if (list != "userEmotes") json["zeroWidth"] = e.IsZeroWidth;
            lists[list].Add(json);
        }
        var emotes = new JsonObject();
        foreach (var (key, list) in lists) emotes[key] = list;
        return new JsonObject { ["emotes"] = emotes };
    }

    /// <summary>GetMonitoredYouTubeBroadcasts from the YouTube module, in the client's MonitoredYouTubeBroadcast shape.</summary>
    private async Task<JsonObject> GetMonitoredBroadcastsAsync(CancellationToken ct)
    {
        var broadcasts = new JsonArray();
        if (await CallAsync("youtube.getMonitoredBroadcasts", null, ct).ConfigureAwait(false) is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var b in list.EnumerateArray())
            {
                broadcasts.Add(new JsonObject
                {
                    ["actualEndTime"] = Prop(b, "actualEnd"),
                    ["actualStartTime"] = Prop(b, "actualStart"),
                    ["categoryId"] = Prop(b, "categoryId"),
                    ["channelId"] = Prop(b, "channelId"),
                    ["description"] = Prop(b, "description"),
                    ["id"] = Prop(b, "id"),
                    ["liveChatId"] = Prop(b, "liveChatId"),
                    ["privacy"] = Prop(b, "privacy"),
                    ["publishedAt"] = Prop(b, "actualStart") is { Length: > 0 } started ? started : Prop(b, "scheduledStart"),
                    ["scheduledEndTime"] = "",
                    ["scheduledStartTime"] = Prop(b, "scheduledStart"),
                    ["status"] = Prop(b, "status"),
                    ["title"] = Prop(b, "title"),
                    ["tags"] = new JsonArray(),
                    ["defaultLanguage"] = "",
                    ["defaultAudioLanguage"] = "",
                    ["url"] = Prop(b, "url"),
                });
            }
        }
        return new JsonObject { ["broadcasts"] = broadcasts, ["count"] = broadcasts.Count };
    }

    /// <summary>GetUserPronouns: looks a Twitch login up on alejo.io (cached), in Streamer.bot's pronoun shape.</summary>
    private async Task<JsonObject> GetUserPronounsAsync(JsonElement message, CancellationToken ct)
    {
        var login = Str(message, "userLogin").Trim().TrimStart('@');
        var platform = Str(message, "platform", "twitch");
        Cuebot.Core.Chat.Pronouns.UserPronouns? found = null;
        if (login.Length > 0 && services.Pronouns is { } pronouns && platform.Equals("twitch", StringComparison.OrdinalIgnoreCase))
        {
            found = await pronouns.GetAsync(login, ct).ConfigureAwait(false);
        }
        var pronoun = StreamerBotPronouns.ToJson(found);
        pronoun["userLogin"] = login;
        return new JsonObject { ["userLogin"] = login, ["pronoun"] = pronoun };
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
                ["type"] = u.Platform.ToLowerInvariant(),
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

    /// <summary>Streamer.bot credits event lists → the Cuebot section that fills them (null: Cuebot has no such section).</summary>
    private static readonly (string Key, string? Section)[] CreditsEvents =
    [
        ("follows", CreditsSections.Follows), ("cheers", CreditsSections.Cheers), ("subs", CreditsSections.Subscriptions),
        ("resubs", null), ("giftsubs", CreditsSections.GiftSubscriptions), ("giftbombs", null), ("raided", CreditsSections.Raids),
        ("rewardredemptions", CreditsSections.Redemptions), ("goalcontributions", null), ("gameupdates", null),
        ("pyramids", null), ("hypetrains", null),
    ];

    private static readonly (string Key, string? Section)[] CreditsUsers =
    [
        ("editors", null), ("moderators", CreditsSections.Moderators), ("subscribers", CreditsSections.Subscribers),
        ("vips", CreditsSections.Vips), ("groups", null), ("users", CreditsSections.Chatters),
    ];

    /// <summary>
    /// The HTTP server's GetCredits/TestCredits shape (PascalCase, as documented for HTTP): <c>Events</c>,
    /// <c>HypeTrainConductor</c>, <c>HypeTrainContributors</c>, <c>User</c>, <c>Custom</c>, <c>TopBits</c>,
    /// <c>TopChannelRewards</c>, built from a WebSocket credits response (whose fields are kept alongside).
    /// </summary>
    public static JsonObject HttpCredits(JsonObject response)
    {
        JsonNode List(string group, string key) => response[group]?[key]?.DeepClone() ?? new JsonArray();
        var http = new JsonObject
        {
            ["Events"] = new JsonObject
            {
                ["Follows"] = List("events", "follows"), ["Cheers"] = List("events", "cheers"), ["Subs"] = List("events", "subs"),
                ["ReSubs"] = List("events", "resubs"), ["GiftSubs"] = List("events", "giftsubs"),
                ["GiftBombs"] = List("events", "giftbombs"), ["Raided"] = List("events", "raided"),
                ["RewardRedemptions"] = List("events", "rewardredemptions"),
                ["GoalContributions"] = List("events", "goalcontributions"), ["GameUpdates"] = List("events", "gameupdates"),
                ["Pyramids"] = List("events", "pyramids"),
            },
            ["HypeTrainConductor"] = List("hypeTrain", "conductors"),
            ["HypeTrainContributors"] = List("hypeTrain", "contributors"),
            ["User"] = new JsonObject
            {
                ["Editors"] = List("users", "editors"), ["Moderator"] = List("users", "moderators"),
                ["Subscriber"] = List("users", "subscribers"), ["VIPs"] = List("users", "vips"), ["Users"] = List("users", "users"),
                ["regulars"] = new JsonArray(),
            },
            ["Custom"] = response["custom"]?.DeepClone() ?? new JsonObject(),
            ["TopBits"] = new JsonObject { ["All"] = List("top", "allBits"), ["Month"] = List("top", "monthBits"), ["Week"] = List("top", "weekBits") },
            ["TopChannelRewards"] = List("top", "channelRewards"),
        };
        foreach (var (key, value) in response) http[key] = value?.DeepClone();
        return http;
    }

    /// <summary>
    /// Credits in Streamer.bot's shape (<c>events</c>, <c>users</c>, <c>hypeTrain</c>, <c>top</c>, <c>groups</c>,
    /// <c>custom</c>: user names per list) at the top level, plus <c>credits</c>: the same lists keyed by Cuebot section
    /// and Cuebot's full snapshot (counts, amounts, platforms) for richer overlays.
    /// </summary>
    private static JsonObject CreditsResponse(CreditsSnapshot? snapshot)
    {
        JsonArray Names(string? section) =>
            new([.. (section is null ? null : snapshot?.Section(section))?.Entries.Select(e => (JsonNode?)JsonValue.Create(e.User)) ?? []]);
        JsonObject Lists((string Key, string? Section)[] map)
        {
            var lists = new JsonObject();
            foreach (var (key, section) in map) lists[key] = Names(section);
            return lists;
        }

        var bySection = new JsonObject();
        foreach (var section in snapshot?.Sections ?? []) bySection[section.Id] = Names(section.Id);
        var custom = new JsonObject();
        foreach (var section in snapshot?.Sections ?? [])
        {
            if (CreditsEvents.All(m => m.Section != section.Id) && CreditsUsers.All(m => m.Section != section.Id)) custom[section.Id] = Names(section.Id);
        }
        var credits = new JsonObject { ["events"] = bySection };
        if (snapshot is not null) credits["cuebot"] = JsonSerializer.SerializeToNode(snapshot, Cuebot.Core.Storage.Json.Options);
        return new JsonObject
        {
            ["events"] = Lists(CreditsEvents),
            ["users"] = Lists(CreditsUsers),
            ["hypeTrain"] = new JsonObject { ["conductors"] = new JsonArray(), ["contributors"] = new JsonArray() },
            ["top"] = new JsonObject
            {
                ["allBits"] = new JsonArray(), ["monthBits"] = new JsonArray(), ["weekBits"] = new JsonArray(), ["channelRewards"] = new JsonArray(),
            },
            ["groups"] = new JsonObject(),
            ["custom"] = custom,
            ["credits"] = credits,
        };
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
                ["ignoreInternal"] = c.IgnoreInternal,
                ["ignoreBotAccount"] = c.IgnoreBotAccount,
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
        // The reference documents "variable"; the official client (2.x) reads variables[name]: both are sent.
        return new JsonObject
        {
            ["variable"] = Variable(found.Name, found.Value, found.LastWrite),
            ["variables"] = new JsonObject { [found.Name] = Variable(found.Name, found.Value, found.LastWrite) },
            ["count"] = 1,
        };
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
        if (name.Length > 0)
        {
            // With a variable name the reference documents one "variable"; the official client reads variables[]:
            // both are sent.
            var found = list.FirstOrDefault() ?? throw new RequestException($"Variable '{name}' not found");
            return new JsonObject
            {
                ["variable"] = Variable(found.Name, found.Value, found.LastWrite),
                ["variables"] = UserVariables([found]),
                ["count"] = 1,
            };
        }
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
        // bot: true sends from the bot account, false from the broadcaster (no field: the platform's setting);
        // internal: false lets commands react to the message's echo. replyId (Twitch, Kick), broadcastId (YouTube).
        var options = new ChatSendOptions(
            Bot: message.ValueKind == JsonValueKind.Object && message.TryGetProperty("bot", out _) ? Bool(message, "bot", false) : null,
            Internal: Bool(message, "internal", true),
            ReplyTo: Str(message, "replyId") is { Length: > 0 } reply ? reply : null,
            BroadcastId: Str(message, "broadcastId") is { Length: > 0 } broadcast ? broadcast : null);
        try
        {
            var sent = await chat.SendAsync(platform, text, options, ct).ConfigureAwait(false);
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
