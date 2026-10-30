using System.Text.Json.Nodes;

namespace Cuebot.Core.Overlays;

/// <summary>
/// The alert queue of every overlay page, controlled from Cuebot: pause and resume (the alert playing finishes, new
/// ones wait), skip the alert playing, and mute alert sound. The state is sent to pages when they connect (in their
/// "hello") and as commands when it changes, so a page opened later starts paused or muted too. Not saved: Cuebot
/// starts with alerts playing and sound on.
/// </summary>
public sealed class OverlayAlertQueue(OverlayFeed feed)
{
    private readonly object _gate = new();
    private bool _paused;
    private bool _muted;

    /// <summary>Raised after <see cref="Paused"/> or <see cref="Muted"/> changed.</summary>
    public event Action? Changed;

    public bool Paused
    {
        get
        {
            lock (_gate) return _paused;
        }
        set => Set(ref _paused, value, value ? "pause" : "resume");
    }

    public bool Muted
    {
        get
        {
            lock (_gate) return _muted;
        }
        set => Set(ref _muted, value, value ? "mute" : "unmute");
    }

    /// <summary>Ends the alert playing on every page; the next one in the queue follows unless paused.</summary>
    public void Skip() => feed.Publish(OverlayMessage.Command("skip"));

    /// <summary><c>{paused, muted}</c> for the "hello" sent to a page that connects.</summary>
    public JsonObject Snapshot()
    {
        lock (_gate) return new JsonObject { ["paused"] = _paused, ["muted"] = _muted };
    }

    private void Set(ref bool field, bool value, string command)
    {
        lock (_gate)
        {
            if (field == value) return;
            field = value;
        }
        feed.Publish(OverlayMessage.Command(command));
        Changed?.Invoke();
    }
}
