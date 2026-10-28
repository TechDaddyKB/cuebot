using Cuebot.Core.Actions;

namespace Cuebot.Scripting;

/// <summary>Parameters shared by the script sub-actions.</summary>
internal static class ScriptSubActions
{
    public const string Category = "Code";

    public static readonly ParameterDescriptor TimeoutParameter = new("timeoutSeconds", "Timeout (seconds)", ParameterKind.Number)
    {
        DefaultValue = "30",
        Help = "Stop the script after this many seconds (0 = no limit)",
    };

    /// <summary>The configured timeout, or null for no limit.</summary>
    public static TimeSpan? GetTimeout(ParameterValues parameters)
    {
        var seconds = parameters.GetDouble(TimeoutParameter.Key, 30);
        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    public static readonly ParameterDescriptor SaveResultParameter = new("saveResult", "Save result to variable", ParameterKind.Boolean)
    {
        DefaultValue = "false",
        Help = "Store the bool result in an argument and always continue the action (by default a false result stops it)",
    };

    public static readonly ParameterDescriptor ResultVariableParameter = new("resultVariable", "Variable name")
    {
        DefaultValue = "",
        Help = "Argument that receives the result when \"Save result to variable\" is on",
    };

    /// <summary>
    /// Applies Streamer.bot's "Save Result to Variable": with it on, the result goes into the named argument and the
    /// action continues whatever it is; otherwise false stops the action.
    /// </summary>
    public static SubActionResult Result(ActionRunContext context, ParameterValues parameters, bool result)
    {
        if (!parameters.GetBool(SaveResultParameter.Key)) return result ? SubActionResult.Next : SubActionResult.Stop;
        var name = parameters[ResultVariableParameter.Key].Trim().Trim('%');
        if (name.Length > 0) context.Args[name] = result;
        return SubActionResult.Next;
    }
}
