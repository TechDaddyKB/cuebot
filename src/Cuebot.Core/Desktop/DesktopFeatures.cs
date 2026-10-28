using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Desktop.Clipboard;
using Cuebot.Core.Desktop.Hotkeys;
using Cuebot.Core.Desktop.Input;
using Cuebot.Core.Desktop.Notifications;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Desktop;

/// <summary>
/// Registration for global hotkeys, desktop notifications and the clipboard. The desktop app replaces the
/// <see cref="IClipboardService"/> and <see cref="IToastPresenter"/> defaults with Avalonia implementations by
/// registering its own after the core (the last registration wins).
/// </summary>
public static class DesktopFeatures
{
    public static IServiceCollection AddDesktopFeatures(this IServiceCollection services)
    {
        services.AddSettingsFile<DesktopSettings>("desktop.json");
        services.AddSettingsFile<HotkeySettings>("hotkeys.json");

        // Hotkeys: the backend connects lazily (first enabled hotkey); the service starts on the first timer tick.
        services.AddSingleton<IGlobalHotkeys>(sp => GlobalHotkeys.CreateDefault(sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new HotkeyService(sp.GetRequiredService<JsonFile<HotkeySettings>>(), sp.GetRequiredService<IGlobalHotkeys>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>(),
            (action, args) => sp.GetRequiredService<ActionEngine>().RunByNameAsync(action, args)));
        services.AddSingleton<IPeriodicService>(sp => sp.GetRequiredService<HotkeyService>());

        // Notifications and clipboard
        services.AddSingleton<IToastPresenter, NullToastPresenter>();
        services.AddSingleton<ISystemNotifier>(sp => new NotifySendNotifier(sp.GetRequiredService<AppLog>()));
        services.AddSingleton<INotifier>(sp => new DesktopNotifier(sp.GetRequiredService<ISystemNotifier>(), sp.GetRequiredService<IToastPresenter>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>(), sp.GetRequiredService<JsonFile<DesktopSettings>>()));
        services.AddSingleton<IClipboardService, NullClipboardService>();
        services.AddSingleton(sp => new ClipboardMonitor(sp.GetRequiredService<IClipboardService>, sp.GetRequiredService<JsonFile<DesktopSettings>>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IPeriodicService>(sp => sp.GetRequiredService<ClipboardMonitor>());

        services.AddSingleton<ITriggerProvider, DesktopTriggers>();
        services.AddSingleton<ISubActionHandler, ShowNotificationSubAction>();
        services.AddSingleton<ISubActionHandler, SetClipboardSubAction>();
        services.AddSingleton<ISubActionHandler, GetClipboardSubAction>();
        services.AddSingleton<IScriptFunctionProvider, DesktopScriptFunctions>();

        // Keyboard output (IKeyboardOutput) and mouse click triggers.
        services.AddInputFeatures();
        return services;
    }
}

/// <summary>Hotkey pressed, toast clicked and clipboard changed triggers.</summary>
public sealed class DesktopTriggers(HotkeyService hotkeys) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType("hotkeys.pressed", ExtrasCategories.System, "Hotkey pressed", HotkeyEvents.Pressed,
            [new ParameterDescriptor("hotkey", "Hotkey")
            {
                Help = "A hotkey name from the Hotkeys page or a combination like Ctrl+Shift+F1; empty = any hotkey",
                Suggestions = () => hotkeys.Names.Concat(hotkeys.Hotkeys.Select(h => h.Gesture)).Where(s => s.Length > 0).Distinct(),
            }],
            (e, s, _) => HotkeyService.Matches(s["hotkey"], e.Args)),
        new EventTriggerType("desktop.toastActivated", ExtrasCategories.System, "Toast notification clicked", DesktopEvents.ToastActivated,
            [new ParameterDescriptor("toastId", "Notification id") { Help = "The id given to 'Show desktop notification'; empty = any" }],
            (e, s, _) => s["toastId"].Trim().Length == 0
                         || string.Equals(e.Args.GetString("toastId"), s["toastId"].Trim(), StringComparison.OrdinalIgnoreCase)),
        new EventTriggerType("desktop.clipboardChanged", ExtrasCategories.System, "Clipboard changed", DesktopEvents.ClipboardChanged,
            [new ParameterDescriptor("contains", "Text contains") { Help = "Needs 'Watch clipboard' on the Hotkeys page; empty = any text" }],
            (e, s, _) => s["contains"].Length == 0 || e.Args.GetString("clipboardText").Contains(s["contains"], StringComparison.OrdinalIgnoreCase)),
    ];
}

public sealed class ShowNotificationSubAction(INotifier notifier) : ISubActionHandler
{
    public static readonly string[] Targets = ["Auto", "In-app", "System"];

