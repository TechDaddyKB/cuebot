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
}
