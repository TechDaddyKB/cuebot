using Avalonia;
using Avalonia.Headless;
using Avalonia.Logging;
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
