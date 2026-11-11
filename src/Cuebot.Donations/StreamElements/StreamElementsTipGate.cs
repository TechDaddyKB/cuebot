using System.Text.Json;
using Cuebot.Core.Logging;

namespace Cuebot.Donations.StreamElements;

/// <summary>
/// Holds StreamElements tips until their moderation state allows them. A tip arrives twice: as a <c>tip</c> activity
/// (<c>channel.activities</c>, <c>data.tipId</c>) and as a tip document (<c>channel.tips</c> /
/// <c>channel.tips.moderation</c>, <c>_id</c>, <c>approved</c> = pending | allowed | rejected, <c>status</c>). The
/// activity is published as <c>streamelements.tip</c> only once its document says <c>allowed</c> (and the payment
/// <c>success</c>); a pending tip raises <c>streamelements.tipPending</c> and waits for a moderator, a rejected one raises
/// <c>streamelements.tipRejected</c> and never runs the tip trigger. When no document arrives within a short wait
/// (the tips topic is not readable, or the ids do not match) the tip is published with <c>tipApproved = unknown</c>.
/// With waiting switched off, tips are published at once unless their document already said <c>rejected</c>.
/// </summary>
internal sealed class StreamElementsTipGate(Func<DonationEvent, bool> publish, TimeSpan unknownWait, AppLog log)
{
    private const int MaxStates = 500;
    private const int MaxHeld = 200;

    private readonly object _gate = new();
    private readonly Dictionary<string, TipState> _states = new(StringComparer.Ordinal);
    private readonly Queue<string> _stateOrder = new();
    private readonly Dictionary<string, Held> _held = new(StringComparer.Ordinal);

    /// <summary>The "wait for tip approval" option.</summary>
    public bool WaitForApproval { get; set; } = true;

    /// <summary>True once <c>channel.tips</c> (or the moderation topic) is subscribed, so documents can be expected.</summary>
    public bool DocumentsExpected { get; set; }

    /// <summary>Tips waiting for a moderator (for tests and status).</summary>
    public int HeldCount
    {
        get { lock (_gate) return _held.Count; }
    }

    private sealed record TipState(string Approved, string Status, string ApprovedBy)
    {
        public bool Rejected => Approved == "rejected";
        public bool Ready => Approved is "allowed" or "" && Status is "success" or "";
    }

    private sealed class Held(DonationEvent tip)
    {
        public DonationEvent Tip { get; } = tip;
        public CancellationTokenSource? Timer { get; set; }
    }

    /// <summary>A mapped <c>tip</c> activity.</summary>
    public void OnActivityTip(DonationEvent tip)
    {
        var id = StreamElementsTopicMapper.Text(tip.Args.GetValueOrDefault("tipId"));
        var isTest = tip.Args.GetValueOrDefault("isTest") is true;
        if (id.Length == 0 || isTest)
        {
            Annotate(tip, null);
            publish(tip);
            return;
        }
        DonationEvent? release = null, rejected = null, pending = null;
        lock (_gate)
        {
            _states.TryGetValue(id, out var state);
            if (state is { Rejected: true })
            {
                rejected = Moderation(tip, StreamElementsEventTypes.TipRejected, state);
            }
            else if (!WaitForApproval || state is { Ready: true } || (state is null && !DocumentsExpected))
            {
                Annotate(tip, state);
                release = tip;
            }
            else if (!_held.ContainsKey(id))
            {
                var held = new Held(tip);
                _held[id] = held;
                Trim();
                if (state is null) held.Timer = StartTimer(id);
                else pending = Moderation(tip, StreamElementsEventTypes.TipPending, state);
            }
        }
        if (release is not null) publish(release);
        if (rejected is not null) publish(rejected);
        if (pending is not null) publish(pending);
    }

