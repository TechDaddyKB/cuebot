using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Cuebot.UiTests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace Cuebot.UiTests;

public static class TestApp
{
    public static readonly BindingErrorSink BindingErrors = new();

    public static AppBuilder BuildAvaloniaApp()
    {
        Logger.Sink = BindingErrors;
        return AppBuilder.Configure<Cuebot.App.App>()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }

    /// <summary>Saves the window's last rendered frame as a PNG (the screenshots the tests leave behind).</summary>
    /// <remarks>
    /// Always save through this. Avalonia's Skia bitmap encodes a view of its own pixels and nothing keeps the bitmap
    /// alive meanwhile, so with <c>CaptureRenderedFrame()?.Save(...)</c> a GC can finalize it and free the pixels
    /// mid-encode: a SIGSEGV that ends the test process, and the runner then reports the tests that ran as if they
    /// were all of them. Holding the frame until the save returns (and disposing it) closes that window.
    /// </remarks>
    public static void SaveFrame(TopLevel window, string path)
    {
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(path, PngBitmapEncoderOptions.Default);
    }
}

/// <summary>Collects binding and XAML errors so tests can fail on broken bindings.</summary>
public sealed class BindingErrorSink : ILogSink
{
    private readonly List<string> _errors = [];

    public IReadOnlyList<string> Errors
    {
        get { lock (_errors) return _errors.ToArray(); }
    }

    public void Clear()
    {
        lock (_errors) _errors.Clear();
    }

    public bool IsEnabled(LogEventLevel level, string area) =>
        level >= LogEventLevel.Warning && area is LogArea.Binding or LogArea.Property;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Log(level, area, source, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (!IsEnabled(level, area)) return;
        var message = propertyValues.Length == 0 ? messageTemplate : $"{messageTemplate} | {string.Join(" | ", propertyValues)}";
        lock (_errors) _errors.Add($"[{area}] {message}");
    }
}
