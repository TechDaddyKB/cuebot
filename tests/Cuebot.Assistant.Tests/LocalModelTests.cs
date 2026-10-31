using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Assistant.Cli;
using Cuebot.Assistant.Providers;

namespace Cuebot.Assistant.Tests;

public sealed class LocalModelTests
{
    private const string Tags = """{"models":[{"name":"nomic-embed-text:latest"},{"name":"gemma4:e4b"},{"name":"qwen3.5:9b"}]}""";

    private static HttpResponseMessage Lines(params string[] lines) =>
        new(HttpStatusCode.OK) { Content = new StringContent(string.Join("\n", lines) + "\n", Encoding.UTF8, "application/x-ndjson") };

    private static string Text(string text, bool done = false) =>
        new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text }, ["done"] = done }.ToJsonString();

    private static string Call(string name, string arguments = "{}") =>
        new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "",
                ["tool_calls"] = new JsonArray(new JsonObject { ["function"] = new JsonObject { ["name"] = name, ["arguments"] = JsonNode.Parse(arguments) } }),
            },
            ["done"] = false,
        }.ToJsonString();

    private static string Done(int promptTokens = 900) => $$"""{"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":{{promptTokens}}}""";

    /// <summary>An Ollama server: /api/version, /api/tags, and /api/chat answered by <paramref name="chat"/> (round number from 1).</summary>
    private static FakeHttp Ollama(Func<int, HttpResponseMessage> chat, string tags = Tags)
    {
        var round = 0;
        return new FakeHttp((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/version" => FakeHttp.Json("""{"version":"0.40.13"}"""),
            "/api/tags" => FakeHttp.Json(tags),
            "/api/chat" => chat(++round),
            _ => FakeHttp.Json("{}", HttpStatusCode.NotFound),
        });
    }

    private static string[] ToolNames(JsonObject body) => [.. body["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>())];

    [Fact]
    public async Task Status_says_whether_Ollama_runs_and_has_a_model()
    {
        var settings = new AssistantProviderSettings();
        var ready = await new LocalModelProvider(new HttpClient(Ollama(_ => Lines())), () => settings).GetStatusAsync();
        Assert.True(ready.Ready);
        Assert.Contains("2 models", ready.Message);   // the embedding model is left out

        var empty = await new LocalModelProvider(new HttpClient(Ollama(_ => Lines(), """{"models":[]}""")), () => settings).GetStatusAsync();
        Assert.False(empty.Ready);
        Assert.Contains("ollama pull", empty.Message);

        var down = new FakeHttp((_, _) => throw new HttpRequestException("Connection refused"));
        var off = await new LocalModelProvider(new HttpClient(down), () => settings).GetStatusAsync();
        Assert.False(off.Ready);
        Assert.Contains("Ollama is not running", off.Message);

        Assert.Equal(["gemma4:e4b", "qwen3.5:9b"], await new LocalModelProvider(new HttpClient(Ollama(_ => Lines())), () => settings).ListModelsAsync());
    }

    [Fact]
    public async Task Ollama_gets_the_focused_tools_its_own_context_size_and_no_thinking()
    {
        await using var bot = new Bot();
        var http = Ollama(round => round == 1 ? Lines(Call("list_actions"), Done()) : Lines(Text("You have one action: "), Text("Hello."), Done()));
        var settings = new AssistantProviderSettings();
        await using var session = new LocalModelProvider(new HttpClient(http), () => settings).StartSession(bot.Context());

        var updates = await Updates.CollectAsync(session.SendAsync("What actions do I have?"));

        Assert.Equal("You have one action: Hello.", updates.Text());
        Assert.Contains(updates, u => u is AssistantToolFinished { Tool: "list_actions", Failed: false });
        var bodies = http.Bodies;
        Assert.Equal(2, bodies.Length);
        Assert.Equal("qwen3.5:9b", bodies[0]["model"]!.GetValue<string>());   // the recommended model, when downloaded
        Assert.False(bodies[0]["think"]!.GetValue<bool>());
        Assert.Equal(32768, bodies[0]["options"]!["num_ctx"]!.GetValue<int>());
        var tools = ToolNames(bodies[0]);
        Assert.Contains("create_action", tools);
        Assert.Contains(LocalToolSet.FindTool, tools);
        Assert.Contains("delete_action", tools);
        Assert.DoesNotContain("start_giveaway", tools);
        Assert.True(tools.Length < 22);
        Assert.Contains(LocalToolSet.FindTool, bodies[0]["messages"]![0]!["content"]!.GetValue<string>());
        var toolMessage = bodies[1]["messages"]!.AsArray().Last()!;
        Assert.Equal("tool", toolMessage["role"]!.GetValue<string>());
        Assert.Equal("list_actions", toolMessage["tool_name"]!.GetValue<string>());
        Assert.Contains("Hello", toolMessage["content"]!.GetValue<string>());
        Assert.IsType<JsonObject>(bodies[1]["messages"]!.AsArray()[2]!["tool_calls"]![0]!["function"]!["arguments"]);
    }

    [Fact]
    public async Task Settings_choose_thinking_context_size_every_tool_and_the_model()
    {
        await using var bot = new Bot();
        var http = Ollama(_ => Lines(Text("ok"), Done()));
        var settings = new AssistantProviderSettings { Thinking = true, ContextLength = 16384, AllTools = true };
        await using var session = new LocalModelProvider(new HttpClient(http), () => settings).StartSession(bot.Context("gemma4:e4b"));

        await Updates.CollectAsync(session.SendAsync("hi"));

        var body = http.Bodies[0];
        Assert.Equal("gemma4:e4b", body["model"]!.GetValue<string>());
        Assert.True(body["think"]!.GetValue<bool>());
        Assert.Equal(16384, body["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Contains("start_giveaway", ToolNames(body));
        Assert.DoesNotContain(LocalToolSet.FindTool, ToolNames(body));
    }

    [Fact]
    public async Task Finding_tools_switches_them_on_for_the_rest_of_the_conversation()
    {
        await using var bot = new Bot();
        var http = Ollama(round => round == 1
            ? Lines(Call(LocalToolSet.FindTool, """{"search":"giveaway"}"""), Done())
            : Lines(Text("I can run giveaways now."), Done()));
        var settings = new AssistantProviderSettings();
        await using var session = new LocalModelProvider(new HttpClient(http), () => settings).StartSession(bot.Context());

        var updates = await Updates.CollectAsync(session.SendAsync("Start a giveaway"));

        Assert.Contains(updates, u => u is AssistantToolFinished { Tool: LocalToolSet.FindTool, Failed: false });
        var bodies = http.Bodies;
        Assert.DoesNotContain("start_giveaway", ToolNames(bodies[0]));
        Assert.Contains("start_giveaway", ToolNames(bodies[1]));
        Assert.Contains("start_giveaway", bodies[1]["messages"]!.AsArray().Last()!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_server_error_is_retried_once_and_an_empty_stop_after_tools_gets_one_nudge()
    {
        await using var bot = new Bot();
        var http = Ollama(round => round switch
        {
            1 => FakeHttp.Json("""{"error":"qwen3.5 tool call parsing failed"}""", HttpStatusCode.InternalServerError),
            2 => Lines(Call("list_actions"), Done()),
            3 => Lines(Done()),                                  // stops silently right after the tool result
            _ => Lines(Text("Done: you have Hello."), Done()),
        });
        var settings = new AssistantProviderSettings();
        await using var session = new LocalModelProvider(new HttpClient(http), () => settings).StartSession(bot.Context());

        var updates = await Updates.CollectAsync(session.SendAsync("What do I have?"));

        Assert.DoesNotContain(updates, u => u is AssistantError);
        Assert.Equal("Done: you have Hello.", updates.Text());
        var last = http.Bodies[^1]["messages"]!.AsArray();
        Assert.Equal("user", last[^1]!["role"]!.GetValue<string>());
        Assert.StartsWith("Continue", last[^1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_server_error_and_a_missing_model_are_reported_plainly()
    {
        await using var bot = new Bot();
        var settings = new AssistantProviderSettings();
        var failing = Ollama(_ => FakeHttp.Json("""{"error":"out of memory"}""", HttpStatusCode.InternalServerError));
        await using (var session = new LocalModelProvider(new HttpClient(failing), () => settings).StartSession(bot.Context()))
        {
            var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Updates.CollectAsync(session.SendAsync("hi")));
            Assert.Contains("out of memory", ex.Message);
        }
        Assert.Equal(2, failing.Bodies.Length);   // one retry, then the error

        var missing = Ollama(_ => FakeHttp.Json("""{"error":"model 'qwen9' not found"}""", HttpStatusCode.NotFound));
        await using var other = new LocalModelProvider(new HttpClient(missing), () => settings).StartSession(bot.Context("qwen9"));
        var notFound = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Updates.CollectAsync(other.SendAsync("hi")));
        Assert.Contains("ollama pull qwen9", notFound.Message);
    }

    [Fact]
    public async Task Other_servers_use_the_OpenAI_style_API_without_thinking()
    {
        await using var bot = new Bot();
        var round = 0;
        static string Chunk(string delta, string? finish = null) =>
            $$"""{"choices":[{"index":0,"delta":{{delta}},"finish_reason":{{(finish is null ? "null" : $"\"{finish}\"")}}}]}""";
        var http = new FakeHttp((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => FakeHttp.Json("""{"data":[{"id":"qwen3.5-4b"}]}"""),
            "/v1/chat/completions" => ++round == 1
                ? FakeHttp.Sse([Chunk("""{"tool_calls":[{"index":0,"id":"c1","type":"function","function":{"name":"list_actions","arguments":"{}"}}]}"""),
                                Chunk("{}", "tool_calls"), "[DONE]"])
                : FakeHttp.Sse([Chunk("""{"content":"Hello is your action."}"""), Chunk("{}", "stop"), "[DONE]"]),
            _ => FakeHttp.Json("{}", HttpStatusCode.NotFound),   // no /api/version: not Ollama
        });
        var settings = new AssistantProviderSettings { ServerUrl = "http://127.0.0.1:1234/v1/" };
        var provider = new LocalModelProvider(new HttpClient(http), () => settings);
        Assert.True((await provider.GetStatusAsync()).Ready);
        await using var session = provider.StartSession(bot.Context());

        var updates = await Updates.CollectAsync(session.SendAsync("What do I have?"));

        Assert.Equal("Hello is your action.", updates.Text());
        var bodies = http.Bodies;
        Assert.Equal("qwen3.5-4b", bodies[0]["model"]!.GetValue<string>());
        Assert.Equal("none", bodies[0]["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("c1", bodies[1]["messages"]!.AsArray().Last()!["tool_call_id"]!.GetValue<string>());
        Assert.All(http.Requests, r => Assert.Equal(1234, r.Request.RequestUri!.Port));
    }

    [Fact]
    public async Task A_bad_server_address_is_explained()
    {
        var settings = new AssistantProviderSettings { ServerUrl = "ftp://box" };
        var status = await new LocalModelProvider(new HttpClient(Ollama(_ => Lines())), () => settings).GetStatusAsync();
        Assert.False(status.Ready);
        Assert.Contains("not a server address", status.Message);
    }

    [Fact]
    public void The_AI_apps_model_lists_are_read()
    {
        Assert.Equal(["grok-4.7", "grok-4.7-build-fast", "grok-4.6"],
            AcpProvider.ParseModelList("You are not authenticated.\n\nDefault model: grok-4.7\n\nAvailable models:\n  - grok-4.7-build-fast\n  - grok-4.6\n  - grok-4.7\n"));
        Assert.Equal(["grok-4.6", "grok-4.5"], AcpProvider.ParseModelList("Default model: grok-4.6\n\nAvailable models:\n  * grok-4.6 (default)\n  - grok-4.5\n"));
        Assert.Equal(["gemini-4-argon-medium", "claude-opus-5-5-low"],
            AntigravityProvider.ParseModelList("Fetching available models...\ngemini-4-argon-medium\tGemini 4 Argon (Medium)\nclaude-opus-5-5-low\tClaude Opus 5.5 (Low)\n"));
    }
}
