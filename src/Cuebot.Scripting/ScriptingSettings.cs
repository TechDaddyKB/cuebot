namespace Cuebot.Scripting;

/// <summary>Scripting module settings, stored in scripting.json.</summary>
public sealed class ScriptingSettings
{
    /// <summary>
    /// Path to an existing Python interpreter to use instead of the managed runtime. When set, Cuebot does not
    /// download Python or create a virtual environment; packages are installed into that interpreter.
    /// </summary>
    public string CustomPythonPath { get; set; } = "";
}
