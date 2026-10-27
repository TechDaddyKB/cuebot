using System.Text.Json;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.SystemTools;

namespace Cuebot.Tests.SystemTools;

public sealed class FileAndJsonSubActionTests : IDisposable
{
    private readonly SystemTestHarness _h = new(
        new ReadFileSubAction(), new WriteFileSubAction(), new ReadRandomLineSubAction(new Random(7)),
        new FileExistsSubAction(), new DeleteFileSubAction(), new JsonGetValueSubAction(), new JsonCountSubAction());

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Write_creates_folders_appends_and_overwrites()
    {
        var path = _h.PathOf(Path.Combine("deep", "er", "log.txt"));
        Assert.NotNull(await _h.RunAsync("core.writeFile", ("path", path), ("content", "one")));
        Assert.NotNull(await _h.RunAsync("core.writeFile", ("path", path), ("content", "two"), ("mode", "Append")));
        Assert.Equal($"one{Environment.NewLine}two{Environment.NewLine}", await File.ReadAllTextAsync(path));

        Assert.NotNull(await _h.RunAsync("core.writeFile", ("path", path), ("content", "fresh"), ("mode", "Overwrite"), ("newLine", "false")));
        Assert.Equal("fresh", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Write_without_create_folders_fails_for_a_missing_folder()
    {
        var path = _h.PathOf(Path.Combine("missing", "x.txt"));
        Assert.Null(await _h.RunAsync("core.writeFile", ("path", path), ("content", "x"), ("createFolders", "false")));
    }

    [Fact]
    public async Task Read_file_into_a_named_argument()
    {
        var path = _h.PathOf("in.txt");
        await File.WriteAllTextAsync(path, "héllo\nworld");
        var args = await _h.RunAsync("core.readFile", ("path", path), ("variable", "greeting"));
        Assert.Equal("héllo\nworld", args!["greeting"]);

        Assert.Equal("héllo\nworld", (await _h.RunAsync("core.readFile", ("path", path)))!["fileContent"]);
        Assert.Null(await _h.RunAsync("core.readFile", ("path", _h.PathOf("nope.txt"))));
    }

    [Fact]
    public async Task Random_line_skips_empty_lines()
    {
        var path = _h.PathOf("lines.txt");
        await File.WriteAllTextAsync(path, "alpha\n\n  \nbeta\n");
        var seen = new HashSet<string>();
        for (var i = 0; i < 30; i++)
        {
            var args = await _h.RunAsync("core.readRandomLine", ("path", path));
            seen.Add((string)args!["randomLine"]!);
            Assert.Equal(2, args["lineCount"]);
            Assert.Contains(args["lineNumber"], new object[] { 1, 4 });
        }
        Assert.Equal(["alpha", "beta"], seen.Order());

        await File.WriteAllTextAsync(path, "\n\n");
        var empty = await _h.RunAsync("core.readRandomLine", ("path", path));
        Assert.Equal("", empty!["randomLine"]);
        Assert.Equal(0, empty["lineNumber"]);
    }

    [Fact]
    public async Task File_exists_takes_the_matching_branch()
    {
        var file = _h.PathOf("here.txt");
        await File.WriteAllTextAsync(file, "x");

        Assert.Equal("True", (await _h.RunAsync("core.fileExists", ("path", file)))!["branchTaken"]);
        Assert.Equal("False", (await _h.RunAsync("core.fileExists", ("path", _h.PathOf("gone.txt"))))!["branchTaken"]);
        Assert.Equal("False", (await _h.RunAsync("core.fileExists", ("path", _h.Folder)))!["branchTaken"]);
        Assert.Equal("True", (await _h.RunAsync("core.fileExists", ("path", _h.Folder), ("kind", "Folder")))!["branchTaken"]);
        var either = await _h.RunAsync("core.fileExists", ("path", file), ("kind", "Either"));
        Assert.Equal(true, either!["fileExists"]);
    }

    [Fact]
    public async Task Delete_file_tolerates_missing_files_unless_asked()
    {
        var file = _h.PathOf("bye.txt");
        await File.WriteAllTextAsync(file, "x");
        Assert.Equal(true, (await _h.RunAsync("core.deleteFile", ("path", file)))!["fileDeleted"]);
        Assert.False(File.Exists(file));
        Assert.Equal(false, (await _h.RunAsync("core.deleteFile", ("path", file)))!["fileDeleted"]);
        Assert.Null(await _h.RunAsync("core.deleteFile", ("path", file), ("failIfMissing", "true")));
        Assert.Null(await _h.RunAsync("core.deleteFile", ("path", " ")));
    }

    [Fact]
    public async Task Json_get_value_reads_from_an_argument()
    {
        var args = new Dictionary<string, object?> { ["httpResponse"] = """{"data":[{"name":"Ada","age":36,"ok":true,"tags":["x"]}]}""" };
        async Task<object?> Get(string path) => (await _h.RunAsync("core.jsonGetValue", args, ("path", path)))!["jsonValue"];

        Assert.Equal("Ada", await Get("$.data[0].name"));
        Assert.Equal(36L, await Get("$.data[0].age"));
        Assert.Equal(true, await Get("data[-1].ok"));
        Assert.Equal("""["x"]""", await Get("$.data[0].tags"));
        var missing = await _h.RunAsync("core.jsonGetValue", args, ("path", "$.data[5].name"));
        Assert.Equal("", missing!["jsonValue"]);
        Assert.Equal(false, missing["jsonFound"]);
    }

    [Fact]
    public async Task Json_get_value_reads_text_into_a_custom_argument()
    {
        var args = await _h.RunAsync("core.jsonGetValue", ("argument", ""), ("json", """{"a":{"b c":1.5}}"""),
            ("path", "$.a['b c']"), ("variable", "price"));
        Assert.Equal(1.5, args!["price"]);
        Assert.Equal(true, args["jsonFound"]);
    }

    [Fact]
    public async Task Invalid_json_sets_not_found_and_warns()
    {
        var args = await _h.RunAsync("core.jsonGetValue", ("argument", ""), ("json", "<html>"), ("path", "$.a"));
        Assert.Equal(false, args!["jsonFound"]);
        Assert.Contains(_h.Log.Recent(), e => e.Message.Contains("Not valid JSON"));
    }

    [Fact]
    public async Task Json_count_counts_arrays_and_objects()
    {
        var args = new Dictionary<string, object?> { ["httpResponse"] = """{"items":[1,2,3],"meta":{"a":1,"b":2},"n":5}""" };
        Assert.Equal(3, (await _h.RunAsync("core.jsonCount", args, ("path", "$.items")))!["jsonCount"]);
        Assert.Equal(2, (await _h.RunAsync("core.jsonCount", args, ("path", "$.meta")))!["jsonCount"]);
        var scalar = await _h.RunAsync("core.jsonCount", args, ("path", "$.n"));
        Assert.Equal(0, scalar!["jsonCount"]);
        Assert.Equal(false, scalar["jsonFound"]);
    }

    [Theory]
    [InlineData("$", "")]
    [InlineData("$.a.b[0].c", ".a.b[0].c")]
    [InlineData("a.b", ".a.b")]
    [InlineData("$['x.y'][\"z\"]", ".x.y.z")]
    [InlineData("$['it\\'s']", ".it's")]
    [InlineData("[-1]", "[-1]")]
    public void Parses_json_paths(string path, string expected) =>
        Assert.Equal(expected, string.Concat(JsonPath.Parse(path)));

    [Theory]
    [InlineData("$.")]
    [InlineData("$[abc]")]
    [InlineData("$[1")]
    [InlineData("$['a'")]
    public void Rejects_malformed_json_paths(string path) =>
        Assert.Throws<FormatException>(() => JsonPath.Parse(path));

    [Fact]
    public void Selects_case_insensitively_as_a_fallback()
    {
        using var doc = JsonDocument.Parse("""{"Name":"exact","items":[{"ID":7}]}""");
        Assert.True(JsonPath.TrySelect(doc.RootElement, "$.name", out var name));
        Assert.Equal("exact", name.GetString());
        Assert.True(JsonPath.TrySelect(doc.RootElement, "$.items[0].id", out var id));
        Assert.Equal(7, id.GetInt32());
        Assert.False(JsonPath.TrySelect(doc.RootElement, "$.items.id", out _));
        Assert.False(JsonPath.TrySelect(doc.RootElement, "$.Name[0]", out _));
        Assert.Equal("", JsonPath.ToArgument(JsonDocument.Parse("null").RootElement));
    }
}
