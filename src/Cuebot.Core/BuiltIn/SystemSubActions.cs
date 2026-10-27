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

public sealed class RunProgramSubAction : ISubActionHandler
{
    public string TypeId => "core.runProgram";
    public string Category => Categories.System;
    public string DisplayName => "Run program";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("path", "Program or file"),
        new("arguments", "Arguments"),
        new("workingDirectory", "Working directory"),
        new("wait", "Wait for exit", ParameterKind.Boolean) { DefaultValue = "false" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var info = new ProcessStartInfo(p["path"], p["arguments"]) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(p["workingDirectory"])) info.WorkingDirectory = p["workingDirectory"];
        using var process = Process.Start(info);
        if (process is not null && p.GetBool("wait"))
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            context.Args["exitCode"] = process.ExitCode;
        }
        return SubActionResult.Next;
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

/// <summary>Picks a random line of a text file.</summary>
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
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(FileSteps.RequirePath(p), ct).ConfigureAwait(false);
        var candidates = lines.Select((text, index) => (text, number: index + 1))
            .Where(l => !p.GetBool("skipEmpty", true) || !string.IsNullOrWhiteSpace(l.text))
            .ToArray();
        var variable = FileSteps.Variable(p, "randomLine");
        if (candidates.Length == 0)
        {
            context.Args[variable] = "";
            context.Args["lineNumber"] = 0;
        }
        else
        {
            var (text, number) = candidates[(random ?? Random.Shared).Next(candidates.Length)];
            context.Args[variable] = text;
            context.Args["lineNumber"] = number;
        }
        context.Args["lineCount"] = candidates.Length;
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
