using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.App;
using Cuebot.Core;
using Cuebot.Core.Scripting;
using Cuebot.Scripting;
using Cuebot.Scripting.Tests;

namespace Cuebot.UiTests;

/// <summary>
/// Python's named methods (cb.vtubestudio_move_model(...)) for every script function of the real app: each one is
/// generated, and calling it sends the function's name and values exactly as cb.call would.
/// </summary>
public sealed class PythonNamedFunctionsTests : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-pyfunctions-").FullName;
    private readonly AppHost _host;

    public PythonNamedFunctionsTests() => _host = AppHost.Create(new AppPaths(_dataDir));

    private IReadOnlyList<ScriptFunction> Functions => _host.Get<ScriptApi>().Functions;

    [Fact]
    public void Every_script_function_has_a_named_python_method()
    {
        var library = PythonFunctionLibrary.Build(Functions);

        // cb.log is hand-written and is the "log" function; nothing else may be left out.
        Assert.Equal(["log: same name as the hand-written cb.log"], library.Skipped);
        Assert.Equal(Functions.Count - 1, library.Methods.Count);
    }

    /// <summary>
    /// Calls every method twice, with every value and with only the required ones, against a recording cb.call, and
    /// checks that every statement of the generated module ran.
    /// </summary>
    [PythonFact]
    public void Every_named_method_sends_its_function_and_values()
    {
        var library = PythonFunctionLibrary.Build(Functions);
        File.WriteAllText(Path.Combine(_dataDir, PythonFunctionLibrary.ModuleFile), library.Module());
        var plan = new JsonArray(library.Methods.Select(m => (JsonNode)new JsonObject
        {
            ["method"] = m.Name,
            ["required"] = new JsonArray(m.Parameters.Where(p => p.Required).Select(p => (JsonNode)p.PythonName).ToArray()),
            ["optional"] = new JsonArray(m.Parameters.Where(p => !p.Required).Select(p => (JsonNode)p.PythonName).ToArray()),
        }).ToArray());
        File.WriteAllText(Path.Combine(_dataDir, "plan.json"), plan.ToJsonString());

        var output = TestPython.Run(_dataDir, """
            import ast, json, os, sys
            sys.path.insert(0, ".")
            module_path = os.path.abspath("cuebot_functions.py")
            ran = set()

            def tracer(frame, event, arg):
                if frame.f_code.co_filename == module_path:
                    if event == "line":
                        ran.add(frame.f_lineno)
                    return tracer
                return None

            sys.settrace(tracer)
            from cuebot_functions import CuebotFunctions

            class Recorder(CuebotFunctions):
                def call(self, function, /, **params):
                    return {"function": function, "params": params}

            cb = Recorder()
            results = []
            for step in json.load(open("plan.json", encoding="utf-8")):
                method = getattr(cb, step["method"])
                positional = [f"{step['method']}:{name}" for name in step["required"]]
                keywords = {name: f"{step['method']}:{name}" for name in step["optional"]}
                results.append({"all": method(*positional, **keywords), "required": method(*positional)})
            sys.settrace(None)

            # Every statement of the module ran (docstrings are not executed, so they do not count).
            def docstring(node):
                return isinstance(node, ast.Expr) and isinstance(node.value, ast.Constant) and isinstance(node.value.value, str)
            tree = ast.parse(open(module_path, encoding="utf-8").read())
            statements = {node.lineno for node in ast.walk(tree) if isinstance(node, ast.stmt) and not docstring(node)}
            print(json.dumps({"results": results, "statements": len(statements), "missed": sorted(statements - ran)}))
            """);

        var report = JsonNode.Parse(output)!;
        Assert.Equal("[]", report["missed"]!.ToJsonString());
        Assert.True(report["statements"]!.GetValue<int>() > 2 * library.Methods.Count);
        var results = report["results"]!.AsArray();
        Assert.Equal(library.Methods.Count, results.Count);
        var problems = new List<string>();
        foreach (var (method, result) in library.Methods.Zip(results))
        {
            var all = method.Parameters.ToDictionary(p => p.Name, p => $"{method.Name}:{p.PythonName}");
            var required = method.Parameters.Where(p => p.Required).ToDictionary(p => p.Name, p => $"{method.Name}:{p.PythonName}");
            Check("all", all);
            Check("required", required);

            void Check(string kind, Dictionary<string, string> expected)
            {
                var call = result![kind]!;
                var sent = call["params"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());
                if (call["function"]!.GetValue<string>() != method.Function)
                    problems.Add($"{method.Name} ({kind}): called {call["function"]}, not {method.Function}");
                if (!sent.OrderBy(p => p.Key).SequenceEqual(expected.OrderBy(p => p.Key)))
                    problems.Add($"{method.Name} ({kind}): sent {JsonSerializer.Serialize(sent)}, expected {JsonSerializer.Serialize(expected)}");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
