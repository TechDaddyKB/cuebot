using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Rahituber;

/// <summary>
/// RahiTuber through "Control States via HTTP" (Preferences > Integration): <c>GET /state?[state,active]</c> where state
/// is the state's name (a JSON string) or its position counted from 0, and active is 1 (like pressing its hotkey) or 0
/// (like letting go; only matters for "while held" states). RahiTuber answers 200 to every request, also for a state it
/// does not have, so Cuebot can only tell that RahiTuber got it. There is no connection to keep open.
/// </summary>
public sealed class RahituberService(JsonFile<StreamAppsSettings> settings, AppLog log, HttpClient http) : IConnectionStatus
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // RahiTuber's small JSON reader may not decode \u escapes, so only quotes and backslashes are escaped.
    private static readonly JsonSerializerOptions PlainJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string Name => "RahiTuber";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Nothing sent yet";
    public event Action? StatusChanged;

    public RahituberSettings Settings => settings.Value.Rahituber;

    public void SaveSettings() => settings.ScheduleSave();

    public Uri BaseUrl
    {
        get
        {
            var host = Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "127.0.0.1";
            if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]";
            return new Uri($"http://{host}:{(Settings.Port is > 0 and < 65536 ? Settings.Port : 8000)}/");
        }
    }

    /// <summary>The request for a state: a whole number is a position (0 = top), anything else a name.</summary>
    internal Uri StateUrl(string state, bool active)
    {
        var target = Steps.Require(state, "State");
        var id = int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            ? index.ToString(CultureInfo.InvariantCulture)
            : JsonSerializer.Serialize(target, PlainJson);
        return new Uri(BaseUrl, "state?" + Uri.EscapeDataString($"[{id},{(active ? 1 : 0)}]"));
    }

    /// <summary>Presses (active) or lets go of a state; throws when RahiTuber is not reachable.</summary>
    public async Task SetStateAsync(string state, bool active, CancellationToken ct = default)
    {
        var url = StateUrl(state, active);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new StreamAppRequestException(
                    $"RahiTuber answered {(int)response.StatusCode}. Is \"Control States via HTTP\" on in its Preferences?");
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
                $"RahiTuber is not reachable at {BaseUrl.Authority}. Is it open, with \"Control States via HTTP\" on? ({ex.Message})");
        }
        catch (StreamAppRequestException ex)
        {
            Fail(ex.Message);
            throw;
        }
        SetStatus(ConnectionState.Connected, $"RahiTuber at {BaseUrl.Authority} got '{state.Trim()}' ({(active ? "pressed" : "let go")})");
    }

    /// <summary>Presses a state, waits, then lets go (for "while held" states).</summary>
    public async Task HoldStateAsync(string state, TimeSpan duration, CancellationToken ct = default)
    {
        await SetStateAsync(state, true, ct).ConfigureAwait(false);
        await Task.Delay(duration, ct).ConfigureAwait(false);
        await SetStateAsync(state, false, ct).ConfigureAwait(false);
    }

    /// <summary>Checks RahiTuber answers, with a state name no avatar uses (RahiTuber ignores unknown states).</summary>
    public Task TestAsync(CancellationToken ct = default) => SetStateAsync("Cuebot test", false, ct);

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
