using Cuebot.App.Diagnostics;
using Cuebot.App.Shell;
using Cuebot.App.Updates;
using Cuebot.Core;
using Cuebot.Core.Desktop.Clipboard;

namespace Cuebot.App.ViewModels.Support;

/// <summary>What the Support page uses from the app; every member can be replaced in tests.</summary>
public sealed class SupportServices
{
    public required AppPaths Paths { get; init; }

    /// <summary>Current connection states (name, state, status message).</summary>
    public Func<IReadOnlyList<ConnectionInfo>> Connections { get; init; } = () => [];

    public IClipboardService? Clipboard { get; init; }

    public Action<string> OpenUrl { get; init; } = UrlLauncher.Open;

    public Action<string> OpenFolder { get; init; } = FolderLauncher.Open;

    /// <summary>For the update checker; null uses the network.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    public GhCli Gh { get; init; } = new();

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;

    public Func<EnvironmentSnapshot> Environment { get; init; } = () => EnvironmentInfo.Collect(Program.BackendName);

    public PackagingEnvironment Packaging { get; init; } = PackagingEnvironment.Current;

    public SemVersion CurrentVersion { get; init; } = SemVersion.Current;

    /// <summary>Run the once-a-day start-up update check (when the user's setting allows it).</summary>
    public bool CheckUpdatesOnStart { get; init; } = true;

    public string? HomeFolder { get; init; } = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

    public static SupportServices From(AppHost host, IShellHost shell) => new()
    {
        Paths = host.Get<AppPaths>(),
        Connections = () => host.GetAll<IConnectionStatus>().Select(c => new ConnectionInfo(c.Name, c.State.ToString(), c.StatusMessage)).ToArray(),
        Clipboard = host.Get<IClipboardService>(),
        OpenFolder = shell.OpenFolder,
    };
}
