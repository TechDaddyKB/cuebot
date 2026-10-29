using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cuebot.Assistant.Providers;

/// <summary>
/// An API-key provider that speaks OpenAI's Chat Completions (streamed, with tool calls): OpenAI itself, xAI and Google's
/// OpenAI-compatible Gemini endpoint. Without a model set, the newest suitable model from the provider's own /models
/// list is used, so Cuebot does not go stale when a provider releases a new model.
/// </summary>
public sealed partial class OpenAiCompatibleProvider(OpenAiCompatibleProvider.Profile profile, HttpClient http, Func<AssistantProviderSettings> settings)
    : IAssistantProvider
{
    /// <summary>One provider's endpoint and how to pick a default model from its list.</summary>
    /// <param name="ModelPrefix">Chat models start with this ("gpt-", "grok-", "gemini-").</param>
    /// <param name="Exclude">Model ids containing any of these are not general chat models (images, audio, embeddings...).</param>
    /// <param name="Prefer">Among the newest versions, ids containing these rank first.</param>
    public sealed record Profile(string Id, string Name, Uri BaseUrl, string KeyHint, string ModelPrefix, string[] Exclude, string[] Prefer);

    public static readonly Profile OpenAi = new("openai", "OpenAI (API key)", new Uri("https://api.openai.com/v1/"), "platform.openai.com",
        "gpt-", ["audio", "realtime", "transcribe", "tts", "image", "search", "instruct", "mini", "nano", "codex", "oss", "chat-latest"], []);

    public static readonly Profile Xai = new("xai", "Grok (xAI API key)", new Uri("https://api.x.ai/v1/"), "console.x.ai",
        "grok-", ["image", "imagine", "vision", "video", "voice", "mini", "build", "code"], ["-reasoning"]);

    public static readonly Profile Google = new("google", "Gemini (Google API key)", new Uri("https://generativelanguage.googleapis.com/v1beta/openai/"),
        "aistudio.google.com", "gemini-", ["embedding", "image", "tts", "audio", "live", "lite", "nano", "robotics", "computer-use", "aqa", "exp"], ["pro"]);

    private readonly HttpClient _http = http;
    private string? _defaultModel;

    public string Id => profile.Id;
    public string Name => profile.Name;
    public AssistantProviderKind Kind => AssistantProviderKind.ApiKey;

    public Task<AssistantProviderStatus> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(Key.Length == 0
            ? AssistantProviderStatus.Unavailable($"Add an API key ({profile.KeyHint})")
            : AssistantProviderStatus.Available("API key set"));

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        if (Key.Length == 0) return [];
        using var request = Request(HttpMethod.Get, "models");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        return [.. (json["data"] as JsonArray ?? []).Select(m => m?["id"]?.GetValue<string>()).OfType<string>()
            .Select(id => id.StartsWith("models/", StringComparison.Ordinal) ? id["models/".Length..] : id)
            .Where(id => id.StartsWith(profile.ModelPrefix, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The newest general chat model in <paramref name="ids"/> for <paramref name="profile"/>, or null.</summary>
    public static string? PickDefault(Profile profile, IEnumerable<string> ids) =>
        ids.Where(id => id.StartsWith(profile.ModelPrefix, StringComparison.OrdinalIgnoreCase)
                        && !profile.Exclude.Any(x => id.Contains(x, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(id => Version(id[profile.ModelPrefix.Length..]), new VersionComparer())
            .ThenByDescending(id => profile.Prefer.Any(p => id.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(id => DatedSnapshot().IsMatch(id))   // an alias tracks the newest snapshot
            .ThenBy(id => id.Contains("preview", StringComparison.OrdinalIgnoreCase))
            .ThenBy(id => id.Length)
            .FirstOrDefault();

    public IAssistantSession StartSession(AssistantContext context) => new Session(this, context);

    private string Key => settings().ApiKey.Trim();

    private async Task<string> ModelAsync(AssistantContext context, CancellationToken ct)
    {
        if (context.Model.Length > 0) return context.Model;
        _defaultModel ??= PickDefault(profile, await ListModelsAsync(ct).ConfigureAwait(false))
                          ?? throw new InvalidOperationException($"{profile.Name}: no chat model found for this key; enter a model on the Assistant page.");
        return _defaultModel;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, JsonNode? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(profile.BaseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ErrorMessage(response, text));
        return JsonNode.Parse(text) as JsonObject ?? [];
    }

    private string ErrorMessage(HttpResponseMessage response, string body)
    {
        string? detail = null;
        try
        {
            var json = JsonNode.Parse(body);
            detail = (json is JsonArray a ? a.FirstOrDefault() : json)?["error"]?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
        }
        return $"{profile.Name}: {(int)response.StatusCode} {response.ReasonPhrase}{(detail is null ? "" : $" - {detail}")}";
    }

    private static int[] Version(string rest) =>
        [.. VersionNumbers().Match(rest) is { Success: true } m
            ? m.Value.Split('.').Select(n => int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0)
            : [0]];

    [GeneratedRegex(@"^\d+(\.\d+)*")]
    private static partial Regex VersionNumbers();

    [GeneratedRegex(@"-\d{4}-?\d{2}-?\d{2}$|-\d{4}$")]
    private static partial Regex DatedSnapshot();

    private sealed class VersionComparer : IComparer<int[]>
    {
        public int Compare(int[]? x, int[]? y)
        {
            x ??= [];
            y ??= [];
            for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
                if (c != 0) return c;
            }
            return 0;
        }
    }

    private sealed class Session(OpenAiCompatibleProvider provider, AssistantContext context) : IAssistantSession
    {
        private const int MaxRounds = 30;
        private readonly JsonArray _messages = [new JsonObject { ["role"] = "system", ["content"] = context.Instructions }];
        private readonly JsonArray _tools = [.. context.Tools.List().Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.InputSchema.DeepClone() },
        })];

        public async IAsyncEnumerable<AssistantUpdate> SendAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var model = await provider.ModelAsync(context, ct).ConfigureAwait(false);
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
            for (var round = 0; round < MaxRounds; round++)
            {
                var answer = new StringBuilder();
                var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)>();
                string? finish = null;
                await foreach (var chunk in StreamAsync(model, ct).ConfigureAwait(false))
                {
                    if (chunk["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice) continue;
                    if (choice["finish_reason"] is JsonValue f && f.TryGetValue<string>(out var reason)) finish = reason;
                    if (choice["delta"] is not JsonObject delta) continue;
                    if (delta["content"] is JsonValue c && c.TryGetValue<string>(out var piece) && piece.Length > 0)
                    {
                        answer.Append(piece);
                        yield return new AssistantText(piece);
                    }
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

                var assistant = new JsonObject { ["role"] = "assistant", ["content"] = answer.Length > 0 ? answer.ToString() : null };
                var toolCalls = calls.Values.Select((c, i) => c with { Id = c.Id.Length > 0 ? c.Id : $"call_{round}_{i}" }).ToList();
                if (toolCalls.Count > 0)
                    assistant["tool_calls"] = new JsonArray([.. toolCalls.Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.Length > 0 ? c.Arguments.ToString() : "{}" },
                    })]);
                _messages.Add(assistant);
                if (toolCalls.Count == 0 || finish == "length")
                {
                    if (finish == "length") yield return new AssistantError("The answer was cut off (too long).");
                    yield break;
                }

                foreach (var call in toolCalls)
                {
                    AssistantToolResult content;
                    if (ParseArguments(call.Arguments.ToString()) is { } arguments)
                    {
                        var result = new StrongBox<AssistantToolResult?>();
                        await foreach (var update in ToolCalls.RunAsync(context.Tools, call.Id, call.Name, arguments, result, ct).ConfigureAwait(false))
                            yield return update;
                        content = result.Value!;
                    }
                    else
                    {
                        content = new AssistantToolResult("""{"error":"The arguments were not valid JSON. Call the tool again with a JSON object."}""", true);
                    }
                    _messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = content.Content });
                }
            }
            yield return new AssistantError("Stopped after too many tool calls in one answer.");
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

        private async IAsyncEnumerable<JsonObject> StreamAsync(string model, [EnumeratorCancellation] CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = _messages.DeepClone(),
                ["tools"] = _tools.DeepClone(),
                ["stream"] = true,
            };
            using var request = provider.Request(HttpMethod.Post, "chat/completions", body);
            using var response = await provider._http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(provider.ErrorMessage(response, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)));
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), Encoding.UTF8, false);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") yield break;
                if (data.Length > 0 && JsonNode.Parse(data) is JsonObject chunk)
                {
                    if (chunk["error"] is JsonObject error) throw new InvalidOperationException($"{provider.Name}: {error["message"]?.GetValue<string>()}");
                    yield return chunk;
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
