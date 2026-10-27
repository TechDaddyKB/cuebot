using Cuebot.Core.Desktop.Input;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat.Moderation;

namespace Cuebot.Scripting.Compat;

public sealed partial class CphProxy
{
    // ---- Chat actions, custom events, other code, keyboard --------------------------------------------------------

    /// <summary>
    /// Sends a /me line to Twitch chat (shown in italics). Uses the Twitch module's action announcer when it has one;
    /// otherwise the text is sent as an ordinary chat message. <paramref name="bot"/> and <paramref name="fallback"/>
    /// follow Cuebot's chat sender setting like <see cref="SendMessage"/>.
    /// </summary>
    public void SendAction(string action, bool bot = true, bool fallback = true)
    {
        if (string.IsNullOrWhiteSpace(action)) return;
        if (_services.ChatModeration?.AnnouncersFor("Twitch", ChatAnnouncementKind.Action) is { Count: > 0 } announcers)
        {
            Sync(announcers[0].AnnounceAsync(action, ChatAnnouncementKind.Action, _ct));
            return;
        }
        SendChat("Twitch", "twitch.sendMessage", action);
    }

    /// <summary>
    /// Fires the custom event <paramref name="eventName"/>: actions with a "Custom code event" trigger for that name
    /// run, receiving this run's arguments when <paramref name="useArgs"/>. Same as <see cref="TriggerCodeEvent(string, bool)"/>.
    /// </summary>
    public void TriggerEvent(string eventName, bool useArgs = true) =>
        Need(_services.Bus).Publish(CodeTriggerRegistry.CreateEvent(eventName, useArgs ? _context?.Args : null));

    /// <summary>
    /// Presses keys, in SendKeys syntax (<c>{W}</c>, <c>{DOWN}</c>, <c>+{W}</c> Shift, <c>^{W}</c> Ctrl, <c>%{W}</c> Alt).
    /// Needs a keyboard output service (<see cref="IKeyboardOutput"/>).
    /// </summary>
    public void KeyboardPress(string keyPress)
    {
        var keyboard = _services.Keyboard ?? throw Unsupported("no keyboard output service is loaded");
        if (string.IsNullOrEmpty(keyPress)) return;
        Sync(keyboard.PressAsync(keyPress, _ct));   // accepts Streamer.bot's SendKeys syntax
    }

    /// <summary>
    /// Runs the public parameterless method <paramref name="methodName"/> of another "Execute C# code" sub-action's
    /// CPHInline class, on that code's long-lived instance, with this run's arguments. The code is found by its
    /// "Code name" setting, or else as the first CPHInline code in the action named <paramref name="codeName"/>.
    /// Returns the method's bool result (true for void methods); false when the code or method is not found.
    /// </summary>
    public bool ExecuteMethod(string codeName, string methodName)
    {
        var host = _inline ?? throw Unsupported("no C# code host is available");
        var context = RequireContext();
        if (string.IsNullOrWhiteSpace(codeName) || string.IsNullOrWhiteSpace(methodName)) return false;
        var code = FindInlineCode(codeName.Trim());
        if (code is null)
        {
            LogWarn($"ExecuteMethod: no C# code (CPHInline class) named '{codeName}'");
            return false;
        }
        var result = host.InvokeMethod(code, methodName.Trim(), this, context.Args);
        if (result is null) LogWarn($"ExecuteMethod: '{codeName}' has no public method {methodName}() without parameters");
        return result ?? false;
    }

    private string? FindInlineCode(string codeName)
    {
        var actions = Profile(nameof(ExecuteMethod)).Snapshot.Actions;
        var named = actions.SelectMany(a => InlineCode(a.SubActions)).FirstOrDefault(s =>
            s.Parameters.TryGetValue(CSharpCodeSubAction.NameParameter, out var name)
            && string.Equals(name.Trim(), codeName, StringComparison.OrdinalIgnoreCase));
        named ??= actions.Where(a => string.Equals(a.Name, codeName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(a => InlineCode(a.SubActions)).FirstOrDefault();
        return named?.Parameters.GetValueOrDefault("code");
    }

    /// <summary>Every C# sub-action holding a CPHInline class, branches included, in order.</summary>
    private static IEnumerable<SubActionDefinition> InlineCode(IEnumerable<SubActionDefinition> steps)
    {
        foreach (var step in steps)
        {
            if (step.Type == "code.csharp" && step.Parameters.TryGetValue("code", out var code) && CphInlineHost.IsInlineClass(code))
                yield return step;
            foreach (var nested in step.Branches.Values.SelectMany(InlineCode)) yield return nested;
        }
    }
}
