using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cuebot.Core.Scripting;

namespace Cuebot.Scripting;

/// <summary>
/// Gives Python scripts a named method for every script function, so <c>cb.call("vtubestudio.moveModel", positionX=0.5)</c>
/// can be written <c>cb.vtubestudio_move_model(position_x=0.5)</c>. The methods are generated from the registered script
/// functions and their help (src/Cuebot.Scripting/Help/functions, embedded) into cuebot_functions.py, which the worker
/// loads as the base class of <c>cb</c>; the same methods, typed loosely, make the cuebot.pyi stub for editors.
/// </summary>
/// <remarks>
/// A function "area.someName" becomes "area_some_name" and a value "someValue" becomes "some_value". Values whose help
/// starts with "Optional" are keyword-only and default to None; a None value is left out of the call, so the function
/// uses its own default. The others are required and can also be given by position, in the help's order. A method
/// that would hide one of the hand-written helpers (<see cref="HandWritten"/>), or a second function with the same
/// Python name, is skipped and reported in <see cref="PythonFunctionLibrary.Skipped"/>.
/// </remarks>
internal sealed partial class PythonFunctionLibrary
{
    public const string ModuleFile = "cuebot_functions.py";
    public const string StubFile = "cuebot.pyi";
    private const string HelpResourcePrefix = "Cuebot.Scripting.Help.functions.";

    private PythonFunctionLibrary(IReadOnlyList<Method> methods, IReadOnlyList<string> skipped)
    {
        Methods = methods;
        Skipped = skipped;
    }

    /// <summary>The generated methods, in function-name order.</summary>
    public IReadOnlyList<Method> Methods { get; }

    /// <summary>Script functions without a method, with the reason ("log: same name as the hand-written cb.log").</summary>
    public IReadOnlyList<string> Skipped { get; }

    public sealed record Parameter(string Name, string PythonName, bool Required, string Help);

    public sealed record Method(string Function, string Name, string Description, string Returns, IReadOnlyList<Parameter> Parameters);

