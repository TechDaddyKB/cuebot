using System.Net;
using System.Text.Json.Nodes;
using Cuebot.Assistant.Providers;

namespace Cuebot.Assistant.Tests;

public sealed class OpenAiCompatibleProviderTests
{
    private static string Chunk(string delta, string? finish = null) =>
        $$"""{"choices":[{"index":0,"delta":{{delta}},"finish_reason":{{(finish is null ? "null" : $"\"{finish}\"")}}}]}""";

    [Fact]
    public async Task Streams_text_runs_tool_calls_and_sends_the_results_back()
    {
        await using var bot = new Bot();
        var round = 0;
        var http = new FakeHttp((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)
            ? ++round == 1
                ? FakeHttp.Sse([
                    Chunk("""{"role":"assistant","content":"Let me look. "}"""),
                    Chunk("""{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"list_actions","arguments":""}}]}"""),
                    Chunk("""{"tool_calls":[{"index":0,"function":{"arguments":"{}"}}]}"""),
                    Chunk("{}", "tool_calls"), "[DONE]"])
                : FakeHttp.Sse([Chunk("""{"content":"You have one action: Hello."}"""), Chunk("{}", "stop"), "[DONE]"])
            : FakeHttp.Json("{}", HttpStatusCode.NotFound));
        var settings = new AssistantProviderSettings { ApiKey = "sk-test", Model = "gpt-test" };
        var provider = new OpenAiCompatibleProvider(OpenAiCompatibleProvider.OpenAi, new HttpClient(http), () => settings);
        await using var session = provider.StartSession(bot.Context("gpt-test"));

        var updates = await Updates.CollectAsync(session.SendAsync("What actions do I have?"));

        Assert.Equal("Let me look. You have one action: Hello.", updates.Text());
        Assert.Contains(updates, u => u is AssistantToolStarted { Tool: "list_actions" });
        Assert.Contains(updates, u => u is AssistantToolFinished { Tool: "list_actions", Failed: false });
        var bodies = http.Bodies;
        Assert.Equal(2, bodies.Length);
        Assert.Equal("Bearer sk-test", http.Requests.First().Request.Headers.Authorization!.ToString());
        Assert.Equal("gpt-test", bodies[0]["model"]!.GetValue<string>());
        Assert.True(bodies[0]["stream"]!.GetValue<bool>());
        Assert.Equal("system", bodies[0]["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Contains(bodies[0]["tools"]!.AsArray(), t => t!["function"]!["name"]!.GetValue<string>() == "list_actions");
        var second = bodies[1]["messages"]!.AsArray();
        Assert.Equal("call_1", second[2]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("tool", second[3]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", second[3]!["tool_call_id"]!.GetValue<string>());
        Assert.Contains("Hello", second[3]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Keeps_the_conversation_across_turns()
    {
        await using var bot = new Bot();
        var http = new FakeHttp((_, _) => FakeHttp.Sse([Chunk("""{"content":"ok"}"""), Chunk("{}", "stop"), "[DONE]"]));
        var settings = new AssistantProviderSettings { ApiKey = "k" };
        await using var session = new OpenAiCompatibleProvider(OpenAiCompatibleProvider.Xai, new HttpClient(http), () => settings).StartSession(bot.Context("grok-x"));

        await Updates.CollectAsync(session.SendAsync("one"));
        await Updates.CollectAsync(session.SendAsync("two"));

        var messages = http.Bodies[1]["messages"]!.AsArray().Select(m => (m!["role"]!.GetValue<string>(), m["content"]?.GetValue<string>())).ToArray();
        Assert.Equal([("system", "You help."), ("user", "one"), ("assistant", "ok"), ("user", "two")], messages);
        Assert.StartsWith("https://api.x.ai/v1/", http.Requests.First().Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Provider_errors_carry_the_providers_message_but_not_the_key()
    {
        await using var bot = new Bot();
        var http = new FakeHttp((_, _) => FakeHttp.Json("""{"error":{"message":"Incorrect API key provided"}}""", HttpStatusCode.Unauthorized));
        var settings = new AssistantProviderSettings { ApiKey = "sk-secret-123", Model = "gpt-x" };
        await using var session = new OpenAiCompatibleProvider(OpenAiCompatibleProvider.OpenAi, new HttpClient(http), () => settings).StartSession(bot.Context("gpt-x"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Updates.CollectAsync(session.SendAsync("hi")));

        Assert.Contains("401", error.Message);
        Assert.Contains("Incorrect API key provided", error.Message);
        Assert.DoesNotContain("sk-secret-123", error.Message);
    }

    [Fact]
    public async Task Without_a_model_the_newest_chat_model_from_the_list_is_used()
    {
        await using var bot = new Bot();
        var http = new FakeHttp((request, _) => request.Method == HttpMethod.Get
            ? FakeHttp.Json("""{"data":[{"id":"models/gemini-2.5-pro"},{"id":"models/gemini-3.1-flash"},{"id":"models/gemini-3.1-pro-preview"},{"id":"models/gemini-embedding-001"}]}""")
            : FakeHttp.Sse([Chunk("""{"content":"hi"}"""), Chunk("{}", "stop"), "[DONE]"]));
        var settings = new AssistantProviderSettings { ApiKey = "k" };
        var provider = new OpenAiCompatibleProvider(OpenAiCompatibleProvider.Google, new HttpClient(http), () => settings);

        Assert.Equal(["gemini-2.5-pro", "gemini-3.1-flash", "gemini-3.1-pro-preview", "gemini-embedding-001"], await provider.ListModelsAsync());
        await using var session = provider.StartSession(bot.Context());
        await Updates.CollectAsync(session.SendAsync("hi"));
        Assert.Equal("gemini-3.1-pro-preview", http.Bodies.Single()["model"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("openai", "gpt-6.1-sol", "gpt-4o,gpt-5.6-terra,gpt-6.1-sol,gpt-6.1-mini,gpt-6.1-sol-2026-09-01,gpt-realtime-2,gpt-image-2")]
    [InlineData("openai", "gpt-5.6", "gpt-5.6,gpt-5.6-2026-05-01,gpt-5.6-codex")]
    [InlineData("xai", "grok-4.6-reasoning", "grok-4.5,grok-4.6,grok-4.6-reasoning,grok-imagine-image,grok-build-0.1")]
    [InlineData("google", "gemini-3.1-pro", "gemini-3.1-flash,gemini-3.1-pro,gemini-3.1-flash-lite,gemini-2.5-pro")]
    public void Picks_the_newest_general_chat_model(string provider, string expected, string ids)
    {
        var profile = provider switch
        {
            "openai" => OpenAiCompatibleProvider.OpenAi,
            "xai" => OpenAiCompatibleProvider.Xai,
            _ => OpenAiCompatibleProvider.Google,
        };
        Assert.Equal(expected, OpenAiCompatibleProvider.PickDefault(profile, ids.Split(',')));
    }

    [Fact]
    public async Task Without_a_key_the_provider_is_not_ready()
    {
        var settings = new AssistantProviderSettings();
        var provider = new OpenAiCompatibleProvider(OpenAiCompatibleProvider.OpenAi, new HttpClient(), () => settings);
        Assert.False((await provider.GetStatusAsync()).Ready);
        settings.ApiKey = " sk ";
        Assert.True((await provider.GetStatusAsync()).Ready);
    }
}

public sealed class AnthropicProviderTests
{
    private static string Event(string type, string json) => $"event: {type}\ndata: {json}";

    private static IEnumerable<string> Message(string stopReason, params string[] blocks) =>
    [
        Event("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}"""),
        .. blocks,
        Event("message_delta", $$$"""{"type":"message_delta","delta":{"stop_reason":"{{{stopReason}}}","stop_sequence":null},"usage":{"output_tokens":5}}"""),
        Event("message_stop", """{"type":"message_stop"}"""),
    ];

    private static string[] TextBlock(int index, string text) =>
    [
        Event("content_block_start", $$$"""{"type":"content_block_start","index":{{{index}}},"content_block":{"type":"text","text":""}}"""),
        Event("content_block_delta", $$$"""{"type":"content_block_delta","index":{{{index}}},"delta":{"type":"text_delta","text":{{{JsonValue.Create(text).ToJsonString()}}}}}"""),
        Event("content_block_stop", $$$"""{"type":"content_block_stop","index":{{{index}}}}"""),
    ];

    private static string[] ThinkingBlock(int index) =>
    [
        Event("content_block_start", $$$"""{"type":"content_block_start","index":{{{index}}},"content_block":{"type":"thinking","thinking":"","signature":""}}"""),
        Event("content_block_delta", $$$"""{"type":"content_block_delta","index":{{{index}}},"delta":{"type":"signature_delta","signature":"sig-abc"}}"""),
        Event("content_block_stop", $$$"""{"type":"content_block_stop","index":{{{index}}}}"""),
    ];

    private static string[] ToolUseBlock(int index, string id, string name, string input) =>
    [
        Event("content_block_start", new JsonObject
        {
            ["type"] = "content_block_start",
            ["index"] = index,
            ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject() },
        }.ToJsonString()),
        Event("content_block_delta", $$$"""{"type":"content_block_delta","index":{{{index}}},"delta":{"type":"input_json_delta","partial_json":{{{JsonValue.Create(input).ToJsonString()}}}}}"""),
        Event("content_block_stop", $$$"""{"type":"content_block_stop","index":{{{index}}}}"""),
    ];

    [Fact]
    public async Task Streams_runs_tools_and_keeps_thinking_blocks_in_the_history()
    {
        await using var bot = new Bot();
        var round = 0;
        var http = new FakeHttp((_, _) => ++round == 1
            ? FakeHttp.Sse(Message("tool_use", [.. ThinkingBlock(0), .. TextBlock(1, "Checking. "), .. ToolUseBlock(2, "toolu_1", "list_actions", "{}")]))
            : FakeHttp.Sse(Message("end_turn", TextBlock(0, "You have Hello."))));
        var settings = new AssistantProviderSettings { ApiKey = "sk-ant-api03-test" };
        var provider = new AnthropicProvider(() => settings, new HttpClient(http), new Uri("https://anthropic.test"));
        await using var session = provider.StartSession(bot.Context());

        var updates = await Updates.CollectAsync(session.SendAsync("What actions?"));

        Assert.DoesNotContain(updates, u => u is AssistantError);
        Assert.Equal("Checking. You have Hello.", updates.Text());
        Assert.Contains(updates, u => u is AssistantToolFinished { Tool: "list_actions", Failed: false });
        var (first, _) = http.Requests.First();
        Assert.Equal("Bearer sk-ant-api03-test", first.Headers.Authorization!.ToString());   // the SDK sends the key as a bearer token
        Assert.Contains("server-side-fallback-2026-07-01", first.Headers.GetValues("anthropic-beta").Single());
        var bodies = http.Bodies;
        Assert.Equal(AnthropicProvider.DefaultModel, bodies[0]["model"]!.GetValue<string>());
        Assert.Equal("default", bodies[0]["fallbacks"]!.GetValue<string>());
        Assert.Equal("adaptive", bodies[0]["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("You help.", bodies[0]["system"]!.GetValue<string>());
        Assert.Contains(bodies[0]["tools"]!.AsArray(), t => t!["name"]!.GetValue<string>() == "list_actions");
        var history = bodies[1]["messages"]!.AsArray();
        var assistant = history[1]!["content"]!.AsArray();
        Assert.Equal("thinking", assistant[0]!["type"]!.GetValue<string>());
        Assert.Equal("sig-abc", assistant[0]!["signature"]!.GetValue<string>());
        Assert.Equal("tool_use", assistant[2]!["type"]!.GetValue<string>());
        var result = history[2]!["content"]![0]!;
        Assert.Equal("tool_result", result["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", result["tool_use_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_refusal_ends_the_turn_and_drops_the_unanswered_message()
    {
        await using var bot = new Bot();
        var round = 0;
        var http = new FakeHttp((_, _) => ++round == 1 ? FakeHttp.Sse(Message("refusal")) : FakeHttp.Sse(Message("end_turn", TextBlock(0, "Sure."))));
        var settings = new AssistantProviderSettings { ApiKey = "k", Model = "claude-sonnet-5-5" };
        await using var session = new AnthropicProvider(() => settings, new HttpClient(http), new Uri("https://anthropic.test")).StartSession(bot.Context("claude-sonnet-5-5"));

        var refused = await Updates.CollectAsync(session.SendAsync("bad"));
        await Updates.CollectAsync(session.SendAsync("good"));

        Assert.IsType<AssistantError>(refused.Last());
        var messages = http.Bodies[1]["messages"]!.AsArray();
        Assert.Single(messages);
        Assert.Equal("claude-sonnet-5-5", http.Bodies[1]["model"]!.GetValue<string>());
    }
}
