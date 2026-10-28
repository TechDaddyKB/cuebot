using System.Reflection;
using System.Runtime.InteropServices;

namespace Cuebot.Controllers.Device.Sdl;

/// <summary>
/// The handful of SDL3 functions Cuebot needs (joystick, gamepad, events, hints), declared from the public SDL3
/// headers (release-3.4). Blittable signatures only, so no unsafe code; strings in are UTF-8, strings out are read
/// from the returned pointer. Every call must be made on the backend thread (see <see cref="SdlControllerBackend"/>).
/// </summary>
internal static class SdlNative
{
    public const string Library = "SDL3";

    public const uint InitJoystick = 0x00000200, InitGamepad = 0x00002000;

    // SDL_EventType values used here.
    public const uint EventJoystickAxis = 0x600, EventJoystickHat = 0x602, EventJoystickButtonDown = 0x603,
        EventJoystickButtonUp = 0x604, EventJoystickAdded = 0x605, EventJoystickRemoved = 0x606, EventJoystickBattery = 0x607;
    public const uint EventGamepadAxis = 0x650, EventGamepadButtonDown = 0x651, EventGamepadButtonUp = 0x652,
        EventGamepadAdded = 0x653, EventGamepadRemoved = 0x654, EventGamepadRemapped = 0x655;

    public const string PropRgbLed = "SDL.joystick.cap.rgb_led";
    public const string PropRumble = "SDL.joystick.cap.rumble";

