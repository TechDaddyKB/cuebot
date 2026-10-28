namespace Cuebot.Controllers;

/// <summary>When Cuebot watches controllers.</summary>
public enum ControllerWatchMode
{
    /// <summary>While an enabled action has an enabled controller trigger, the Controllers page is open, or a
    /// sub-action/script used a controller in the last few minutes.</summary>
    Automatic,

    /// <summary>From startup until Cuebot exits.</summary>
    Always,

    /// <summary>Never; controller sub-actions and scripts fail with a clear message.</summary>
    Off,
}

/// <summary>Persisted game-controller configuration (controllers.json).</summary>
public sealed class ControllerSettings
{
    public ControllerWatchMode Mode { get; set; } = ControllerWatchMode.Automatic;

    /// <summary>Stick and joystick-axis deadzone, 0-0.95 (fraction of full travel).</summary>
    public double StickDeadzone { get; set; } = 0.15;

    /// <summary>Analog trigger deadzone, 0-0.95.</summary>
    public double TriggerDeadzone { get; set; } = 0.05;

    /// <summary>At most one axis event per axis in this many milliseconds (the last value of a movement is always sent).</summary>
    public int AxisThrottleMs { get; set; } = 50;

    /// <summary>Smallest axis change (fraction of full travel) that produces an event.</summary>
    public double AxisMinChange { get; set; } = 0.02;

    /// <summary>
    /// A community gamecontrollerdb.txt (https://github.com/mdqinc/SDL_GameControllerDB) loaded on top of SDL's
    /// built-in mappings. Empty = use gamecontrollerdb.txt (or gamecontroller.txt) in the data folder when present.
    /// It only changes how buttons and axes of a device are mapped and named.
    /// </summary>
    public string MappingsFile { get; set; } = "";

    /// <summary>Report the D-pad of a gamepad as Hat1 too, so one Hat trigger covers D-pads and joystick hats.</summary>
    public bool DPadAsHat { get; set; } = true;

    /// <summary>Also report devices without a gamepad mapping (flight sticks, wheels, button boxes).</summary>
    public bool IncludeJoysticks { get; set; } = true;
}
