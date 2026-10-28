using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.SystemTools;

namespace Cuebot.Core.BuiltIn;

internal static class SystemCategories
{
    public const string Files = "Files";
    public const string Network = "Network";
    public const string Json = "JSON";
}

/// <summary>
/// Runs a program, script, document or URI. With environment variables or output capture the command must be a
/// program (it is started directly, not through the shell). When waiting with capture on, each line the program
/// writes becomes %output1%..%outputN% (Streamer.bot numbering) with %outputCount%; stderr lines go to %error1%...
/// "Wait at most" (seconds, 0 = no limit) stops waiting, leaving the program running, and sets %timedOut%.
/// </summary>
public sealed class RunProgramSubAction : ISubActionHandler
{
    /// <summary>Captured lines kept per stream.</summary>
    public const int MaxOutputLines = 1000;

    public string TypeId => "core.runProgram";
    public string Category => Categories.System;
    public string DisplayName => "Run program";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("path", "Program or file"),
        new("arguments", "Arguments"),
        new("workingDirectory", "Working directory"),
        new("wait", "Wait for exit", ParameterKind.Boolean) { DefaultValue = "false" },
        new("waitSeconds", "Wait at most (seconds, 0 = no limit)", ParameterKind.Number) { DefaultValue = "0" },
        new("captureOutput", "Capture output (%output1%...)", ParameterKind.Boolean) { DefaultValue = "false", Help = "Needs Wait for exit" },
        new("environment", "Environment variables", ParameterKind.Multiline) { Help = "One NAME=value per line" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var wait = p.GetBool("wait");
        var capture = wait && p.GetBool("captureOutput");
        var environment = SendSignalSubAction.ParseArguments(p["environment"]).ToArray();
        var direct = capture || environment.Length > 0;
        var info = new ProcessStartInfo(p["path"].Trim(), p["arguments"]) { UseShellExecute = !direct };
        if (!string.IsNullOrWhiteSpace(p["workingDirectory"])) info.WorkingDirectory = p["workingDirectory"].Trim();
        foreach (var (name, value) in environment) info.Environment[name] = value;
        var output = new List<string>();
        var errors = new List<string>();
        if (capture)
        {
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.CreateNoWindow = true;
        }

        using var process = new Process { StartInfo = info };
        if (capture)
        {
            process.OutputDataReceived += (_, e) => Collect(output, e.Data);
            process.ErrorDataReceived += (_, e) => Collect(errors, e.Data);
        }
        if (!process.Start()) return SubActionResult.Next;
        if (capture)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        if (!wait) return SubActionResult.Next;

        var limit = p.GetDouble("waitSeconds");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (limit > 0) timeout.CancelAfter(TimeSpan.FromSeconds(limit));
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
        }
        context.Args["timedOut"] = timedOut;
        if (!timedOut)
        {
            context.Args["exitCode"] = process.ExitCode;
            if (capture) process.WaitForExit(); // flushes the asynchronous readers
        }
        if (capture)
        {
            lock (output) Store(context, "output", output);
            lock (errors) Store(context, "error", errors);
        }
        return SubActionResult.Next;
    }

    private static void Collect(List<string> lines, string? line)
    {
        if (line is null) return;
        lock (lines)
        {
            if (lines.Count < MaxOutputLines) lines.Add(line);
        }
    }

    private static void Store(ActionRunContext context, string prefix, List<string> lines)
    {
        context.Args[prefix + "Count"] = lines.Count;
        for (var i = 0; i < lines.Count; i++) context.Args[prefix + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)] = lines[i];
    }
}

/// <summary>Helpers shared by the file sub-actions.</summary>
internal static class FileSteps
{
    public static readonly ParameterDescriptor Path = new("path", "File") { Help = "Full path of the file" };

    public static string RequirePath(ParameterValues p)
    {
        var path = p["path"].Trim();
        if (path.Length == 0) throw new ArgumentException("No file path given.");
        return path;
    }

    public static string Variable(ParameterValues p, string fallback)
    {
        var name = p["variable"].Trim().Trim('%');
        return name.Length == 0 ? fallback : name;
    }

    public static ParameterDescriptor VariableParameter(string fallback) =>
        new("variable", "Store in argument") { DefaultValue = fallback };
}