    /// <summary>
    /// SDL_Event is a 128-byte union; every event used here starts with {Uint32 type; Uint32 reserved; Uint64 timestamp;
    /// SDL_JoystickID which;} followed by small fields at fixed offsets (SDL_events.h).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    public struct Event
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(16)] public uint Which;

        /// <summary>axis / button / hat index (Uint8 at offset 20).</summary>
        [FieldOffset(20)] public byte Index;

        /// <summary>Button "down" (bool) or hat value (Uint8) at offset 21.</summary>
        [FieldOffset(21)] public byte Byte21;

        /// <summary>Axis value (Sint16) at offset 24.</summary>
        [FieldOffset(24)] public short AxisValue;

        /// <summary>Battery: SDL_PowerState at 20, percent at 24.</summary>
        [FieldOffset(20)] public int PowerState;
        [FieldOffset(24)] public int Percent;
    }

    /// <summary>SDL_GUID (Uint8 data[16]), passed and returned by value; two 8-byte fields have the same ABI.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Guid
    {
        public ulong Low;
        public ulong High;
    }

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SDL_SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SDL_Init(uint flags);

    [DllImport(Library)] public static extern void SDL_Quit();
    [DllImport(Library)] public static extern IntPtr SDL_GetError();
    [DllImport(Library)] public static extern int SDL_GetVersion();
    [DllImport(Library)] public static extern IntPtr SDL_GetRevision();
    [DllImport(Library)] public static extern void SDL_free(IntPtr memory);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SDL_PollEvent(out Event e);

    [DllImport(Library)] public static extern int SDL_AddGamepadMappingsFromFile([MarshalAs(UnmanagedType.LPUTF8Str)] string file);

    [DllImport(Library)] public static extern IntPtr SDL_GetJoysticks(out int count);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SDL_IsGamepad(uint id);

    [DllImport(Library)] public static extern IntPtr SDL_OpenGamepad(uint id);
    [DllImport(Library)] public static extern void SDL_CloseGamepad(IntPtr gamepad);
    [DllImport(Library)] public static extern IntPtr SDL_GetGamepadName(IntPtr gamepad);
    [DllImport(Library)] public static extern int SDL_GetGamepadType(IntPtr gamepad);
    [DllImport(Library)] public static extern int SDL_GetGamepadPlayerIndex(IntPtr gamepad);
    [DllImport(Library)] public static extern ushort SDL_GetGamepadVendor(IntPtr gamepad);
    [DllImport(Library)] public static extern ushort SDL_GetGamepadProduct(IntPtr gamepad);
    [DllImport(Library)] public static extern IntPtr SDL_GetGamepadSerial(IntPtr gamepad);
    [DllImport(Library)] public static extern int SDL_GetGamepadPowerInfo(IntPtr gamepad, out int percent);
    [DllImport(Library)] public static extern IntPtr SDL_GetGamepadJoystick(IntPtr gamepad);
    [DllImport(Library)] public static extern short SDL_GetGamepadAxis(IntPtr gamepad, int axis);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SDL_GetGamepadButton(IntPtr gamepad, int button);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SDL_RumbleGamepad(IntPtr gamepad, ushort low, ushort high, uint durationMs);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SDL_SetGamepadLED(IntPtr gamepad, byte red, byte green, byte blue);

    [DllImport(Library)] public static extern IntPtr SDL_OpenJoystick(uint id);
    [DllImport(Library)] public static extern void SDL_CloseJoystick(IntPtr joystick);
    [DllImport(Library)] public static extern IntPtr SDL_GetJoystickName(IntPtr joystick);
    [DllImport(Library)] public static extern Guid SDL_GetJoystickGUID(IntPtr joystick);
    [DllImport(Library)] public static extern int SDL_GetJoystickPlayerIndex(IntPtr joystick);
    [DllImport(Library)] public static extern ushort SDL_GetJoystickVendor(IntPtr joystick);
    [DllImport(Library)] public static extern ushort SDL_GetJoystickProduct(IntPtr joystick);
    [DllImport(Library)] public static extern int SDL_GetNumJoystickAxes(IntPtr joystick);
    [DllImport(Library)] public static extern int SDL_GetNumJoystickButtons(IntPtr joystick);
    [DllImport(Library)] public static extern int SDL_GetNumJoystickHats(IntPtr joystick);
    [DllImport(Library)] public static extern short SDL_GetJoystickAxis(IntPtr joystick, int axis);
    [DllImport(Library)] public static extern byte SDL_GetJoystickHat(IntPtr joystick, int hat);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SDL_GetJoystickButton(IntPtr joystick, int button);

    [DllImport(Library)] public static extern int SDL_GetJoystickPowerInfo(IntPtr joystick, out int percent);
    [DllImport(Library)] public static extern uint SDL_GetJoystickProperties(IntPtr joystick);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SDL_RumbleJoystick(IntPtr joystick, ushort low, ushort high, uint durationMs);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SDL_SetJoystickLED(IntPtr joystick, byte red, byte green, byte blue);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SDL_GetBooleanProperty(uint properties, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.U1)] bool defaultValue);

    [DllImport(Library)] public static extern void SDL_GUIDToString(Guid guid, byte[] buffer, int size);

    public static string Text(IntPtr utf8) => utf8 == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";

    public static string Error() => Text(SDL_GetError());

    public static string GuidText(Guid guid)
    {
        var buffer = new byte[33];
        SDL_GUIDToString(guid, buffer, buffer.Length);
        var end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    /// <summary>"SDL 3.4.18 (release-3.4.18-0-g...)" from SDL_GetVersion's major*1000000 + minor*1000 + micro.</summary>
    public static string VersionText()
    {
        var v = SDL_GetVersion();
        var revision = Text(SDL_GetRevision());
        var text = $"SDL {v / 1_000_000}.{v / 1000 % 1000}.{v % 1000}";
        return revision.Length > 0 ? $"{text} ({revision})" : text;
    }

    private static int _resolverInstalled;

    /// <summary>
    /// Where SDL3 is looked for: <c>CUEBOT_SDL3_LIBRARY</c> (a full path) first, then the copy shipped next to Cuebot
    /// (runtimes/&lt;rid&gt;/native from the ppy.SDL3-CS package), then the system's libSDL3.so.0 (distribution package).
    /// </summary>
    public static void InstallResolver()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0) return;
        NativeLibrary.SetDllImportResolver(typeof(SdlNative).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return IntPtr.Zero;
        if (Environment.GetEnvironmentVariable("CUEBOT_SDL3_LIBRARY") is { Length: > 0 } custom
            && NativeLibrary.TryLoad(custom, out var handle)) return handle;
        if (NativeLibrary.TryLoad(name, assembly, searchPath, out handle)) return handle;
        foreach (var candidate in SystemNames())
            if (NativeLibrary.TryLoad(candidate, out handle)) return handle;
        return IntPtr.Zero;
    }

    /// <summary>Loads SDL3 the way P/Invoke will; null when it loads, else why not (checked before the first call).</summary>
    public static string? Probe()
    {
        InstallResolver();
        var handle = Resolve(Library, typeof(SdlNative).Assembly, null);
        if (handle == IntPtr.Zero)
            return OperatingSystem.IsLinux()
                ? "the SDL3 library was not found (libSDL3.so next to Cuebot, or the system package sdl3 / libsdl3-0)"
                : "the SDL3 library (SDL3.dll) was not found next to Cuebot";
        return NativeLibrary.TryGetExport(handle, "SDL_AddGamepadMappingsFromFile", out _)
            ? null
            : "the SDL3 library found is too old (SDL 3.2 or newer is needed)";
    }

    private static string[] SystemNames() =>
        OperatingSystem.IsWindows() ? ["SDL3.dll"]
        : OperatingSystem.IsMacOS() ? ["libSDL3.0.dylib", "libSDL3.dylib"]
        : ["libSDL3.so.0", "libSDL3.so"];
}
