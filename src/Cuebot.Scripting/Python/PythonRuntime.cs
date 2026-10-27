using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Scripting;

/// <summary>
/// The Python interpreter scripts run with: either a managed CPython (downloaded to &lt;data&gt;/python, with its own
/// virtual environment) or a user-chosen interpreter from <see cref="ScriptingSettings.CustomPythonPath"/>.
/// Also manages pip packages. Install and package operations run one at a time.
/// </summary>
public sealed class PythonRuntime : IAsyncDisposable
{
    private const string Source = "Python";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    private readonly JsonFile<ScriptingSettings> _settings;
    private readonly PythonInstaller _installer;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    public PythonRuntime(AppPaths paths, JsonFile<ScriptingSettings> settings, HttpClient http, AppLog log)
    {
        Layout = new PythonLayout(paths.GetDirectory("python"));
        _settings = settings;
        _installer = new PythonInstaller(http, Layout);
        _log = log;
        DetectWithoutStarting();
    }

    /// <summary>Raised (on a background thread) whenever state, status, version or executable change.</summary>
    public event Action? StateChanged;

    public PythonRuntimeState State { get; private set; }
    public string StatusMessage { get; private set; } = "";
    public string? Version { get; private set; }

    /// <summary>The interpreter scripts run with; null unless <see cref="State"/> is Ready.</summary>
    public string? PythonExecutable { get; private set; }

    public ScriptingSettings Settings => _settings.Value;

    internal PythonLayout Layout { get; }

    /// <summary>Set by the script host: stops processes using the interpreter before its files change.</summary>
    internal Func<Task>? ReleaseInterpreter { get; set; }

    private string CustomPath => Settings.CustomPythonPath.Trim();

    /// <summary>Saves <see cref="Settings"/>. Call <see cref="RefreshAsync"/> afterwards to apply an interpreter change.</summary>
    public void SaveSettings() => _settings.SaveNow();

    /// <summary>Verifies the interpreter by starting it (called at app start).</summary>
    public Task StartAsync(CancellationToken ct = default) => RefreshCoreAsync(ct);

