using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Storage;

namespace Cuebot.Server.Mcp;

/// <summary>Where the stdio bridge sends messages: the running Cuebot's MCP endpoint and its token.</summary>
public sealed record McpBridgeTarget(Uri Endpoint, string Token);

/// <summary>
/// <c>Cuebot --mcp-stdio</c>: an MCP stdio server for desktop AI clients that spawn a process. It does not start Cuebot;
/// it forwards each newline-delimited JSON-RPC message from stdin to the running instance's Streamable HTTP endpoint
/// (address, port and token from servers.json in the data folder) and writes the answers to stdout. Diagnostics go to
/// stderr only. Override with the environment variables CUEBOT_MCP_URL and CUEBOT_API_TOKEN.
/// </summary>
public static class McpStdioBridge
{
    public const string Switch = "--mcp-stdio";
    public const string UrlVariable = "CUEBOT_MCP_URL";
    public const string TokenVariable = "CUEBOT_API_TOKEN";
    private const int NotReachable = -32000;

    public static bool IsRequested(IEnumerable<string> args) =>
        args.Any(a => a.Equals(Switch, StringComparison.OrdinalIgnoreCase) || a.Equals("/mcp-stdio", StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs the bridge on the process's stdin/stdout until stdin closes. Returns the exit code.</summary>
    public static int Run(string dataFolder)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
        using var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };
        var target = ResolveTarget(dataFolder, Environment.GetEnvironmentVariable, out var problem);
        if (target is null)
        {
            stderr.WriteLine($"cuebot --mcp-stdio: {problem}");
            return 1;
        }
        if (problem.Length > 0) stderr.WriteLine($"cuebot --mcp-stdio: {problem}");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        try
        {
            return RunAsync(stdin, stdout, stderr, target, ct: cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The MCP endpoint and token of the Cuebot using <paramref name="dataFolder"/>. Null with an error when they cannot
    /// be found; otherwise <paramref name="warning"/> may still explain a likely problem (e.g. MCP switched off).
    /// </summary>
    public static McpBridgeTarget? ResolveTarget(string dataFolder, Func<string, string?> env, out string warning)
    {
        warning = "";
        var url = env(UrlVariable);
        var token = env(TokenVariable);
        ServerSettings? settings = null;
        var file = Path.Combine(dataFolder, "servers.json");
        if (File.Exists(file))
        {
            try
            {
                settings = JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(file), Json.Options);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                warning = $"Could not read {file}: {ex.Message}";
            }
        }

        if (string.IsNullOrWhiteSpace(token)) token = settings?.Api.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            warning = settings is null
                ? $"No Cuebot settings in {dataFolder}. Start Cuebot, enable the MCP server on the Servers page, or set {TokenVariable} and {UrlVariable}."
                : $"No API token yet: enable the MCP server on Cuebot's Servers page (or set {TokenVariable}).";
            return null;
        }

        Uri? endpoint;
        if (!string.IsNullOrWhiteSpace(url))
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out endpoint) || endpoint.Scheme is not ("http" or "https"))
            {
                warning = $"{UrlVariable} is not an http(s) URL: {url}";
                return null;
            }
        }
        else
        {
            var api = settings?.Api ?? new Api.ApiServerSettings();
            var host = "127.0.0.1";
            if (settings?.AllowLan == true && IPAddress.TryParse(api.Address, out var address)
                && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
            {
                host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
            }
            endpoint = new Uri($"http://{host}:{api.Port}{Api.ApiServer.McpPath}");
            if (settings is { Mcp.Enabled: false }) warning = "The MCP server is off in Cuebot's settings (Servers page); requests will fail until it is enabled.";
        }
        return new McpBridgeTarget(endpoint, token.Trim());
    }

    /// <summary>
    /// Forwards messages until <paramref name="input"/> ends. Requests run concurrently (initialize first, so the session
    /// is known); answers are written one per line.
    /// </summary>
    public static async Task<int> RunAsync(TextReader input, TextWriter output, TextWriter log, McpBridgeTarget target,
        HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromMinutes(6);   // run_action may wait up to 5 minutes
        var bridge = new Bridge(http, target, output, log);
        var pending = new List<Task>();
        while (await input.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Trim().Length == 0) continue;
            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                await bridge.WriteAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = null,
                    ["error"] = new JsonObject { ["code"] = McpException.ParseError, ["message"] = "Parse error" },
                }).ConfigureAwait(false);
                continue;
            }
            var forward = bridge.ForwardAsync(message, ct);
            if (message is JsonObject o && o["method"]?.GetValueKind() == JsonValueKind.String && o["method"]!.GetValue<string>() == "initialize")
                await forward.ConfigureAwait(false);
            else
                pending.Add(forward);
            pending.RemoveAll(t => t.IsCompleted);
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
        return 0;
    }

    private sealed class Bridge(HttpClient http, McpBridgeTarget target, TextWriter output, TextWriter log)
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        private string? _session;
        private string? _protocol;

        public async Task WriteAsync(JsonNode message)
        {
            await _write.WaitAsync().ConfigureAwait(false);
            try
            {
                await output.WriteAsync(message.ToJsonString() + "\n").ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _write.Release();
            }
        }

        public async Task ForwardAsync(JsonNode? message, CancellationToken ct)
        {
            var isInitialize = message is JsonObject o && o["method"] is JsonValue m && m.TryGetValue<string>(out var method) && method == "initialize";
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, target.Endpoint)
                {
                    Content = new StringContent(message?.ToJsonString() ?? "null", Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.Accept.ParseAdd("text/event-stream");
                if (!isInitialize && _session is { } session) request.Headers.TryAddWithoutValidation(McpServer.SessionHeader, session);
                if (!isInitialize && _protocol is { } protocol) request.Headers.TryAddWithoutValidation(McpServer.ProtocolHeader, protocol);

                HttpResponseMessage response;
                try
                {
                    response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    await log.WriteLineAsync($"cuebot --mcp-stdio: {target.Endpoint}: {ex.Message}").ConfigureAwait(false);
                    await ReplyErrorAsync(message, NotReachable,
                        $"Cannot reach Cuebot at {target.Endpoint} ({ex.Message}). Is Cuebot running with the MCP server enabled on the Servers page?").ConfigureAwait(false);
                    return;
                }

                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.NotFound && !isInitialize && _session is not null && attempt == 0)
                    {
                        // Cuebot restarted and forgot the session: continue without one.
                        await log.WriteLineAsync("cuebot --mcp-stdio: session expired, continuing without it").ConfigureAwait(false);
                        _session = null;
                        continue;
                    }
                    if (isInitialize && response.Headers.TryGetValues(McpServer.SessionHeader, out var ids)) _session = ids.FirstOrDefault();
                    if (response.StatusCode == HttpStatusCode.Accepted) return;

                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var messages = response.Content.Headers.ContentType?.MediaType == "text/event-stream" ? SseMessages(body) : [body];
                    var wrote = false;
                    foreach (var text in messages)
                    {
                        JsonNode? answer = null;
                        try { answer = JsonNode.Parse(text); }
                        catch (JsonException) { }
                        if (answer is JsonObject { } rpc && rpc.ContainsKey("jsonrpc") || answer is JsonArray)
                        {
                            if (isInitialize && answer?["result"]?["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var negotiated)) _protocol = negotiated;
                            await WriteAsync(answer!).ConfigureAwait(false);
                            wrote = true;
                        }
                    }
                    if (!wrote && !response.IsSuccessStatusCode)
                    {
                        var reason = response.StatusCode switch
                        {
                            HttpStatusCode.Unauthorized => "Cuebot rejected the API token (it may have been regenerated): update the token or restart the AI client.",
                            HttpStatusCode.NotFound => "The MCP server is off in Cuebot: enable it on the Servers page.",
                            HttpStatusCode.Forbidden => "Cuebot refused the request (origin not allowed).",
                            _ => $"Cuebot answered HTTP {(int)response.StatusCode}.",
                        };
                        await log.WriteLineAsync($"cuebot --mcp-stdio: {reason}").ConfigureAwait(false);
                        await ReplyErrorAsync(message, NotReachable, reason).ConfigureAwait(false);
                    }
                    return;
                }
            }
        }

        private async Task ReplyErrorAsync(JsonNode? message, int code, string text)
        {
            var requests = message is JsonArray batch ? batch.ToList() : [message];
            foreach (var request in requests.OfType<JsonObject>().Where(r => r.ContainsKey("id") && r.ContainsKey("method")))
            {
                await WriteAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = request["id"]?.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = code, ["message"] = text },
                }).ConfigureAwait(false);
            }
        }

        /// <summary>The data of each event in an SSE body.</summary>
        private static List<string> SseMessages(string body)
        {
            var result = new List<string>();
            var data = new StringBuilder();
            foreach (var raw in body.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0)
                {
                    if (data.Length > 0) result.Add(data.ToString());
                    data.Clear();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(5).TrimStart(' '));
                }
            }
            if (data.Length > 0) result.Add(data.ToString());
            return result;
        }
    }
}
