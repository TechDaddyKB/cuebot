using Cuebot.Core.Actions;

namespace Cuebot.Scripting;

/// <summary>"Execute Python code": runs a Python script body with <c>cb</c>. Returning False stops the action.</summary>
public sealed class PythonCodeSubAction(PythonScriptHost host) : ISubActionHandler
{
    internal const string DefaultCode =
        """
        # cb: Cuebot helpers, e.g. cb.get_arg("user"), cb.set_arg(...), cb.log(...), cb.send_chat(...), cb.call("name", key=value).
        # print() output goes to the log. Return False to stop the action.
        user = cb.get_arg("user", "someone")
        cb.log(f"Hello from Python, {user}!")
        cb.set_arg("greeting", f"Hi {user}")
        return True
        """;

    public string TypeId => "code.python";
    public string Category => ScriptSubActions.Category;
    public string DisplayName => "Execute Python code";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("code", "Code", ParameterKind.Code)
        {
            Language = "python",
            DefaultValue = DefaultCode,
            Help = "Script body using cb. Install Python and packages from the Scripting page. Scripts run with full access to your computer; only use code you trust.",
        },
        ScriptSubActions.TimeoutParameter,
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var proceed = await host.RunAsync(context, parameters["code"], ScriptSubActions.GetTimeout(parameters), ct)
            .ConfigureAwait(false);
        return proceed ? SubActionResult.Next : SubActionResult.Stop;
    }
}
