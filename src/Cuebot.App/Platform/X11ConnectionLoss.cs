using System.Runtime.InteropServices;

namespace Cuebot.App.Platform;

/// <summary>
/// When the X server goes away, Xlib calls its I/O error handler and then exits the process, so nothing after it runs.
/// This handler runs <c>onLost</c> (flushing the stores) first; Xlib still exits afterwards, with code 1.
/// </summary>
internal static class X11ConnectionLoss
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int IoErrorHandler(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XSetIOErrorHandler(IoErrorHandler handler);

    private static IoErrorHandler? s_handler;   // kept alive for as long as Xlib can call it

    public static void Handle(Action onLost)
    {
        if (!OperatingSystem.IsLinux() || s_handler is not null) return;
        s_handler = _ =>
        {
            try { onLost(); }
            catch { }   // Xlib exits whatever happens here
            return 0;
        };
        try { XSetIOErrorHandler(s_handler); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { s_handler = null; }
    }
}