    /// <summary>The hand-written members of the worker's Cuebot class (cuebot_worker.py), as they appear in the stub.</summary>
    internal const string HandWrittenStub = """"
            @property
            def args(self) -> dict[str, Any]:
                """A snapshot of the action's arguments when the script started (updated by set_arg)."""
            def call(self, function: str, /, **params: Any) -> Any:
                """Calls any Cuebot script function by name, like cb.call("speech.speak", text="Hi")."""
            def get_arg(self, name: str, default: Any = None) -> Any:
                """One of the action's arguments, or default when it is not there."""
            def set_arg(self, name: str, value: Any) -> None:
                """Sets an argument; later steps can use it as %name%."""
            def get_global(self, name: str, default: Any = None) -> Any:
                """Reads a global variable, or default when it does not exist."""
            def set_global(self, name: str, value: Any, persisted: bool = True) -> None:
                """Saves a global variable (as text)."""
            def log(self, message: Any, level: str = "Info") -> None:
                """Writes a line in Cuebot's log."""
            def run_action(self, name: str) -> bool:
                """Runs another action and waits for it; returns whether it succeeded."""
            def send_chat(self, message: Any) -> None:
                """Sends a message in your Twitch chat."""
            def speak(self, text: Any, alias: str | None = None) -> None:
                """Says the text with text-to-speech."""
            def obs(self, request_type: str, data: Any = None) -> Any:
                """Sends an obs-websocket request and returns its response data."""
        """";

    /// <summary>Names generated methods must not take: the hand-written helpers of <c>cb</c>.</summary>
    public static readonly IReadOnlySet<string> HandWritten =
        DefinedName().Matches(HandWrittenStub).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> Keywords =
    [
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else",
        "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise",
        "return", "try", "while", "with", "yield", "self",
    ];

    /// <summary>Builds the methods for the given functions, using the embedded help.</summary>
    public static PythonFunctionLibrary Build(IEnumerable<ScriptFunction> functions) => Build(functions, EmbeddedHelp.Value);

    /// <summary>Builds the methods for the given functions; <paramref name="help"/> maps a function name to its help entry.</summary>
    public static PythonFunctionLibrary Build(IEnumerable<ScriptFunction> functions, IReadOnlyDictionary<string, JsonObject> help)
    {
        var methods = new List<Method>();
        var skipped = new List<string>();
        var taken = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var function in functions.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!FunctionName().IsMatch(function.Name)) { skipped.Add($"{function.Name}: not a name Python can call"); continue; }
            var name = MethodName(function.Name);
            if (HandWritten.Contains(name)) { skipped.Add($"{function.Name}: same name as the hand-written cb.{name}"); continue; }
            if (taken.TryGetValue(name, out var other)) { skipped.Add($"{function.Name}: cb.{name} is already {other}"); continue; }
            taken[name] = function.Name;

            var entry = help.GetValueOrDefault(function.Name);
            var parameterHelp = entry?["parameters"] as JsonObject;
            var used = new HashSet<string>(StringComparer.Ordinal);
            var parameters = new List<Parameter>();
            foreach (var parameter in function.ParameterNames)
            {
                var text = Text(parameterHelp?[parameter]);
                var pythonName = ParameterName(parameter);
                for (var n = 2; !used.Add(pythonName); n++) pythonName = $"{ParameterName(parameter)}_{n}";
                // Without help nothing says a value is needed, so every value is optional.
                var required = text.Length > 0 && !text.StartsWith("Optional", StringComparison.OrdinalIgnoreCase);
                parameters.Add(new Parameter(parameter, pythonName, required, text));
            }
            // Required values come first, in their own order, so they can be given by position.
            parameters = [.. parameters.Where(p => p.Required), .. parameters.Where(p => !p.Required)];
            var description = Text(entry?["description"]) is { Length: > 0 } d ? d : function.Description;
            methods.Add(new Method(function.Name, name, description, Text(entry?["returns"]), parameters));
        }
        return new PythonFunctionLibrary(methods, skipped);
    }

    /// <summary>"vtubestudio.moveModel" → "vtubestudio_move_model".</summary>
    public static string MethodName(string function)
    {
        var name = string.Join('_', function.Split('.').Select(Snake));
        return Keywords.Contains(name) ? name + "_" : name;
    }

    /// <summary>"positionX" → "position_x"; Python keywords and "self" get a trailing underscore ("from" → "from_").</summary>
    public static string ParameterName(string name)
    {
        var snake = Snake(name);
        if (snake.Length == 0 || char.IsAsciiDigit(snake[0])) snake = "value_" + snake;
        return Keywords.Contains(snake) ? snake + "_" : snake;
    }

    /// <summary>camelCase, PascalCase and acronyms to snake_case ("userID" → "user_id", "HTTPServer" → "http_server").</summary>
    private static string Snake(string name)
    {
        var words = NotIdentifier().Replace(name, "_");
        words = AcronymEnd().Replace(words, "$1_$2");
        words = LowerToUpper().Replace(words, "$1_$2");
        return Underscores().Replace(words, "_").Trim('_').ToLowerInvariant();
    }

    /// <summary>The cuebot_functions.py module: a CuebotFunctions class the worker's Cuebot class derives from.</summary>
    public string Module()
    {
        var sb = new StringBuilder();
        sb.Append("\"\"\"Named Cuebot script functions for Python scripts. Cuebot writes this file each time Python starts; do not edit it.\"\"\"\n\n\n");
        sb.Append("class CuebotFunctions:\n");
        sb.Append("    \"\"\"One method per Cuebot script function, like cb.vtubestudio_move_model(position_x=0.5).\"\"\"\n");
        sb.Append("\n    def _call_named(self, function, params):\n");
        sb.Append("        return self.call(function, **{name: value for name, value in params.items() if value is not None})\n");
        foreach (var method in Methods)
        {
            sb.Append($"\n    def {method.Name}({Signature(method, typed: false)}):\n");
            sb.Append(Docstring(method));
            var values = string.Join(", ", method.Parameters.Select(p => $"\"{Escape(p.Name)}\": {p.PythonName}"));
            sb.Append($"        return self._call_named(\"{method.Function}\", {{{values}}})\n");
        }
        return sb.ToString();
    }

    /// <summary>A stub of the whole <c>cb</c> object, hand-written helpers included, for code editors.</summary>
    public string Stub()
    {
        var sb = new StringBuilder();
        sb.Append("# Cuebot's `cb` object for Python scripts, for code editors: put this file next to your script to get\n");
        sb.Append("# completion and help for every Cuebot function. Cuebot itself does not need it.\n");
        sb.Append("from typing import Any\n\n\n");
        sb.Append("class CuebotError(Exception):\n    \"\"\"Raised when a Cuebot function fails.\"\"\"\n\n\n");
        sb.Append("class Cuebot:\n");
        // The source file has CRLF line endings in a Windows checkout; the stub always uses LF.
        sb.Append(HandWrittenStub.ReplaceLineEndings("\n")).Append('\n');
        foreach (var method in Methods)
        {
            sb.Append($"    def {method.Name}({Signature(method, typed: true)}) -> Any:\n");
            sb.Append(Docstring(method));
        }
        sb.Append("\n\ncb: Cuebot\n");
        return sb.ToString();
    }

    private static string Signature(Method method, bool typed)
    {
        var parts = new List<string> { "self" };
        parts.AddRange(method.Parameters.Where(p => p.Required).Select(p => typed ? $"{p.PythonName}: Any" : p.PythonName));
        var optional = method.Parameters.Where(p => !p.Required).ToArray();
        if (optional.Length > 0)
        {
            parts.Add("*");
            parts.AddRange(optional.Select(p => typed ? $"{p.PythonName}: Any = None" : $"{p.PythonName}=None"));
        }
        return string.Join(", ", parts);
    }

    private static string Docstring(Method method)
    {
        var lines = new List<string> { method.Description, "" };
        lines.AddRange(method.Parameters.Select(p => $"{p.PythonName}: {(p.Help.Length > 0 ? p.Help : "Optional.")}"));
        if (method.Returns.Length > 0) lines.Add($"Gives back: {method.Returns}");
        lines.Add($"Same as cb.call(\"{method.Function}\", ...).");
        var body = string.Join("\n", lines.Select(l => l.Length == 0 ? "" : "        " + Escape(l)));
        return $"        \"\"\"{body.TrimStart()}\n        \"\"\"\n";
    }

    /// <summary>Makes text safe inside a triple-quoted Python string.</summary>
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");

    private static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";

    private static readonly Lazy<IReadOnlyDictionary<string, JsonObject>> EmbeddedHelp = new(ReadEmbeddedHelp);

    private static Dictionary<string, JsonObject> ReadEmbeddedHelp()
    {
        var help = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(PythonFunctionLibrary).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(HelpResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            // Every help file is {"functions": {name: entry}} (DeveloperReferenceTests reads them the same way).
            foreach (var (name, entry) in JsonNode.Parse(stream)!["functions"]!.AsObject())
                help[name] = entry!.AsObject();
        }
        return help;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)*$")]
    private static partial Regex FunctionName();

    [GeneratedRegex(@"def (\w+)\(")]
    private static partial Regex DefinedName();

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex NotIdentifier();

    [GeneratedRegex(@"([A-Z]+)([A-Z][a-z])")]
    private static partial Regex AcronymEnd();

    [GeneratedRegex(@"([a-z0-9])([A-Z])")]
    private static partial Regex LowerToUpper();

    [GeneratedRegex(@"_+")]
    private static partial Regex Underscores();
}
