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

    /// <summary>
    /// Calls a host function (e.g. "obs.request") with an object of named arguments. Field names of the result are
    /// camelCase (<c>userName</c>), like the REST API's.
    /// </summary>
    public async Task<JsonElement> Call(string name, object? args = null)
    {
        var result = await _api.CallAsync(_context, name, args, _ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, ResultJson);
    }

    /// <summary>How script function results reach C# and Python code: camelCase field names.</summary>
    internal static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web);

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

    // ---- Steps, the way the action editor runs them (what "Export to code" writes) ----

    /// <summary>
    /// Runs one step by its type id, with its settings' %variables% filled in first, exactly as the action editor
    /// would. Returns the branch a step with branches picked (like "True"), else null. When the step stops the action,
    /// this throws <see cref="StopActionException"/>, which ends the script the way <c>return false;</c> does.
    /// </summary>
    public async Task<string?> Step(string type, Dictionary<string, string>? parameters = null)
    {
        var result = await Call("steps.run", new { type, parameters = parameters ?? [] }).ConfigureAwait(false);
        if (result.GetProperty("outcome").GetString() != "completed") throw new StopActionException();
        var branch = result.GetProperty("branch");
        return branch.ValueKind == JsonValueKind.String ? branch.GetString() : null;
    }

    /// <summary>
    /// Runs steps (with the steps inside them) in order, from their actions.json form. Returns "completed", "stop",
    /// "break" or "continue".
    /// </summary>
    public async Task<string> RunSteps(string stepsJson)
    {
        using var steps = JsonDocument.Parse(stepsJson);
        return (await Call("steps.runList", new { steps = steps.RootElement }).ConfigureAwait(false)).GetString() ?? "completed";
    }

    /// <summary>Checks a condition the way the If step does (both values' %variables% filled in first).</summary>
    public async Task<bool> Check(string left, string op, string right = "") =>
        (await Call("steps.check", new { left, @operator = op, right }).ConfigureAwait(false)).ValueKind == JsonValueKind.True;

    /// <summary>Fills in %variables% and ~globals~ in a text, the way step settings are.</summary>
    public async Task<string> Resolve(string text) =>
        (await Call("args.resolve", new { text }).ConfigureAwait(false)).GetString() ?? "";

    /// <summary>Picks a position at random by weight (0 = first), like a Group set to run one random step.</summary>
    public async Task<int> Pick(params double[] weights) =>
        (await Call("steps.pick", new { weights }).ConfigureAwait(false)).GetInt32();

    /// <summary>Reads one argument of the run as text ("" when it is missing).</summary>
    public string Arg(string name) => _context.Args.TryGetValue(name, out var value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "" : "";

    /// <summary>One argument of the run as it is stored (null when it is missing).</summary>
    public object? ArgValue(string name) => _context.Args.GetValueOrDefault(name);

    /// <summary>Whether the run has an argument with this name.</summary>
    public bool HasArg(string name) => _context.Args.ContainsKey(name);

    /// <summary>Sets one argument of the run; later steps can use it as %name%.</summary>
    public void SetArg(string name, object? value) => _context.Args[name] = value;

    /// <summary>Removes an argument of the run.</summary>
    public void RemoveArg(string name) => _context.Args.Remove(name);

    /// <summary>
    /// Runs lanes at the same time, like the "Run at the same time" step, and waits for all of them. Each lane gets
    /// its own <c>Cb</c> with its own copy of the arguments (merged back in lane order afterwards). A lane that
    /// returns false stops the action and cancels the others; then this returns false.
    /// </summary>
    public Task<bool> Together(params Func<CuebotHelpers, Task<bool>>[] lanes) =>
        _context.Engine.RunTogetherAsync(_context, lanes.Select(lane => (Func<ActionRunContext, CancellationToken, Task<bool>>)
            (async (fork, token) =>
            {
                try
                {
                    return await lane(new CuebotHelpers(_api, fork, token)).ConfigureAwait(false);
                }
                catch (StopActionException)
                {
                    return false;
                }
            })).ToArray(), _ct);
}

/// <summary>Thrown by <see cref="CuebotHelpers.Step"/> when a step stops the action; the script then stops it too.</summary>
public sealed class StopActionException() : Exception("A step stopped the action");
