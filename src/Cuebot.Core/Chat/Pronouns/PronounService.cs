using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Chat.Pronouns;

/// <summary>A pronoun set from the alejo.io catalogue, e.g. id "hehim", subject "He", object "Him".</summary>
/// <param name="Singular">Shown alone ("Any", "Other") rather than as "Subject/Object".</param>
public sealed record PronounInfo(string Id, string Subject, string Object, bool Singular)
{
    public string Display => Singular ? Subject : $"{Subject}/{Object}";
}

/// <summary>A Twitch user's pronouns: a primary set and optionally an alternate one ("He/They").</summary>
public sealed record UserPronouns(string Login, PronounInfo Primary, PronounInfo? Alt)
{
    public string Display => Alt is null ? Primary.Display : $"{Primary.Subject}/{Alt.Subject}";
}

/// <summary>
/// Looks up pronouns from the public alejo.io API (https://api.pronouns.alejo.io/v1: /pronouns for the catalogue,
/// /users/{login} per Twitch user; 404 = none set). Results, including "none", are cached in memory.
/// </summary>
public sealed class PronounService
{
    public const string DefaultBaseUrl = "https://api.pronouns.alejo.io/v1/";
    public static readonly TimeSpan UserTtl = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan CatalogueTtl = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly AppLog _log;
    private readonly TimeProvider _time;
    private readonly string _baseUrl;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, UserPronouns? Value)> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<UserPronouns?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _catalogueGate = new(1, 1);
    private (DateTimeOffset At, IReadOnlyDictionary<string, PronounInfo> Map)? _catalogue;

    public PronounService(HttpClient http, AppLog log, TimeProvider? time = null, string baseUrl = DefaultBaseUrl)
    {
        _http = http;
        _log = log;
        _time = time ?? TimeProvider.System;
        _baseUrl = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
    }

    /// <summary>Raised when a lookup finishes with pronouns that were not cached before.</summary>
    public event Action<UserPronouns>? PronounsResolved;

    /// <summary>A cached answer without network access: (true, value-or-null) when known, (false, null) otherwise.</summary>
    public (bool Known, UserPronouns? Pronouns) TryGetCached(string login)
    {
        login = Normalize(login);
        return _users.TryGetValue(login, out var hit) && _time.GetUtcNow() - hit.At < UserTtl ? (true, hit.Value) : (false, null);
    }

    /// <summary>The user's pronouns, or null when they have none set or the service cannot be reached.</summary>
    public Task<UserPronouns?> GetAsync(string login, CancellationToken ct = default)
    {
        login = Normalize(login);
        if (login.Length == 0) return Task.FromResult<UserPronouns?>(null);
        var (known, cached) = TryGetCached(login);
        if (known) return Task.FromResult(cached);
        var task = _inFlight.GetOrAdd(login, l => FetchAsync(l, ct));
        if (task.IsCompleted) _inFlight.TryRemove(KeyValuePair.Create(login, task));   // finished before it was stored
        return task;
    }

    private async Task<UserPronouns?> FetchAsync(string login, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync($"users/{Uri.EscapeDataString(login)}", ct).ConfigureAwait(false);
            UserPronouns? result = null;
            if (doc is not null)
            {
                var catalogue = await CatalogueAsync(ct).ConfigureAwait(false);
                var root = doc.RootElement;
                var primary = Lookup(catalogue, Str(root, "pronoun_id"));
                if (primary is not null) result = new UserPronouns(login, primary, Lookup(catalogue, Str(root, "alt_pronoun_id")));
            }
            _users[login] = (_time.GetUtcNow(), result);
            if (result is not null) PronounsResolved?.Invoke(result);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _log.Warn("Pronouns", $"Could not look up pronouns for {login}: {ex.Message}");
            return null;
        }
        finally
        {
            _inFlight.TryRemove(login, out _);
        }
    }

    /// <summary>The pronoun catalogue (id → info), cached for a day.</summary>
    public async Task<IReadOnlyDictionary<string, PronounInfo>> CatalogueAsync(CancellationToken ct = default)
    {
        if (_catalogue is { } c && _time.GetUtcNow() - c.At < CatalogueTtl) return c.Map;
        await _catalogueGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_catalogue is { } again && _time.GetUtcNow() - again.At < CatalogueTtl) return again.Map;
            using var doc = await GetJsonAsync("pronouns", ct).ConfigureAwait(false);
            var map = new Dictionary<string, PronounInfo>(StringComparer.OrdinalIgnoreCase);
            if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    var v = p.Value;
                    var id = Str(v, "name") is { Length: > 0 } n ? n : p.Name;
                    map[p.Name] = new PronounInfo(id, Str(v, "subject"), Str(v, "object"),
                        v.ValueKind == JsonValueKind.Object && v.TryGetProperty("singular", out var s) && s.ValueKind == JsonValueKind.True);
                }
            _catalogue = (_time.GetUtcNow(), map);
            return map;
        }
        finally
        {
            _catalogueGate.Release();
        }
    }

    public void ClearCache()
    {
        _users.Clear();
        _catalogue = null;
    }

    private static PronounInfo? Lookup(IReadOnlyDictionary<string, PronounInfo> catalogue, string id) =>
        id.Length == 0 ? null
        : catalogue.TryGetValue(id, out var info) ? info
        : new PronounInfo(id, id, "", true);   // unknown id: show it verbatim rather than nothing

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"alejo.io returned {(int)response.StatusCode}", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Normalize(string login) => login.Trim().TrimStart('@').ToLowerInvariant();
}
