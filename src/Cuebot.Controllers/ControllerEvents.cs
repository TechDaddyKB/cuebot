namespace Cuebot.Controllers;

/// <summary>
/// Bus event types. Streamer.bot 1.1 (alpha) names the matching events InputControllerButton, InputControllerAxis and
/// InputControllerHat; it does not publish their variables, so the argument names below are Cuebot's own.
/// Type ids are stored in actions.json and must never change.
/// </summary>
public static class ControllerEvents
{
    public const string Button = "controller.button";
    public const string Axis = "controller.axis";
    public const string Hat = "controller.hat";
    public const string Connected = "controller.connected";
    public const string Disconnected = "controller.disconnected";

    /// <summary>Trigger id of the button-combination trigger (listens to <see cref="Button"/>).</summary>
    public const string ButtonCombo = "controller.buttonCombo";

    public const string Category = "Game Controller";
}