    public string TypeId => "desktop.showNotification";
    public string Category => ExtrasCategories.System;
    public string DisplayName => "Show desktop notification";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("title", "Title") { DefaultValue = "Cuebot", Suggestions = () => ["Cuebot", "%user%"] },
        new("message", "Message", ParameterKind.Multiline) { Suggestions = () => ["%user% followed!", "%message%"] },
        new("icon", "Icon file") { Help = "Optional image path" },
        new("id", "Notification id") { Help = "Optional; reported by the 'Toast notification clicked' trigger" },
        new("duration", "Seconds on screen", ParameterKind.Number) { DefaultValue = "5" },
        new("target", "Show with", ParameterKind.Choice)
        {
            DefaultValue = "Auto", Choices = Targets,
            Help = "Auto: the system's notifications where available (Linux), else Cuebot's own toast",
        },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var title = ExtrasText.Clean(p["title"]);
        var message = p["message"].Trim();
        if (title.Length == 0 && message.Length == 0) throw new InvalidOperationException("The notification has no title or message");
        var notification = new DesktopNotification(title, message)
        {
            IconPath = ExtrasText.Clean(p["icon"]) is { Length: > 0 } icon ? icon : null,
            Id = ExtrasText.Clean(p["id"]),
            DurationSeconds = Math.Clamp(p.GetInt("duration", 5), 1, 120),
        };
        var target = p["target"].Trim().ToLowerInvariant() switch
        {
            "in-app" or "inapp" => NotificationTarget.InApp,
            "system" => NotificationTarget.System,
            _ => NotificationTarget.Auto,
        };
        context.Args["notificationShownWith"] = await notifier.ShowAsync(notification, target, ct);
        return SubActionResult.Next;
    }
}

public sealed class SetClipboardSubAction(IClipboardService clipboard) : ISubActionHandler
{
    public string TypeId => "desktop.setClipboard";
    public string Category => ExtrasCategories.System;
    public string DisplayName => "Set clipboard text";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("text", "Text", ParameterKind.Multiline) { Suggestions = () => ["%message%", "%input%", "%quote%"] },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        if (!clipboard.IsAvailable) throw new InvalidOperationException(NullClipboardService.Reason);
        await clipboard.SetTextAsync(p["text"], ct);
        return SubActionResult.Next;
    }
}

public sealed class GetClipboardSubAction(IClipboardService clipboard) : ISubActionHandler
{
    public string TypeId => "desktop.getClipboard";
    public string Category => ExtrasCategories.System;
    public string DisplayName => "Get clipboard text";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("destination", "Store in argument") { DefaultValue = "clipboardText", Suggestions = () => ["clipboardText"] },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        if (!clipboard.IsAvailable) throw new InvalidOperationException(NullClipboardService.Reason);
        var destination = Variables.VariableNames.Require(p["destination"].Trim().Length > 0 ? p["destination"] : "clipboardText", "Argument name");
        var text = await clipboard.GetTextAsync(ct);
        context.Args[destination] = text ?? "";
        context.Args[destination + "Found"] = text is not null;
        return SubActionResult.Next;
    }
}

/// <summary>Script functions: desktop.notify, desktop.setClipboard, desktop.getClipboard, hotkeys.list, hotkeys.press.</summary>
public sealed class DesktopScriptFunctions(INotifier notifier, IClipboardService clipboard, HotkeyService hotkeys) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("desktop.notify", "Show a desktop notification; returns where it was shown", ["title", "message", "icon", "id", "duration"],
            async (_, a, ct) =>
            {
                var duration = int.TryParse(a.GetString("duration", "5"), out var d) ? d : 5;
                try
                {
                    return await notifier.ShowAsync(new DesktopNotification(a.GetString("title", "Cuebot"), a.GetString("message"))
                    {
                        IconPath = a.GetString("icon") is { Length: > 0 } icon ? icon : null,
                        Id = a.GetString("id"),
                        DurationSeconds = Math.Clamp(duration, 1, 120),
                    }, NotificationTarget.Auto, ct);
                }
                catch (InvalidOperationException ex)
                {
                    throw new ScriptException(ex.Message);
                }
            }),
        new("desktop.setClipboard", "Put text on the clipboard", ["text"], async (_, a, ct) =>
        {
            if (!clipboard.IsAvailable) throw new ScriptException(NullClipboardService.Reason);
            await clipboard.SetTextAsync(a.GetString("text"), ct);
            return true;
        }),
        new("desktop.getClipboard", "The clipboard text (null when it holds none)", [], async (_, _, ct) =>
        {
            if (!clipboard.IsAvailable) throw new ScriptException(NullClipboardService.Reason);
            return await clipboard.GetTextAsync(ct);
        }),
        new("hotkeys.list", "Defined hotkeys as [{id, name, gesture, enabled, action, group}]", [],
            (_, _, _) => Task.FromResult<object?>(hotkeys.Hotkeys.ToArray())),
        new("hotkeys.press", "Act as if a hotkey (by name or id) was pressed; returns false when unknown", ["hotkey"], (_, a, _) =>
        {
            var wanted = a.GetString("hotkey").Trim();
            var match = hotkeys.Hotkeys.FirstOrDefault(h => string.Equals(h.Name, wanted, StringComparison.OrdinalIgnoreCase)
                                                            || string.Equals(h.Id.ToString(), wanted, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult<object?>(match is not null && hotkeys.Simulate(match.Id));
        }),
    ];
}
