using Cuebot.Core.Actions;
using Cuebot.Core.Variables;
using Streamer.bot.Plugin.Interface.Model;

namespace Cuebot.Scripting.Compat;

public sealed partial class CphProxy
{
    // ---- Global variables -----------------------------------------------------------------------------------------

    /// <summary>A global converted to <typeparamref name="T"/>; default when missing or stored with the other persistence.</summary>
    public T? GetGlobalVar<T>(string varName, bool persisted = true)
    {
        var globals = Globals();
        if (!globals.TryGetValue(varName, out var value) || globals.IsPersisted(varName) != persisted) return default;
        return CphConvert.To<T>(value);
    }

    public void SetGlobalVar(string varName, object? value, bool persisted = true) =>
        Globals().SetValue(varName, CphConvert.ToStored(value), persisted);

    public void UnsetGlobalVar(string varName, bool persisted = true)
    {
        var globals = Globals();
        if (globals.Contains(varName) && globals.IsPersisted(varName) == persisted) globals.Remove(varName);
    }

    public List<GlobalVariableValue> GetGlobalVarValues(bool persisted = true) =>
        Globals().Snapshot(persisted).Select(v => new GlobalVariableValue
        {
            VariableName = v.Name,
            Value = v.Value,
            LastWrite = v.LastWrite.LocalDateTime,
        }).ToList();

    public void ClearNonPersistedGlobals() => Globals().ClearTemporary();

    private GlobalVariables Globals([System.Runtime.CompilerServices.CallerMemberName] string method = "") =>
        _context?.Globals ?? Need(_services.Globals, method);

    // ---- User variables (Twitch / YouTube / Kick) -----------------------------------------------------------------

    public T? GetTwitchUserVar<T>(string userLogin, string varName, bool persisted = true) => GetUserVarByName<T>("twitch", userLogin, varName);
    public T? GetTwitchUserVarById<T>(string userId, string varName, bool persisted = true) => GetUserVarById<T>("twitch", userId, varName);
    public void SetTwitchUserVar(string userLogin, string varName, object? value, bool persisted = true) => SetUserVarByName("twitch", userLogin, varName, value, persisted);
    public void SetTwitchUserVarById(string userId, string varName, object? value, bool persisted = true) => SetUserVarById("twitch", userId, varName, value, persisted);
    public void UnsetTwitchUserVar(string userLogin, string varName, bool persisted = true) => UnsetUserVarByName("twitch", userLogin, varName);
    public void UnsetTwitchUserVarById(string userId, string varName, bool persisted = true) => UserVars().Delete("twitch", userId, varName);
    public void UnsetAllTwitchUsersVar(string varName, bool persisted = true) => UserVars().DeleteForAllUsers(varName, "twitch");
    public List<UserVariableValue<T>> GetTwitchUsersVar<T>(string varName, bool persisted = true) => UsersVar<T>("twitch", varName, persisted);

    public T? GetYouTubeUserVar<T>(string userName, string varName, bool persisted = true) => GetUserVarByName<T>("youtube", userName, varName);
    public T? GetYouTubeUserVarById<T>(string userId, string varName, bool persisted = true) => GetUserVarById<T>("youtube", userId, varName);
    public void SetYouTubeUserVar(string userName, string varName, object? value, bool persisted = true) => SetUserVarByName("youtube", userName, varName, value, persisted);
    public void SetYouTubeUserVarById(string userId, string varName, object? value, bool persisted = true) => SetUserVarById("youtube", userId, varName, value, persisted);
    public void UnsetYouTubeUserVar(string userName, string varName, bool persisted = true) => UnsetUserVarByName("youtube", userName, varName);
    public void UnsetYouTubeUserVarById(string userId, string varName, bool persisted = true) => UserVars().Delete("youtube", userId, varName);
    public void UnsetAllYouTubeUsersVar(string varName, bool persisted = true) => UserVars().DeleteForAllUsers(varName, "youtube");
    public List<UserVariableValue<T>> GetYouTubeUsersVar<T>(string varName, bool persisted = true) => UsersVar<T>("youtube", varName, persisted);

