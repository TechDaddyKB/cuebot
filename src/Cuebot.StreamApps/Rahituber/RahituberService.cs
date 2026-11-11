using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Rahituber;

/// <summary>
/// RahiTuber through "Control States via HTTP" (Preferences > Integration): <c>GET /state?[state,active]</c> where state
/// is the state's name (a JSON string) or its position counted from 0, and active is 1 (like pressing its hotkey) or
/// anything else (like letting go; only matters for "while held" states). RahiTuber answers
/// <c>{"state":..,"active":n}</c> (application/json) to every /state request before matching it, so Cuebot can only tell
/// that RahiTuber got it; with RahiTuber's folder set, the avatar file lists the states and RahiTuber_Log.txt confirms
/// each request. There is no connection to keep open.
/// </summary>
/// <remarks>
/// RahiTuber's request queue is not thread-safe (pushed on its HTTP thread, popped on its render thread), so only one
/// request is in flight at a time, with a short gap after each; presses of the same state are spaced past RahiTuber's
/// 0.2 s repeat guard.
/// </remarks>
public sealed partial class RahituberService(JsonFile<StreamAppsSettings> settings, AppLog log, HttpClient http) : IConnectionStatus
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>The name used by the connection check; no avatar has a state called this, so RahiTuber drops it.</summary>
    internal const string ProbeState = "Cuebot connection check";

    // RahiTuber's small JSON reader may not decode \u escapes, so only quotes and backslashes are escaped.
    private static readonly JsonSerializerOptions PlainJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, long> _lastSentByState = new(StringComparer.Ordinal);
    private long _lastSentAny;
    private int _sendNumber;
    private volatile AvatarCache? _avatarCache;

    private sealed record AvatarCache(string Key, RahituberAvatar? Avatar, string? Problem);

    public string Name => "RahiTuber";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Nothing sent yet";
    public event Action? StatusChanged;

    /// <summary>The gap kept after every request (RahiTuber reads its queue once per frame, about every 16 ms).</summary>
    internal TimeSpan RequestGap { get; set; } = TimeSpan.FromMilliseconds(20);

    /// <summary>How long Cuebot looks in RahiTuber_Log.txt for a request (tests use less).</summary>
    internal TimeSpan LogWait { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The last background look in RahiTuber_Log.txt (tests wait for it).</summary>
    internal Task LastLogCheck { get; private set; } = Task.CompletedTask;

    public RahituberSettings Settings => settings.Value.Rahituber;

    public void SaveSettings() => settings.ScheduleSave();

    public Uri BaseUrl
    {
        get
        {
            var host = Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "127.0.0.1";
            if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]";
            return new Uri($"http://{host}:{Port}/");
        }
    }

    private int Port => Settings.Port is > 0 and < 65536 ? Settings.Port : 8000;

    /// <summary>RahiTuber's folder (where config.xml and RahiTuber_Log.txt are), or null when not set.</summary>
    public string? Folder => Settings.Folder.Trim() is { Length: > 0 } folder ? folder : null;

    /// <summary>
    /// The avatar RahiTuber has loaded, read from its folder (re-read when the files change), or null when the folder
    /// is not set or has no RahiTuber files (<see cref="AvatarProblem"/> says why).
    /// </summary>
    public RahituberAvatar? Avatar => LoadAvatar().Avatar;

    /// <summary>Why <see cref="Avatar"/> is null although the folder is set; null otherwise.</summary>
    public string? AvatarProblem => LoadAvatar().Problem;

    /// <summary>State names (or places, for states without a name) for the step's suggestions.</summary>
    public IEnumerable<string> StateTargets() => Avatar?.States.Select(s => s.Target) ?? [];

    private (RahituberAvatar? Avatar, string? Problem) LoadAvatar()
    {
        if (Folder is not { } folder) return (null, null);
        try
        {
            var config = Path.Combine(folder, RahituberFiles.ConfigFile);
            var path = RahituberFiles.AvatarPath(folder);
            var key = $"{folder}|{Stamp(config)}|{path}|{(path is null ? "" : Stamp(path))}";
            if (_avatarCache is { } cached && cached.Key == key) return (cached.Avatar, cached.Problem);
            var loaded = path is null
                ? new AvatarCache(key, null, $"No RahiTuber files (config.xml, lastLayers.xml) in {folder}")
                : new AvatarCache(key, new RahituberAvatar(path, RahituberFiles.ReadStates(path)), null);
            _avatarCache = loaded;
            return (loaded.Avatar, loaded.Problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return (null, $"Could not read RahiTuber's files: {ex.Message}");
        }

        static string Stamp(string file)
        {
            var info = new FileInfo(file);
            return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "-";
        }
    }

    /// <summary>What to send for a state, and the state it means in the avatar file (when known).</summary>
    internal (Uri Url, string Id, RahituberStateInfo? Info, string? Note) Prepare(string state, int active)
    {
        if (string.IsNullOrWhiteSpace(state)) throw new ArgumentException("State is required.");
        var trimmed = state.Trim();
        var avatar = Avatar;
        string target = trimmed;
        RahituberStateInfo? info = null;
        string? note = null;
        if (avatar is not null)
        {
            // RahiTuber compares exact text, so a name with spaces at its ends only matches when sent as it is.
            if (avatar.Find(state) is { } exact) (info, target) = (exact, state);
            else if (avatar.Find(trimmed) is { } found) info = found;
            else if (avatar.States.FirstOrDefault(s => s.Name.Length > 0 && s.Name.Trim() == trimmed) is { } spaced) (info, target) = (spaced, spaced.Name);
            else note = $"'{trimmed}' is not a state in {avatar.Name}.xml (names must match exactly, capitals too)";
        }
        var id = int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            ? index.ToString(CultureInfo.InvariantCulture)
            : JsonSerializer.Serialize(target, PlainJson);
        // Escaped, brackets and all: RahiTuber decodes into a buffer as long as the escaped query.
        var url = new Uri(BaseUrl, "state?" + Uri.EscapeDataString($"[{id},{active.ToString(CultureInfo.InvariantCulture)}]"));
        return (url, target, info, note);
    }

    /// <summary>The URL for a press (active) or let go, as sent (for tests).</summary>
    internal Uri StateUrl(string state, bool active) => Prepare(state, active ? 1 : 0).Url;

    /// <summary>Presses (active) or lets go of a state; throws when RahiTuber is not reachable.</summary>
    public async Task<RahituberStateInfo?> SetStateAsync(string state, bool active, CancellationToken ct = default)
    {
        var (info, note, target, logMark) = await SendAsync(state, active ? 1 : 0, ct).ConfigureAwait(false);
        var text = $"RahiTuber at {BaseUrl.Authority} got '{state.Trim()}' ({(active ? "pressed" : "let go")})";
        if (info is not null)
        {
            if (info.Type == RahituberStateType.Permanent && active)
                note = Join(note, "it is a Permanent state, so it changed the avatar's defaults for good");
            else if (!active && info.Type != RahituberStateType.Held)
                note = Join(note, $"letting go only matters for \"while held\" states; '{info.Target}' is {info.Type}");
            if (info.Condition != RahituberStateCondition.Always)
                note = Join(note, $"RahiTuber only switches it {(info.Condition == RahituberStateCondition.WhileTalking ? "while you talk" : "while you are quiet")}");
            if (!info.Enabled) note = Join(note, "it is switched off in RahiTuber, so nothing happens");
        }
        Report(text, note);
        if (logMark is { } mark)
        {
            var number = Interlocked.Increment(ref _sendNumber);
            LastLogCheck = ConfirmFromLogAsync(mark, target, active ? 1 : 0, number);
        }
        return info;
    }

    /// <summary>
    /// Presses a state, waits, then lets go (for "while held" states). The let go is sent even when the action is
    /// stopped during the wait, so a held state never stays on.
    /// </summary>
    public async Task HoldStateAsync(string state, TimeSpan duration, CancellationToken ct = default)
    {
        var info = await SetStateAsync(state, true, ct).ConfigureAwait(false);
        if (info is { Type: not RahituberStateType.Held })
            log.Info(Name, $"Hold on '{info.Target}' works like a press: it is a {info.Type} state, so letting go does nothing" +
                (info.Type == RahituberStateType.Toggle && info.Timeout is { } t ? $" (it turns off by itself after {t.ToString("0.##", CultureInfo.InvariantCulture)} s)" : ""));
        try
        {
            await Task.Delay(duration, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await SetStateAsync(state, false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                // The stop wins; the failed let go is in the log and the status.
                log.Warn(Name, $"Could not let go of '{state.Trim()}' after the action stopped: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Checks that RahiTuber (and not another program) answers, with a state name no avatar uses, so nothing changes.
    /// Returns what was found, such as "RahiTuber 14.51 answers at 127.0.0.1:8000; avatar Demo: 3 states".
    /// </summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        await SendAsync(ProbeState, -1, ct).ConfigureAwait(false);
        var folder = Folder;
        var version = folder is null ? null : RahituberFiles.Version(folder);
        var text = $"RahiTuber{(version is null ? "" : " " + version)} answers at {BaseUrl.Authority}";
        var (avatar, problem) = LoadAvatar();
        if (avatar is not null) text += $"; avatar {avatar.Name}: {avatar.States.Count} state{(avatar.States.Count == 1 ? "" : "s")}";
        if (avatar is { States.Count: 0 })
            text += " (requests wait in RahiTuber until the avatar has states)";
        SetStatus(ConnectionState.Connected, problem is null ? text : $"{text}; {problem}");
        return StatusMessage;
    }

    private static string Join(string? a, string b) => a is null ? b : $"{a}; {b}";

    private void Report(string text, string? note)
    {
        SetStatus(ConnectionState.Connected, note is null ? text : $"{text}. Note: {note}");
        if (note is not null) log.Info(Name, $"{text}. Note: {note}");
    }

    /// <summary>Sends one request, one at a time, with the gaps RahiTuber needs; checks the answer is RahiTuber's.</summary>
    private async Task<(RahituberStateInfo? Info, string? Note, string Target, long? LogMark)> SendAsync(string state, int active, CancellationToken ct)
    {
        var (url, target, info, note) = Prepare(state, active);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Presses of the same state closer than RahiTuber's 0.2 s repeat guard would be swallowed.
            var wait = Remaining(_lastSentAny, RequestGap);
            if (info is not { Type: RahituberStateType.Held } && Settings.SameStateGapMs > 0 && _lastSentByState.TryGetValue(target, out var last))
                wait = Max(wait, Remaining(last, TimeSpan.FromMilliseconds(Settings.SameStateGapMs)));
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);

            var logMark = LogLength();
            try
            {
                await GetAsync(url, active, ct).ConfigureAwait(false);
            }
            finally
            {
                _lastSentAny = Stopwatch.GetTimestamp();
            }
            // Names can come from chat, so forget states not sent for a while.
            if (_lastSentByState.Count >= 256)
                foreach (var old in _lastSentByState.Where(p => Stopwatch.GetElapsedTime(p.Value) > TimeSpan.FromSeconds(10)).Select(p => p.Key).ToList())
                    _lastSentByState.Remove(old);
            _lastSentByState[target] = _lastSentAny;
            return (info, note, target, logMark);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TimeSpan Remaining(long since, TimeSpan gap) =>
        since == 0 ? TimeSpan.Zero : gap - Stopwatch.GetElapsedTime(since);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private async Task GetAsync(Uri url, int active, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new StreamAppRequestException(
                    $"{BaseUrl.Authority} answered {(int)response.StatusCode}, which RahiTuber never does. Another program may have " +
                    $"port {Port}: pick another port in both apps.");
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!IsRahituberReply(response.Content.Headers.ContentType?.MediaType, body))
                throw new StreamAppRequestException(
                    $"Something else answers at {BaseUrl.Authority}, not RahiTuber. RahiTuber does not say so when another program " +
                    $"already has port {Port}: pick another port in both apps (RahiTuber: Preferences > Integration).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Fail("RahiTuber did not answer in time");
            throw new TimeoutException("RahiTuber did not answer in time. Is RahiTuber open?");
        }
        catch (HttpRequestException ex)
        {
            Fail($"{BaseUrl.Authority} not reachable ({ex.Message})");
            throw new StreamAppRequestException(
                $"RahiTuber is not reachable at {BaseUrl.Authority}. Is it open, with \"Control States via HTTP\" on and the same port? " +
                $"If it is, another program may have port {Port} (RahiTuber does not say so): pick another port in both apps. " +
                $"{RahituberFiles.LogFile} in RahiTuber's folder lists every request it gets. ({ex.Message})");
        }
        catch (StreamAppRequestException ex)
        {
            Fail(ex.Message);
            throw;
        }
    }

    /// <summary>
    /// RahiTuber's answer: application/json with an "active" number. (Its "state" value is not reliable: RahiTuber
    /// prints the text with %d.)
    /// </summary>
    internal static bool IsRahituberReply(string? mediaType, string body) =>
        string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) && ActiveField().IsMatch(body);

    [GeneratedRegex("\"active\"\\s*:\\s*-?\\d+")]
    private static partial Regex ActiveField();

    private string? LogPath => Folder is { } folder ? Path.Combine(folder, RahituberFiles.LogFile) : null;

    private long? LogLength()
    {
        try { return LogPath is { } path && File.Exists(path) ? new FileInfo(path).Length : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Looks in RahiTuber_Log.txt for "State change added to queue: id = active" written after <paramref name="mark"/>,
    /// and says so in the status (only for the latest request).
    /// </summary>
    private async Task ConfirmFromLogAsync(long mark, string id, int active, int number)
    {
        var line = $"State change added to queue: {id} = {active.ToString(CultureInfo.InvariantCulture)}";
        var deadline = Stopwatch.GetTimestamp();
        while (true)
        {
            if (ReadLogFrom(mark) is { } text && text.Split('\n').Any(l => l.TrimEnd('\r') == line))
            {
                if (number == Volatile.Read(ref _sendNumber) && State == ConnectionState.Connected)
                    SetStatus(ConnectionState.Connected, StatusMessage + $" - confirmed by {RahituberFiles.LogFile}");
                return;
            }
            if (Stopwatch.GetElapsedTime(deadline) >= LogWait) break;
            await Task.Delay(100).ConfigureAwait(false);
        }
        log.Debug(Name, $"'{id}' did not show up in {RahituberFiles.LogFile} within {LogWait.TotalSeconds:0.#} s");
    }

    private string? ReadLogFrom(long mark)
    {
        try
        {
            if (LogPath is not { } path) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // A new log (RahiTuber restarted) is shorter than the mark: read it from the start.
            stream.Seek(stream.Length < mark ? 0 : mark, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void Fail(string message)
    {
        log.Warn(Name, message);
        SetStatus(ConnectionState.Error, message);
    }

    private void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }
}
