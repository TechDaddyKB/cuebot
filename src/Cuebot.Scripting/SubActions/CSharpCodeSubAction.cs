using Cuebot.Core.Actions;

namespace Cuebot.Scripting;

/// <summary>
/// "Execute C# code": runs a C# script body with <c>Args</c> and <c>Cb</c> (or a Streamer.bot <c>CPHInline</c>
/// class). Returning false stops the action, unless the result is saved to a variable.
/// </summary>
public sealed class CSharpCodeSubAction(CSharpScriptEngine engine) : ISubActionHandler
{
    public const string Id = "code.csharp";

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

    /// <summary>Parameter holding the name <c>CPH.ExecuteMethod</c> finds this code by.</summary>
    internal const string NameParameter = "name";

    internal const string PrecompileParameter = "precompile";
    internal const string DelayedStartParameter = "delayedStart";
    internal const string ReferencesParameter = "references";

    public string TypeId => Id;
    public string Category => ScriptSubActions.Category;
    public string DisplayName => "Run C# code";

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
            Help = "Optional. Lets other code call a public method of this code's CPHInline class with CPH.ExecuteMethod(name, method) or the \"Execute C# method\" sub-action.",
        },
        new("description", "Description", ParameterKind.Multiline) { DefaultValue = "", Help = "Optional notes about what this code does" },
        new(PrecompileParameter, "Precompile on application start", ParameterKind.Boolean)
        {
            DefaultValue = "false",
            Help = "Compile when Cuebot starts instead of on the first run; a CPHInline class's Init() then runs at startup. Only for enabled actions.",
        },
        new(DelayedStartParameter, "Delayed start", ParameterKind.Boolean)
        {
            DefaultValue = "false",
            Help = "With precompile: compile after Cuebot has finished starting (platforms connected) instead of during startup",
        },
        ScriptSubActions.SaveResultParameter,
        ScriptSubActions.ResultVariableParameter,
        new(ReferencesParameter, "References", ParameterKind.Multiline)
        {
            DefaultValue = "",
            Help = "Extra .NET DLLs this code uses, one full path per line. Framework assemblies and Newtonsoft.Json are always available. Only files you list are loaded.",
        },
    ];

    /// <summary>The custom DLLs a stored C# step lists.</summary>
    internal static IReadOnlyList<string> References(SubActionDefinition step) =>
        CSharpReferences.Parse(step.Parameters.GetValueOrDefault(ReferencesParameter));

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var proceed = await engine.RunAsync(context, parameters["code"], ScriptSubActions.GetTimeout(parameters), ct,
            CSharpReferences.Parse(parameters[ReferencesParameter])).ConfigureAwait(false);
        return ScriptSubActions.Result(context, parameters, proceed);
    }
}

/// <summary>
/// "Execute C# method": calls a public parameterless method (or <c>Execute</c>) of the CPHInline class in another
/// "Execute C# code" step, found by its code name (else by its action's name). Like Streamer.bot's sub-action, a
/// false result stops the action unless it is saved to a variable.
/// </summary>
public sealed class CSharpMethodSubAction(CSharpScriptEngine engine, Func<IEnumerable<string>>? codeNames = null) : ISubActionHandler
{
    public const string Id = "code.csharpMethod";

    public string TypeId => Id;
    public string Category => ScriptSubActions.Category;
    public string DisplayName => "Run C# method";

    public IReadOnlyList<ParameterDescriptor> Parameters =>
    [
        new("codeName", "C# code")
        {
            Suggestions = codeNames,
            Help = "The \"Code name\" of an \"Execute C# code\" step with a CPHInline class (or the name of its action)",
        },
        new("method", "Method") { DefaultValue = "Execute", Help = "A public method without parameters returning bool or void" },
        ScriptSubActions.TimeoutParameter,
        ScriptSubActions.SaveResultParameter,
        ScriptSubActions.ResultVariableParameter,
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var codeName = parameters["codeName"].Trim();
        var method = parameters["method"].Trim();
        if (codeName.Length == 0) throw new ArgumentException("No C# code is chosen.");
        if (method.Length == 0) throw new ArgumentException("No method is named.");
        var proceed = await engine.RunMethodAsync(context, codeName, method, ScriptSubActions.GetTimeout(parameters), ct)
            .ConfigureAwait(false);
        return ScriptSubActions.Result(context, parameters, proceed);
    }
}
