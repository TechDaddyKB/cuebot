using Cuebot.Core.Events;

namespace Cuebot.Remote.Tests;

public class ProtocolTests
{
    [Theory]
    [InlineData("127.0.0.1", 7475, "ws://127.0.0.1:7475/api/v1/remote")]
    [InlineData(" studio-pc ", 9000, "ws://studio-pc:9000/api/v1/remote")]
    [InlineData("wss://cuebot.example.com", 0, "wss://cuebot.example.com/api/v1/remote")]
    [InlineData("https://cuebot.example.com:8443/", 0, "wss://cuebot.example.com:8443/api/v1/remote")]
    [InlineData("ws://10.0.0.5:7475/custom/path", 0, "ws://10.0.0.5:7475/custom/path")]
    public void BuildUri_accepts_hosts_and_urls(string host, int port, string expected) =>
        Assert.Equal(expected, RemoteProtocol.BuildUri(host, port).ToString());

    [Theory]
    [InlineData("", 7475)]
    [InlineData("host", 0)]
    [InlineData("ftp://host", 0)]
    public void BuildUri_rejects_unusable_input(string host, int port) =>
        Assert.Throws<UriFormatException>(() => RemoteProtocol.BuildUri(host, port));

    [Fact]
    public void Hello_and_signal_round_trip()
    {
        var hello = RemoteProtocol.TryParse(RemoteProtocol.Hello(new RemoteInstanceInfo("id-1", "Alpha", "Linux", "0.1.0")))!;
        Assert.Equal("hello", hello.Type);
        Assert.Equal(RemoteProtocol.Version, hello.Protocol);
        Assert.Equal(new RemoteInstanceInfo("id-1", "Alpha", "Linux", "0.1.0"), hello.Instance);

        var signal = RemoteProtocol.TryParse(RemoteProtocol.Signal("go", "My action", new Dictionary<string, object?>
        {
            ["text"] = "hi", ["n"] = 42, ["x"] = 1.5, ["flag"] = false, ["none"] = null, ["when"] = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            ["list"] = new[] { 1, 2 },
        }))!;
        Assert.Equal("signal", signal.Type);
        Assert.Equal("go", signal.Name);
        Assert.Equal("My action", signal.Sender);
        Assert.Equal("hi", signal.Args["text"]);
        Assert.Equal(42L, signal.Args["n"]);
        Assert.Equal(1.5, signal.Args["x"]);
        Assert.Equal(false, signal.Args["flag"]);
        Assert.Null(signal.Args["none"]);
        Assert.Equal("2026-01-02T03:04:05.0000000Z", signal.Args["when"]);
        Assert.IsType<string>(signal.Args["list"]);
        Assert.Equal("hi", signal.Args["TEXT"]);   // case-insensitive like action arguments
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"name\":\"no type\"}")]
    public void Garbage_is_not_a_message(string text) => Assert.Null(RemoteProtocol.TryParse(text));

    [Fact]
    public void Argument_lines_parse_key_value_pairs()
    {
        var args = RemoteProtocol.ParseArgumentLines("a=1\r\n\n b = two words \n=skipped\nnoequals\nurl=http://x/?q=1\na=again");
        Assert.Equal(3, args.Count);
        Assert.Equal("again", args["a"]);
        Assert.Equal("two words", args["b"]);
        Assert.Equal("http://x/?q=1", args["url"]);
    }

    [Theory]
    [InlineData("", "studio", true)]
    [InlineData("*", "studio", true)]
    [InlineData("Any", "studio", true)]
    [InlineData("STUDIO", "studio", true)]
    [InlineData("Alpha", "studio", true)]
    [InlineData("id-1", "studio", true)]
    [InlineData("other", "studio", false)]
    public void Instance_filter_matches_label_name_or_id(string filter, string label, bool expected) =>
        Assert.Equal(expected, RemoteHub.Matches(filter, label, new RemoteInstanceInfo("id-1", "Alpha", "Windows", "0.1.0")));

    [Fact]
    public void Trigger_filters_use_signal_name_and_instance()
    {
        var e = new BotEvent(RemoteHub.SignalEvent, new Dictionary<string, object?>
        {
            ["signalName"] = "Go", ["instanceId"] = "id-1", ["instanceName"] = "Alpha", ["instanceLabel"] = "studio",
        });
        Assert.True(RemoteTriggers.MatchesName(e, ""));
        Assert.True(RemoteTriggers.MatchesName(e, "go"));
        Assert.False(RemoteTriggers.MatchesName(e, "stop"));
        Assert.True(RemoteTriggers.MatchesInstance(e, "studio"));
        Assert.False(RemoteTriggers.MatchesInstance(e, "other"));
    }

    [Fact]
    public void Refusals_explain_the_http_status()
    {
        Assert.Contains("wrong token", RemoteClient.Refusal(System.Net.HttpStatusCode.Unauthorized));
        Assert.Contains("REST API is off", RemoteClient.Refusal(System.Net.HttpStatusCode.NotFound));
        Assert.Contains("HTTP 500", RemoteClient.Refusal(System.Net.HttpStatusCode.InternalServerError));
    }
}
