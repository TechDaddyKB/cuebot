using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace Cuebot.Assistant.Providers;

/// <summary>
/// Claude through the Anthropic API with the streamer's API key (official Anthropic SDK). Streams the answer, runs
/// Cuebot's tools in process, keeps the whole conversation (thinking blocks included, unchanged) and lets the API fall
/// back to another model when a request is declined (<c>fallbacks: "default"</c>).
/// </summary>
public sealed class AnthropicProvider(Func<AssistantProviderSettings> settings, HttpClient? http = null, Uri? baseUrl = null) : IAssistantProvider
{
    public const string DefaultModel = "claude-opus-5-5";
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    public string Id => "anthropic";
    public string Name => "Claude (Anthropic API key)";
    public AssistantProviderKind Kind => AssistantProviderKind.ApiKey;

    public Task<AssistantProviderStatus> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(settings().ApiKey.Trim().Length == 0
            ? AssistantProviderStatus.Unavailable("Add an Anthropic API key (console.anthropic.com)")
            : AssistantProviderStatus.Available("API key set"));

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        if (settings().ApiKey.Trim().Length == 0) return [DefaultModel];
        var page = await Client().Models.List(cancellationToken: ct).ConfigureAwait(false);
        return [.. page.Items.Select(m => m.ID)];
    }

    public IAssistantSession StartSession(AssistantContext context) =>
        new Session(Client(), context, context.Model.Length > 0 ? context.Model : DefaultModel);

    private static readonly HttpClient DefaultHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private AnthropicClient Client() => new()
    {
        ApiKey = settings().ApiKey.Trim(),
        HttpClient = http ?? DefaultHttp,
        BaseUrl = (baseUrl ?? new Uri("https://api.anthropic.com")).ToString().TrimEnd('/'),
    };

    private sealed class Session(AnthropicClient client, AssistantContext context, string model) : IAssistantSession
    {
        private const int MaxRounds = 30;
        private readonly List<BetaMessageParam> _messages = [];
        private readonly List<BetaToolUnion> _tools = [.. context.Tools.List().Select(ToTool)];

        public async IAsyncEnumerable<AssistantUpdate> SendAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
        {
            _messages.Add(new BetaUserMessageParam { Content = text });
            for (var round = 0; round < MaxRounds; round++)
            {
                var events = new List<BetaRawMessageStreamEvent>();
                await foreach (var e in client.Beta.Messages.CreateStreaming(Request(), ct).ConfigureAwait(false))
                {
                    events.Add(e);
                    if (e.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var piece) && piece.Text.Length > 0)
                        yield return new AssistantText(piece.Text);
                }
                var message = await Replay(events).Aggregate().ConfigureAwait(false);

                if (message.StopReason == "refusal")
                {
                    _messages.RemoveAt(_messages.Count - 1);   // nothing was answered; the next message starts from the last answer
                    yield return new AssistantError("Claude declined this request. Try rephrasing it.");
                    yield break;
                }

                var (assistant, toolUses) = ToParams(message);
                _messages.Add(new BetaAssistantMessageParam { Content = assistant });
                if (toolUses.Count == 0 || message.StopReason == "max_tokens")
                {
                    if (message.StopReason == "max_tokens") yield return new AssistantError("The answer was cut off (too long).");
                    yield break;
                }

                var results = new List<BetaContentBlockParam>();
                foreach (var use in toolUses)
                {
                    var result = new StrongBox<AssistantToolResult?>();
                    await foreach (var update in ToolCalls.RunAsync(context.Tools, use.ID, use.Name, Arguments(use.Input), result, ct).ConfigureAwait(false))
                        yield return update;
                    results.Add(new BetaToolResultBlockParam { ToolUseID = use.ID, Content = result.Value!.Content, IsError = result.Value.IsError });
                }
                _messages.Add(new BetaUserMessageParam { Content = results });
            }
            yield return new AssistantError("Stopped after too many tool calls in one answer.");
        }

        private MessageCreateParams Request() => new()
        {
            Model = model,
            MaxTokens = 64000,
            Betas = [FallbackBeta],
            Fallbacks = new Default(),
            System = context.Instructions,
            Thinking = new BetaThinkingConfigAdaptive(),
            OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
            CacheControl = new BetaCacheControlEphemeral(),
            Tools = _tools,
            Messages = [.. _messages],
        };

        private static BetaToolUnion ToTool(AssistantToolDefinition tool) =>
            BetaTool.FromRawUnchecked(new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement(tool.Name),
                ["description"] = JsonSerializer.SerializeToElement(tool.Description),
                ["input_schema"] = JsonSerializer.SerializeToElement(tool.InputSchema),
            });

        /// <summary>The response blocks as they go back into the history (unchanged; thinking keeps its signature).</summary>
        private static (List<BetaContentBlockParam> Blocks, List<BetaToolUseBlock> ToolUses) ToParams(BetaMessage message)
        {
            List<BetaContentBlockParam> blocks = [];
            List<BetaToolUseBlock> uses = [];
            foreach (var block in message.Content)
            {
                if (block.TryPickText(out var text)) blocks.Add(new BetaTextBlockParam { Text = text.Text });
                else if (block.TryPickThinking(out var thinking))
                    blocks.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                else if (block.TryPickRedactedThinking(out var redacted)) blocks.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                else if (block.TryPickToolUse(out var use))
                {
                    blocks.Add(new BetaToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                    uses.Add(use);
                }
                else if (block.TryPickFallback(out var fallback))
                    blocks.Add(BetaFallbackBlockParam.FromRawUnchecked(fallback.RawData));
            }
            return (blocks, uses);
        }

        private static JsonObject Arguments(IReadOnlyDictionary<string, JsonElement> input)
        {
            var args = new JsonObject();
            foreach (var (key, value) in input) args[key] = JsonNode.Parse(value.GetRawText());
            return args;
        }

        private static async IAsyncEnumerable<BetaRawMessageStreamEvent> Replay(List<BetaRawMessageStreamEvent> events)
        {
            foreach (var e in events) yield return e;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
