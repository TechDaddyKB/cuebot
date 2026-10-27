using Cuebot.Core.Actions;
using Cuebot.Core.Desktop.Notifications;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Desktop.Input;

/// <summary>Keyboard output (the "Keyboard Press" sub-action, <see cref="IKeyboardOutput"/>) and the Mouse Click trigger.</summary>
public static class InputFeatures
{
    public const string MouseClickTriggerId = "input.mouseClick";

    /// <summary>Registers <see cref="IKeyboardOutput"/> (singleton), the mouse click service, the trigger, sub-action and script functions.</summary>
    public static IServiceCollection AddInputFeatures(this IServiceCollection services)
    {
        services.AddSingleton<IKeyboardOutput>(sp =>
        {
            var settings = sp.GetRequiredService<JsonFile<DesktopSettings>>();
            return KeyboardOutputs.CreateDefault(sp.GetRequiredService<AppLog>(),
                () => settings.Value.KeyboardPortalToken,
                token =>
                {
                    settings.Value.KeyboardPortalToken = token;
                    settings.ScheduleSave();
                });
        });
        services.AddSingleton<IMouseClickSource>(sp => MouseClickSources.CreateDefault(sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new MouseClickService(sp.GetRequiredService<IMouseClickSource>(), sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>(), () => UsesMouseClicks(sp.GetRequiredService<ProfileService>().Snapshot)));
        services.AddSingleton<IPeriodicService>(sp => sp.GetRequiredService<MouseClickService>());
        services.AddSingleton<ITriggerProvider, InputTriggers>();
        services.AddSingleton<ISubActionHandler, KeyboardPressSubAction>();
        services.AddSingleton<IScriptFunctionProvider, InputScriptFunctions>();
        return services;
    }

    /// <summary>True when an enabled action has an enabled Mouse Click trigger.</summary>
    public static bool UsesMouseClicks(ProfileSnapshot profile) =>
        profile.Actions.Any(a => a.Enabled && a.Triggers.Any(t => t.Enabled && t.Type == MouseClickTriggerId));
}

public sealed class InputTriggers(MouseClickService mouse) : ITriggerProvider
{
    public static readonly string[] Buttons = ["Any", .. Enum.GetNames<MouseButton>()];

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType(InputFeatures.MouseClickTriggerId, ExtrasCategories.System, "Mouse click", InputEvents.MouseClick,
            [
                new ParameterDescriptor("button", "Button", ParameterKind.Choice) { DefaultValue = "Left", Choices = Buttons },
                new ParameterDescriptor("modifiers", "Modifier keys")
                {
                    Help = "Empty = any; None = no modifier held; else exactly these, e.g. Ctrl+Shift. "
                           + "Windows and X11 only (" + MouseClickSources.WaylandReason + "). Now: " + mouse.BackendName,
                    Suggestions = () => ["None", "Ctrl", "Shift", "Alt", "Ctrl+Shift", "Ctrl+Alt"],
                },
            ],
            (e, s, _) => MouseClickService.Matches(s["button"], s["modifiers"], e.Args)),
    ];
}

/// <summary>Sends a key combination (or several, one after the other) to the focused application.</summary>
public sealed class KeyboardPressSubAction(IKeyboardOutput keyboard) : ISubActionHandler
{
    public string TypeId => "input.keyboardPress";
    public string Category => ExtrasCategories.System;
    public string DisplayName => "Keyboard press";
    public IReadOnlyList<string> Branches => [];
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("keys", "Keys")
        {
            DefaultValue = "Ctrl+Shift+F1",
            Help = "A combination like Ctrl+Shift+F1, several separated by commas (Ctrl+C, Ctrl+V), or SendKeys text such as ^c or {ENTER}. "
                   + "Linux: the desktop asks once for permission (Wayland portal); X11 is the fallback.",
            Suggestions = () => ["Ctrl+Shift+F1", "Alt+Tab", "Ctrl+C, Ctrl+V", "MediaPlayPause", "{ENTER}"],
        },
        new("delay", "Pause between combos (ms)", ParameterKind.Number) { DefaultValue = "30" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        if (!KeyCombos.TryParse(p["keys"], out var combos, out var error)) throw new InvalidOperationException(error);
        var delay = Math.Clamp(p.GetInt("delay", 30), 0, 10_000);
        for (var i = 0; i < combos.Count; i++)
        {
            if (i > 0 && delay > 0) await Task.Delay(delay, ct);
            await keyboard.PressAsync([combos[i]], ct);
        }
        context.Args["keysPressed"] = string.Join(", ", combos);
        return SubActionResult.Next;
    }
}

/// <summary>Script functions: input.keyPress, input.status.</summary>
public sealed class InputScriptFunctions(IKeyboardOutput keyboard, MouseClickService mouse) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("input.keyPress", "Press keys in the focused application (\"Ctrl+Shift+F1\", \"Ctrl+C, Ctrl+V\" or SendKeys \"^c\")", ["keys"],
            async (_, a, ct) =>
            {
                try
                {
                    await keyboard.PressAsync(a.GetString("keys"), ct);
                    return true;
                }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException)
                {
                    throw new ScriptException(ex.Message);
                }
            }),
        new("input.status", "Keyboard output and mouse click backends as {keyboard, keyboardStatus, mouse, mouseStatus}", [],
            (_, _, _) => Task.FromResult<object?>(new Dictionary<string, object?>
            {
                ["keyboard"] = keyboard.BackendName, ["keyboardStatus"] = keyboard.Status,
                ["mouse"] = mouse.BackendName, ["mouseStatus"] = mouse.Status,
            })),
    ];
}
