using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Cuebot.StreamDeck.Hid;

/// <summary>
/// HID on macOS through IOKit's IOHIDManager/IOHIDDevice. Devices of the vendor are found with a matching dictionary;
/// <see cref="HidDeviceInfo.Path"/> is "iokit:" plus the device's IORegistry entry id, which stays the same while it is
/// plugged in. An open device gets its own thread with a run loop for input reports and removal; output and feature
/// reports are synchronous IOHIDDeviceSetReport/GetReport calls. Report bytes follow hidapi's macOS rules: byte 0 is
/// the report id, and is left out of the call when the id is 0 (an unnumbered report). Unlike Linux no permission is
/// needed, but the Elgato Stream Deck app must be quit: it keeps the deck for itself.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacHidTransport : IHidTransport
{
    internal const string PathPrefix = "iokit:";

    public string? UnavailableReason => null;

    public IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId)
    {
        var found = new List<HidDeviceInfo>();
        var manager = Native.IOHIDManagerCreate(0, 0);
        if (manager == 0) return found;
        try
        {
            var matching = Native.Dictionary(("VendorID", vendorId));
            Native.IOHIDManagerSetDeviceMatching(manager, matching);
            Native.CFRelease(matching);
            var set = Native.IOHIDManagerCopyDevices(manager);
            if (set == 0) return found;
            try
            {
                var count = (int)Native.CFSetGetCount(set);
                var devices = new nint[count];
                Native.CFSetGetValues(set, devices);
                foreach (var device in devices)
                {
                    var service = Native.IOHIDDeviceGetService(device);
                    if (service == 0 || Native.IORegistryEntryGetRegistryEntryID(service, out var entryId) != 0) continue;
                    found.Add(new HidDeviceInfo(
                        PathFor(entryId),
                        vendorId,
                        (ushort)(Native.IntProperty(device, "ProductID") ?? 0),
                        Native.StringProperty(device, "SerialNumber") ?? "",
                        Native.StringProperty(device, "Product") ?? "",
                        Native.IntProperty(device, "LocationID") is { } location ? $"0x{location:x8}" : ""));
                }
            }
            finally
            {
                Native.CFRelease(set);
            }
        }
        finally
        {
            Native.CFRelease(manager);
        }
        return found.OrderBy(d => d.UsbPort, StringComparer.Ordinal).ToArray();
    }

    internal static string PathFor(ulong entryId) => PathPrefix + entryId.ToString(CultureInfo.InvariantCulture);

    internal static ulong? EntryId(string path) =>
        path.StartsWith(PathPrefix, StringComparison.Ordinal)
        && ulong.TryParse(path.AsSpan(PathPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>The <see cref="HidOpenException"/> for an IOReturn from IOHIDDeviceOpen.</summary>
    internal static HidOpenException OpenError(int ioReturn, string name) => ioReturn switch
    {
        Native.ExclusiveAccess => new HidOpenException(HidOpenError.Busy,
            $"{name} is in use by another program (quit the Elgato Stream Deck app)"),
        Native.NotPermitted or Native.NotPrivileged => new HidOpenException(HidOpenError.PermissionDenied,
            $"macOS did not allow opening {name} (allow Cuebot under System Settings > Privacy & Security > Input Monitoring)"),
        Native.NoDevice or Native.NotAttached => new HidOpenException(HidOpenError.Gone, $"{name} went away"),
        _ => new HidOpenException(HidOpenError.Other, $"cannot open {name} (IOReturn 0x{ioReturn:x8})"),
    };

    public IHidDevice Open(HidDeviceInfo device)
    {
        var name = device.Name.Length > 0 ? device.Name : "the device";
        if (device.Path is not { } path || EntryId(path) is not { } entryId)
            throw new HidOpenException(HidOpenError.Gone, $"{name} has no IOKit path");
        var service = Native.IOServiceGetMatchingService(0, Native.IORegistryEntryIDMatching(entryId));
        if (service == 0) throw new HidOpenException(HidOpenError.Gone, $"{name} went away");
        var handle = Native.IOHIDDeviceCreate(0, service);
        Native.IOObjectRelease(service);
        if (handle == 0) throw new HidOpenException(HidOpenError.Gone, $"{name} went away");
        var result = Native.IOHIDDeviceOpen(handle, 0);
        if (result != 0)
        {
            Native.CFRelease(handle);
            throw OpenError(result, name);
        }
        return new MacHidDevice(handle, name, Native.IntProperty(handle, "MaxInputReportSize") ?? 1024);
    }

    // ---- An open device ---------------------------------------------------------------------------------------------

    private sealed class MacHidDevice : IHidDevice
    {
        private const int ReportTypeOutput = 1, ReportTypeFeature = 2;
        private readonly BlockingCollection<byte[]> _reports = new(new ConcurrentQueue<byte[]>(), 64);
        private readonly Native.ReportCallback _onReport;   // kept alive while registered
        private readonly Native.RemovalCallback _onRemoved;
        private readonly string _name;
        private readonly nint _buffer;
        private readonly Thread _thread;
        private nint _device;
        private nint _runLoop;
        private volatile bool _gone;

        public MacHidDevice(nint device, string name, long maxInputReport)
        {
            _device = device;
            _name = name;
            var size = (int)Math.Clamp(maxInputReport, 64, 65536);
            _buffer = Marshal.AllocHGlobal(size);
            _onReport = (_, result, _, _, _, report, length) =>
            {
                if (result != 0 || length <= 0) return;
                var copy = new byte[length];
                Marshal.Copy(report, copy, 0, (int)length);
                if (!_reports.TryAdd(copy)) { _reports.TryTake(out _); _reports.TryAdd(copy); }   // keep the newest
            };
            _onRemoved = (_, _, _) => _gone = true;
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _runLoop = Native.CFRunLoopGetCurrent();
                Native.IOHIDDeviceRegisterInputReportCallback(_device, _buffer, size, _onReport, 0);
                Native.IOHIDDeviceRegisterRemovalCallback(_device, _onRemoved, 0);
                Native.IOHIDDeviceScheduleWithRunLoop(_device, _runLoop, Native.DefaultMode);
                ready.Set();
                Native.CFRunLoopRun();
            }) { IsBackground = true, Name = "Cuebot HID " + name };
            _thread.Start();
            ready.Wait();
        }

        public int Read(byte[] buffer, int timeoutMs)
        {
            if (!_reports.TryTake(out var report, timeoutMs))
            {
                if (_gone) throw new HidDisconnectedException($"{_name} was disconnected");
                return 0;
            }
            var n = Math.Min(report.Length, buffer.Length);
            report.AsSpan(0, n).CopyTo(buffer);
            return n;
        }

        public void Write(ReadOnlySpan<byte> report) => SetReport(ReportTypeOutput, report, "write");

        public void SetFeature(ReadOnlySpan<byte> report) => SetReport(ReportTypeFeature, report, "set a feature report on");

        public byte[] GetFeature(byte reportId, int length)
        {
            var buffer = new byte[length];
            buffer[0] = reportId;
            var skip = reportId == 0 ? 1 : 0;
            nint got = length - skip;
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                Check(Native.IOHIDDeviceGetReport(Device(), ReportTypeFeature, reportId, handle.AddrOfPinnedObject() + skip, ref got),
                    "read a feature report from");
            }
            finally
            {
                handle.Free();
            }
            return buffer[..Math.Min(length, (int)got + skip)];
        }

        private void SetReport(int type, ReadOnlySpan<byte> report, string what)
        {
            if (report.IsEmpty) return;
            var id = report[0];
            var data = id == 0 ? report[1..].ToArray() : report.ToArray();
            Check(Native.IOHIDDeviceSetReport(Device(), type, id, data, data.Length), what);
        }

        private nint Device() => _gone || _device == 0 ? throw new HidDisconnectedException($"{_name} was disconnected") : _device;

        private void Check(int ioReturn, string what)
        {
            if (ioReturn == 0) return;
            if (_gone || ioReturn is Native.NoDevice or Native.NotAttached or Native.NotResponding)
            {
                _gone = true;
                throw new HidDisconnectedException($"{_name} was disconnected");
            }
            throw new IOException($"Could not {what} {_name} (IOReturn 0x{ioReturn:x8})");
        }

        public void Dispose()
        {
            if (_device == 0) return;
            Native.IOHIDDeviceUnscheduleFromRunLoop(_device, _runLoop, Native.DefaultMode);
            Native.CFRunLoopStop(_runLoop);
            _thread.Join(TimeSpan.FromSeconds(2));
            Native.IOHIDDeviceClose(_device, 0);
            Native.CFRelease(_device);
            _device = 0;
            Marshal.FreeHGlobal(_buffer);
            GC.KeepAlive(_onReport);
            GC.KeepAlive(_onRemoved);
        }
    }

    // ---- Native -----------------------------------------------------------------------------------------------------

    private static class Native
    {
        private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const uint Utf8 = 0x08000100;
        private const int NumberSInt64 = 4;

        // IOReturn (IOKit/IOReturn.h)
        public const int NotPrivileged = unchecked((int)0xE00002C1), NoDevice = unchecked((int)0xE00002C0),
            ExclusiveAccess = unchecked((int)0xE00002C5), NotAttached = unchecked((int)0xE00002D9),
            NotPermitted = unchecked((int)0xE00002E2), NotResponding = unchecked((int)0xE00002ED);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ReportCallback(nint context, int result, nint sender, int type, uint reportId, nint report, nint length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void RemovalCallback(nint context, int result, nint sender);

        [DllImport(IOKit)] public static extern nint IOHIDManagerCreate(nint allocator, uint options);
        [DllImport(IOKit)] public static extern void IOHIDManagerSetDeviceMatching(nint manager, nint matching);
        [DllImport(IOKit)] public static extern nint IOHIDManagerCopyDevices(nint manager);
        [DllImport(IOKit)] public static extern nint IOHIDDeviceCreate(nint allocator, uint service);
        [DllImport(IOKit)] public static extern uint IOHIDDeviceGetService(nint device);
        [DllImport(IOKit)] public static extern nint IOHIDDeviceGetProperty(nint device, nint key);
        [DllImport(IOKit)] public static extern int IOHIDDeviceOpen(nint device, uint options);
        [DllImport(IOKit)] public static extern int IOHIDDeviceClose(nint device, uint options);
        [DllImport(IOKit)] public static extern int IOHIDDeviceSetReport(nint device, int type, nint reportId, byte[] report, nint length);
        [DllImport(IOKit)] public static extern int IOHIDDeviceGetReport(nint device, int type, nint reportId, nint report, ref nint length);
        [DllImport(IOKit)] public static extern void IOHIDDeviceRegisterInputReportCallback(nint device, nint report, nint length, ReportCallback callback, nint context);
        [DllImport(IOKit)] public static extern void IOHIDDeviceRegisterRemovalCallback(nint device, RemovalCallback callback, nint context);
        [DllImport(IOKit)] public static extern void IOHIDDeviceScheduleWithRunLoop(nint device, nint runLoop, nint mode);
        [DllImport(IOKit)] public static extern void IOHIDDeviceUnscheduleFromRunLoop(nint device, nint runLoop, nint mode);
        [DllImport(IOKit)] public static extern int IORegistryEntryGetRegistryEntryID(uint entry, out ulong id);
        [DllImport(IOKit)] public static extern nint IORegistryEntryIDMatching(ulong id);
        [DllImport(IOKit)] public static extern uint IOServiceGetMatchingService(uint mainPort, nint matching);
        [DllImport(IOKit)] public static extern int IOObjectRelease(uint obj);

        [DllImport(CF)] public static extern void CFRelease(nint cf);
        [DllImport(CF)] public static extern nint CFSetGetCount(nint set);
        [DllImport(CF)] public static extern void CFSetGetValues(nint set, [Out] nint[] values);
        [DllImport(CF)] public static extern nint CFRunLoopGetCurrent();
        [DllImport(CF)] public static extern void CFRunLoopRun();
        [DllImport(CF)] public static extern void CFRunLoopStop(nint runLoop);
        [DllImport(CF)] private static extern nint CFStringCreateWithCString(nint allocator, byte[] text, uint encoding);
        [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(nint cf, byte[] buffer, nint size, uint encoding);
        [DllImport(CF)] private static extern nint CFGetTypeID(nint cf);
        [DllImport(CF)] private static extern nint CFStringGetTypeID();
        [DllImport(CF)] private static extern nint CFNumberGetTypeID();
        [DllImport(CF)] private static extern nint CFNumberCreate(nint allocator, int type, ref long value);
        [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(nint number, int type, out long value);
        [DllImport(CF)] private static extern nint CFDictionaryCreate(nint allocator, nint[] keys, nint[] values, nint count, nint keyCallbacks, nint valueCallbacks);

        private static readonly Lazy<nint> CfLibrary = new(() => NativeLibrary.Load(CF));
        private static nint Export(string name) => NativeLibrary.GetExport(CfLibrary.Value, name);

        public static nint DefaultMode => Marshal.ReadIntPtr(Export("kCFRunLoopDefaultMode"));

        private static nint Cf(string text) => CFStringCreateWithCString(0, Encoding.UTF8.GetBytes(text + "\0"), Utf8);

        /// <summary>A CFDictionary of string keys to numbers (a matching dictionary).</summary>
        public static nint Dictionary(params (string Key, long Value)[] entries)
        {
            var keys = entries.Select(e => Cf(e.Key)).ToArray();
            var values = entries.Select(e => { var v = e.Value; return CFNumberCreate(0, NumberSInt64, ref v); }).ToArray();
            var dict = CFDictionaryCreate(0, keys, values, entries.Length,
                Export("kCFTypeDictionaryKeyCallBacks"), Export("kCFTypeDictionaryValueCallBacks"));
            foreach (var cf in keys.Concat(values)) CFRelease(cf);
            return dict;
        }

        public static long? IntProperty(nint device, string key)
        {
            var value = Property(device, key);
            return value != 0 && CFGetTypeID(value) == CFNumberGetTypeID() && CFNumberGetValue(value, NumberSInt64, out var n) ? n : null;
        }

        public static string? StringProperty(nint device, string key)
        {
            var value = Property(device, key);
            if (value == 0 || CFGetTypeID(value) != CFStringGetTypeID()) return null;
            var buffer = new byte[512];
            return CFStringGetCString(value, buffer, buffer.Length, Utf8) ? Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0)) : null;
        }

        /// <summary>A property of the device (not owned by the caller: IOHIDDeviceGetProperty follows the Get rule).</summary>
        private static nint Property(nint device, string key)
        {
            var cfKey = Cf(key);
            try { return IOHIDDeviceGetProperty(device, cfKey); }
            finally { CFRelease(cfKey); }
        }
    }
}
