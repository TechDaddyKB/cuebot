using Cuebot.Core.Actions;

namespace Cuebot.Scripting;

/// <summary>"Execute C# code": runs a C# script body with <c>Args</c> and <c>Cb</c>. Returning false stops the action.</summary>
public sealed class CSharpCodeSubAction(CSharpScriptEngine engine) : ISubActionHandler
{
    internal const string DefaultCode =
        """
        // Args: this run's arguments (changes are kept for later steps).
        // Cb: Cuebot helpers, e.g. await Cb.Log("..."), await Cb.SendChat("..."), await Cb.Call("name", new { ... }).
        // Return false to stop the action.
        var user = Args.TryGetValue("user", out var value) ? value?.ToString() : "someone";
        await Cb.Log($"Hello from C#, {user}!");
        Args["greeting"] = $"Hi {user}";
        return true;
        """;

    public string TypeId => "code.csharp";
    public string Category => ScriptSubActions.Category;
    public string DisplayName => "Execute C# code";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("code", "Code", ParameterKind.Code)
        {
            Language = "csharp",
            DefaultValue = DefaultCode,
            Help = "Script body using Args, Cb and CancellationToken. Scripts run inside Cuebot with full access to your computer; only use code you trust.",
        },
        ScriptSubActions.TimeoutParameter,
        new(NameParameter, "Code name")
        {
            DefaultValue = "",
            Help = "Optional. Lets other scripts call a public method of this code's CPHInline class with CPH.ExecuteMethod(name, method).",
        },
    ];

    /// <summary>Parameter holding the name <c>CPH.ExecuteMethod</c> finds this code by.</summary>
    internal const string NameParameter = "name";

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var proceed = await engine.RunAsync(context, parameters["code"], ScriptSubActions.GetTimeout(parameters), ct)
            .ConfigureAwait(false);
        return proceed ? SubActionResult.Next : SubActionResult.Stop;
    }
}
