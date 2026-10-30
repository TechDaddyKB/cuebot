using Cuebot.Core.Actions;

namespace Cuebot.Scripting;

/// <summary>Shown above the Python code editor while Python is not ready, with an "Install Python" button.</summary>
internal sealed class PythonInstallNotice : IParameterNoticeSource
{
    private readonly PythonRuntime _runtime;

    public PythonInstallNotice(PythonRuntime runtime)
    {
        _runtime = runtime;
        runtime.StateChanged += () => Changed?.Invoke();
    }

    public event Action? Changed;

    public ParameterNotice? Current => _runtime.State switch
    {
        PythonRuntimeState.Ready => null,
        PythonRuntimeState.Installing => new(_runtime.StatusMessage),
        PythonRuntimeState.NotInstalled when _runtime.UsesManagedPython && _runtime.ManagedPythonDescription is { } python =>
            new(_runtime.Settings.InstallPythonAutomatically
                    ? $"Python steps need {python}, downloaded once. It is installed the first time this step runs, or install it now."
                    : $"Python steps need {python}, downloaded once.",
                "Install Python", () => _runtime.InstallAsync()),
        PythonRuntimeState.NotInstalled =>
            new("Python steps need a Python interpreter. Set one on Automation > Scripting."),
        _ => new(_runtime.StatusMessage,
            _runtime.UsesManagedPython && _runtime.ManagedPythonDescription is not null ? "Try again" : null,
            _runtime.UsesManagedPython ? () => _runtime.InstallAsync() : null),
    };
}
