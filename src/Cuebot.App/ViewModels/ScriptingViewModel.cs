using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Scripting;
using Cuebot.Scripting;

namespace Cuebot.App.ViewModels;

/// <summary>Python runtime installation, pip packages and the script API reference.</summary>
public sealed partial class ScriptingViewModel : PageViewModel
{
    private readonly PythonRuntime _python;

    public ScriptingViewModel(PythonRuntime python, ScriptApi api) : base("Scripting")
    {
        _python = python;
        Functions = api.Functions.Select(f => $"{f.Name}({string.Join(", ", f.ParameterNames)}) — {f.Description}").ToArray();
        python.StateChanged += () => OnUi(() =>
        {
            OnPropertyChanged(string.Empty);
            if (python.State == PythonRuntimeState.Ready) _ = RefreshPackages();
        });
        if (python.State == PythonRuntimeState.Ready) _ = RefreshPackages();
    }

    public IReadOnlyList<string> Functions { get; }
    public ObservableCollection<PythonPackage> Packages { get; } = [];

    public string State => _python.State.ToString();
    public string StatusMessage => _python.StatusMessage;
    public string Version => _python.Version ?? "—";
    public bool IsReady => _python.State == PythonRuntimeState.Ready;
    public bool CanInstall => _python.State is PythonRuntimeState.NotInstalled or PythonRuntimeState.Error;

    public string CustomPythonPath
    {
        get => _python.Settings.CustomPythonPath;
        set { _python.Settings.CustomPythonPath = value.Trim(); _python.SaveSettings(); _ = _python.RefreshAsync(); }
    }

    [ObservableProperty] private string _packageSpec = "";
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private bool _isBusy;

    private const int MaxOutput = 8000;

    private IProgress<string> Progress => new Progress<string>(line =>
    {
        var text = Output + line + "\n";
        Output = text.Length > MaxOutput ? text[^MaxOutput..] : text;
    });

    [RelayCommand]
    private Task InstallRuntime() => Busy(() => _python.InstallAsync(Progress));

    [RelayCommand]
    private Task InstallPackage() => Busy(async () =>
    {
        await _python.InstallPackageAsync(PackageSpec, Progress);
        PackageSpec = "";
        await RefreshPackages();
    });

    [RelayCommand]
    private Task UninstallPackage(PythonPackage package) => Busy(async () =>
    {
        await _python.UninstallPackageAsync(package.Name, Progress);
        await RefreshPackages();
    });

    [RelayCommand]
    private async Task RefreshPackages()
    {
        try
        {
            var packages = await _python.ListPackagesAsync();
            Packages.Clear();
            foreach (var p in packages.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) Packages.Add(p);
        }
        catch (Exception ex)
        {
            Output += ex.Message + "\n";
        }
    }

    private async Task Busy(Func<Task> operation)
    {
        IsBusy = true;
        try { await operation(); }
        catch (Exception ex) { Output += $"Error: {ex.Message}\n"; }
        finally { IsBusy = false; }
    }
}
