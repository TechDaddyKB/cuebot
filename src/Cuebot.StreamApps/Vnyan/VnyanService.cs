using System.Net.Http.Json;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Vnyan;

/// <summary>
/// VNyan through its HTTP API: <c>POST /</c> with <c>{"action":"&lt;name&gt;","payload":{..}}</c> fires every
/// "API Message" node whose name is that action, with the payload's values as its text outputs. VNyan answers
/// <c>{"result":0}</c> once it accepted the message; it cannot tell whether any node used it. There is no
/// connection to keep open, so the status shows how the last message went.
/// </summary>
public sealed class VnyanService(JsonFile<StreamAppsSettings> settings, AppLog log, HttpClient http) : IConnectionStatus
{
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public string Name => "VNyan";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Nothing sent yet";
    public event Action? StatusChanged;

    public VnyanSettings Settings => settings.Value.Vnyan;

    public void SaveSettings() => settings.ScheduleSave();

    public Uri Url
    {
        get
        {
            var host = Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "127.0.0.1";
            if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]";
            return new Uri($"http://{host}:{(Settings.Port is > 0 and < 65536 ? Settings.Port : 8069)}/");
        }
    }

    /// <summary>Sends an API message; throws when VNyan is not reachable or refused it.</summary>
    public async Task SendAsync(string action, IReadOnlyDictionary<string, string> payload, CancellationToken ct = default)
    {
        var name = Steps.Require(action, "Action name");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.PostAsJsonAsync(Url, new { action = name, payload }, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new StreamAppRequestException($"VNyan answered {(int)response.StatusCode}: {Short(body)}");
            if (JsonUtil.TryParse(body) is { ValueKind: JsonValueKind.Object } json && json.Num("result") is { } result && result != 0)
                throw new StreamAppRequestException($"VNyan refused the message (result {result}).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Fail("VNyan did not answer in time");
            throw new TimeoutException("VNyan did not answer in time. Is VNyan open?");
        }
        catch (HttpRequestException ex)
        {
            Fail($"{Url.Authority} not reachable ({ex.Message})");
            throw new StreamAppRequestException($"VNyan is not reachable at {Url.Authority}. Is VNyan open? ({ex.Message})");
        }
        catch (StreamAppRequestException ex)
        {
            Fail(ex.Message);
            throw;
        }
        SetStatus(ConnectionState.Connected, $"VNyan at {Url.Authority} accepted '{name}'");
    }

    /// <summary>Sends a message no node listens to, to check VNyan answers.</summary>
    public Task TestAsync(CancellationToken ct = default) => SendAsync("CuebotTest", new Dictionary<string, string>(), ct);

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

    private static string Short(string text) => text.Length > 200 ? text[..200] + "…" : text;
}
