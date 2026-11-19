using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Logging;
using Cuebot.Portal.Members;

namespace Cuebot.Portal.Relay;

/// <summary>
/// A push topic a feature offers to signed-in mod devices (services/portal/RELAY.md, "Push"). A device may subscribe only while
/// its member exists, is not paused, still has this device, and passes <see cref="Permission"/> and
/// <see cref="Authorize"/>; this is checked again whenever the member list changes and before every delivery.
/// </summary>
public sealed record PortalTopic(string Name)
{
    /// <summary>The permission the member's role needs (none: any signed-in member).</summary>
    public PortalPermission? Permission { get; init; }

    /// <summary>A further check on the member and their role. Called often: keep it cheap and side-effect free.</summary>
    public Func<PortalMember, PortalRole?, bool>? Authorize { get; init; }

    /// <summary>Checks the device's subscribe parameters: an error for the device, or null to accept them.</summary>
    public Func<JsonObject, string?>? CheckParams { get; init; }
}

/// <summary>One device's subscription, as a publisher's filter sees it. <see cref="Params"/> must not be changed.</summary>
public sealed record PortalSubscription(long Id, string Topic, Guid MemberId, Guid DeviceId, JsonObject Params);

/// <summary>Batching and backpressure limits of <see cref="PortalPush"/> (tests use their own).</summary>
public sealed record PortalPushLimits
{
    /// <summary>How often queued events are sent. <see cref="Timeout.InfiniteTimeSpan"/>: only on <see cref="PortalPush.FlushAsync"/>.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public int MaxEventsPerFrame { get; init; } = 100;

    /// <summary>Largest push frame (plaintext). Well under the relay's caps (services/portal/RELAY.md "Limits").</summary>
    public int MaxFrameBytes { get; init; } = 32 * 1024;

    /// <summary>Largest single event; <see cref="PortalPush.Publish"/> refuses bigger ones.</summary>
    public int MaxEventBytes { get; init; } = 16 * 1024;

    /// <summary>Frames per subscription per flush: with the defaults at most 1,600 events a second reach one device.</summary>
    public int MaxFramesPerFlush { get; init; } = 4;

    /// <summary>A subscription with more waiting than this is dropped to a resync frame instead of growing.</summary>
    public int MaxQueuedEvents { get; init; } = 500;

    public int MaxQueuedBytes { get; init; } = 256 * 1024;

    public int MaxSubscriptionsPerDevice { get; init; } = 8;

    public int MaxParamsBytes { get; init; } = 2 * 1024;
}

/// <summary>What features use to push events to subscribed mod devices. Registered as a singleton by <c>AddPortal</c>.</summary>
public interface IPortalPush
{
    /// <summary>Offers a topic (replacing one of the same name; existing subscriptions are checked against it).</summary>
    void Register(PortalTopic topic);

    /// <summary>Withdraws a topic; its subscriptions end.</summary>
    bool Unregister(string topic);

    /// <summary>
    /// Queues one event (any JSON, at most <see cref="PortalPushLimits.MaxEventBytes"/> serialized) for every
    /// subscription to <paramref name="topic"/> that <paramref name="to"/> accepts (all when null; called under a lock,
    /// keep it cheap). Never blocks on the network. Returns how many subscriptions it was queued for.
    /// </summary>
    /// <exception cref="ArgumentException">The event is larger than the limit.</exception>
    int Publish(string topic, JsonNode payload, Func<PortalSubscription, bool>? to = null);

    /// <summary>Live subscriptions to a topic (for example to stop producing events nobody reads).</summary>
    int Subscribers(string topic);
}

/// <summary>A device link the hub can send to: the relay client's encrypted link (or a test fake).</summary>
internal interface IPortalPushLink
{
    PortalLinkContext? Context { get; }

    /// <summary>False once the link closed (checked under the hub's lock, so a closing link gets no new subscription).</summary>
    bool IsOpen { get; }

    /// <summary>Encrypts and sends one plaintext message in order with the link's answers.</summary>
    Task SendAsync(byte[] plaintext, CancellationToken ct);
}

/// <summary>
/// Server push on the encrypted portal link (docs/design/UNIFIED_MOD_CHAT_PLAN.md, section 5.4). Devices send
/// <c>{"op":"subscribe","topic":"…","params":{…}}</c> and <c>{"op":"unsubscribe","sub":n}</c>; Cuebot sends
/// <c>{"push":"&lt;topic&gt;","sub":n,"events":[…]}</c> frames, batched every <see cref="PortalPushLimits.FlushInterval"/>.
/// A subscription that falls behind loses its queue and gets <c>{"push":…,"sub":n,"resync":true,"missed":k}</c>
/// instead of growing memory; one that loses its authorization gets <c>{"push":…,"sub":n,"ended":true,"error":"…"}</c>.
/// Nothing is stored: everything lives in memory for the device's connection.
/// </summary>
public sealed class PortalPush : IPortalPush, IDisposable
{
    private const string LogSource = "Mod portal";