    public T? GetKickUserVar<T>(string userLogin, string varName, bool persisted = true) => GetUserVarByName<T>("kick", userLogin, varName);
    public T? GetKickUserVarById<T>(string userId, string varName, bool persisted = true) => GetUserVarById<T>("kick", userId, varName);
    public void SetKickUserVar(string userLogin, string varName, object? value, bool persisted = true) => SetUserVarByName("kick", userLogin, varName, value, persisted);
    public void SetKickUserVarById(string userId, string varName, object? value, bool persisted = true) => SetUserVarById("kick", userId, varName, value, persisted);
    public void UnsetKickUserVar(string userLogin, string varName, bool persisted = true) => UnsetUserVarByName("kick", userLogin, varName);
    public void UnsetKickUserVarById(string userId, string varName, bool persisted = true) => UserVars().Delete("kick", userId, varName);
    public void UnsetAllKickUsersVar(string varName, bool persisted = true) => UserVars().DeleteForAllUsers(varName, "kick");
    public List<UserVariableValue<T>> GetKickUsersVar<T>(string varName, bool persisted = true) => UsersVar<T>("kick", varName, persisted);

    // Older platform-less names (Twitch users).
    public T? GetUserVar<T>(string userName, string varName, bool persisted = true) => GetTwitchUserVar<T>(userName, varName, persisted);
    public void SetUserVar(string userName, string varName, object? value, bool persisted = true) => SetTwitchUserVar(userName, varName, value, persisted);
    public void UnsetUserVar(string userName, string varName, bool persisted = true) => UnsetTwitchUserVar(userName, varName, persisted);

    public void UnsetUser(string userName, bool persisted = true)
    {
        if (FindUserId("twitch", userName) is { } id) UserVars().DeleteUser("twitch", id);
    }

    public void UnsetAllUsersVar(string varName, bool persisted = true) => UserVars().DeleteForAllUsers(varName);
    public void ClearNonPersistedUserGlobals() => UserVars().ClearTemporary();

    private T? GetUserVarByName<T>(string platform, string userName, string varName) =>
        FindUserId(platform, userName) is { } id ? GetUserVarById<T>(platform, id, varName) : default;

    private T? GetUserVarById<T>(string platform, string userId, string varName) =>
        UserVars().TryGet(platform, userId, varName, out var value) ? CphConvert.To<T>(value) : default;

    private void SetUserVarByName(string platform, string userName, string varName, object? value, bool persisted)
    {
        var name = userName.Trim().TrimStart('@');
        // Unknown users are keyed by their lower-cased name until the platform reports their id.
        var id = FindUserId(platform, name) ?? name.ToLowerInvariant();
        UserVars().Set(platform, id, name, varName, VariableValues.Normalize(CphConvert.ToStored(value)), persisted);
    }

    private void SetUserVarById(string platform, string userId, string varName, object? value, bool persisted) =>
        UserVars().Set(platform, userId, null, varName, VariableValues.Normalize(CphConvert.ToStored(value)), persisted);

    private void UnsetUserVarByName(string platform, string userName, string varName)
    {
        if (FindUserId(platform, userName) is { } id) UserVars().Delete(platform, id, varName);
    }

    private List<UserVariableValue<T>> UsersVar<T>(string platform, string varName, bool persisted) =>
        UserVars().Snapshot(platform, persisted: persisted)
            .Where(v => string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase))
            .Select(v => new UserVariableValue<T>
            {
                UserId = v.UserId,
                UserLogin = v.UserName.ToLowerInvariant(),
                UserName = v.UserName,
                VariableName = v.Name,
                Value = CphConvert.To<T>(v.Value),
                LastWrite = v.LastWrite.LocalDateTime,
            }).ToList();

    private string? FindUserId(string platform, string userName)
    {
        var name = userName.Trim().TrimStart('@');
        if (_services.Users?.FindByName(platform, name) is { UserId.Length: > 0 } user) return user.UserId;
        return UserVars().FindUserId(platform, name)
               ?? (UserVars().Snapshot(platform, name.ToLowerInvariant()).Count > 0 ? name.ToLowerInvariant() : null);
    }

    private UserVariables UserVars([System.Runtime.CompilerServices.CallerMemberName] string method = "") =>
        Need(_services.UserVariables, method);
}
