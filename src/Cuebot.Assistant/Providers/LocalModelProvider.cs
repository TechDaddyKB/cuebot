using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Assistant.Providers;

/// <summary>
/// A model on the streamer's own computer: Ollama (its own <c>/api/chat</c>, so Cuebot can set the context size and switch
/// thinking off) or any OpenAI-compatible server (LM Studio, llama.cpp's llama-server) through <c>/v1/chat/completions</c>.
/// Small models cope badly with Cuebot's 117 tools (about 14K tokens), so sessions send <see cref="LocalToolSet"/>'s focused
/// set plus a tool that finds the others, retry once when the server fails on a malformed tool call, and nudge the model
/// once when it stops with an empty answer right after tool results. See docs/ASSISTANT.md, "Local models".
/// </summary>
public sealed class LocalModelProvider(HttpClient http, Func<AssistantProviderSettings> settings) : IAssistantProvider
{
    public const string ProviderId = "local";
    public const string DefaultServer = "http://127.0.0.1:11434";

    /// <summary>Picked when no model is set, in this order (Cuebot's tests: docs/ASSISTANT.md); else the first one listed.</summary>
    private static readonly string[] Preferred = ["qwen3.5:9b", "qwen3.5:4b", "qwen3.5", "qwen3", "gemma4"];

    private const int MaxRounds = 30;

    /// <summary>Tool results are cut to this many characters: a small model's context is precious.</summary>
    private const int MaxToolResult = 12_000;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public string Id => ProviderId;
    public string Name => "Local model (Ollama, LM Studio)";
    public AssistantProviderKind Kind => AssistantProviderKind.Local;

