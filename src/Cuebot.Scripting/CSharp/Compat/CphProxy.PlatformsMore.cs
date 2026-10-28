using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Variables;
using Streamer.bot.Plugin.Interface.Model;

namespace Cuebot.Scripting.Compat;

/// <summary>
/// Streamer.bot's YouTube, Kick, Meld Studio and OBS filter methods, the bulk user-variable methods and
/// <c>Parse</c>. Methods returning bool report a failed request as false with a warning; a missing module throws.
/// </summary>
public sealed partial class CphProxy
{
    // ---- YouTube --------------------------------------------------------------------------------------------------

    /// <summary>The signed-in YouTube broadcaster, or null.</summary>
    public YouTubeUserInfo? YouTubeGetBroadcaster() => YouTubeAccount("youtube.getBroadcaster");

    /// <summary>The signed-in YouTube bot account, or null when there is none.</summary>
    public YouTubeUserInfo? YouTubeGetBot() => YouTubeAccount("youtube.getBot");

    /// <summary>Sends to the chat of the most recently monitored broadcast.</summary>
    public void SendYouTubeMessageToLatestMonitored(string message, bool useBot = true, bool fallback = true)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Call("youtube.sendMessageToLatestMonitored", new { message, useBot, fallback });
    }

    // The broadcast methods change the latest monitored broadcast when broadcastId is null.
    public bool YouTubeSetTitle(string title, string? broadcastId = null) =>
        !string.IsNullOrWhiteSpace(title) && YouTube("youtube.setTitle", new { title, broadcastId = broadcastId ?? "" });

    public bool YouTubeSetDescription(string description, string? broadcastId = null) =>
        YouTube("youtube.setDescription", new { description = description ?? "", broadcastId = broadcastId ?? "" });

    /// <summary>Sets the category by name ("Gaming", "Music", ...) or by YouTube's numeric category id.</summary>
    public bool YouTubeSetCategory(string categoryName, string? broadcastId = null)
    {
        var id = YouTubeCategoryId(categoryName);
        if (id is null)
        {
            LogWarn($"YouTubeSetCategory: '{categoryName}' is not a YouTube category");
            return false;
        }
        return YouTube("youtube.setCategory", new { categoryId = id, broadcastId = broadcastId ?? "" });
    }

    /// <summary>Privacy: public, unlisted or private.</summary>
    public bool YouTubeSetPrivacy(string privacy, string? broadcastId = null) =>
        !string.IsNullOrWhiteSpace(privacy) && YouTube("youtube.setPrivacy", new { privacy = privacy.Trim(), broadcastId = broadcastId ?? "" });

    /// <summary>Sets every given value (null or empty ones are left unchanged). True when all changes were made.</summary>
    public bool YouTubeSetMetaData(string? title, string? description, string? privacy, string? categoryName, string? broadcastId = null)
    {
        var ok = true;
        if (!string.IsNullOrEmpty(title) || description is not null)
        {
            var args = new Dictionary<string, object?> { ["broadcastId"] = broadcastId ?? "" };
            if (!string.IsNullOrEmpty(title)) args["title"] = title;
            if (description is not null) args["description"] = description;
            ok &= YouTube("youtube.setMetadata", args);
        }
        if (!string.IsNullOrWhiteSpace(privacy)) ok &= YouTubeSetPrivacy(privacy, broadcastId);
        if (!string.IsNullOrWhiteSpace(categoryName)) ok &= YouTubeSetCategory(categoryName, broadcastId);
        return ok;
    }

    public bool YouTubeAddTags(List<string> tags, string? broadcastId = null) =>
        YouTube("youtube.addTags", new { tags = tags ?? [], broadcastId = broadcastId ?? "" });

    public bool YouTubeRemoveTags(List<string> tags, string? broadcastId = null) =>
        YouTube("youtube.removeTags", new { tags = tags ?? [], broadcastId = broadcastId ?? "" });

    public bool YouTubeClearTags(string? broadcastId = null) => YouTube("youtube.clearTags", new { broadcastId = broadcastId ?? "" });

    /// <summary>Bans a user by channel id from the broadcast's chat.</summary>
    public bool YouTubeBanUserById(string userId, string? broadcastId = null) =>
        !string.IsNullOrWhiteSpace(userId) && YouTube("youtube.banUser", new { user = userId.Trim(), broadcastId = broadcastId ?? "" });

    /// <summary>Bans a user by name (a recent chatter's name or an @handle).</summary>
    public bool YouTubeBanUserByName(string userName, string? broadcastId = null) =>
        !string.IsNullOrWhiteSpace(userName) && YouTube("youtube.banUser", new { user = userName.Trim(), broadcastId = broadcastId ?? "" });

    public bool YouTubeTimeoutUserById(string userId, int duration, string? broadcastId = null) =>
        !string.IsNullOrWhiteSpace(userId)
        && YouTube("youtube.timeoutUser", new { user = userId.Trim(), duration = Num(Math.Max(1, duration)), broadcastId = broadcastId ?? "" });

    public bool YouTubeTimeoutUserByName(string userName, int duration, string? broadcastId = null) =>
        !string.IsNullOrWhiteSpace(userName)
        && YouTube("youtube.timeoutUser", new { user = userName.Trim(), duration = Num(Math.Max(1, duration)), broadcastId = broadcastId ?? "" });

    private bool YouTube(string function, object args, [System.Runtime.CompilerServices.CallerMemberName] string method = "") =>
        Try(() => Call(function, args, method), method);

    private YouTubeUserInfo? YouTubeAccount(string function, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        var account = Call(function, null, method);
        var id = Prop(account, "userId");
        if (id.Length == 0 || Prop(account, "isSignedIn") == "False") return null;
        var name = Prop(account, "userName");
        var handle = Prop(account, "customUrl");
        var known = _services.Users?.Get("YouTube", id);
        return new YouTubeUserInfo
        {
            UserId = id,
            UserName = name,
            UserLogin = handle.Length > 0 ? handle : name,
            ProfileImageUrl = Prop(account, "profileImageUrl"),
            LastActive = LocalTime(known?.LastSeen),
        };
    }

    /// <summary>YouTube's standard video categories by name (YouTube Data API category ids).</summary>
    private static readonly Dictionary<string, string> YouTubeCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Film & Animation"] = "1", ["Autos & Vehicles"] = "2", ["Music"] = "10", ["Pets & Animals"] = "15", ["Sports"] = "17",
        ["Travel & Events"] = "19", ["Gaming"] = "20", ["People & Blogs"] = "22", ["Comedy"] = "23", ["Entertainment"] = "24",
        ["News & Politics"] = "25", ["Howto & Style"] = "26", ["Education"] = "27", ["Science & Technology"] = "28",
        ["Nonprofits & Activism"] = "29",
    };

    private static string? YouTubeCategoryId(string? category)
    {
        var text = category?.Trim() ?? "";
        if (text.Length == 0) return null;
        if (text.All(char.IsAsciiDigit)) return text;
        return YouTubeCategories.TryGetValue(text.Replace(" and ", " & ", StringComparison.OrdinalIgnoreCase), out var id) ? id : null;
    }

    // ---- Kick -----------------------------------------------------------------------------------------------------

    public bool KickBanUser(string userName, string? reason = null, bool bot = false) =>
        !string.IsNullOrWhiteSpace(userName) && Try(() => Call("kick.ban", new { user = Login(userName), reason = reason ?? "" }));

    /// <summary>Times a user out for <paramref name="duration"/> seconds (Kick counts in minutes, so it is rounded up).</summary>
    public bool KickTimeoutUser(string username, int duration, string? reason = null, bool bot = false) =>
        !string.IsNullOrWhiteSpace(username)
        && Try(() => Call("kick.timeout", new { user = Login(username), seconds = Num(Math.Max(1, duration)), reason = reason ?? "" }));

    public bool KickUnbanUser(string userName, bool bot = false) =>
        !string.IsNullOrWhiteSpace(userName) && Try(() => Call("kick.unban", new { user = Login(userName) }));

    public bool KickDeleteChatMessage(string messageId) =>
        !string.IsNullOrWhiteSpace(messageId) && Try(() => Call("kick.deleteMessage", new { messageId = messageId.Trim() }));

    /// <summary>Replies to a Kick chat message, with the bot account when <paramref name="useBot"/> (else, with <paramref name="fallback"/>, the broadcaster).</summary>
    public bool KickReplyToMessage(string message, string replyId, bool useBot = true, bool fallback = true) =>
        !string.IsNullOrWhiteSpace(message)
        && Try(() => Call("kick.reply", new { message, replyTo = replyId?.Trim() ?? "", useBot, fallback }));

    public bool KickSetTitle(string title) => !string.IsNullOrWhiteSpace(title) && Try(() => Call("kick.setTitle", new { title }));

    /// <summary>Sets the Kick category by name (or id); returns it, or null when Kick refused. Only the name is filled.</summary>
    public KickCategory? KickSetCategory(string categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName)) return null;
        var set = TryCall("kick.setCategory", new { category = categoryName.Trim() });
        if (set is not { } name) return null;
        var text = name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "";
        return new KickCategory
        {
            Name = text.Length > 0 ? text : categoryName.Trim(),
            Id = int.TryParse(categoryName.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0,
        };
    }

    // ---- Bulk user variables --------------------------------------------------------------------------------------

    /// <summary>Sets the variable for every listed Kick user id.</summary>
    public void SetKickUsersVarById(List<string> userIds, string varName, object? value, bool persisted = true)
    {
        foreach (var id in Ids(userIds)) SetUserVarById("kick", id, varName, value, persisted);
    }

    /// <summary>Adds <paramref name="value"/> to the variable of every Twitch user who has it (with that persistence).</summary>
    public void IncrementAllTwitchUsersVar(string varName, object? value, bool persisted = true) => IncrementAll("twitch", varName, value, persisted);
    public void IncrementAllYouTubeUsersVar(string varName, object? value, bool persisted = true) => IncrementAll("youtube", varName, value, persisted);
    public void IncrementAllKickUsersVar(string varName, object? value, bool persisted = true) => IncrementAll("kick", varName, value, persisted);

    /// <summary>Adds <paramref name="value"/> to the variable of each listed user that has it; others are skipped.</summary>
    public void IncrementTwitchUsersVarById(List<string> userIds, string varName, object? value, bool persisted = true) =>
        IncrementByIds("twitch", userIds, varName, value, persisted, create: false);
    public void IncrementYouTubeUsersVarById(List<string> userIds, string varName, object? value, bool persisted = true) =>
        IncrementByIds("youtube", userIds, varName, value, persisted, create: false);

    /// <summary>Kick's name for <see cref="IncrementTwitchUsersVarById"/>: skips listed users without the variable.</summary>
    public void IncrementAllKickUsersVarById(List<string> userIds, string varName, object? value, bool persisted = true) =>
        IncrementByIds("kick", userIds, varName, value, persisted, create: false);

    /// <summary>Adds <paramref name="value"/> to the variable of each listed user, creating it (from 0) where missing.</summary>
    public void IncrementOrCreateTwitchUsersVarById(List<string> userIds, string varName, object? value, bool persisted = true) =>
        IncrementByIds("twitch", userIds, varName, value, persisted, create: true);
    public void IncrementOrCreateYouTubeUsersVarById(List<string> userIds, string varName, object? value, bool persisted = true) =>
        IncrementByIds("youtube", userIds, varName, value, persisted, create: true);
    public void IncrementOrCreateKickUsersVarById(List<string> userIds, string varName, object? value, bool persisted = true) =>
        IncrementByIds("kick", userIds, varName, value, persisted, create: true);

    private void IncrementAll(string platform, string varName, object? value, bool persisted,
        [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        if (!Amount(value, method, out var amount) || string.IsNullOrWhiteSpace(varName)) return;
        var vars = UserVars(method);
        foreach (var v in vars.Snapshot(platform, persisted: persisted).Where(v => string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase)).ToList())
            Increment(vars, platform, v.UserId, varName, amount, persisted, method);
    }

    private void IncrementByIds(string platform, List<string> userIds, string varName, object? value, bool persisted, bool create,
        [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        if (!Amount(value, method, out var amount) || string.IsNullOrWhiteSpace(varName)) return;
        var vars = UserVars(method);
        foreach (var id in Ids(userIds))
        {
            var has = vars.Snapshot(platform, id, persisted).Any(v => string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase));
            if (has || create) Increment(vars, platform, id, varName, amount, persisted, method);
        }
    }

    private void Increment(UserVariables vars, string platform, string userId, string varName, decimal amount, bool persisted, string method)
    {
        try
        {
            vars.Increment(platform, userId, null, varName, amount, persisted);
        }
        catch (InvalidOperationException ex)
        {
            LogWarn($"{method}: {ex.Message}");
        }
    }

    private bool Amount(object? value, string method, out decimal amount)
    {
        if (CphConvert.TryTo(value, out amount)) return true;
        LogWarn($"{method}: '{Text(value)}' is not a number");
        return false;
    }

    private static IEnumerable<string> Ids(IEnumerable<string>? ids) =>
        (ids ?? []).Select(i => i?.Trim() ?? "").Where(i => i.Length > 0).Distinct(StringComparer.Ordinal);

    // ---- Meld Studio (Cuebot has one Meld Studio connection; index -1 or 0 selects it) ---------------------------

    /// <summary>Starts connecting to Meld Studio (Cuebot keeps retrying in the background). True when started.</summary>
    public bool MeldStudioConnect(int connectionIdx = -1) => Meld(connectionIdx) && Try(() => Call("meldstudio.connect", null));

    public void MeldStudioDisconnect(int connectionIdx = -1)
    {
        if (Meld(connectionIdx)) Call("meldstudio.disconnect", null);
    }

    public bool MeldStudioIsConnected(int connectionIdx = -1) =>
        Meld(connectionIdx) && Call("meldstudio.isConnected", null).ValueKind == JsonValueKind.True;

    /// <summary>The index of the named connection: 0, Cuebot's only Meld Studio connection, for any name.</summary>
    public int MeldStudioGetConnectionByName(string name) => HasFunction("meldstudio.isConnected") ? 0 : -1;

    /// <summary>Shows a scene by its Meld id (a name also works).</summary>
    public bool MeldStudioShowScene(string sceneId, int connectionIdx = -1) =>
        Meld(connectionIdx) && !string.IsNullOrWhiteSpace(sceneId) && Try(() => Call("meldstudio.showScene", new { scene = sceneId.Trim() }));

    public bool MeldStudioShowSceneByName(string sceneName, int connectionIdx = -1) =>
        Meld(connectionIdx) && !string.IsNullOrWhiteSpace(sceneName)
        && Try(() => Call("meldstudio.showSceneByName", new { sceneName = sceneName.Trim() }));

    public bool MeldStudioShowLayerByName(string sceneName, string layerName, int connectionIdx = -1) =>
        Meld(connectionIdx) && !string.IsNullOrWhiteSpace(layerName)
        && Try(() => Call("meldstudio.showLayer", new { layer = layerName.Trim(), scene = sceneName?.Trim() ?? "" }));

    public bool MeldStudioHideLayerByName(string sceneName, string layerName, int connectionIdx = -1) =>
        Meld(connectionIdx) && !string.IsNullOrWhiteSpace(layerName)
        && Try(() => Call("meldstudio.hideLayer", new { layer = layerName.Trim(), scene = sceneName?.Trim() ?? "" }));

    private bool Meld(int connectionIdx, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        if (connectionIdx is -1 or 0) return true;
        LogWarn($"{method}: Cuebot has a single Meld Studio connection (use -1 or 0, not {connectionIdx})");
        return false;
    }

    // ---- OBS filters and chapters ---------------------------------------------------------------------------------

    /// <summary>Adds a chapter marker to the current recording (Hybrid MP4 recordings, OBS 30.2 or later).</summary>
    public bool ObsCreateRecordChapter(string chapterName, int connection = 0) =>
        Try(() => ObsRequest(connection, "CreateRecordChapter", new { chapterName = chapterName ?? "" }));

    /// <summary>Whether a filter of the scene <paramref name="scene"/> is enabled.</summary>
    public bool ObsIsFilterEnabled(string scene, string filterName, int connection = 0) => FilterEnabled(connection, scene, filterName);

    /// <summary>Whether a filter of the source <paramref name="source"/> (in <paramref name="scene"/>) is enabled.</summary>
    public bool ObsIsFilterEnabled(string scene, string source, string filterName, int connection = 0) => FilterEnabled(connection, source, filterName);

    /// <summary>State: 0 visible (enabled), 1 hidden (disabled), 2 toggle.</summary>
    public void ObsSetFilterState(string scene, string filterName, int state, int connection = 0) => SetFilterState(connection, scene, filterName, state);
    public void ObsSetFilterState(string scene, string source, string filterName, int state, int connection = 0) =>
        SetFilterState(connection, source, filterName, state);

    public void ObsToggleFilter(string scene, string filterName, int connection = 0) => SetFilterState(connection, scene, filterName, 2);
    public void ObsToggleFilter(string scene, string source, string filterName, int connection = 0) => SetFilterState(connection, source, filterName, 2);

    /// <summary>Sets one randomly chosen filter of the scene to <paramref name="state"/> (0 visible, 1 hidden, 2 toggle).</summary>
    public void ObsSetRandomFilterState(string scene, int state, int connection = 0) => RandomFilter(connection, scene, state);
    public void ObsSetRandomFilterState(string scene, string source, int state, int connection = 0) => RandomFilter(connection, source, state);

    /// <summary>Disables every filter of the source.</summary>
    public void ObsHideSourcesFilters(string scene, string source, int connection = 0) => HideFilters(connection, source);

    /// <summary>Disables every filter of the scene.</summary>
    public void ObsHideScenesFilters(string scene, int connection = 0) => HideFilters(connection, scene);

    private bool FilterEnabled(int connection, string source, string filter, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        var data = ObsRequest(connection, "GetSourceFilter", new { sourceName = source, filterName = filter }, method);
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("filterEnabled", out var on) && on.ValueKind == JsonValueKind.True;
    }

    private void SetFilterState(int connection, string source, string filter, int state, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        var enabled = state switch
        {
            0 => true,
            1 => false,
            2 => !FilterEnabled(connection, source, filter, method),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Use 0 (visible), 1 (hidden) or 2 (toggle)"),
        };
        ObsRequest(connection, "SetSourceFilterEnabled", new { sourceName = source, filterName = filter, filterEnabled = enabled }, method);
    }

    private List<string> FilterNames(int connection, string source, string method)
    {
        var data = ObsRequest(connection, "GetSourceFilterList", new { sourceName = source }, method);
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("filters", out var filters) && filters.ValueKind == JsonValueKind.Array
            ? filters.EnumerateArray().Select(f => Prop(f, "filterName")).Where(n => n.Length > 0).ToList()
            : [];
    }

    private void RandomFilter(int connection, string source, int state, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        var names = FilterNames(connection, source, method);
        if (names.Count == 0) return;
        SetFilterState(connection, source, names[Random.Shared.Next(names.Count)], state, method);
    }

    private void HideFilters(int connection, string source, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        foreach (var name in FilterNames(connection, source, method))
            ObsRequest(connection, "SetSourceFilterEnabled", new { sourceName = source, filterName = name, filterEnabled = false }, method);
    }

    // ---- Parse ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Replaces variables in <paramref name="input"/> (<c>%arg%</c>, <c>~global~</c>, inline functions) using
    /// <paramref name="args"/>, or this run's arguments when null.
    /// </summary>
    public string Parse(string input, Dictionary<string, object?>? args = null)
    {
        if (string.IsNullOrEmpty(input)) return input ?? "";
        var values = _context?.Args is { } runArgs && args is null
            ? runArgs
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in args ?? []) values[key] = value;
        var globals = _context?.Globals ?? _services.Globals;
        return VariableResolver.Resolve(input, values, globals is null ? null : globals.Get);
    }
}
