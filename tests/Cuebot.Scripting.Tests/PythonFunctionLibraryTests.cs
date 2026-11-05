using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cuebot.Core.Scripting;

namespace Cuebot.Scripting.Tests;

/// <summary>The named Python methods generated from script functions (PythonFunctionLibrary).</summary>
public sealed class PythonFunctionLibraryTests
{
    private static ScriptFunction Function(string name, params string[] parameters) =>
        new(name, $"{name} from code", parameters, (_, _, _) => Task.FromResult<object?>(null));

    private static Dictionary<string, JsonObject> Help(string json) =>
        JsonNode.Parse(json)!.AsObject().ToDictionary(p => p.Key, p => p.Value!.AsObject(), StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("vtubestudio.moveModel", "vtubestudio_move_model")]
    [InlineData("obs.getSceneItemID", "obs_get_scene_item_id")]
    [InlineData("twitch.getUserInfoById", "twitch_get_user_info_by_id")]
    [InlineData("web.HTTPRequest", "web_http_request")]
    [InlineData("obs.scene.setCurrent", "obs_scene_set_current")]
    [InlineData("midi.sendCC2", "midi_send_cc2")]
    [InlineData("tools.already_snake", "tools_already_snake")]
    [InlineData("import", "import_")]
    public void Function_names_become_snake_case_method_names(string function, string method) =>
        Assert.Equal(method, PythonFunctionLibrary.MethodName(function));

    [Theory]
    [InlineData("positionX", "position_x")]
    [InlineData("userID", "user_id")]
    [InlineData("HTTPServer", "http_server")]
    [InlineData("data1", "data1")]
    [InlineData("from", "from_")]
    [InlineData("self", "self_")]
    [InlineData("class", "class_")]
    [InlineData("a-b c", "a_b_c")]
    [InlineData("2fa", "value_2fa")]
    [InlineData("--", "value_")]
    public void Value_names_become_valid_python_parameter_names(string name, string python) =>
        Assert.Equal(python, PythonFunctionLibrary.ParameterName(name));

    [Fact]
    public void Values_marked_optional_are_keyword_only_and_required_ones_come_first()
    {
        var help = Help("""
            {"twitch.replyToMessage": {
               "description": "Replies to a message.",
               "parameters": {"messageId": "Optional. Which message.", "message": "What to say.", "sendAs": "optional: who sends it."},
               "returns": "Nothing."}}
            """);

        var method = Assert.Single(PythonFunctionLibrary.Build([Function("twitch.replyToMessage", "messageId", "message", "sendAs")], help).Methods);

        Assert.Equal("twitch_reply_to_message", method.Name);
        Assert.Equal("Replies to a message.", method.Description);
        Assert.Equal("Nothing.", method.Returns);
        Assert.Equal(["message", "message_id", "send_as"], method.Parameters.Select(p => p.PythonName));
        Assert.Equal([true, false, false], method.Parameters.Select(p => p.Required));
        Assert.Equal(["message", "messageId", "sendAs"], method.Parameters.Select(p => p.Name));
    }

    [Fact]
    public void Without_help_every_value_is_optional_and_the_code_description_is_used()
    {
        var method = Assert.Single(PythonFunctionLibrary.Build([Function("custom.doThing", "first", "second")], new Dictionary<string, JsonObject>()).Methods);

        Assert.Equal("custom.doThing from code", method.Description);
        Assert.Equal("", method.Returns);
        Assert.All(method.Parameters, p => Assert.False(p.Required));
        Assert.All(method.Parameters, p => Assert.Equal("", p.Help));
    }

    [Fact]
    public void Clashing_names_are_skipped_or_numbered()
    {
        var library = PythonFunctionLibrary.Build(
        [
            Function("log", "message"),
            Function("custom.fooBar", "userId", "user_id"),
            Function("custom.foo_bar"),
            Function("bad name!"),
        ], new Dictionary<string, JsonObject>());

        var method = Assert.Single(library.Methods);
        Assert.Equal("custom.fooBar", method.Function);
        Assert.Equal(["user_id", "user_id_2"], method.Parameters.Select(p => p.PythonName));
        Assert.Equal(
        [
            "bad name!: not a name Python can call",
            "custom.foo_bar: cb.custom_foo_bar is already custom.fooBar",
            "log: same name as the hand-written cb.log",
        ], library.Skipped);
    }

