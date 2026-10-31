using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cuebot.Core;
using Cuebot.Core.Storage;

namespace Cuebot.App.Diagnostics;

/// <summary>Report settings, stored as <c>reports.json</c> in the data folder.</summary>
public sealed class ReportSettings
{
    /// <summary>
    /// A random id for this install (32 hex characters), made on first use. It only lets the report intake limit how
    /// often one install sends; issues show a short hash of it, never the id.
    /// </summary>
    public string InstallId { get; set; } = "";

    /// <summary>Send a crash report on the next start without asking (off until the user turns it on).</summary>
    public bool SendCrashesAutomatically { get; set; }
}

public sealed partial class ReportSettingsStore
{
    public const string FileName = "reports.json";
    private readonly object _gate = new();

    public ReportSettingsStore(AppPaths paths)
    {
        Path = paths.File(FileName);
        Settings = Load(Path);
        if (!IdPattern().IsMatch(Settings.InstallId))
        {
            Settings.InstallId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            Save();
        }
    }

    public string Path { get; }

    public ReportSettings Settings { get; }

    /// <summary>Saves, ignoring IO errors (the setting simply does not stick).</summary>
    public void Save()
    {
        lock (_gate)
        {
            try
            {
                var temp = Path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Settings, Json.Options));
                File.Move(temp, Path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static ReportSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ReportSettings>(File.ReadAllText(path), Json.Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ReportSettings();
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex IdPattern();
}

/// <summary>A crash's identity across installs: the root exception's type and its top stack frames' methods.</summary>
public static partial class CrashSignature
{
    public const int Frames = 5;

    /// <summary>16 hex characters; the same crash gives the same signature whatever the paths, line numbers or messages.</summary>
    public static string Of(ExceptionInfo exception)
    {
        var root = exception.Root;
        var frames = root.StackTrace.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => Frame().Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .Take(Frames);
        var text = root.Type + "\n" + string.Join("\n", frames);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    // "   at Namespace.Type.Method(Args) in /path/File.cs:line 42" → "Namespace.Type.Method(Args)"
    [GeneratedRegex(@"^\s*at\s+(.+?)(?:\s+in\s+.+)?$")]
    private static partial Regex Frame();
}

/// <summary>What the intake answered: the issue (new, or the open one a repeat crash was added to) or why not.</summary>
public sealed record ReportResult(bool Sent, string Url, int Number, bool Duplicate, string Error)
{
    public static ReportResult Failed(string error) => new(false, "", 0, false, error);
}

/// <summary>
/// Sends a report to Cuebot's report intake (<see cref="SupportLinks.ReportEndpoint"/>), which files it as a GitHub
/// issue so streamers need no GitHub account. Only the text the user saw in the preview is sent, plus the version, OS
/// and this install's random id.
/// </summary>
public sealed class ReportSender(HttpMessageHandler? handler = null, Uri? endpoint = null)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly Uri _endpoint = endpoint ?? new Uri(SupportLinks.ReportEndpoint);

    /// <summary>The longest body the intake accepts (it refuses longer ones).</summary>
    public const int MaxBody = 60_000;

    public async Task<ReportResult> SendAsync(string kind, string title, string body, string version, string os, string installId,
        string? signature = null, CancellationToken ct = default)
    {
        if (body.Length > MaxBody)
            body = body[..(MaxBody - 200)] + "\n\n_(The rest of the report was cut to fit; the full report is in the reports folder.)_";
        var payload = new JsonObject
        {
            ["kind"] = kind, ["title"] = title.Trim(), ["body"] = body, ["version"] = version, ["os"] = os, ["installId"] = installId,
        };
        if (signature is not null) payload["signature"] = signature;
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout;
        try
        {
            using var response = await http.PostAsJsonAsync(_endpoint, payload, ct).ConfigureAwait(false);
            var json = await ReadAsync(response, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = json?["retryAfter"]?.GetValue<int>() ?? 3600;
                return ReportResult.Failed($"Too many reports were sent from this PC recently. Try again in about {Math.Max(1, (wait + 59) / 60)} minutes, or open the issue in your browser instead.");
            }
            if (!response.IsSuccessStatusCode)
                return ReportResult.Failed(json?["error"]?.GetValue<string>() is { Length: > 0 } error
                    ? $"The report was not taken: {error}"
                    : $"The report service answered {(int)response.StatusCode}. Try again later, or open the issue in your browser instead.");
            return new ReportResult(true, json?["url"]?.GetValue<string>() ?? "", json?["number"]?.GetValue<int>() ?? 0,
                json?["duplicate"]?.GetValue<bool>() ?? false, "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return ReportResult.Failed("Could not reach the report service. Check the internet connection, or open the issue in your browser instead.");
        }
    }

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
