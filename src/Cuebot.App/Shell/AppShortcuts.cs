namespace Cuebot.App.Shell;

/// <summary>One keyboard shortcut. <see cref="Id"/> names the window command it runs (window shortcuts only).</summary>
public sealed record AppShortcut(string Id, string Gesture, string Description, string Where);

/// <summary>
/// The app's keyboard shortcuts (after Streamer.bot's documented ones). The main window builds its KeyBindings from
/// <see cref="Window"/>, and the Settings page lists <see cref="All"/>, so the two cannot drift apart.
/// </summary>
public static class AppShortcuts
{
    public const string Anywhere = "Anywhere";
    public const string ActionsPage = "Actions page";

    public static readonly IReadOnlyList<AppShortcut> Window =
    [
        new("save", "Ctrl+S", "Save everything now", Anywhere),
        new("help", "F1", "Open the Support page", Anywhere),
        new("testTrigger", "F5", "Test the selected action's trigger", ActionsPage),
        new("import", "Ctrl+Alt+I", "Import (opens Import / Export)", Anywhere),
        new("export", "Ctrl+Alt+E", "Export (opens Import / Export)", Anywhere),
        new("chat", "Ctrl+Alt+C", "Open Chat", Anywhere),
        new("variables", "Ctrl+Alt+V", "Open Variables", Anywhere),
        new("logs", "Ctrl+Alt+L", "Open the log folder", Anywhere),
    ];

    /// <summary>Handled by the Actions page itself (they depend on which list has focus).</summary>
    public static readonly IReadOnlyList<AppShortcut> Actions =
    [
        new("", "Delete", "Delete the selected trigger or sub-action (in the action list: the action, press twice)", ActionsPage),
        new("", "Ctrl+D", "Duplicate the selected action, trigger or sub-action", ActionsPage),
        new("", "Ctrl+O", "Turn the selected action, trigger or sub-action on or off", ActionsPage),
        new("", "Ctrl+Up / Ctrl+Down", "Move the selected sub-action up or down", ActionsPage),
        new("", "Ctrl+Home / Ctrl+End", "Move the selected sub-action to the top or bottom", ActionsPage),
        new("", "Ctrl+R", "Rename the selected group (puts the cursor in its name)", ActionsPage),
        new("", "Ctrl+C / Ctrl+V", "Copy / paste actions, triggers and sub-actions", ActionsPage),
        new("", "Ctrl+Z / Ctrl+Y", "Undo / redo", ActionsPage),
    ];

    /// <summary>Handled by the other list pages (Streamer.bot's per-view shortcuts) while their list has focus.</summary>
    public static readonly IReadOnlyList<AppShortcut> Pages =
    [
        new("", "Delete", "Delete the selected command", "Commands"),
        new("", "Ctrl+O", "Turn the selected command on or off", "Commands"),
        new("", "Ctrl+C", "Copy the selected command's id", "Commands"),
        new("", "Ctrl+R", "Rename the selected command's group", "Commands"),
        new("", "Ctrl+* / Ctrl+-", "Expand / collapse every group", "Commands"),
        new("", "Delete / Ctrl+O", "Remove the focused timer / turn it on or off", "Timers"),
        new("", "Delete / Ctrl+O", "Delete the focused device or event / turn an event on or off", "MIDI"),
        new("", "Delete", "Delete the selected voice command", "Voice Control"),
        new("", "Ctrl+Up / Down / Home / End", "Move the selected voice command", "Voice Control"),
        new("", "Delete", "Delete the selected quote", "Quotes"),
        new("", "Delete / Ctrl+A / Ctrl+D", "Delete / add / duplicate a channel point reward", "Twitch > Rewards"),
        new("", "Ctrl+O", "Turn the selected reward on or off (saved to Twitch)", "Twitch > Rewards"),
    ];

    public static IReadOnlyList<AppShortcut> All { get; } = [.. Window, .. Actions, .. Pages];
}