    /// <summary>Re-detects the interpreter (custom path or managed install) and its version.</summary>
    public Task RefreshAsync() => RefreshCoreAsync(CancellationToken.None);

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await ProbeAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Downloads and installs the managed Python runtime, replacing any previous one.</summary>
    public async Task InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (CustomPath.Length > 0)
            throw new InvalidOperationException("A custom Python interpreter is configured; clear it to use the managed runtime");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await ReleaseInterpreterAsync().ConfigureAwait(false);
            SetState(PythonRuntimeState.Installing, "Installing Python...", null, null);
            var reporter = new InlineProgress<string>(message =>
            {
                StatusMessage = message;
                StateChanged?.Invoke();
                progress?.Report(message);
            });
            await _installer.InstallAsync(reporter, linked.Token).ConfigureAwait(false);
            _log.Info(Source, "Python runtime installed");
        }
        catch (OperationCanceledException)
        {
            await ProbeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(Source, "Installing Python failed", ex);
            SetState(PythonRuntimeState.Error, $"Installing Python failed: {ex.Message}", null, null);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PythonPackage>> ListPackagesAsync(CancellationToken ct = default)
    {
        var result = await RunPipAsync(["list", "--format=json"], null, ct, modifiesEnvironment: false).ConfigureAwait(false);
        return ParsePackageList(result.StandardOutput);
    }

    /// <summary>Installs a requirement such as "requests" or "requests==2.32.3".</summary>
    /// <exception cref="ArgumentException">The spec is not a plain requirement specifier.</exception>
    public async Task InstallPackageAsync(string spec, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var requirement = PackageSpec.ValidateRequirement(spec);
        await RunPipAsync(["install", requirement], progress, ct, modifiesEnvironment: true).ConfigureAwait(false);
        _log.Info(Source, $"Installed package {requirement}");
    }

    public async Task UninstallPackageAsync(string name, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var package = PackageSpec.ValidateName(name);
        await RunPipAsync(["uninstall", "-y", package], progress, ct, modifiesEnvironment: true).ConfigureAwait(false);
        _log.Info(Source, $"Uninstalled package {package}");
    }

    internal static IReadOnlyList<PythonPackage> ParsePackageList(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(p => new PythonPackage(p.GetProperty("name").GetString() ?? "", p.GetProperty("version").GetString() ?? ""))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<ProcessResult> RunPipAsync(string[] pipArguments, IProgress<string>? progress, CancellationToken ct,
        bool modifiesEnvironment)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var python = PythonExecutable ?? throw new InvalidOperationException(NotReadyMessage());
            if (modifiesEnvironment) await ReleaseInterpreterAsync().ConfigureAwait(false);
            var result = await ProcessRunner.RunAsync(python, ["-m", "pip", .. pipArguments], Layout.Root, progress, linked.Token)
                .ConfigureAwait(false);
            if (result.ExitCode != 0) throw new InvalidOperationException($"pip {pipArguments[0]} failed: {result.LastErrorLine}");
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Message explaining why Python cannot run right now.</summary>
    internal string NotReadyMessage() => State switch
    {
        PythonRuntimeState.Installing => "Python is still being installed",
        PythonRuntimeState.Error => $"Python is not available: {StatusMessage}",
        _ => "Python is not installed. Install Python from the Scripting page",
    };

    /// <summary>Quick file-based detection so the state is right before <see cref="StartAsync"/> runs.</summary>
    private void DetectWithoutStarting()
    {
        if (CustomPath.Length > 0)
        {
            if (File.Exists(CustomPath)) SetState(PythonRuntimeState.Ready, "Custom Python interpreter", null, CustomPath);
            else SetState(PythonRuntimeState.Error, $"Custom Python interpreter not found: {CustomPath}", null, null);
        }
        else if (File.Exists(Layout.EnvironmentExecutable))
        {
            SetState(PythonRuntimeState.Ready, "Python is installed", Layout.ReadEnvironmentVersion(), Layout.EnvironmentExecutable);
        }
        else
        {
            SetState(PythonRuntimeState.NotInstalled, "Python is not installed", null, null);
        }
    }

    private async Task ProbeAsync()
    {
        var custom = CustomPath;
        var executable = custom.Length > 0 ? custom : Layout.EnvironmentExecutable;
        if (!File.Exists(executable))
        {
            if (custom.Length > 0) SetState(PythonRuntimeState.Error, $"Custom Python interpreter not found: {custom}", null, null);
            else SetState(PythonRuntimeState.NotInstalled, "Python is not installed", null, null);
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(ProbeTimeout);
            var result = await ProcessRunner.RunAsync(executable,
                ["-c", "import platform; print(platform.python_version())"], Layout.Root, null, timeout.Token).ConfigureAwait(false);
            var version = result.StandardOutput.Trim();
            if (result.ExitCode != 0 || version.Length == 0)
                throw new InvalidOperationException(result.LastErrorLine.Length > 0 ? result.LastErrorLine : $"exit code {result.ExitCode}");

            var description = custom.Length > 0 ? "Custom Python interpreter" : "Python is installed";
            SetState(PythonRuntimeState.Ready, $"{description} ({version})", version, executable);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            SetState(PythonRuntimeState.Error, $"Python at {executable} did not respond within {ProbeTimeout.TotalSeconds:0} seconds", null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(Source, $"Python at {executable} could not be started", ex);
            SetState(PythonRuntimeState.Error, $"Python could not be started: {ex.Message}", null, null);
        }
    }

    private Task ReleaseInterpreterAsync() => ReleaseInterpreter?.Invoke() ?? Task.CompletedTask;

    private void SetState(PythonRuntimeState state, string message, string? version, string? executable)
    {
        State = state;
        StatusMessage = message;
        Version = version;
        PythonExecutable = executable;
        StateChanged?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        return ValueTask.CompletedTask;
    }
}