    private readonly PortalDirectory _directory;
    private readonly AppLog? _log;
    private readonly PortalPushLimits _limits;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PortalTopic> _topics = new(StringComparer.Ordinal);
    private readonly Dictionary<IPortalPushLink, Device> _devices = [];
    private readonly Timer? _timer;
    private readonly CancellationTokenSource _stop = new();
    private long _nextId;
    private int _flushing;

    public PortalPush(PortalDirectory directory, AppLog? log = null, PortalPushLimits? limits = null)
    {
        _directory = directory;
        _log = log;
        _limits = limits ?? new PortalPushLimits();
        _directory.Changed += Revalidate;
        if (_limits.FlushInterval > TimeSpan.Zero && _limits.FlushInterval != Timeout.InfiniteTimeSpan)
            _timer = new Timer(_ => _ = FlushQuietlyAsync(), null, _limits.FlushInterval, _limits.FlushInterval);
    }

    public PortalPushLimits Limits => _limits;

    public void Register(PortalTopic topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic.Name);
        lock (_gate) _topics[topic.Name] = topic;
        Revalidate();
    }

    public bool Unregister(string topic)
    {
        bool removed;
        lock (_gate) removed = _topics.Remove(topic);
        if (removed) Revalidate();
        return removed;
    }

    public int Subscribers(string topic)
    {
        lock (_gate) return _devices.Values.Sum(d => d.Subs.Count(s => s.Topic == topic));
    }

    public int Publish(string topic, JsonNode payload, Func<PortalSubscription, bool>? to = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (bytes.Length > _limits.MaxEventBytes)
            throw new ArgumentException($"A push event is {bytes.Length} bytes; the limit is {_limits.MaxEventBytes}.", nameof(payload));
        var count = 0;
        lock (_gate)
        {
            if (!_topics.ContainsKey(topic)) return 0;
            foreach (var device in _devices.Values)
                foreach (var sub in device.Subs)
                {
                    if (sub.Topic != topic || (to is not null && !to(sub.View))) continue;
                    sub.Pending.Enqueue(bytes);
                    sub.PendingBytes += bytes.Length;
                    if (sub.Pending.Count > _limits.MaxQueuedEvents || sub.PendingBytes > _limits.MaxQueuedBytes)
                    {
                        // The device is not keeping up: forget the queue and tell it to load the current state again.
                        sub.Missed += sub.Pending.Count;
                        sub.Pending.Clear();
                        sub.PendingBytes = 0;
                    }
                    count++;
                }
        }
        return count;
    }

    // ---- The device side (called by the relay client's links) ----

    /// <summary>
    /// The answer to a <c>subscribe</c> or <c>unsubscribe</c> request, or null for any other message (the link's
    /// handler takes those). <paramref name="answered"/> must be called once the answer was sent: only then do the
    /// subscription's frames start, so a device always sees its answer first.
    /// </summary>
    internal byte[]? TryHandle(IPortalPushLink link, byte[] message, out Action? answered)
    {
        answered = null;
        if (message.AsSpan().IndexOf("subscribe"u8) < 0) return null;
        JsonObject request;
        try { request = JsonNode.Parse(message) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return null; }
        var op = Str(request, "op");
        if (op is not ("subscribe" or "unsubscribe")) return null;
        var req = request["req"]?.DeepClone();
        JsonObject answer;
        if (op == "unsubscribe")
        {
            var id = request["sub"] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
            lock (_gate)
                if (_devices.TryGetValue(link, out var device)) device.Subs.RemoveAll(s => s.View.Id == id);
            answer = new JsonObject { ["ok"] = true };
        }
        else
        {
            answer = Subscribe(link, request, out var created);
            if (created is not null)
                answered = () => { lock (_gate) created.Active = true; };
        }
        if (req is not null) answer["req"] = req;
        return JsonSerializer.SerializeToUtf8Bytes(answer);
    }

    private JsonObject Subscribe(IPortalPushLink link, JsonObject request, out Sub? created)
    {
        created = null;
        var context = link.Context;
        if (context?.MemberId is not { } memberId || context.DeviceId is not { } deviceId)
            return new JsonObject { ["ok"] = false, ["error"] = "Sign in first.", ["signIn"] = true };
        var name = Str(request, "topic");
        PortalTopic? topic;
        lock (_gate) _topics.TryGetValue(name, out topic);
        if (topic is null) return Error($"Unknown topic '{name}'.");
        if (Refusal(topic, memberId, deviceId) is { } refused) return Error(refused);

        var parameters = request["params"] switch
        {
            JsonObject p => (JsonObject)p.DeepClone(),
            null => new JsonObject(),
            _ => null,
        };
        if (parameters is null) return Error("The subscription's params must be an object.");
        if (JsonSerializer.SerializeToUtf8Bytes(parameters).Length > _limits.MaxParamsBytes) return Error("The subscription's params are too long.");
        if (topic.CheckParams?.Invoke(parameters) is { } problem) return Error(problem);

        lock (_gate)
        {
            if (!link.IsOpen) return Error("The connection is closing.");
            if (!_devices.TryGetValue(link, out var device)) _devices[link] = device = new Device(link);
            if (device.Subs.Count >= _limits.MaxSubscriptionsPerDevice) return Error("Too many subscriptions on this device.");
            var id = ++_nextId;
            created = new Sub(new PortalSubscription(id, name, memberId, deviceId, parameters));
            device.Subs.Add(created);
            return new JsonObject { ["ok"] = true, ["sub"] = id, ["topic"] = name };
        }
    }

    /// <summary>Forgets a device whose link closed (its subscriptions end with it).</summary>
    internal void RemoveLink(IPortalPushLink link)
    {
        lock (_gate) _devices.Remove(link);
    }

    /// <summary>Why this member's device may not receive a topic now, or null when it may.</summary>
    private string? Refusal(PortalTopic topic, Guid memberId, Guid deviceId)
    {
        var member = _directory.Member(memberId);
        if (member is null || member.Suspended) return "Your access was removed or paused by the streamer.";
        if (!HasDevice(member, deviceId)) return "This device was signed out.";
        var role = _directory.RoleOf(member);
        if (topic.Permission is { } p && role?.Permissions.Contains(p) != true) return "Your role does not include this.";
        if (topic.Authorize is { } check && !check(member, role)) return "Your role does not include this.";
        return null;
    }

    private static bool HasDevice(PortalMember member, Guid deviceId)
    {
        try
        {
            var devices = member.Devices;
            for (var i = 0; i < devices.Count; i++)
                if (devices[i].Id == deviceId) return true;
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;   // changed while reading: the Changed event that follows checks again
        }
    }

    /// <summary>Ends subscriptions whose device signed out, whose member was removed or paused, or that lost the right.</summary>
    private void Revalidate()
    {
        List<(Sub Sub, IPortalPushLink Link)> all;
        Dictionary<string, PortalTopic> topics;
        lock (_gate)
        {
            all = [.. _devices.Values.SelectMany(d => d.Subs.Select(s => (s, d.Link)))];
            topics = new(_topics);
        }
        foreach (var (sub, link) in all)
        {
            var reason = Check(sub, link, topics);
            if (reason is null) continue;
            lock (_gate)
            {
                if (!_devices.TryGetValue(link, out var device) || !device.Subs.Remove(sub)) continue;
                device.Notices.Add(Ended(sub, reason));
            }
        }
    }

    private string? Check(Sub sub, IPortalPushLink link, Dictionary<string, PortalTopic> topics)
    {
        var context = link.Context;
        if (context?.MemberId != sub.View.MemberId || context.DeviceId != sub.View.DeviceId) return "This device was signed out.";
        if (!topics.TryGetValue(sub.Topic, out var topic)) return "Cuebot no longer offers this.";
        return Refusal(topic, sub.View.MemberId, sub.View.DeviceId);
    }

    // ---- Sending ----

    private async Task FlushQuietlyAsync()
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return;
        try { await FlushAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log?.Warn(LogSource, $"Sending portal updates failed: {ex.Message}"); }
        finally { Volatile.Write(ref _flushing, 0); }
    }

    /// <summary>
    /// Sends what is queued: per subscription up to <see cref="PortalPushLimits.MaxFramesPerFlush"/> frames, a resync
    /// frame first when it fell behind. A device whose previous sends are still under way is skipped (its queue grows
    /// until it is dropped to a resync). Runs on a timer; tests call it directly.
    /// </summary>
    internal async Task FlushAsync()
    {
        Dictionary<string, PortalTopic> topics;
        List<(Device Device, List<Sub> Subs)> candidates;
        lock (_gate)
        {
            topics = new(_topics);
            candidates = [.. _devices.Values.Where(d => !d.Sending)
                .Select(d => (d, d.Subs.Where(s => s.Active && (s.Pending.Count > 0 || s.Missed > 0)).ToList()))
                .Where(x => x.Item2.Count > 0 || x.d.Notices.Count > 0)];
        }
        if (candidates.Count == 0) return;

        // Authorization is checked outside the lock (it reads the member list) and again before every delivery.
        var refused = candidates.SelectMany(c => c.Subs.Select(s => (c.Device, Sub: s, Reason: Check(s, c.Device.Link, topics))))
            .Where(x => x.Reason is not null).ToList();

        var work = new List<(Device Device, List<byte[]> Frames)>();
        lock (_gate)
        {
            foreach (var (device, sub, reason) in refused)
                if (device.Subs.Remove(sub)) device.Notices.Add(Ended(sub, reason!));
            foreach (var (device, subs) in candidates)
            {
                if (device.Sending || !_devices.ContainsKey(device.Link)) continue;
                var frames = new List<byte[]>(device.Notices);
                device.Notices.Clear();
                foreach (var sub in subs)
                {
                    if (!device.Subs.Contains(sub)) continue;
                    if (sub.Missed > 0)
                    {
                        frames.Add(Resync(sub));
                        sub.Missed = 0;
                    }
                    for (var n = 0; n < _limits.MaxFramesPerFlush && sub.Pending.Count > 0; n++) frames.Add(Batch(sub));
                }
                if (frames.Count == 0) continue;
                device.Sending = true;
                work.Add((device, frames));
            }
        }
        await Task.WhenAll(work.Select(w => SendAsync(w.Device, w.Frames))).ConfigureAwait(false);
    }

    private async Task SendAsync(Device device, List<byte[]> frames)
    {
        try
        {
            foreach (var frame in frames) await device.Link.SendAsync(frame, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or InvalidOperationException
            or System.Net.WebSockets.WebSocketException or IOException)
        {
            RemoveLink(device.Link);   // the link is gone
        }
        finally
        {
            lock (_gate) device.Sending = false;
        }
    }

    /// <summary>Takes events off the queue for one frame, within the event and byte caps (always at least one).</summary>
    private byte[] Batch(Sub sub)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Min(_limits.MaxFrameBytes, 4096));
        using (var w = new Utf8JsonWriter(buffer))
        {
            Head(w, sub);
            w.WriteStartArray("events");
            var size = 64 + sub.Topic.Length;
            var n = 0;
            while (sub.Pending.TryPeek(out var next) && n < _limits.MaxEventsPerFrame && (n == 0 || size + next.Length + 1 <= _limits.MaxFrameBytes))
            {
                sub.Pending.Dequeue();
                sub.PendingBytes -= next.Length;
                w.WriteRawValue(next, skipInputValidation: true);
                size += next.Length + 1;
                n++;
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] Resync(Sub sub) => Small(sub, w =>
    {
        w.WriteBoolean("resync", true);
        w.WriteNumber("missed", sub.Missed);
    });

    private static byte[] Ended(Sub sub, string reason) => Small(sub, w =>
    {
        w.WriteBoolean("ended", true);
        w.WriteString("error", reason);
    });

    private static byte[] Small(Sub sub, Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            Head(w, sub);
            body(w);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void Head(Utf8JsonWriter w, Sub sub)
    {
        w.WriteStartObject();
        w.WriteString("push", sub.Topic);
        w.WriteNumber("sub", sub.View.Id);
    }

    private static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

    private static string Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";

    public void Dispose()
    {
        _directory.Changed -= Revalidate;
        _timer?.Dispose();
        _stop.Cancel();
        _stop.Dispose();
        lock (_gate) _devices.Clear();
    }

    private sealed class Device(IPortalPushLink link)
    {
        public IPortalPushLink Link { get; } = link;
        public List<Sub> Subs { get; } = [];
        /// <summary>"ended" frames waiting for the next flush.</summary>
        public List<byte[]> Notices { get; } = [];
        public bool Sending { get; set; }
    }

    private sealed class Sub(PortalSubscription view)
    {
        public PortalSubscription View { get; } = view;
        public string Topic => View.Topic;
        public Queue<byte[]> Pending { get; } = new();
        public int PendingBytes { get; set; }
        public long Missed { get; set; }
        /// <summary>False until the subscribe answer went out; events queue meanwhile.</summary>
        public bool Active { get; set; }
    }
}
