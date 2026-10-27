using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;

namespace Cuebot.Scripting;

/// <summary>
/// The <c>Cb</c> object of C# scripts: calls any host script function by name, plus shortcuts for common ones.
/// </summary>
public sealed class CuebotHelpers
{
    private readonly ScriptApi _api;
    private readonly ActionRunContext _context;
    private readonly CancellationToken _ct;

    internal CuebotHelpers(ScriptApi api, ActionRunContext context, CancellationToken ct)
    {
        _api = api;
        _context = context;
        _ct = ct;
    }

    /// <summary>Calls a host function (e.g. "obs.request") with an object of named arguments.</summary>
    public async Task<JsonElement> Call(string name, object? args = null)
    {
        var result = await _api.CallAsync(_context, name, args, _ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result);
    }

    public Task Log(string message, string level = "Info") => Call("log", new { message, level });

    public async Task<string?> GetGlobal(string name)
    {
        var value = await Call("globals.get", new { name }).ConfigureAwait(false);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    public Task SetGlobal(string name, string value, bool persisted = true) =>
        Call("globals.set", new { name, value, persisted });

    /// <summary>Runs another action by name and waits for it. Returns whether it succeeded.</summary>
    public async Task<bool> RunAction(string name) =>
        (await Call("actions.run", new { name }).ConfigureAwait(false)).ValueKind == JsonValueKind.True;

    public Task SendChat(string message) => Call("twitch.sendMessage", new { message });

    public Task Speak(string text, string? alias = null) => Call("speech.speak", new { text, alias });

    /// <summary>Sends an obs-websocket request and returns its response data.</summary>
    public Task<JsonElement> Obs(string requestType, object? data = null) =>
        Call("obs.request", new { requestType, requestData = data });
}