    [Fact]
    public void Embedded_help_describes_the_functions()
    {
        var method = Assert.Single(PythonFunctionLibrary.Build([Function("vtubestudio.moveModel", "time", "relative", "positionX", "positionY", "rotation", "size")]).Methods);

        Assert.StartsWith("Moves, turns or resizes your model", method.Description);
        Assert.All(method.Parameters, p => Assert.StartsWith("Optional", p.Help));
        Assert.All(method.Parameters, p => Assert.False(p.Required));
    }

    /// <summary>The generator must not hide a helper the worker has, and must know about every one.</summary>
    [Fact]
    public void Hand_written_helpers_match_the_worker()
    {
        var worker = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cuebot.Scripting", "Python", "cuebot_worker.py"));
        var start = worker.IndexOf("\nclass Cuebot(", StringComparison.Ordinal);
        var end = worker.IndexOf("\nclass ", start + 1, StringComparison.Ordinal);
        var defined = Regex.Matches(worker[start..end], @"\n    def ([a-z]\w*)\(").Select(m => m.Groups[1].Value).ToHashSet();

        Assert.Equal(defined.Order(), PythonFunctionLibrary.HandWritten.Order());
    }

    [Fact]
    public void Module_and_stub_spell_out_each_method()
    {
        var help = Help("""
            {"custom.say": {"description": "Says it.", "parameters": {"text": "What to say.", "voice": "Optional. Which voice."}, "returns": "true."}}
            """);
        var library = PythonFunctionLibrary.Build([Function("custom.say", "text", "voice"), Function("custom.ping")], help);

        var module = library.Module();
        Assert.Contains("    def custom_say(self, text, *, voice=None):\n", module);
        Assert.Contains("        return self._call_named(\"custom.say\", {\"text\": text, \"voice\": voice})\n", module);
        Assert.Contains("    def custom_ping(self):\n", module);
        Assert.Contains("        return self._call_named(\"custom.ping\", {})\n", module);

        var stub = library.Stub();
        Assert.Contains("    def custom_say(self, text: Any, *, voice: Any = None) -> Any:\n", stub);
        Assert.Contains("    def custom_ping(self) -> Any:\n", stub);
        Assert.Contains("    def obs(self, request_type: str, data: Any = None) -> Any:\n", stub);
        Assert.EndsWith("\n\ncb: Cuebot\n", stub);
    }

    /// <summary>Runs the generated module and stub in Python: they parse, and help text with quotes and backslashes survives.</summary>
    [PythonFact]
    public void Generated_code_is_valid_python_with_intact_help()
    {
        var help = Help("""
            {"custom.quote": {"description": "Says \"\"\"hi\"\"\" and C:\\path\\ \r\nover two lines\\",
                              "parameters": {"from": "The \"sender\".", "data1": "Optional."}}}
            """);
        var library = PythonFunctionLibrary.Build([Function("custom.quote", "from", "data1")], help);
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, PythonFunctionLibrary.ModuleFile), library.Module());
        File.WriteAllText(Path.Combine(dir.Path, "cuebot.pyi"), library.Stub());

        var output = TestPython.Run(dir.Path, """
            import ast, json, sys
            sys.path.insert(0, ".")
            ast.parse(open("cuebot.pyi", encoding="utf-8").read())
            from cuebot_functions import CuebotFunctions

            class Fake(CuebotFunctions):
                def call(self, function, /, **params):
                    return [function, params]

            fake = Fake()
            print(json.dumps({
                "doc": fake.custom_quote.__doc__,
                "all": fake.custom_quote("ann", data1=0),
                "positional": fake.custom_quote("ann"),
                "none": fake.custom_quote(from_=None),
            }))
            """);

        var result = JsonNode.Parse(output)!;
        var doc = result["doc"]!.GetValue<string>();
        Assert.StartsWith("Says \"\"\"hi\"\"\" and C:\\path\\  over two lines\\\n", doc);
        Assert.Contains("from_: The \"sender\".", doc);
        Assert.Contains("data1: Optional.", doc);
        Assert.Contains("Same as cb.call(\"custom.quote\", ...).", doc);
        Assert.Equal("""["custom.quote",{"from":"ann","data1":0}]""", result["all"]!.ToJsonString());
        Assert.Equal("""["custom.quote",{"from":"ann"}]""", result["positional"]!.ToJsonString());
        Assert.Equal("""["custom.quote",{}]""", result["none"]!.ToJsonString());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cuebot.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cuebot.slnx not found");
    }
}