/// <summary>Reads a whole text file into an argument.</summary>
public sealed class ReadFileSubAction : ISubActionHandler
{
    public string TypeId => "core.readFile";
    public string Category => SystemCategories.Files;
    public string DisplayName => "Read file";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [FileSteps.Path, FileSteps.VariableParameter("fileContent")];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(FileSteps.RequirePath(p), ct).ConfigureAwait(false);
        context.Args[FileSteps.Variable(p, "fileContent")] = text;
        return SubActionResult.Next;
    }
}

/// <summary>Writes or appends text to a file, creating missing folders.</summary>
public sealed class WriteFileSubAction : ISubActionHandler
{
    public const string Overwrite = "Overwrite";
    public const string Append = "Append";

    public string TypeId => "core.writeFile";
    public string Category => SystemCategories.Files;
    public string DisplayName => "Write file";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        FileSteps.Path,
        new("content", "Text", ParameterKind.Multiline),
        new("mode", "Mode", ParameterKind.Choice) { DefaultValue = Append, Choices = [Append, Overwrite] },
        new("newLine", "End with a new line", ParameterKind.Boolean) { DefaultValue = "true" },
        new("createFolders", "Create missing folders", ParameterKind.Boolean) { DefaultValue = "true" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var path = FileSteps.RequirePath(p);
        var text = p["content"] + (p.GetBool("newLine", true) ? Environment.NewLine : "");
        if (p.GetBool("createFolders", true) && System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) is { Length: > 0 } folder)
            Directory.CreateDirectory(folder);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (string.Equals(p["mode"], Overwrite, StringComparison.OrdinalIgnoreCase))
            await File.WriteAllTextAsync(path, text, encoding, ct).ConfigureAwait(false);
        else
            await File.AppendAllTextAsync(path, text, encoding, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>Line handling shared by the "read lines" sub-actions: Streamer.bot's Parse Variables and auto-typing.</summary>
internal static class LineSteps
{
    public static readonly ParameterDescriptor Parse =
        new("parseVariables", "Parse variables", ParameterKind.Boolean) { DefaultValue = "false", Help = "Replace %variables% found in the file" };

    public static readonly ParameterDescriptor AutoType =
        new("autoType", "Attempt auto-typing", ParameterKind.Boolean) { DefaultValue = "false", Help = "A line holding a number becomes a number" };

    /// <summary>The file's lines, or null when it does not exist (sets %fileFound%).</summary>
    public static async Task<string[]?> ReadAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var path = FileSteps.RequirePath(p);
        var found = File.Exists(path);
        context.Args["fileFound"] = found;
        if (!found)
        {
            context.Log.Warn(context.Action.Name, $"File not found: {path}");
            return null;
        }
        return await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
    }

    public static object Value(ActionRunContext context, ParameterValues p, string line)
    {
        var text = p.GetBool("parseVariables") ? context.Resolve(line) : line;
        return p.GetBool("autoType") ? Variables.VariableValues.AutoType(text) ?? text : text;
    }

    public static string Suffix(int i) => i.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Streamer.bot "Read Random Lines From File": picks Count distinct random lines. The first goes to %randomLine% (or the
/// chosen name), the next ones to %randomLine1%, %randomLine2%...; their 0-based numbers to %randomLineNumber%,
/// %randomLineNumber1%... Cuebot also sets %lineNumber% (1-based, first pick) and %lineCount% (candidate lines). A
/// missing file sets %fileFound% false and empty results.
/// </summary>
public sealed class ReadRandomLineSubAction(Random? random = null) : ISubActionHandler
{
    public string TypeId => "core.readRandomLine";
    public string Category => SystemCategories.Files;
    public string DisplayName => "Read random line";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        FileSteps.Path,
        FileSteps.VariableParameter("randomLine"),
        new("skipEmpty", "Skip empty lines", ParameterKind.Boolean) { DefaultValue = "true" },
        new("count", "How many lines", ParameterKind.Number) { DefaultValue = "1" },
        LineSteps.Parse,
        LineSteps.AutoType,
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var lines = await LineSteps.ReadAsync(context, p, ct).ConfigureAwait(false) ?? [];
        var candidates = lines.Select((text, index) => (text, number: index + 1))
            .Where(l => !p.GetBool("skipEmpty", true) || !string.IsNullOrWhiteSpace(l.text))
            .ToList();
        var variable = FileSteps.Variable(p, "randomLine");
        var count = Math.Clamp(p.GetInt("count", 1), 1, Math.Max(1, candidates.Count));
        var rng = random ?? Random.Shared;
        context.Args["lineCount"] = candidates.Count;
        if (candidates.Count == 0)
        {
            context.Args[variable] = "";
            context.Args["lineNumber"] = 0;
            return SubActionResult.Next;
        }
        for (var i = 0; i < count; i++)
        {
            // Partial Fisher-Yates: each pick is distinct.
            var j = i + rng.Next(candidates.Count - i);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            var (text, number) = candidates[i];
            var suffix = i == 0 ? "" : LineSteps.Suffix(i);
            context.Args[variable + suffix] = LineSteps.Value(context, p, text);
            context.Args["randomLineNumber" + suffix] = number - 1;
            if (i == 0) context.Args["lineNumber"] = number;
        }
        return SubActionResult.Next;
    }
}

/// <summary>
/// Streamer.bot "Read Lines From File": %line0%, %line1%... (or another prefix) and %lineCount%, with %fileFound%.
/// </summary>
public sealed class ReadLinesSubAction : ISubActionHandler
{
    /// <summary>Lines beyond this many are not turned into arguments (%lineCount% still counts them all).</summary>
    public const int MaxLines = 10000;

    public string TypeId => "core.readLines";
    public string Category => SystemCategories.Files;
    public string DisplayName => "Read lines from file";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [FileSteps.Path, FileSteps.VariableParameter("line"), LineSteps.Parse, LineSteps.AutoType];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var lines = await LineSteps.ReadAsync(context, p, ct).ConfigureAwait(false) ?? [];
        var prefix = FileSteps.Variable(p, "line");
        context.Args["lineCount"] = lines.Length;
        for (var i = 0; i < lines.Length && i < MaxLines; i++) context.Args[prefix + LineSteps.Suffix(i)] = LineSteps.Value(context, p, lines[i]);
        return SubActionResult.Next;
    }
}

/// <summary>Streamer.bot "Read Specific Line From File": line N (from 1) into %line%; empty when out of range.</summary>
public sealed class ReadSpecificLineSubAction : ISubActionHandler
{
    public string TypeId => "core.readSpecificLine";
    public string Category => SystemCategories.Files;
    public string DisplayName => "Read specific line from file";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        FileSteps.Path,
        FileSteps.VariableParameter("line"),
        new("lineNumber", "Line number (from 1)", ParameterKind.Number) { DefaultValue = "1" },
        LineSteps.Parse,
        LineSteps.AutoType,
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var lines = await LineSteps.ReadAsync(context, p, ct).ConfigureAwait(false) ?? [];
        var n = p.GetInt("lineNumber", 1);
        context.Args["lineCount"] = lines.Length;
        context.Args[FileSteps.Variable(p, "line")] = n >= 1 && n <= lines.Length ? LineSteps.Value(context, p, lines[n - 1]) : "";
        return SubActionResult.Next;
    }
}

