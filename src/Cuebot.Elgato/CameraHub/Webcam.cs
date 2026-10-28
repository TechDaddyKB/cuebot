namespace Cuebot.Elgato.CameraHub;

/// <summary>Standard UVC camera controls (what Camera Hub's webcam settings map to on the device).</summary>
public enum WebcamControl
{
    Brightness,
    Contrast,
    Saturation,
    Sharpness,
    WhiteBalance,
    AutoWhiteBalance,
    AntiFlicker,
    Exposure,
    AutoExposure,
    Focus,
    AutoFocus,
    Pan,
    Tilt,
    Zoom,
    Mirror,
    Flip,
}

/// <summary>A control's range as the device reports it.</summary>
public sealed record WebcamRange(int Minimum, int Maximum, int Step, int Default);

/// <summary>A video capture device. <see cref="Id"/> is stable while it stays on the same USB port.</summary>
public sealed record WebcamDeviceInfo(string Id, string Name, string Path);

/// <summary>Reads and writes camera controls (Linux: V4L2). Implementations may block; callers use a worker thread.</summary>
public interface IWebcamBackend
{
    /// <summary>Shown in the status line, e.g. "V4L2".</summary>
    string Description { get; }

    IReadOnlyList<WebcamDeviceInfo> Enumerate();

    /// <summary>The control's range, or null when the device does not have it (or it is disabled).</summary>
    WebcamRange? Query(WebcamDeviceInfo device, WebcamControl control);

    int Get(WebcamDeviceInfo device, WebcamControl control);

    void Set(WebcamDeviceInfo device, WebcamControl control, int value);
}

/// <summary>Anti-flicker (power line frequency) values, V4L2/UVC numbering.</summary>
public static class AntiFlicker
{
    public static readonly string[] Names = ["Off", "50 Hz", "60 Hz", "Auto"];

    public static string Name(int value) => value >= 0 && value < Names.Length ? Names[value] : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static int Parse(string text)
    {
        var t = text.Trim().Replace(" ", "").ToLowerInvariant();
        return t switch
        {
            "off" or "disabled" or "0" => 0,
            "50" or "50hz" or "1" => 1,
            "60" or "60hz" or "2" => 2,
            "auto" or "3" => 3,
            _ => throw new ArgumentException($"Anti flicker must be Off, 50 Hz, 60 Hz or Auto (got '{text}')."),
        };
    }
}

/// <summary>A webcam and the last values read from it.</summary>
public sealed class Webcam
{
    internal Webcam(WebcamDeviceInfo device) => Device = device;

    public WebcamDeviceInfo Device { get; }

    public string Id => Device.Id;

    public string Name => Device.Name;

    public IReadOnlyDictionary<WebcamControl, WebcamRange> Ranges { get; internal set; } = new Dictionary<WebcamControl, WebcamRange>();

    public IReadOnlyDictionary<WebcamControl, int> Values { get; internal set; } = new Dictionary<WebcamControl, int>();

    public bool Has(WebcamControl control) => Ranges.ContainsKey(control);

    /// <summary>The value in Streamer.bot's units (see <see cref="WebcamUnits"/>), or null when the camera lacks the control.</summary>
    public int? Display(WebcamControl control) =>
        Values.TryGetValue(control, out var raw) && Ranges.TryGetValue(control, out var range) ? WebcamUnits.ToDisplay(control, raw, range) : null;
}

/// <summary>
/// Converts between device units and the units of Streamer.bot's Camera Hub sub-actions: brightness, contrast, saturation,
/// focus, pan and tilt are 0-100 % of the device range; zoom is 100-400 % over the device range; white balance stays in
/// Kelvin and sharpness in device steps; switches are 0/1.
/// </summary>
public static class WebcamUnits
{
    public const int MinZoomPercent = 100;
    public const int MaxZoomPercent = 400;

    public static bool IsPercent(WebcamControl c) =>
        c is WebcamControl.Brightness or WebcamControl.Contrast or WebcamControl.Saturation or WebcamControl.Focus or WebcamControl.Pan or WebcamControl.Tilt;

    public static int ToDisplay(WebcamControl control, int raw, WebcamRange range)
    {
        if (control == WebcamControl.AutoExposure) return raw == ManualExposure ? 0 : 1;
        if (control == WebcamControl.Zoom) return Scale(raw, range, MinZoomPercent, MaxZoomPercent);
        return IsPercent(control) ? Scale(raw, range, 0, 100) : raw;
    }

    public static int ToRaw(WebcamControl control, double display, WebcamRange range)
    {
        if (control == WebcamControl.AutoExposure) return display >= 0.5 ? AperturePriority : ManualExposure;
        double raw;
        if (control == WebcamControl.Zoom) raw = Unscale(display, range, MinZoomPercent, MaxZoomPercent);
        else if (IsPercent(control)) raw = Unscale(display, range, 0, 100);
        else raw = display;
        var step = Math.Max(1, range.Step);
        var snapped = range.Minimum + (int)Math.Round((raw - range.Minimum) / step) * step;
        return Math.Clamp(snapped, range.Minimum, range.Maximum);
    }

    /// <summary>V4L2 exposure_auto menu: 1 = manual, 3 = aperture priority (what UVC cameras call auto).</summary>
    public const int ManualExposure = 1;
    public const int AperturePriority = 3;

    /// <summary>Exposure time in 100 µs units to a shutter speed such as "1/60".</summary>
    public static string ShutterSpeed(int exposure100us) =>
        exposure100us <= 0 ? "" : exposure100us >= 10000
            ? $"{exposure100us / 10000.0:0.#}s"
            : $"1/{Math.Round(10000.0 / exposure100us):0}";

    private static int Scale(int raw, WebcamRange r, int low, int high) =>
        r.Maximum <= r.Minimum ? low : (int)Math.Round(low + (raw - r.Minimum) * (double)(high - low) / (r.Maximum - r.Minimum));

    private static double Unscale(double display, WebcamRange r, int low, int high) =>
        r.Minimum + (Math.Clamp(display, low, high) - low) * (r.Maximum - r.Minimum) / (high - low);
}
