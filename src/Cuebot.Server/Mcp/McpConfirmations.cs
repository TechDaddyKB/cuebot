using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Cuebot.Server.Mcp;

/// <summary>
/// One-time confirmation tokens for destructive tools: bound to the tool and its exact arguments, valid for a few
/// minutes, usable once.
/// </summary>
public sealed class McpConfirmations(TimeProvider? time = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public const string ArgumentName = "confirmationToken";
    private const int MaxPending = 256;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (string Tool, string Args, DateTimeOffset Expires)> _pending = new();

    public string Issue(string tool, JsonObject args)
    {
        Prune();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        _pending[token] = (tool, Canonical(args), _time.GetUtcNow() + Lifetime);
        return token;
    }

    /// <summary>True (and the token is used up) when it was issued for this tool and these arguments and has not expired.</summary>
    public bool TryConsume(string token, string tool, JsonObject args)
    {
        if (!_pending.TryGetValue(token, out var entry)) return false;
        if (entry.Expires < _time.GetUtcNow())
        {
            _pending.TryRemove(token, out _);
            return false;
        }
        if (entry.Tool != tool || entry.Args != Canonical(args)) return false;
        return _pending.TryRemove(token, out _);
    }

    private void Prune()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, value) in _pending)
        {
            if (value.Expires < now) _pending.TryRemove(key, out _);
        }
        if (_pending.Count < MaxPending) return;
        foreach (var key in _pending.OrderBy(p => p.Value.Expires).Take(_pending.Count - MaxPending + 1).Select(p => p.Key).ToArray())
            _pending.TryRemove(key, out _);
    }

    /// <summary>The arguments without the token, with object keys sorted, as compact JSON.</summary>
    internal static string Canonical(JsonObject args)
    {
        var copy = new JsonObject();
        foreach (var (key, value) in args.Where(p => p.Key != ArgumentName).OrderBy(p => p.Key, StringComparer.Ordinal))
            copy[key] = Sort(value);
        return copy.ToJsonString();

        static JsonNode? Sort(JsonNode? node) => node switch
        {
            JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
            JsonArray a => new JsonArray([.. a.Select(Sort)]),
            _ => node?.DeepClone(),
        };
    }
}