/// <summary>
/// Streamer.bot "UDP Broadcast": sends the payload (UTF-8) as one UDP datagram to a port. The default address is the
/// local network broadcast address; use 127.0.0.1 for a server on this computer (e.g. Speaker.bot's UDP server).
/// Sets %udpBytesSent%.
/// </summary>
public sealed class UdpBroadcastSubAction : ISubActionHandler
{
    public const string Broadcast = "255.255.255.255";

    public string TypeId => "core.udpBroadcast";
    public string Category => SystemCategories.Network;
    public string DisplayName => "UDP broadcast";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("port", "UDP port", ParameterKind.Number) { DefaultValue = "6669" },
        new("payload", "Payload", ParameterKind.Multiline) { Help = "Text or JSON to send" },
        new("host", "Address") { DefaultValue = Broadcast, Help = "255.255.255.255 broadcasts on the local network; 127.0.0.1 is this computer" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var port = p.GetInt("port");
        if (port is < 1 or > 65535) throw new ArgumentException($"'{p["port"]}' is not a UDP port (1-65535)");
        var host = p["host"].Trim().Length > 0 ? p["host"].Trim() : Broadcast;
        var address = System.Net.IPAddress.TryParse(host, out var ip)
            ? ip
            : (await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false)).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
              ?? throw new ArgumentException($"Cannot resolve '{host}'");
        using var udp = new System.Net.Sockets.UdpClient(address.AddressFamily);
        udp.EnableBroadcast = true;
        var bytes = Encoding.UTF8.GetBytes(p["payload"]);
        context.Args["udpBytesSent"] = await udp.SendAsync(bytes, new System.Net.IPEndPoint(address, port), ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>Branches on whether a file (or folder) exists.</summary>
public sealed class FileExistsSubAction : ISubActionHandler
{
    public const string True = "True";
    public const string False = "False";

    public string TypeId => "core.fileExists";
    public string Category => SystemCategories.Files;
    public string DisplayName => "File exists";
    public IReadOnlyList<string> Branches { get; } = [True, False];
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("path", "File or folder"),
        new("kind", "Look for", ParameterKind.Choice) { DefaultValue = "File", Choices = ["File", "Folder", "Either"] },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var path = p["path"].Trim();
        var exists = p["kind"].ToLowerInvariant() switch
        {
            "folder" => Directory.Exists(path),
            "either" => File.Exists(path) || Directory.Exists(path),
            _ => File.Exists(path),
        };
        context.Args["fileExists"] = exists;
        return Task.FromResult(SubActionResult.TakeBranch(exists ? True : False));
    }
}

/// <summary>Deletes a file. A missing file is not an error unless asked.</summary>
public sealed class DeleteFileSubAction : ISubActionHandler
{
    public string TypeId => "core.deleteFile";
    public string Category => SystemCategories.Files;
    public string DisplayName => "Delete file";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        FileSteps.Path,
        new("failIfMissing", "Fail if missing", ParameterKind.Boolean) { DefaultValue = "false" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var path = FileSteps.RequirePath(p);
        var existed = File.Exists(path);
        if (!existed && p.GetBool("failIfMissing")) throw new FileNotFoundException($"File not found: {path}", path);
        if (existed) File.Delete(path);
        context.Args["fileDeleted"] = existed;
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>
/// Sends an HTTP request. Sets the result argument (body), httpStatusCode, httpSuccess, httpHeaders and, on a
/// network failure or timeout, httpError. Header values are never logged.
/// </summary>
public sealed class HttpRequestSubAction(HttpClient http) : ISubActionHandler
{
    public static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];

    public string TypeId => "core.httpRequest";
    public string Category => SystemCategories.Network;
    public string DisplayName => "HTTP request";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("method", "Method", ParameterKind.Choice) { DefaultValue = "GET", Choices = Methods },
        new("url", "URL"),
        new("headers", "Headers", ParameterKind.Multiline) { Help = "One \"Name: value\" per line" },
        new("body", "Body", ParameterKind.Multiline),
        new("contentType", "Content type") { DefaultValue = "application/json" },
        new("timeout", "Timeout (seconds)", ParameterKind.Number) { DefaultValue = "30", Help = "At most 30 seconds (the shared client's limit)" },
        new("variable", "Store response in argument") { DefaultValue = "httpResponse" },
        new("parseJson", "Parse result as JSON", ParameterKind.Boolean) { DefaultValue = "false", Help = "Also sets %<argument>.<key>% for every JSON value" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var variable = FileSteps.Variable(p, "httpResponse");
        var method = new HttpMethod(string.IsNullOrWhiteSpace(p["method"]) ? "GET" : p["method"].Trim().ToUpperInvariant());
        if (!Uri.TryCreate(p["url"].Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The URL must be an absolute http:// or https:// address.");

        using var request = new HttpRequestMessage(method, uri);
        var body = p["body"];
        if (body.Length > 0 || method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = null;
            var contentType = p["contentType"].Trim();
            if (contentType.Length > 0) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        foreach (var (name, value) in HttpHeaderLines.Parse(p["headers"]))
        {
            if (request.Headers.TryAddWithoutValidation(name, value)) continue;
            if (request.Content is null) continue; // content headers without a body
            request.Content.Headers.Remove(name);
            request.Content.Headers.TryAddWithoutValidation(name, value);
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(p.GetDouble("timeout", 30), 1, 3600));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            context.Args[variable] = text;
            if (p.GetBool("parseJson")) ParseJson(context, variable, text);
            context.Args["httpStatusCode"] = (int)response.StatusCode;
            context.Args["httpSuccess"] = response.IsSuccessStatusCode;
            context.Args["httpHeaders"] = HttpHeaderLines.Format(response);
            context.Log.Debug("HTTP", $"{method} {uri.Host} -> {(int)response.StatusCode}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
        {
            var message = ex is OperationCanceledException ? $"Timed out after {timeout.TotalSeconds:0.#} s" : ex.Message;
            context.Args[variable] = "";
            context.Args["httpStatusCode"] = ex is HttpRequestException { StatusCode: { } status } ? (int)status : 0;
            context.Args["httpSuccess"] = false;
            context.Args["httpHeaders"] = "";
            context.Args["httpError"] = message;
            context.Log.Warn(context.Action.Name, $"HTTP {method} {uri.Host} failed: {message}");
        }
        return SubActionResult.Next;
    }

    /// <summary>Streamer.bot "Parse result as JSON": %variable.key% for every value; sets %httpJsonParsed%.</summary>
    internal static void ParseJson(ActionRunContext context, string variable, string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            foreach (var (key, value) in JsonPath.Flatten(document.RootElement, variable).ToList()) context.Args[key] = value;
            context.Args["httpJsonParsed"] = true;
        }
        catch (JsonException ex)
        {
            context.Args["httpJsonParsed"] = false;
            context.Log.Warn(context.Action.Name, $"The response is not valid JSON: {ex.Message}");
        }
    }
}

/// <summary>Shared input handling for the JSON sub-actions.</summary>
internal static class JsonSteps
{
    public static readonly ParameterDescriptor Input = new("json", "JSON", ParameterKind.Multiline)
    {
        Help = "JSON text; ignored when \"Read from argument\" is set",
    };

    public static readonly ParameterDescriptor Argument = new("argument", "Read from argument")
    {
        DefaultValue = "httpResponse",
        Help = "Name of an argument holding JSON (e.g. httpResponse); empty uses the JSON text",
    };

    public static readonly ParameterDescriptor Path = new("path", "Path") { DefaultValue = "$", Help = "e.g. $.data[0].name" };

    /// <summary>Selects the value, or returns false (logging why) when the JSON is invalid or the path is missing.</summary>
    public static bool TrySelect(ActionRunContext context, ParameterValues p, out JsonElement value)
    {
        value = default;
        var name = p["argument"].Trim().Trim('%');
        var text = name.Length > 0
            ? context.Args.GetValueOrDefault(name) switch { null => "", string s => s, var other => JsonSerializer.Serialize(other) }
            : p["json"];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            context.Log.Warn(context.Action.Name, $"Not valid JSON{(name.Length > 0 ? $" in argument {name}" : "")}: {ex.Message}");
            return false;
        }
        using (document)
        {
            if (!JsonPath.TrySelect(document.RootElement, p["path"], out var found)) return false;
            value = found.Clone();
            return true;
        }
    }
}

/// <summary>Reads one value from JSON into an argument.</summary>
public sealed class JsonGetValueSubAction : ISubActionHandler
{
    public string TypeId => "core.jsonGetValue";
    public string Category => SystemCategories.Json;
    public string DisplayName => "JSON get value";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [JsonSteps.Argument, JsonSteps.Input, JsonSteps.Path, FileSteps.VariableParameter("jsonValue")];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var found = JsonSteps.TrySelect(context, p, out var value);
        context.Args[FileSteps.Variable(p, "jsonValue")] = found ? JsonPath.ToArgument(value) : "";
        context.Args["jsonFound"] = found;
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Counts the elements of a JSON array (or the properties of an object).</summary>
public sealed class JsonCountSubAction : ISubActionHandler
{
    public string TypeId => "core.jsonCount";
    public string Category => SystemCategories.Json;
    public string DisplayName => "JSON array count";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [JsonSteps.Argument, JsonSteps.Input, JsonSteps.Path, FileSteps.VariableParameter("jsonCount")];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var count = JsonSteps.TrySelect(context, p, out var value) ? JsonPath.Count(value) : null;
        context.Args[FileSteps.Variable(p, "jsonCount")] = count ?? 0;
        context.Args["jsonFound"] = count is not null;
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Enables, disables or toggles a file tail (or any watcher) by name.</summary>
public sealed class SetWatcherStateSubAction(WatcherService watchers) : ISubActionHandler
{
    public string TypeId => "core.setWatcherState";
    public string Category => Categories.System;
    public string DisplayName => "Set watcher state";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("watcher", "Watcher or file tail")
        {
            Suggestions = () => watchers.Names(s => s.FileTails.Cast<WatcherDefinition>().Concat(s.FileWatchers).Concat(s.ProcessWatchers)),
        },
        new("state", "State", ParameterKind.Choice) { DefaultValue = "Enable", Choices = ["Enable", "Disable", "Toggle"] },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        bool? enabled = p["state"].ToLowerInvariant() switch { "disable" => false, "toggle" => null, _ => true };
        if (!watchers.SetEnabled(p["watcher"], enabled))
            context.Log.Warn(context.Action.Name, $"No watcher named \"{p["watcher"]}\"");
        return Task.FromResult(SubActionResult.Next);
    }
}