    /// <summary>A tip document from <c>channel.tips</c> or <c>channel.tips.moderation</c>.</summary>
    public void OnTipDocument(JsonElement doc)
    {
        var id = doc.Str("_id");
        if (id.Length == 0) return;
        var state = new TipState(doc.Str("approved"), doc.Str("status"), doc.Str("approvedBy"));
        DonationEvent? release = null, rejected = null, pending = null;
        lock (_gate)
        {
            if (!_states.ContainsKey(id)) _stateOrder.Enqueue(id);
            _states[id] = state;
            while (_stateOrder.Count > MaxStates) _states.Remove(_stateOrder.Dequeue());

            if (_held.TryGetValue(id, out var held))
            {
                if (state.Ready)
                {
                    Release(id, held);
                    Annotate(held.Tip, state);
                    release = held.Tip;
                }
                else if (state.Rejected)
                {
                    Release(id, held);
                    rejected = Moderation(held.Tip, StreamElementsEventTypes.TipRejected, state);
                }
                else
                {
                    held.Timer?.Cancel();
                    held.Timer = null;
                    pending = Moderation(held.Tip, StreamElementsEventTypes.TipPending, state);
                }
            }
            else if (state.Rejected)
            {
                rejected = StreamElementsEventMapper.TipDocument(doc, StreamElementsEventTypes.TipRejected);
            }
            else if (!state.Ready)
            {
                pending = StreamElementsEventMapper.TipDocument(doc, StreamElementsEventTypes.TipPending);
            }
        }
        if (release is not null) publish(release);
        if (rejected is not null) publish(rejected);
        if (pending is not null) publish(pending);
    }

    /// <summary>Publishes every held tip whose state is still unknown (e.g. the tips topic was refused).</summary>
    public void ReleaseUnknown()
    {
        List<DonationEvent> released = [];
        lock (_gate)
        {
            foreach (var (id, held) in _held.ToArray())
            {
                if (_states.ContainsKey(id)) continue;
                Release(id, held);
                Annotate(held.Tip, null);
                released.Add(held.Tip);
            }
        }
        foreach (var tip in released) publish(tip);
    }

    private CancellationTokenSource StartTimer(string id)
    {
        var cts = new CancellationTokenSource();
        _ = Task.Delay(unknownWait, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            DonationEvent? release = null;
            lock (_gate)
            {
                if (_held.TryGetValue(id, out var held) && held.Timer == cts && !_states.ContainsKey(id))
                {
                    Release(id, held);
                    Annotate(held.Tip, null);
                    release = held.Tip;
                }
            }
            if (release is null) return;
            log.Warn(StreamElementsEventMapper.Service, $"No moderation state arrived for tip {id}; publishing it");
            publish(release);
        }, TaskScheduler.Default);
        return cts;
    }

    private void Release(string id, Held held)
    {
        held.Timer?.Cancel();
        held.Timer = null;
        _held.Remove(id);
    }

    /// <summary>Keeps at most <see cref="MaxHeld"/> tips waiting (a moderator who never decides must not leak memory).</summary>
    private void Trim()
    {
        while (_held.Count > MaxHeld)
        {
            var (oldest, held) = _held.First();
            Release(oldest, held);
            log.Warn(StreamElementsEventMapper.Service, $"Dropped tip {oldest}: too many tips are waiting for approval");
        }
    }

    private static void Annotate(DonationEvent tip, TipState? state)
    {
        tip.Args["tipApproved"] = state is null ? (tip.Args.GetValueOrDefault("isTest") is true ? "allowed" : "unknown") : state.Approved.Length > 0 ? state.Approved : "allowed";
        tip.Args["tipStatus"] = state?.Status ?? "";
        tip.Args["approvedBy"] = state?.ApprovedBy ?? "";
    }

    /// <summary>A pending/rejected event made from the tip activity's args.</summary>
    private static DonationEvent Moderation(DonationEvent tip, string type, TipState state)
    {
        var args = new Dictionary<string, object?>(tip.Args, StringComparer.OrdinalIgnoreCase)
        {
            ["tipApproved"] = state.Approved,
            ["tipStatus"] = state.Status,
            ["approvedBy"] = state.ApprovedBy,
        };
        var id = StreamElementsTopicMapper.Text(tip.Args.GetValueOrDefault("tipId"));
        return new DonationEvent(type, [$"{type}:{id}"], args);
    }
}