    /// <summary>The server's root, e.g. http://127.0.0.1:11434 (a trailing /v1 is removed; Cuebot adds the paths).</summary>
    internal Uri Root
    {
        get
        {
            var text = settings().ServerUrl.Trim();
            if (text.Length == 0) text = DefaultServer;
            if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
            text = text.TrimEnd('/');
            if (text.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) text = text[..^3];
            return Uri.TryCreate(text + "/", UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? uri
                : throw new InvalidOperationException($"'{settings().ServerUrl}' is not a server address. Use something like {DefaultServer}.");
        }
    }

    public async Task<AssistantProviderStatus> GetStatusAsync(CancellationToken ct = default)
    {
        Uri root;
        try
        {
            root = Root;
        }
        catch (InvalidOperationException ex)
        {
            return AssistantProviderStatus.Unavailable(ex.Message);
        }
        try
        {
            var server = await DetectAsync(root, ct).ConfigureAwait(false);
            var models = await ListModelsAsync(root, server, ct).ConfigureAwait(false);
            if (models.Count == 0)
                return AssistantProviderStatus.Unavailable(server == ServerKind.Ollama
                    ? "Ollama is running but has no models yet. Download one, e.g. ollama pull qwen3.5:4b"
                    : "The model server is running but has no model loaded. Load one in LM Studio (or your server) first.");
            var name = server == ServerKind.Ollama ? "Ollama" : "Model server";
            return AssistantProviderStatus.Available($"{name} ready ({models.Count} model{(models.Count == 1 ? "" : "s")}). Small models make more mistakes: check what it builds.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return AssistantProviderStatus.Unavailable(settings().ServerUrl.Trim().Length == 0
                ? "Ollama is not running on this computer. Install it from ollama.com and start it, or enter your model server's address."
                : $"Cannot reach the model server at {root}. Start it, or check the address.");
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var root = Root;
        return await ListModelsAsync(root, await DetectAsync(root, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    public IAssistantSession StartSession(AssistantContext context) => new Session(this, context, settings());

    internal enum ServerKind { Ollama, OpenAi }

    /// <summary>Ollama answers /api/version; anything else is treated as an OpenAI-compatible server.</summary>
    internal async Task<ServerKind> DetectAsync(Uri root, CancellationToken ct)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probe.CancelAfter(ProbeTimeout);
        try
        {
            using var response = await http.GetAsync(new Uri(root, "api/version"), probe.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode
                && JsonNode.Parse(await response.Content.ReadAsStringAsync(probe.Token).ConfigureAwait(false))?["version"] is not null)
                return ServerKind.Ollama;
        }
        catch (JsonException)
        {
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
        }
        return ServerKind.OpenAi;
    }

    private async Task<IReadOnlyList<string>> ListModelsAsync(Uri root, ServerKind server, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        var json = await http.GetFromJsonAsync<JsonObject>(new Uri(root, server == ServerKind.Ollama ? "api/tags" : "v1/models"), timeout.Token)
            .ConfigureAwait(false) ?? [];
        var names = server == ServerKind.Ollama
            ? (json["models"] as JsonArray ?? []).Select(m => m?["name"]?.GetValue<string>())
            : (json["data"] as JsonArray ?? []).Select(m => m?["id"]?.GetValue<string>());
        // Embedding models cannot chat.
        return [.. names.OfType<string>().Where(n => !n.Contains("embed", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The model to use when none is set: a recommended one if downloaded, else the first.</summary>
    internal static string? PickDefault(IReadOnlyList<string> models) =>
        Preferred.Select(p => models.FirstOrDefault(m => m.Equals(p, StringComparison.OrdinalIgnoreCase))
                              ?? models.FirstOrDefault(m => m.StartsWith(p + ":", StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(m => m is not null) ?? models.FirstOrDefault();

    private sealed class Session(LocalModelProvider provider, AssistantContext context, AssistantProviderSettings options) : IAssistantSession
    {
        private readonly LocalToolSet _tools = new(context.Tools, options.AllTools);
        private readonly JsonArray _messages = [new JsonObject { ["role"] = "system", ["content"] = context.Instructions + LocalToolSet.Instructions(options.AllTools) }];
        private (Uri Root, ServerKind Kind, string Model)? _server;

        public async IAsyncEnumerable<AssistantUpdate> SendAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var (root, kind, model) = _server ??= await ConnectAsync(ct).ConfigureAwait(false);
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
            var nudged = false;
            var afterTools = false;
            for (var round = 0; round < MaxRounds; round++)
            {
                var answer = new StringBuilder();
                var calls = new List<(string Id, string Name, string Arguments)>();
                Round? last = null;
                await foreach (var piece in StreamWithRetryAsync(root, kind, model, ct).ConfigureAwait(false))
                {
                    switch (piece)
                    {
                        case RoundText t:
                            answer.Append(t.Text);
                            yield return new AssistantText(t.Text);
                            break;
                        case RoundCall c:
                            calls.Add((c.Id.Length > 0 ? c.Id : $"call_{round}_{calls.Count}", c.Name, c.Arguments));
                            break;
                        case Round r:
                            last = r;
                            break;
                    }
                }

                var assistant = new JsonObject { ["role"] = "assistant", ["content"] = answer.ToString() };
                if (calls.Count > 0)
                    assistant["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = kind == ServerKind.Ollama
                            ? new JsonObject { ["name"] = c.Name, ["arguments"] = ParseArguments(c.Arguments) ?? [] }
                            : new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.Length > 0 ? c.Arguments : "{}" },
                    })]);
                _messages.Add(assistant);

                if (last is { ContextFull: true })
                    yield return new AssistantText("\n\n(The conversation has filled this model's memory, so it may forget the start. Press New chat, or raise the memory size.)");
                if (calls.Count == 0)
                {
                    // Small models sometimes stop with nothing at all (Qwen3.5 in Ollama, now and then, with thinking off),
                    // at the start of a turn or right after tool results; ask once to carry on.
                    if (answer.ToString().Trim().Length == 0 && !nudged)
                    {
                        nudged = true;
                        _messages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = afterTools
                                ? "Continue: finish what I asked with Cuebot's tools, then tell me in one or two sentences what you did."
                                : "Please answer my last message. Use Cuebot's tools if you need to look something up or change something.",
                        });
                        continue;
                    }
                    if (last is { CutOff: true }) yield return new AssistantError("The answer was cut off (too long).");
                    yield break;
                }

                foreach (var call in calls)
                {
                    AssistantToolResult content;
                    if (ParseArguments(call.Arguments) is not { } arguments)
                    {
                        content = new AssistantToolResult("""{"error":"The arguments were not valid JSON. Call the tool again with a JSON object."}""", true);
                    }
                    else if (call.Name == LocalToolSet.FindTool)
                    {
                        yield return new AssistantToolStarted(call.Id, call.Name);
                        content = new AssistantToolResult(_tools.Find(arguments), false);
                        yield return new AssistantToolFinished(call.Id, call.Name, false);
                    }
                    else
                    {
                        var result = new StrongBox<AssistantToolResult?>();
                        await foreach (var update in ToolCalls.RunAsync(context.Tools, call.Id, call.Name, arguments, result, ct).ConfigureAwait(false))
                            yield return update;
                        content = result.Value!;
                    }
                    var output = content.Content.Length > MaxToolResult
                        ? content.Content[..MaxToolResult] + "\n…(cut: ask for less, e.g. with a search word)"
                        : content.Content;
                    var message = new JsonObject { ["role"] = "tool", ["content"] = output };
                    if (kind == ServerKind.Ollama) message["tool_name"] = call.Name;
                    else message["tool_call_id"] = call.Id;
                    _messages.Add(message);
                }
                afterTools = true;
            }
            yield return new AssistantError("Stopped after too many tool calls in one answer.");
        }

        private async Task<(Uri, ServerKind, string)> ConnectAsync(CancellationToken ct)
        {
            var root = provider.Root;
            var kind = await provider.DetectAsync(root, ct).ConfigureAwait(false);
            var model = context.Model;
            if (model.Length == 0)
            {
                var models = await provider.ListModelsAsync(root, kind, ct).ConfigureAwait(false);
                model = PickDefault(models) ?? throw new InvalidOperationException(kind == ServerKind.Ollama
                    ? "Ollama has no models yet. Download one, e.g. ollama pull qwen3.5:4b"
                    : "The model server has no model loaded.");
            }
            return (root, kind, model);
        }

        /// <summary>One round; a server error before anything was shown is retried once (Qwen sometimes writes a broken tool call).</summary>
        private async IAsyncEnumerable<object> StreamWithRetryAsync(Uri root, ServerKind kind, string model, [EnumeratorCancellation] CancellationToken ct)
        {
            for (var attempt = 1; ; attempt++)
            {
                var pieces = new List<object>();
                Exception? failure = null;
                var enumerator = Stream(root, kind, model, ct).GetAsyncEnumerator(ct);
                try
                {
                    while (true)
                    {
                        object piece;
                        try
                        {
                            if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                            piece = enumerator.Current;
                        }
                        catch (RetryableException ex) when (attempt == 1 && pieces.Count == 0)
                        {
                            failure = ex;
                            break;
                        }
                        pieces.Add(piece);
                        yield return piece;
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                if (failure is null) yield break;
            }
        }

        private IAsyncEnumerable<object> Stream(Uri root, ServerKind kind, string model, CancellationToken ct) =>
            kind == ServerKind.Ollama ? StreamOllamaAsync(root, model, ct) : StreamOpenAiAsync(root, model, ct);

        private async IAsyncEnumerable<object> StreamOllamaAsync(Uri root, string model, [EnumeratorCancellation] CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = _messages.DeepClone(),
                ["tools"] = _tools.Definitions(),
                ["stream"] = true,
                ["think"] = options.Thinking,
                ["options"] = new JsonObject { ["num_ctx"] = Math.Max(4096, options.ContextLength) },
            };
            using var response = await SendAsync(new Uri(root, "api/chat"), body, ct).ConfigureAwait(false);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), Encoding.UTF8, false);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.Trim().Length == 0 || JsonNode.Parse(line) is not JsonObject chunk) continue;
                if (chunk["error"] is JsonNode error) throw new RetryableException($"Local model: {error.GetValue<string>()}");
                var message = chunk["message"] as JsonObject;
                if (message?["content"] is JsonValue c && c.TryGetValue<string>(out var piece) && piece.Length > 0) yield return new RoundText(piece);
                foreach (var call in message?["tool_calls"] as JsonArray ?? [])
                {
                    var function = call?["function"];
                    yield return new RoundCall(call?["id"]?.GetValue<string>() ?? "", function?["name"]?.GetValue<string>() ?? "",
                        function?["arguments"] is JsonObject args ? args.ToJsonString() : function?["arguments"]?.GetValue<string>() ?? "{}");
                }
                if (chunk["done"]?.GetValue<bool>() == true)
                {
                    var used = chunk["prompt_eval_count"]?.GetValue<int>() ?? 0;
                    yield return new Round(CutOff: chunk["done_reason"]?.GetValue<string>() == "length",
                        ContextFull: used >= Math.Max(4096, options.ContextLength) - 512);
                }
            }
        }

        private async IAsyncEnumerable<object> StreamOpenAiAsync(Uri root, string model, [EnumeratorCancellation] CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = _messages.DeepClone(),
                ["tools"] = _tools.Definitions(),
                ["stream"] = true,
            };
            if (!options.Thinking) body["reasoning_effort"] = "none";
            using var response = await SendAsync(new Uri(root, "v1/chat/completions"), body, ct).ConfigureAwait(false);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), Encoding.UTF8, false);
            var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)>();
            string? finish = null;
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                if (data.Length == 0 || JsonNode.Parse(data) is not JsonObject chunk) continue;
                if (chunk["error"] is JsonObject error) throw new RetryableException($"Local model: {error["message"]?.GetValue<string>()}");
                if (chunk["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice) continue;
                if (choice["finish_reason"] is JsonValue f && f.TryGetValue<string>(out var reason)) finish = reason;
                if (choice["delta"] is not JsonObject delta) continue;
                if (delta["content"] is JsonValue c && c.TryGetValue<string>(out var piece) && piece.Length > 0) yield return new RoundText(piece);
                foreach (var call in delta["tool_calls"] as JsonArray ?? [])
                {
                    var index = call?["index"]?.GetValue<int>() ?? calls.Count;
                    var entry = calls.TryGetValue(index, out var known) ? known : (Id: "", Name: "", Arguments: new StringBuilder());
                    if (call?["id"]?.GetValue<string>() is { Length: > 0 } id) entry.Id = id;
                    if (call?["function"]?["name"]?.GetValue<string>() is { Length: > 0 } name) entry.Name += name;
                    if (call?["function"]?["arguments"]?.GetValue<string>() is { } args) entry.Arguments.Append(args);
                    calls[index] = entry;
                }
            }
            foreach (var call in calls.Values) yield return new RoundCall(call.Id, call.Name, call.Arguments.ToString());
            yield return new Round(CutOff: finish == "length", ContextFull: false);
        }

        private async Task<HttpResponseMessage> SendAsync(Uri url, JsonObject body, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            HttpResponseMessage response;
            try
            {
                response = await provider.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException($"Cannot reach the model server at {url.GetLeftPart(UriPartial.Authority)}: {ex.Message}");
            }
            if (response.IsSuccessStatusCode) return response;
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            var detail = Detail(text);
            var message = response.StatusCode == HttpStatusCode.NotFound && detail.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? $"The model '{body["model"]}' is not on the model server. Pick one with List models, or download it (ollama pull {body["model"]})."
                : $"Local model: {(int)response.StatusCode} {response.ReasonPhrase}{(detail.Length > 0 ? " - " + detail : "")}";
            throw (int)response.StatusCode >= 500 ? new RetryableException(message) : new InvalidOperationException(message);
        }

        private static string Detail(string body)
        {
            try
            {
                var json = JsonNode.Parse(body);
                return json?["error"] is JsonObject o ? o["message"]?.GetValue<string>() ?? "" : json?["error"]?.GetValue<string>() ?? "";
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return body.Length > 200 ? body[..200] : body;
            }
        }

        private static JsonObject? ParseArguments(string text)
        {
            if (text.Trim().Length == 0) return [];
            try
            {
                return JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

    private sealed record RoundText(string Text);

    private sealed record RoundCall(string Id, string Name, string Arguments);

    /// <param name="CutOff">The answer hit the output limit.</param>
    /// <param name="ContextFull">The conversation filled the context (Ollama reports how much it read).</param>
    private sealed record Round(bool CutOff, bool ContextFull);

    /// <summary>A server error worth one more try (the model's next attempt usually comes out right).</summary>
    private sealed class RetryableException(string message) : InvalidOperationException(message);
}
