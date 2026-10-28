using System.Net;
using System.Net.Sockets;
using System.Text;
using Cuebot.Core.BuiltIn;

namespace Cuebot.Tests.SystemTools;

/// <summary>Read Lines / Specific Line / Random Lines (Count), UDP Broadcast, Run a Program capture, Fetch URL JSON.</summary>
public sealed class StreamerBotSystemStepTests : IDisposable
{
    private readonly HttpRequestSubActionTests.FakeHandler _http = new();
    private readonly SystemTestHarness _h;

    public StreamerBotSystemStepTests() => _h = new SystemTestHarness(
        new ReadLinesSubAction(), new ReadSpecificLineSubAction(), new ReadRandomLineSubAction(new Random(3)),
        new UdpBroadcastSubAction(), new RunProgramSubAction(), new HttpRequestSubAction(new HttpClient(_http)));

    public void Dispose() => _h.Dispose();

    private string Lines(params string[] lines)
    {
        var path = _h.PathOf("lines.txt");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public async Task Read_lines_sets_numbered_lines_count_and_file_found()
    {
        var path = Lines("hello %who%", "42");
        var args = await _h.RunAsync("core.readLines", new Dictionary<string, object?> { ["who"] = "world" },
            ("path", path), ("parseVariables", "true"), ("autoType", "true"));
        Assert.Equal(("hello world", 2, true), (args!["line0"], args["lineCount"], args["fileFound"]));
        Assert.Equal(42L, Convert.ToInt64(args["line1"]));
        Assert.IsNotType<string>(args["line1"]);

        var plain = await _h.RunAsync("core.readLines", ("path", path), ("variable", "row"));
        Assert.Equal("hello %who%", plain!["row0"]);

        var missing = await _h.RunAsync("core.readLines", ("path", _h.PathOf("nope.txt")));
        Assert.Equal((false, 0), (missing!["fileFound"], missing["lineCount"]));
    }

    [Fact]
    public async Task Read_specific_line_counts_from_one()
    {
        var path = Lines("a", "b", "c");
        Assert.Equal("b", (await _h.RunAsync("core.readSpecificLine", ("path", path), ("lineNumber", "2")))!["line"]);
        Assert.Equal("", (await _h.RunAsync("core.readSpecificLine", ("path", path), ("lineNumber", "9")))!["line"]);
        var missing = await _h.RunAsync("core.readSpecificLine", ("path", _h.PathOf("none.txt")));
        Assert.Equal((false, ""), (missing!["fileFound"], missing["line"]));
    }

    [Fact]
    public async Task Read_random_lines_with_count_picks_distinct_lines()
    {
        var path = Lines("a", "b", "c", "d");
        var args = await _h.RunAsync("core.readRandomLine", ("path", path), ("count", "3"));
        var picked = new[] { args!["randomLine"], args["randomLine1"], args["randomLine2"] }.Cast<string>().ToArray();
        Assert.Equal(3, picked.Distinct().Count());
        var numbers = new[] { args["randomLineNumber"], args["randomLineNumber1"], args["randomLineNumber2"] }.Cast<int>().ToArray();
        Assert.All(numbers, n => Assert.InRange(n, 0, 3));
        Assert.Equal(picked[0], new[] { "a", "b", "c", "d" }[numbers[0]]);
        Assert.False(args.ContainsKey("randomLine3"));

        var missing = await _h.RunAsync("core.readRandomLine", ("path", _h.PathOf("none.txt")));
        Assert.Equal((false, ""), (missing!["fileFound"], missing["randomLine"]));
    }

    [Fact]
    public async Task Udp_broadcast_sends_the_payload_to_the_port()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        var receive = listener.ReceiveAsync();
        var args = await _h.RunAsync("core.udpBroadcast", ("host", "127.0.0.1"), ("port", port.ToString()), ("payload", "{\"command\":\"speak\"}"));
        var got = await receive.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("{\"command\":\"speak\"}", Encoding.UTF8.GetString(got.Buffer));
        Assert.Equal(19, args!["udpBytesSent"]);
        Assert.Null(await _h.RunAsync("core.udpBroadcast", ("port", "0")));
    }

    [Fact]
    public async Task Run_program_passes_environment_and_captures_output_lines()
    {
        var (program, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c echo %CUEBOT_TEST%& echo second")
            : ("/bin/sh", "-c \"echo $CUEBOT_TEST; echo second\"");
        var args = await _h.RunAsync("core.runProgram", ("path", program), ("arguments", arguments), ("wait", "true"),
            ("captureOutput", "true"), ("environment", "CUEBOT_TEST=from env"), ("waitSeconds", "30"));
        Assert.Equal((2, "from env", "second", 0, false), (args!["outputCount"], args["output1"], args["output2"], args["exitCode"], args["timedOut"]));
    }

    [Fact]
    public async Task Fetch_url_can_parse_the_result_as_json()
    {
        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"fact":"cats","length":4,"tags":["a","b"],"meta":{"ok":true}}"""),
        };
        var args = await _h.RunAsync("core.httpRequest", ("url", "https://catfact.example.test/fact"), ("variable", "jsonResult"), ("parseJson", "true"));
        Assert.Equal(("cats", 4L, "b", true, true), (args!["jsonResult.fact"], args["jsonResult.length"], args["jsonResult.tags[1]"],
            args["jsonResult.meta.ok"], args["httpJsonParsed"]));

        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") };
        var bad = await _h.RunAsync("core.httpRequest", ("url", "https://catfact.example.test/fact"), ("variable", "jsonResult"), ("parseJson", "true"));
        Assert.Equal(("not json", false), (bad!["jsonResult"], bad["httpJsonParsed"]));
    }
}
