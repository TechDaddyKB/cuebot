using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Desktop.Notifications;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Desktop.Clipboard;

/// <summary>Text clipboard access. The desktop app implements it with Avalonia's clipboard; headless keeps the no-op.</summary>
public interface IClipboardService
{
    bool IsAvailable { get; }

    Task SetTextAsync(string text, CancellationToken ct = default);

    /// <returns>The clipboard text, or null when it holds no text.</returns>
    Task<string?> GetTextAsync(CancellationToken ct = default);
}

public sealed class NullClipboardService : IClipboardService
{
    public const string Reason = "The clipboard is not available (headless mode or no desktop session)";

    public bool IsAvailable => false;
    public Task SetTextAsync(string text, CancellationToken ct = default) => throw new InvalidOperationException(Reason);
    public Task<string?> GetTextAsync(CancellationToken ct = default) => throw new InvalidOperationException(Reason);
}

/// <summary>
/// When "Watch clipboard" is on (desktop.json), polls the clipboard text about once a second (ticked by the timer
/// service) and publishes "desktop.clipboardChanged" (clipboardText) when it changes.
/// </summary>
public sealed class ClipboardMonitor(Func<IClipboardService> clipboard, JsonFile<DesktopSettings>? settings, EventBus bus, AppLog log)
    : IPeriodicService
{
    private int _busy;
    private string? _last;
    private bool _primed;

    void IPeriodicService.Tick(DateTimeOffset now) => _ = PollAsync();

    /// <summary>One poll (public for tests). The first poll only records the current text.</summary>
    public async Task PollAsync()
    {
        if (settings?.Value.WatchClipboard != true)
        {
            _primed = false;
            return;
        }
        var service = clipboard();
        if (!service.IsAvailable || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var text = await service.GetTextAsync();
            if (!_primed)
            {
                _primed = true;
                _last = text;
                return;
            }
            if (text is null || text == _last) return;
            _last = text;
            // Cuebot watches text only, so the Streamer.bot format list is always "Text".
            bus.Publish(new BotEvent(DesktopEvents.ClipboardChanged, new Dictionary<string, object?>
            {
                ["clipboardText"] = text,
                ["clipboard.text"] = text,
                ["containsText"] = true,
                ["containsImage"] = false,
                ["containsAudio"] = false,
                ["formats.count"] = 1,
                ["formats.0"] = "Text",
            }));
        }
        catch (Exception ex)
        {
            log.Debug("Clipboard", "Reading the clipboard failed: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }
}
