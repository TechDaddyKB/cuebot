namespace Cuebot.Scripting;

/// <summary>Scripting module settings, stored in scripting.json.</summary>
public sealed class ScriptingSettings
{
    /// <summary>
    /// Path to an existing Python interpreter to use instead of the managed runtime. When set, Cuebot does not
    /// download Python or create a virtual environment; packages are installed into that interpreter.
    /// </summary>
    public string CustomPythonPath { get; set; } = "";

    /// <summary>
    /// Extra .NET DLLs every C# code is compiled with (Streamer.bot's C# compiler references), as full paths. Only
    /// these files are loaded; nothing is downloaded. Each "Execute C# code" step can list more of its own.
    /// </summary>
    public List<string> CSharpReferences { get; set; } = [];
}
